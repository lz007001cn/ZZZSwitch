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

        var blockers = _processMonitor.FindRelatedProcesses().Where(x =>
            x.StartsWith("ZenlessZoneZero", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("HYUpdater", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("PCGamePlatform", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("game_security_protection", StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith("ZZZSwitch", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (blockers.Length > 0)
        {
            return new()
            {
                OperationId = $"restore_{record.OperationId}",
                Success = false,
                Error = $"以下进程阻止恢复：{string.Join("、", blockers)}"
            };
        }

        foreach (var relative in record.BackedUpFiles.Concat(record.OriginallyMissingFiles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = PathSafety.ResolveOrThrow(record.GamePath, relative);
            if (!_files.FileExists(target))
            {
                continue;
            }

            try
            {
                using var handle = _files.OpenExclusive(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new()
                {
                    OperationId = $"restore_{record.OperationId}",
                    Success = false,
                    Error = $"文件被占用，无法恢复：{target}"
                };
            }
        }

        var success = _backups.Rollback(backupPath, record, out var detail);
        if (success)
        {
            record.RestoredAt = DateTimeOffset.Now;
            record.RollbackResult = "manual_restore_success";
            _backups.SaveRecord(backupPath, record);
            _stateStore.Save(new AppState
            {
                GamePath = record.GamePath,
                GameVersion = record.GameVersion,
                CurrentProfile = record.SourceProfile,
                LastSuccessfulSwitch = record.RestoredAt,
                LastOperationId = $"restore_{record.OperationId}",
                LastReplaceCount = record.BackedUpFiles.Count,
                LastDeleteCount = record.OriginallyMissingFiles.Count,
                LastBackupPath = backupPath
            });
        }

        return new()
        {
            OperationId = $"restore_{record.OperationId}",
            Success = success,
            RolledBack = success,
            SuccessfulReplace = success ? record.BackedUpFiles.Count : 0,
            SuccessfulDelete = success ? record.OriginallyMissingFiles.Count : 0,
            BackupPath = backupPath,
            Error = success ? null : detail
        };
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
                IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (string.Empty, null);
        }
    }
}
