using System.Text.Json;

namespace ZZZSwitch.Core.Services;

public sealed class AppPaths
{
    private string _storageRoot;
    private string _backupsRoot;
    private string? _storageGamePath;

    public AppPaths(
        string? dataRoot = null,
        string? configRoot = null,
        string? storageRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZSwitch");
        ConfigRoot = configRoot ?? Path.Combine(AppContext.BaseDirectory, "config");
        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            _storageRoot = Path.GetFullPath(storageRoot);
        }
        else
        {
            (_storageRoot, _storageGamePath) = LoadConfiguredStorage();
        }

        _backupsRoot = LoadConfiguredBackupsRoot();
    }

    public string DataRoot { get; }
    public string ConfigRoot { get; }
    public string StorageRoot => _storageRoot;
    public string? StorageGamePath => _storageGamePath;
    public bool UsesLegacyDataRoot => SamePath(StorageRoot, DataRoot);
    public string DefaultBackupsRoot => Path.Combine(StorageRoot, GameStorageLayout.BackupRecordsDirectoryName);
    public string BackupsRoot => _backupsRoot;
    public string LogsRoot => Path.Combine(StorageRoot, GameStorageLayout.LogsDirectoryName);
    public string TempRoot => Path.Combine(StorageRoot, GameStorageLayout.TempDirectoryName);
    public string ProfileSnapshotsRoot => Path.Combine(StorageRoot, GameStorageLayout.SnapshotsDirectoryName);
    public string HotUpdateManifestsRoot => Path.Combine(StorageRoot, GameStorageLayout.BlocksManifestsDirectoryName);
    public string ManifestCacheRoot => Path.Combine(StorageRoot, GameStorageLayout.SophonManifestsDirectoryName);
    public string OnlineDifferenceFilesRoot => Path.Combine(StorageRoot, GameStorageLayout.DownloadsDirectoryName);
    public string LegacyBackupContentRoot => Path.Combine(StorageRoot, GameStorageLayout.BackupContentDirectoryName);
    public string HotUpdateJournalFile => Path.Combine(DataRoot, "hot-update-transaction.json");
    public string FileTransactionJournalFile => Path.Combine(DataRoot, "file-transaction.json");
    public string ApplicationLockFile => Path.Combine(DataRoot, "application.lock");
    public string OperationLockFile => Path.Combine(DataRoot, "operation.lock");
    public string StateFile => Path.Combine(DataRoot, "state.json");
    public string CacheLocationsFile => Path.Combine(DataRoot, "cache-locations.json");
    public string BackupLocationFile => Path.Combine(DataRoot, "backup-location.json");
    public string StorageLocationFile => Path.Combine(DataRoot, "storage-location.json");
    public string UiSettingsFile => Path.Combine(DataRoot, "ui-settings.json");

    internal void SetBackupsRoot(string path) =>
        _backupsRoot = BackupLocationService.NormalizeBackupRoot(path);

    internal void SetGameStorage(string gamePath, bool persist)
    {
        var normalizedGamePath = Path.GetFullPath(gamePath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        _storageRoot = GameStorageLayout.GetAppDataRoot(normalizedGamePath);
        _storageGamePath = normalizedGamePath;
        _backupsRoot = LoadConfiguredBackupsRoot();
        if (persist)
        {
            Directory.CreateDirectory(DataRoot);
            AtomicJsonFile.Write(StorageLocationFile, new StorageLocationSettings
            {
                GamePath = normalizedGamePath
            });
        }
    }

    public void EnsureWritableDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(ProfileSnapshotsRoot);
        Directory.CreateDirectory(HotUpdateManifestsRoot);
        Directory.CreateDirectory(ManifestCacheRoot);
        Directory.CreateDirectory(OnlineDifferenceFilesRoot);
    }

    private string LoadConfiguredBackupsRoot()
    {
        var settingsPath = Path.Combine(DataRoot, "backup-location.json");
        if (!File.Exists(settingsPath))
        {
            return DefaultBackupsRoot;
        }

        try
        {
            using var stream = File.OpenRead(settingsPath);
            var settings = JsonSerializer.Deserialize<BackupLocationSettings>(stream, JsonSupport.Options);
            return string.IsNullOrWhiteSpace(settings?.BackupRootPath)
                ? DefaultBackupsRoot
                : BackupLocationService.NormalizeBackupRoot(settings.BackupRootPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return DefaultBackupsRoot;
        }
    }

    private (string StorageRoot, string? GamePath) LoadConfiguredStorage()
    {
        var settingsPath = Path.Combine(DataRoot, "storage-location.json");
        if (!File.Exists(settingsPath))
        {
            return (DataRoot, null);
        }

        try
        {
            using var stream = File.OpenRead(settingsPath);
            var settings = JsonSerializer.Deserialize<StorageLocationSettings>(stream, JsonSupport.Options);
            if (string.IsNullOrWhiteSpace(settings?.GamePath) ||
                !Path.IsPathFullyQualified(settings.GamePath))
            {
                return (DataRoot, null);
            }

            var gamePath = Path.GetFullPath(settings.GamePath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return (GameStorageLayout.GetAppDataRoot(gamePath), gamePath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return (DataRoot, null);
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

public sealed class StorageLocationSettings
{
    public string? GamePath { get; init; }
}
