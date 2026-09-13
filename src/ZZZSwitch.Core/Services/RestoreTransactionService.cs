using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

// Only manual restoration pays for staging and an undo copy. Normal switching
// keeps its existing transaction and I/O path.
internal sealed class RestoreTransactionService
{
    private readonly BackupService _backups;
    private readonly AppPaths _paths;
    private readonly IFileOperations _files;
    private readonly StateStore _state;
    private readonly IProcessMonitor _processes;
    private readonly FileTransactionJournalStore _journals;
    private readonly VerifiedFileTransfer _transfer;

    internal RestoreTransactionService(BackupService backups, StateStore state, IProcessMonitor processes)
    {
        _backups = backups; _paths = backups.Paths; _files = backups.Files;
        _state = state; _processes = processes; _journals = new(_paths); _transfer = new(_files);
    }

    internal static bool IsRestore(FileTransactionStage stage) => stage is
        FileTransactionStage.RestoreStaging or FileTransactionStage.RestorePrepared or
        FileTransactionStage.RestoreApplied or FileTransactionStage.RestoreRolledBack;

    internal OperationResult Execute(string backupPath, BackupRecord selected, string gamePath)
    {
        var id = "restore_" + Guid.NewGuid().ToString("N");
        FileTransactionJournal? journal = null;
        var prepared = false;
        var committed = false;
        try
        {
            if (_journals.Exists || File.Exists(_paths.HotUpdateJournalFile))
                throw new InvalidOperationException("有未完成事务，请先重试恢复。");
            EnsureStopped();
            var loaded = _state.LoadWithStatus();
            if (loaded.Warning is not null) throw new InvalidDataException(loaded.Warning);
            var record = _backups.LoadRecord(backupPath);
            if (record.OperationId != selected.OperationId || !Same(record.GamePath, gamePath))
                throw new InvalidDataException("备份记录已改变或属于其他安装，请刷新备份列表。");
            RecoveryGuard.EnsureVersion(gamePath, record.GameVersion);
            var affected = record.BackedUpFiles.Concat(record.OriginallyMissingFiles).ToArray();
            if (affected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != affected.Length)
                throw new InvalidDataException("备份中存在重复或冲突的恢复路径。");
            var stamps = PlanFileGuard.Capture(gamePath, affected);
            long restoreBytes = 0, undoBytes = 0;
            foreach (var relative in record.BackedUpFiles)
            {
                var source = _backups.GetRestoreSource(backupPath, record, relative);
                RecoveryGuard.EnsureOrdinaryPath(source);
                using var readable = _files.OpenRead(source);
                restoreBytes = checked(restoreBytes + _files.GetLength(source));
            }
            foreach (var relative in affected)
            {
                var target = PathSafety.ResolveOrThrow(gamePath, relative);
                RecoveryGuard.EnsureOrdinaryPath(target);
                if (!_files.FileExists(target)) continue;
                using var handle = _files.OpenExclusive(target);
                undoBytes = checked(undoBytes + _files.GetLength(target));
            }
            CheckSpace(gamePath, restoreBytes, _paths.BackupsRoot, undoBytes);
            var protection = Path.Combine(_paths.BackupsRoot, "." + id);
            var staging = GameStorageLayout.GetOperationStagingDirectory(gamePath, id);
            RecoveryGuard.EnsureOrdinaryPath(protection);
            RecoveryGuard.EnsureOrdinaryPath(staging);
            journal = new FileTransactionJournal
            {
                OperationId = id, CreatedAt = DateTimeOffset.Now, GamePath = Path.GetFullPath(gamePath),
                GameVersion = record.GameVersion, SourceProfile = record.TargetProfile, TargetProfile = record.SourceProfile,
                BackupPath = Path.GetFullPath(backupPath), RestoreRecordPath = Path.Combine(protection, "restore.json"),
                Stage = FileTransactionStage.RestoreStaging
            };
            _journals.Save(journal);
            var data = new RestoreData { OperationId = id, OriginalOperationId = record.OperationId };
            foreach (var relative in record.BackedUpFiles)
            {
                var source = _backups.GetRestoreSource(backupPath, record, relative);
                var target = PathSafety.ResolveOrThrow(staging, relative);
                _files.CreateDirectory(Path.GetDirectoryName(target)!);
                var copy = _transfer.CopyAndVerify(source, target, false,
                    record.LegacyContentLengths?.TryGetValue(relative, out var legacyLength) == true ? legacyLength : null,
                    record.LegacyContentObjects?.GetValueOrDefault(relative));
                data.RestoreFiles.Add(new() { Path = relative, Length = copy.BytesCopied, Sha256 = copy.Sha256 });
            }
            data.DeleteFiles.AddRange(record.OriginallyMissingFiles);
            foreach (var relative in affected)
            {
                var source = PathSafety.ResolveOrThrow(gamePath, relative);
                if (!_files.FileExists(source)) { data.UndoMissing.Add(relative); continue; }
                var target = PathSafety.ResolveOrThrow(Path.Combine(protection, "undo"), relative);
                _files.CreateDirectory(Path.GetDirectoryName(target)!);
                var copy = _transfer.CopyAndVerify(source, target, false);
                data.UndoFiles.Add(new() { Path = relative, Length = copy.BytesCopied, Sha256 = copy.Sha256 });
            }
            AtomicJsonFile.Write(journal.RestoreRecordPath, data);
            EnsureStopped();
            RecoveryGuard.EnsureVersion(gamePath, record.GameVersion);
            PlanFileGuard.Validate(gamePath, stamps);
            journal.Stage = FileTransactionStage.RestorePrepared;
            _journals.Save(journal);
            prepared = true;
            foreach (var item in data.RestoreFiles)
            {
                var target = PathSafety.ResolveOrThrow(gamePath, item.Path);
                _files.CreateDirectory(Path.GetDirectoryName(target)!);
                _files.MoveFile(PathSafety.ResolveOrThrow(staging, item.Path), target, true);
            }
            foreach (var relative in data.DeleteFiles)
                _files.DeleteFile(PathSafety.ResolveOrThrow(gamePath, relative));
            ValidateFiles(gamePath, data.RestoreFiles, data.DeleteFiles);
            journal.Stage = FileTransactionStage.RestoreApplied;
            _journals.Save(journal);
            _state.Save(new AppState
            {
                GamePath = journal.GamePath, GameVersion = record.GameVersion, CurrentProfile = record.SourceProfile,
                LastOperationId = id, LastSuccessfulSwitch = DateTimeOffset.Now, LastBackupPath = backupPath,
                LastReplaceCount = data.RestoreFiles.Count, LastDeleteCount = data.DeleteFiles.Count
            });
            committed = true;
            var completed = Complete(journal, data);
            return new() { OperationId = id, Success = true, RolledBack = true, BackupPath = backupPath,
                SuccessfulReplace = data.RestoreFiles.Count, SuccessfulDelete = data.DeleteFiles.Count,
                Error = completed ? null : "恢复已提交，收尾清理未完成；请在检查中重试恢复。" };
        }
        catch (Exception ex)
        {
            // A failed state write can have an uncertain outcome. Read it back
            // before deciding whether any committed restoration may be undone.
            var status = _state.LoadWithStatus();
            committed |= journal is not null && IsCommitted(status.State, journal);
            if (committed)
                return new() { OperationId = id, Success = true, BackupPath = backupPath,
                    Error = "恢复已提交，收尾待处理：" + ex.Message };
            var recovered = journal is null || (!prepared ? Cleanup(journal) :
                status.Warning is null && Recover(journal).Success);
            return new() { OperationId = id, Success = false, RolledBack = prepared && recovered,
                GameFilesUnchanged = !prepared, BackupPath = backupPath,
                Error = ex.Message + (recovered ? "" : "\n恢复未完成，已保留事务与副本，请重试恢复。") };
        }
    }

    internal PendingRecoveryResult Recover(FileTransactionJournal journal)
    {
        try
        {
            ValidateJournal(journal);
            EnsureStopped();
            if (File.Exists(_paths.HotUpdateJournalFile))
                throw new InvalidDataException("手动恢复与 Blocks 事务冲突，已保留两份记录。");
            if (journal.Stage is FileTransactionStage.RestoreStaging or FileTransactionStage.RestoreRolledBack)
                return Result(Cleanup(journal), "未修改游戏的恢复暂存已清理。", "恢复暂存清理失败，请重试。");
            var status = _state.LoadWithStatus();
            if (status.Warning is not null) throw new InvalidDataException(status.Warning);
            var data = ReadData(journal);
            if (IsCommitted(status.State, journal))
                return Result(Complete(journal, data), "已完成恢复收尾。", "恢复已提交，收尾仍需重试。");
            RecoveryGuard.EnsureVersion(journal.GamePath, journal.GameVersion);
            var undo = Path.Combine(Path.GetDirectoryName(journal.RestoreRecordPath)!, "undo");
            // Check every undo input before touching the current installation.
            ValidateFiles(undo, data.UndoFiles, []);
            foreach (var item in data.UndoFiles)
            {
                var target = PathSafety.ResolveOrThrow(journal.GamePath, item.Path);
                RecoveryGuard.EnsureOrdinaryPath(target);
                _files.CreateDirectory(Path.GetDirectoryName(target)!);
                _transfer.CopyAndVerify(PathSafety.ResolveOrThrow(undo, item.Path), target, true, item.Length, item.Sha256);
            }
            foreach (var relative in data.UndoMissing)
            {
                var target = PathSafety.ResolveOrThrow(journal.GamePath, relative);
                RecoveryGuard.EnsureOrdinaryPath(target);
                _files.DeleteFile(target);
            }
            ValidateFiles(journal.GamePath, data.UndoFiles, data.UndoMissing);
            journal.Stage = FileTransactionStage.RestoreRolledBack;
            _journals.Save(journal);
            return Result(Cleanup(journal), "已撤销中断的手动恢复，回到恢复前状态。", "文件已撤销，暂存清理待重试。");
        }
        catch (Exception ex)
        {
            return new() { Found = true, Success = false, Message = "手动恢复待处理：" + ex.Message };
        }
    }

    private bool Complete(FileTransactionJournal journal, RestoreData data)
    {
        var record = _backups.LoadRecord(journal.BackupPath);
        if (record.OperationId != data.OriginalOperationId) throw new InvalidDataException("原备份身份已改变。");
        record.RestoredAt ??= _state.Load()?.LastSuccessfulSwitch ?? DateTimeOffset.Now;
        record.RollbackResult = "manual_restore_success";
        _backups.SaveRecord(journal.BackupPath, record);
        // Mark cleanup-only durably, so partial cleanup needs no undo metadata.
        journal.Stage = FileTransactionStage.RestoreRolledBack;
        _journals.Save(journal);
        return Cleanup(journal);
    }

    private bool Cleanup(FileTransactionJournal journal)
    {
        try
        {
            ValidateJournal(journal);
            var staging = GameStorageLayout.GetOperationStagingDirectory(journal.GamePath, journal.OperationId);
            var protection = Path.GetDirectoryName(journal.RestoreRecordPath)!;
            foreach (var path in new[] { staging, protection })
            {
                RecoveryGuard.EnsureOrdinaryPath(path);
                if (!Directory.Exists(path)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                    RecoveryGuard.EnsureOrdinaryPath(entry);
                _files.DeleteDirectory(path, true);
            }
            return _journals.TryDelete();
        }
        catch { return false; }
    }

    private void ValidateJournal(FileTransactionJournal journal)
    {
        if (!IsRestore(journal.Stage) || !journal.OperationId.StartsWith("restore_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(journal.OperationId[8..], "N", out _) ||
            !Path.IsPathFullyQualified(journal.GamePath) || !Path.IsPathFullyQualified(journal.BackupPath) ||
            !ProfileIds.All.Contains(journal.SourceProfile) || !ProfileIds.All.Contains(journal.TargetProfile))
            throw new InvalidDataException("恢复事务身份无效。");
        var expected = Path.Combine(_paths.BackupsRoot, "." + journal.OperationId, "restore.json");
        if (journal.RestoreRecordPath is null || !Same(expected, journal.RestoreRecordPath))
            throw new InvalidDataException("恢复保护目录不匹配。");
        var prefix = Path.GetFullPath(_paths.BackupsRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(journal.BackupPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原备份目录越界。");
        RecoveryGuard.EnsureOrdinaryPath(expected);
        RecoveryGuard.EnsureOrdinaryPath(journal.GamePath);
    }

    private RestoreData ReadData(FileTransactionJournal journal)
    {
        using var stream = File.OpenRead(journal.RestoreRecordPath!);
        var data = JsonSerializer.Deserialize<RestoreData>(stream, JsonSupport.Options)
            ?? throw new InvalidDataException("恢复元数据为空。");
        if (data.OperationId != journal.OperationId || string.IsNullOrWhiteSpace(data.OriginalOperationId) ||
            data.RestoreFiles is null || data.UndoFiles is null || data.DeleteFiles is null || data.UndoMissing is null)
            throw new InvalidDataException("恢复元数据损坏。");
        foreach (var file in data.RestoreFiles.Concat(data.UndoFiles))
            if (file is null || file.Length < 0 || !FileIntegrityService.IsValidSha256(file.Sha256))
                throw new InvalidDataException("恢复文件校验记录损坏。");
        var targets = data.RestoreFiles.Select(x => x.Path).Concat(data.DeleteFiles).ToArray();
        var undo = data.UndoFiles.Select(x => x.Path).Concat(data.UndoMissing).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length ||
            undo.Distinct(StringComparer.OrdinalIgnoreCase).Count() != undo.Length ||
            !targets.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(undo))
            throw new InvalidDataException("恢复与撤销路径不一致。");
        foreach (var relative in targets) _ = PathSafety.ResolveOrThrow(journal.GamePath, relative);
        var original = _backups.LoadRecord(journal.BackupPath);
        if (original.OperationId != data.OriginalOperationId || original.GameVersion != journal.GameVersion ||
            original.SourceProfile != journal.TargetProfile || original.TargetProfile != journal.SourceProfile ||
            !Same(original.GamePath, journal.GamePath) ||
            !original.BackedUpFiles.Concat(original.OriginallyMissingFiles).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(targets))
            throw new InvalidDataException("原备份与恢复事务身份或文件集合不一致。");
        return data;
    }

    private void ValidateFiles(string root, IEnumerable<FileSignature> files, IEnumerable<string> missing)
    {
        var integrity = new FileIntegrityService(_files);
        foreach (var file in files)
        {
            var path = PathSafety.ResolveOrThrow(root, file.Path);
            RecoveryGuard.EnsureOrdinaryPath(path);
            var result = integrity.Validate(path, file.Length, file.Sha256);
            if (!result.IsValid) throw new InvalidDataException($"恢复文件校验失败：{file.Path}；{result.Message}");
        }
        foreach (var relative in missing)
            if (_files.FileExists(PathSafety.ResolveOrThrow(root, relative)))
                throw new IOException("应删除的文件仍存在：" + relative);
    }

    private void EnsureStopped()
    {
        var running = _processes.FindRelatedProcesses();
        if (running.Count > 0) throw new InvalidOperationException("请关闭游戏与启动器后重试：" + string.Join("、", running));
    }

    private static void CheckSpace(string game, long staged, string backups, long undo)
    {
        var drives = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in new[] { (game, staged), (backups, undo) })
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(path))!;
            drives[drive] = checked(drives.GetValueOrDefault(drive) + bytes);
        }
        foreach (var (drive, bytes) in drives)
            if (new DriveInfo(drive).AvailableFreeSpace < checked(bytes + 16L * 1024 * 1024))
                throw new IOException($"恢复空间不足：{drive} 需要 {ByteSizeFormatter.Format(bytes)}，另需少量记录空间。");
    }

    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool IsCommitted(AppState? state, FileTransactionJournal journal) => state?.LastOperationId == journal.OperationId &&
        state.CurrentProfile == journal.TargetProfile && state.GameVersion == journal.GameVersion &&
        state.GamePath is not null && Same(state.GamePath, journal.GamePath) &&
        state.LastBackupPath is not null && Same(state.LastBackupPath, journal.BackupPath);
    private static PendingRecoveryResult Result(bool ok, string success, string error) => new() { Found = true, Success = ok, Message = ok ? success : error };

    private sealed class RestoreData
    {
        public string OperationId { get; init; } = "";
        public string OriginalOperationId { get; init; } = "";
        public List<FileSignature> RestoreFiles { get; init; } = [];
        public List<string> DeleteFiles { get; init; } = [];
        public List<FileSignature> UndoFiles { get; init; } = [];
        public List<string> UndoMissing { get; init; } = [];
    }
}
