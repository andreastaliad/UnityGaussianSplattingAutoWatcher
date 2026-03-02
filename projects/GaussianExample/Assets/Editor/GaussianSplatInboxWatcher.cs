using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GaussianSplatInboxWatcher
{
    private const string InboxFolderAssetPath = "Assets/PLY/inbox";
    private const string ProcessedFolderAssetPath = "Assets/PLY/processed";
    private const string FailedFolderAssetPath = "Assets/PLY/failed";
    private const string OutputFolderAssetPath = "Assets/GaussianAssets";

    private const bool MoveProcessedFiles = true;
    private const bool MoveFailedFiles = true;
    private const bool ImportCamerasJson = false;
    private const double PollIntervalSeconds = 10.0;
    private const double FileStableForSeconds = 5.0;
    private const string CreatorTypeName = "GaussianSplatting.Editor.GaussianSplatAssetCreator, GaussianSplattingEditor";

    private static readonly Dictionary<string, FileObservation> s_Observations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> s_MissingObservationKeys = new();
    private static readonly List<ProcessedEntry> s_ProcessedEntries = new();
    private static readonly HashSet<string> s_ProcessedKeys = new(StringComparer.Ordinal);

    private static readonly string s_ProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
    private static readonly string s_StateFilePath = Path.Combine(s_ProjectRoot, "Library", "GaussianSplatInboxState.json");

    private static bool s_IsScanRunning;
    private static double s_NextPollAt;

    static GaussianSplatInboxWatcher()
    {
        LoadState();
        EnsureAssetFolders();
        s_NextPollAt = EditorApplication.timeSinceStartup + 2.0;
        EditorApplication.update += OnEditorUpdate;
    }

    private static void OnEditorUpdate()
    {
        if (s_IsScanRunning)
            return;

        if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            return;

        var now = EditorApplication.timeSinceStartup;
        if (now < s_NextPollAt)
            return;

        s_NextPollAt = now + PollIntervalSeconds;
        ScanForNewPly();
    }

    private static void ScanForNewPly()
    {
        s_IsScanRunning = true;
        try
        {
            // Docker bind-mount writes can miss Unity file watcher notifications.
            // Poll cycle forces a sync refresh so new external files become visible.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            string inboxAbsolute = AssetPathToAbsolute(InboxFolderAssetPath);
            if (!Directory.Exists(inboxAbsolute))
                return;

            var plyFiles = Directory.GetFiles(inboxAbsolute, "*.ply", SearchOption.TopDirectoryOnly);
            Array.Sort(plyFiles, StringComparer.OrdinalIgnoreCase);

            CleanupMissingObservations(plyFiles);

            foreach (var filePath in plyFiles)
            {
                if (!TryGetFingerprint(filePath, out var fingerprint))
                    continue;

                if (s_ProcessedKeys.Contains(MakeProcessedKey(fingerprint)))
                    continue;

                if (!IsStable(filePath, fingerprint))
                    continue;

                if (!TryConvertToGaussianAsset(filePath, out var errorMessage))
                {
                    Debug.LogError($"[GaussianSplatInboxWatcher] Failed to convert '{filePath}': {errorMessage}");
                    if (MoveFailedFiles)
                        MoveToFolder(filePath, FailedFolderAssetPath, "failed");
                    s_Observations.Remove(filePath);
                    continue;
                }

                MarkProcessed(fingerprint);

                if (MoveProcessedFiles)
                    MoveToFolder(filePath, ProcessedFolderAssetPath, "processed");

                Debug.Log($"[GaussianSplatInboxWatcher] Converted '{Path.GetFileName(filePath)}'.");
                break;
            }
        }
        finally
        {
            s_IsScanRunning = false;
        }
    }

    private static bool TryConvertToGaussianAsset(string inputFilePath, out string errorMessage)
    {
        errorMessage = string.Empty;

        var creatorType = Type.GetType(CreatorTypeName, throwOnError: false);
        if (creatorType == null)
        {
            errorMessage = $"Could not resolve type '{CreatorTypeName}'.";
            return false;
        }

        ScriptableObject creator = null;
        try
        {
            creator = ScriptableObject.CreateInstance(creatorType);
            if (creator == null)
            {
                errorMessage = "Could not create GaussianSplatAssetCreator instance.";
                return false;
            }

            SetPrivateField(creatorType, creator, "m_InputFile", inputFilePath);
            SetPrivateField(creatorType, creator, "m_OutputFolder", OutputFolderAssetPath);
            SetPrivateField(creatorType, creator, "m_ImportCameras", ImportCamerasJson);

            var qualityField = creatorType.GetField("m_Quality", BindingFlags.Instance | BindingFlags.NonPublic);
            if (qualityField != null && qualityField.FieldType.IsEnum)
            {
                var medium = Enum.Parse(qualityField.FieldType, "Medium");
                qualityField.SetValue(creator, medium);
            }

            InvokePrivateMethod(creatorType, creator, "ApplyQualityLevel");
            InvokePrivateMethod(creatorType, creator, "CreateAsset");

            string creatorError = GetPrivateField<string>(creatorType, creator, "m_ErrorMessage");
            if (!string.IsNullOrWhiteSpace(creatorError))
            {
                errorMessage = creatorError;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex is TargetInvocationException tie ? (tie.InnerException?.Message ?? tie.Message) : ex.Message;
            return false;
        }
        finally
        {
            if (creator != null)
                ScriptableObject.DestroyImmediate(creator);

            EditorUtility.ClearProgressBar();
        }
    }

    private static void MoveToFolder(string sourceAbsolutePath, string destinationFolderAssetPath, string destinationLabel)
    {
        EnsureAssetFolders();

        string sourceAssetPath = AbsoluteToAssetPath(sourceAbsolutePath);
        if (string.IsNullOrEmpty(sourceAssetPath))
            return;

        AssetDatabase.ImportAsset(sourceAssetPath, ImportAssetOptions.ForceSynchronousImport);
        string destinationAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{destinationFolderAssetPath}/{Path.GetFileName(sourceAbsolutePath)}");
        string moveError = AssetDatabase.MoveAsset(sourceAssetPath, destinationAssetPath);
        if (!string.IsNullOrEmpty(moveError))
            Debug.LogWarning($"[GaussianSplatInboxWatcher] Could not move '{sourceAssetPath}' to {destinationLabel} folder: {moveError}");
    }

    private static bool IsStable(string filePath, FileFingerprint fingerprint)
    {
        var now = EditorApplication.timeSinceStartup;

        if (!s_Observations.TryGetValue(filePath, out var obs))
        {
            s_Observations[filePath] = FileObservation.From(fingerprint, now);
            return false;
        }

        bool sameFingerprint = obs.Size == fingerprint.Size && obs.LastWriteUtcTicks == fingerprint.LastWriteUtcTicks;
        if (!sameFingerprint)
        {
            s_Observations[filePath] = FileObservation.From(fingerprint, now);
            return false;
        }

        return now - obs.FirstSeenUnchangedAt >= FileStableForSeconds;
    }

    private static void CleanupMissingObservations(string[] existingFiles)
    {
        s_MissingObservationKeys.Clear();

        var existing = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var key in s_Observations.Keys)
        {
            if (!existing.Contains(key))
                s_MissingObservationKeys.Add(key);
        }

        foreach (var key in s_MissingObservationKeys)
            s_Observations.Remove(key);
    }

    private static bool TryGetFingerprint(string filePath, out FileFingerprint fingerprint)
    {
        fingerprint = default;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return false;

            fingerprint = new FileFingerprint
            {
                RelativePath = AbsoluteToAssetPath(filePath),
                Size = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            };

            return !string.IsNullOrWhiteSpace(fingerprint.RelativePath);
        }
        catch
        {
            return false;
        }
    }

    private static string MakeProcessedKey(in FileFingerprint fingerprint)
    {
        return $"{fingerprint.RelativePath}|{fingerprint.Size}|{fingerprint.LastWriteUtcTicks}";
    }

    private static void MarkProcessed(in FileFingerprint fingerprint)
    {
        var key = MakeProcessedKey(fingerprint);
        if (!s_ProcessedKeys.Add(key))
            return;

        s_ProcessedEntries.Add(new ProcessedEntry
        {
            RelativePath = fingerprint.RelativePath,
            Size = fingerprint.Size,
            LastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
        });

        SaveState();
    }

    private static void EnsureAssetFolders()
    {
        EnsureFolder("Assets", "PLY");
        EnsureFolder("Assets/PLY", "inbox");
        EnsureFolder("Assets/PLY", "processed");
        EnsureFolder("Assets/PLY", "failed");
        EnsureFolder("Assets", "GaussianAssets");
    }

    private static void EnsureFolder(string parentAssetPath, string folderName)
    {
        string childPath = $"{parentAssetPath}/{folderName}";
        if (AssetDatabase.IsValidFolder(childPath))
            return;

        AssetDatabase.CreateFolder(parentAssetPath, folderName);
    }

    private static string AssetPathToAbsolute(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return string.Empty;

        if (!assetPath.StartsWith("Assets", StringComparison.Ordinal))
            return string.Empty;

        string relative = assetPath.Substring("Assets".Length).TrimStart('/', '\\');
        return Path.Combine(Application.dataPath, relative);
    }

    private static string AbsoluteToAssetPath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return string.Empty;

        string normalizedAbsolute = Path.GetFullPath(absolutePath).Replace('\\', '/');
        string normalizedAssetsRoot = Path.GetFullPath(Application.dataPath).Replace('\\', '/');

        if (!normalizedAbsolute.StartsWith(normalizedAssetsRoot, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        string relativeToAssets = normalizedAbsolute.Substring(normalizedAssetsRoot.Length).TrimStart('/');
        return string.IsNullOrEmpty(relativeToAssets) ? "Assets" : $"Assets/{relativeToAssets}";
    }

    private static void LoadState()
    {
        s_ProcessedEntries.Clear();
        s_ProcessedKeys.Clear();

        if (!File.Exists(s_StateFilePath))
            return;

        try
        {
            string json = File.ReadAllText(s_StateFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var state = JsonUtility.FromJson<ProcessedState>(json);
            if (state?.Entries == null)
                return;

            foreach (var entry in state.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.RelativePath))
                    continue;

                var fp = new FileFingerprint
                {
                    RelativePath = entry.RelativePath,
                    Size = entry.Size,
                    LastWriteUtcTicks = entry.LastWriteUtcTicks,
                };

                var key = MakeProcessedKey(fp);
                if (!s_ProcessedKeys.Add(key))
                    continue;

                s_ProcessedEntries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GaussianSplatInboxWatcher] Could not load state file: {ex.Message}");
        }
    }

    private static void SaveState()
    {
        try
        {
            var state = new ProcessedState { Entries = s_ProcessedEntries };
            string json = JsonUtility.ToJson(state, true);
            File.WriteAllText(s_StateFilePath, json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GaussianSplatInboxWatcher] Could not save state file: {ex.Message}");
        }
    }

    private static void SetPrivateField(Type type, object instance, string fieldName, object value)
    {
        var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
            throw new MissingFieldException(type.FullName, fieldName);

        field.SetValue(instance, value);
    }

    private static T GetPrivateField<T>(Type type, object instance, string fieldName)
    {
        var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
            return default;

        return field.GetValue(instance) is T value ? value : default;
    }

    private static void InvokePrivateMethod(Type type, object instance, string methodName)
    {
        var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
            throw new MissingMethodException(type.FullName, methodName);

        method.Invoke(instance, null);
    }

    [Serializable]
    private sealed class ProcessedState
    {
        public List<ProcessedEntry> Entries = new();
    }

    [Serializable]
    private sealed class ProcessedEntry
    {
        public string RelativePath;
        public long Size;
        public long LastWriteUtcTicks;
    }

    private struct FileFingerprint
    {
        public string RelativePath;
        public long Size;
        public long LastWriteUtcTicks;
    }

    private struct FileObservation
    {
        public long Size;
        public long LastWriteUtcTicks;
        public double FirstSeenUnchangedAt;

        public static FileObservation From(in FileFingerprint fingerprint, double now)
        {
            return new FileObservation
            {
                Size = fingerprint.Size,
                LastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
                FirstSeenUnchangedAt = now,
            };
        }
    }
}
