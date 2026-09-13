using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class RestoreService
{
    private readonly BackupService _backups;
    private readonly IProcessMonitor _processMonitor;
    private readonly IFileOperations _files;
    private readonly StateStore _stateStore;
    private readonly LegacyRestoreSafetyPolicy _safetyPolicy;

    public RestoreService(
        BackupService backups,
        IProcessMonitor processMonitor,
        IFileOperations files,
        StateStore stateStore,
        LegacyRestoreSafetyPolicy safetyPolicy)
    {
        _backups = backups;
        _processMonitor = processMonitor;
        _files = files;
        _stateStore = stateStore;
        _safetyPolicy = safetyPolicy;
    }

    public OperationResult RestoreLatest(string expectedGamePath)
    {
        var latest = FindLatest(expectedGamePath);

        if (latest.Record is null)
        {
            return new()
            {
                OperationId = "restore",
                Success = false,
                Error = "没有与最后一次切换精确对应的可恢复备份。"
            };
        }

        return Restore(latest.Path, latest.Record, expectedGamePath);
    }

    public BackupRecord? FindLatestRecord(string expectedGamePath) => FindLatest(expectedGamePath).Record;

    public OperationResult Restore(string backupPath, BackupRecord record, string expectedGamePath)
    {
        var safety = _safetyPolicy.Evaluate(expectedGamePath, record);
        if (!safety.CanRestore)
        {
            return new()
            {
                OperationId = $"restore_{record.OperationId}",
                Success = false,
                Error = safety.Reason
            };
        }

        return new RestoreTransactionService(_backups, _stateStore, _processMonitor).Execute(backupPath, record, expectedGamePath);
    }

    private (string Path, BackupRecord? Record) FindLatest(string expectedGamePath)
    {
        string normalizedExpected;
        try
        {
            normalizedExpected = Path.GetFullPath(expectedGamePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (string.Empty, null);
        }

        var state = _stateStore.Load();
        if (state is null ||
            string.IsNullOrWhiteSpace(state.LastBackupPath) ||
            string.IsNullOrWhiteSpace(state.LastOperationId) ||
            string.IsNullOrWhiteSpace(state.CurrentProfile) ||
            string.IsNullOrWhiteSpace(state.GameVersion))
        {
            return (string.Empty, null);
        }

        try
        {
            if (!string.Equals(
                    Path.GetFullPath(state.GamePath ?? string.Empty),
                    normalizedExpected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return (string.Empty, null);
            }

            var backupPath = Path.GetFullPath(state.LastBackupPath);
            var record = _backups.LoadRecord(backupPath);
            var isExactPredecessor = string.Equals(record.OperationResult, "success", StringComparison.Ordinal) &&
                                     record.RestoredAt is null &&
                                     string.Equals(record.OperationId, state.LastOperationId, StringComparison.Ordinal) &&
                                     string.Equals(record.TargetProfile, state.CurrentProfile, StringComparison.Ordinal) &&
                                     string.Equals(record.GameVersion, state.GameVersion, StringComparison.Ordinal) &&
                                     string.Equals(
                                         Path.GetFullPath(record.GamePath),
                                         normalizedExpected,
                                         StringComparison.OrdinalIgnoreCase);
            return isExactPredecessor ? (backupPath, record) : (string.Empty, null);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or
                IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
        {
            return (string.Empty, null);
        }
    }
}
