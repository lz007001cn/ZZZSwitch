namespace ZZZSwitch.Core.Services;

public sealed class ClientMaintenanceService(AppPaths paths, InspectionService inspection)
{
    // The caller owns the normal operation lease. Preserve all authoritative
    // installation/backup/operation fields; only derived detection is reset.
    public void ResetDetection()
    {
        if (File.Exists(paths.FileTransactionJournalFile) || File.Exists(paths.HotUpdateJournalFile))
            throw new InvalidOperationException("有未完成事务，请先重试恢复；重置不能解除事务保护。");
        var store = new StateStore(paths);
        var loaded = store.LoadWithStatus();
        if (loaded.Warning is not null) throw new InvalidDataException(loaded.Warning + " 原始记录已保留。");
        if (loaded.State is { } state)
        {
            state.ClientDetection = null;
            store.Save(state);
        }
        inspection.InvalidateDetection();
    }
}
