using System.Text.Json;

namespace ZZZSwitch.Core.Services;

public sealed class AppPaths
{
    private string _backupsRoot;

    public AppPaths(string? dataRoot = null, string? configRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZSwitch");
        ConfigRoot = configRoot ?? Path.Combine(AppContext.BaseDirectory, "config");
        _backupsRoot = LoadConfiguredBackupsRoot();
    }

    public string DataRoot { get; }
    public string ConfigRoot { get; }
    public string DefaultBackupsRoot => Path.Combine(DataRoot, "Backups");
    public string BackupsRoot => _backupsRoot;
    public string LogsRoot => Path.Combine(DataRoot, "Logs");
    public string TempRoot => Path.Combine(DataRoot, "Temp");
    public string ProfileSnapshotsRoot => Path.Combine(DataRoot, "ProfileSnapshots");
    public string HotUpdateManifestsRoot => Path.Combine(DataRoot, "HotUpdateCaches");
    public string HotUpdateJournalFile => Path.Combine(DataRoot, "hot-update-transaction.json");
    public string FileTransactionJournalFile => Path.Combine(DataRoot, "file-transaction.json");
    public string ApplicationLockFile => Path.Combine(DataRoot, "application.lock");
    public string OperationLockFile => Path.Combine(DataRoot, "operation.lock");
    public string StateFile => Path.Combine(DataRoot, "state.json");
    public string CacheLocationsFile => Path.Combine(DataRoot, "cache-locations.json");
    public string BackupLocationFile => Path.Combine(DataRoot, "backup-location.json");

    internal void SetBackupsRoot(string path) =>
        _backupsRoot = BackupLocationService.NormalizeBackupRoot(path);

    public void EnsureWritableDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(ProfileSnapshotsRoot);
        Directory.CreateDirectory(HotUpdateManifestsRoot);
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
}
