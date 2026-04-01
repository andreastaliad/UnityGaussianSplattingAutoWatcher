using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using RabbitMQ.Client;
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
    private const string CreatorTypeName = "GaussianSplatting.Editor.GaussianSplatAssetCreator, GaussianSplattingEditor";

    private const string RabbitMqHost = "localhost";
    private const int RabbitMqPort = 5672;
    private const string RabbitMqUser = "guest";
    private const string RabbitMqPass = "guest";
    private const string RabbitMqVirtualHost = "/";
    private const string RabbitMqQueueName = "gaussian.ply.inbox";
    private const bool RabbitMqQueueDurable = true;

    private static readonly List<ProcessedEntry> s_ProcessedEntries = new();
    private static readonly HashSet<string> s_ProcessedKeys = new(StringComparer.Ordinal);

    private static readonly string s_ProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
    private static readonly string s_StateFilePath = Path.Combine(s_ProjectRoot, "Library", "GaussianSplatInboxState.json");

    private static bool s_IsScanRunning;
    private static double s_NextPollAt;
    private static IConnection s_RabbitConnection;
    private static IChannel s_RabbitChannel;
    private static bool s_RabbitReady;

    static GaussianSplatInboxWatcher()
    {
        LoadState();
        EnsureAssetFolders();
        s_NextPollAt = EditorApplication.timeSinceStartup + 2.0;
        EditorApplication.update += OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload += DisposeRabbitMq;
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
            if (!TryInitializeRabbitMq())
                return;

            var delivery = s_RabbitChannel
                .BasicGetAsync(RabbitMqQueueName, autoAck: false, cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (delivery == null)
                return;

            ulong deliveryTag = delivery.DeliveryTag;

            try
            {
                string message = Encoding.UTF8.GetString(delivery.Body.ToArray()).Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    s_RabbitChannel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return;
                }

                string filePath = ResolveIncomingPath(message);
                if (!TryGetFingerprint(filePath, out var fingerprint))
                {
                    Debug.LogWarning($"[GaussianSplatInboxWatcher] Queue message did not resolve to a valid file: '{message}'.");
                    s_RabbitChannel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return;
                }

                if (s_ProcessedKeys.Contains(MakeProcessedKey(fingerprint)))
                {
                    s_RabbitChannel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return;
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                if (!TryConvertToGaussianAsset(filePath, out var errorMessage))
                {
                    Debug.LogError($"[GaussianSplatInboxWatcher] Failed to convert '{filePath}': {errorMessage}");
                    if (MoveFailedFiles)
                        MoveToFolder(filePath, FailedFolderAssetPath, "failed");
                    s_RabbitChannel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return;
                }

                MarkProcessed(fingerprint);

                if (MoveProcessedFiles)
                    MoveToFolder(filePath, ProcessedFolderAssetPath, "processed");

                Debug.Log($"[GaussianSplatInboxWatcher] Converted '{Path.GetFileName(filePath)}'.");
                s_RabbitChannel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GaussianSplatInboxWatcher] Queue message handling failed: {ex.Message}");
                s_RabbitChannel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        finally
        {
            s_IsScanRunning = false;
        }
    }

    private static bool TryInitializeRabbitMq()
    {
        if (s_RabbitReady && s_RabbitConnection?.IsOpen == true && s_RabbitChannel?.IsOpen == true)
            return true;

        DisposeRabbitMq();

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = RabbitMqHost,
                Port = RabbitMqPort,
                UserName = RabbitMqUser,
                Password = RabbitMqPass,
                VirtualHost = RabbitMqVirtualHost,
            };

            s_RabbitConnection = factory.CreateConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
            s_RabbitChannel = s_RabbitConnection.CreateChannelAsync(new CreateChannelOptions(false, false), CancellationToken.None).GetAwaiter().GetResult();
            s_RabbitChannel.QueueDeclareAsync(
                queue: RabbitMqQueueName,
                durable: RabbitMqQueueDurable,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            s_RabbitReady = true;
            return true;
        }
        catch (Exception ex)
        {
            s_RabbitReady = false;
            Debug.LogWarning($"[GaussianSplatInboxWatcher] RabbitMQ connection failed ({RabbitMqHost}:{RabbitMqPort}, queue '{RabbitMqQueueName}'): {ex.Message}");
            return false;
        }
    }

    private static void DisposeRabbitMq()
    {
        try
        {
            s_RabbitChannel?.Dispose();
            s_RabbitConnection?.Dispose();
        }
        catch
        {
        }
        finally
        {
            s_RabbitChannel = null;
            s_RabbitConnection = null;
            s_RabbitReady = false;
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
        catch (Exception ex)
        {
            Debug.LogWarning($"[GaussianSplatInboxWatcher] Could not fingerprint '{filePath}': {ex.GetType().Name}: {ex.Message}");
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

    private static string ResolveIncomingPath(string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return string.Empty;

        if (Path.IsPathRooted(incoming))
            return Path.GetFullPath(incoming);

        if (incoming.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            return AssetPathToAbsolute(incoming);

        return Path.GetFullPath(Path.Combine(s_ProjectRoot, incoming));
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

}
