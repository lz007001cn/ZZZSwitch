using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed partial class PendingTransactionRecoveryService
{
    private readonly AppPaths _paths;
    private readonly StateStore _stateStore;
    private readonly BackupService _backups;
    private readonly HotUpdateCacheService _hotUpdateCaches;
    private readonly FileTransactionJournalStore _fileTransactions;
    private readonly IProcessMonitor _processMonitor;

    public PendingTransactionRecoveryService(
        AppPaths paths,
        StateStore stateStore,
        BackupService backups,
        HotUpdateCacheService hotUpdateCaches,
        FileTransactionJournalStore fileTransactions,
        IProcessMonitor processMonitor)
    {
        _paths = paths;
        _stateStore = stateStore;
        _backups = backups;
        _hotUpdateCaches = hotUpdateCaches;
        _fileTransactions = fileTransactions;
        _processMonitor = processMonitor;
    }

    public PendingRecoveryResult RecoverPending()
    {
        var state = _stateStore.LoadWithStatus().State;
        if (!_fileTransactions.Exists)
        {
            try
            {
                var blocksRecovery = _hotUpdateCaches.RecoverPending(state?.CurrentProfile);
                return blocksRecovery is null
                    ? new() { Found = false, Success = true, Message = "没有待恢复事务。" }
                    : new() { Found = true, Success = true, Message = blocksRecovery };
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                return new()
                {
                    Found = true,
                    Success = false,
                    Message = $"Blocks 事务自动恢复失败：{ex.Message}"
                };
            }
        }

        FileTransactionJournal journal;
        try
        {
            journal = _fileTransactions.Load()
                      ?? throw new InvalidDataException("普通文件事务日志内容为空。");
            ValidateJournal(journal);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return new()
            {
                Found = true,
                Success = false,
                Message = $"普通文件事务日志损坏或不可信，已停止自动恢复：{ex.Message}"
            };
        }

        var running = _processMonitor.FindRelatedProcesses();
        if (running.Count > 0)
        {
            return new()
            {
                Found = true,
                Success = false,
                Message = $"检测到未完成事务。请完全退出游戏和 HoYoPlay 后重新打开 ZZZSwitch：{string.Join("、", running)}"
            };
        }

        if (IsCommitted(state, journal))
        {
            try
            {
                var blocksResult = _hotUpdateCaches.RecoverPending(journal.TargetProfile);
                if (!_fileTransactions.TryDelete())
                {
                    throw new IOException("无法清理已完成的普通文件事务日志。");
                }

                return new()
                {
                    Found = true,
                    Success = true,
                    Message = string.IsNullOrWhiteSpace(blocksResult)
                        ? "已清理上次成功切换遗留的普通文件事务记录。"
                        : $"{blocksResult}{Environment.NewLine}已清理普通文件事务记录。"
                };
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                return new()
                {
                    Found = true,
                    Success = false,
                    Message = $"已提交事务的收尾清理失败：{ex.Message}"
                };
            }
        }

        var details = new List<string>();
        var blocksRecovered = false;
        try
        {
            // The ordinary-file journal proves that this exact operation was not
            // committed. Use its source profile instead of a possibly stale state
            // profile so the paired Blocks transaction is always rolled back.
            var blocksResult = _hotUpdateCaches.RecoverPending(journal.SourceProfile);
            blocksRecovered = true;
            details.Add(blocksResult ?? "没有需要撤销的 Blocks 目录交换。");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            details.Add($"Blocks 恢复失败：{ex.Message}");
        }

        BackupRecord? record = null;
        var filesRecovered = false;
        try
        {
            record = _backups.LoadRecord(journal.BackupPath);
            ValidateRecord(journal, record);
            filesRecovered = _backups.Rollback(journal.BackupPath, record, out var rollbackDetail);
            details.Add(filesRecovered
                ? "普通文件已从事务备份完整恢复。"
                : $"普通文件恢复未完成：{rollbackDetail}");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            details.Add($"普通文件恢复失败：{ex.Message}");
        }

        var recovered = blocksRecovered && filesRecovered;
        if (record is not null)
        {
            record.OperationResult = "interrupted";
            record.RollbackResult = recovered ? "startup_recovery_success" : "startup_recovery_incomplete";
            try
            {
                _backups.SaveRecord(journal.BackupPath, record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                recovered = false;
                details.Add($"备份记录更新失败：{ex.Message}");
            }
        }

        if (recovered && !_fileTransactions.TryDelete())
        {
            recovered = false;
            details.Add("恢复完成，但普通文件事务日志无法清理。下次启动将再次核对。");
        }

        return new()
        {
            Found = true,
            Success = recovered,
            Message = string.Join(Environment.NewLine, details)
        };
    }

    private void ValidateJournal(FileTransactionJournal journal)
    {
        if (string.IsNullOrWhiteSpace(journal.OperationId) ||
            journal.OperationId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !ProfileIds.All.Contains(journal.SourceProfile, StringComparer.Ordinal) ||
            !ProfileIds.All.Contains(journal.TargetProfile, StringComparer.Ordinal) ||
            string.Equals(journal.SourceProfile, journal.TargetProfile, StringComparison.Ordinal) ||
            !GameVersionRegex().IsMatch(journal.GameVersion) ||
            !Enum.IsDefined(journal.Stage))
        {
            throw new InvalidDataException("事务身份字段无效。");
        }

        if (!Path.IsPathFullyQualified(journal.GamePath) ||
            !Path.IsPathFullyQualified(journal.BackupPath))
        {
            throw new InvalidDataException("事务路径必须是完整绝对路径。");
        }

        var backupsRoot = Path.GetFullPath(_paths.BackupsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var backupPath = Path.GetFullPath(journal.BackupPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!backupPath.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(backupPath, backupsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("事务备份路径不在应用备份目录内。");
        }

        _ = Path.GetFullPath(journal.GamePath);
    }

    private static void ValidateRecord(FileTransactionJournal journal, BackupRecord record)
    {
        if (!string.Equals(record.OperationId, journal.OperationId, StringComparison.Ordinal) ||
            !string.Equals(record.SourceProfile, journal.SourceProfile, StringComparison.Ordinal) ||
            !string.Equals(record.TargetProfile, journal.TargetProfile, StringComparison.Ordinal) ||
            !string.Equals(record.GameVersion, journal.GameVersion, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(record.GamePath), Path.GetFullPath(journal.GamePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("事务日志与备份记录身份不一致。");
        }
    }

    private static bool IsCommitted(AppState? state, FileTransactionJournal journal) =>
        state is not null &&
        !string.IsNullOrWhiteSpace(state.GamePath) &&
        string.Equals(state.LastOperationId, journal.OperationId, StringComparison.Ordinal) &&
        string.Equals(state.CurrentProfile, journal.TargetProfile, StringComparison.Ordinal) &&
        string.Equals(state.GameVersion, journal.GameVersion, StringComparison.Ordinal) &&
        string.Equals(Path.GetFullPath(state.GamePath ?? string.Empty), Path.GetFullPath(journal.GamePath), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex GameVersionRegex();
}
