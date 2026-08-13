namespace ZZZSwitch.Core.Services;

public sealed record LogCleanupResult(int RemovedFileCount, long FreedBytes);

public sealed class LogMaintenanceService
{
    private readonly AppPaths _paths;

    public LogMaintenanceService(AppPaths paths) => _paths = paths;

    public LogCleanupResult CleanExpiredLogs(int retentionDays, DateTimeOffset? now = null)
    {
        if (retentionDays is not (7 or 30))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        if (!Directory.Exists(_paths.LogsRoot))
        {
            return new(0, 0);
        }

        var cutoff = (now ?? DateTimeOffset.Now).UtcDateTime.AddDays(-retentionDays);
        var removed = 0;
        var bytes = 0L;
        foreach (var path in Directory.GetFiles(_paths.LogsRoot, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            bytes += info.Length;
            info.IsReadOnly = false;
            info.Delete();
            removed++;
        }

        return new(removed, bytes);
    }
}
