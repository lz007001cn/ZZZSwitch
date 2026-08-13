using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class SwitchEngine
{
    private readonly IFileOperations _files;
    private readonly AppPaths _paths;
    private readonly BackupService _backups;
    private readonly StateStore _stateStore;
    private readonly OperationLogger _logger;
    private readonly ProfileSnapshotService _snapshots;
    private readonly HotUpdateCacheService? _hotUpdateCaches;
    private readonly FileTransactionJournalStore _fileTransactions;
    private readonly FileIntegrityService _integrity;
    private readonly IniFileEditor _iniFiles = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public SwitchEngine(
        IFileOperations files,
        AppPaths paths,
        BackupService backups,
        StateStore stateStore,
        OperationLogger logger,
        ProfileSnapshotService snapshots,
        HotUpdateCacheService? hotUpdateCaches = null,
        FileTransactionJournalStore? fileTransactions = null)
    {
        _files = files;
        _paths = paths;
        _backups = backups;
        _stateStore = stateStore;
        _logger = logger;
        _snapshots = snapshots;
        _hotUpdateCaches = hotUpdateCaches;
        _fileTransactions = fileTransactions ?? new FileTransactionJournalStore(paths);
        _integrity = new FileIntegrityService(files);
    }

    public async Task<OperationResult> ExecuteAsync(
        SwitchPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Execute(plan, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private OperationResult Execute(SwitchPlan plan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.Equals(plan.Manifest.SourceProfile, plan.Manifest.TargetProfile, StringComparison.OrdinalIgnoreCase))
        {
            return new()
            {
                OperationId = plan.OperationId,
                Success = true,
                WasNoOp = true,
                PlannedReplace = 0,
                PlannedDelete = 0
            };
        }

        if (!plan.CanExecute)
        {
            return Failure(plan, false, 0, 0, 0, 0, 0, 0, string.Join(Environment.NewLine, plan.Issues.Where(x => x.Severity == IssueSeverity.Error).Select(x => x.Message)));
        }

        if (_fileTransactions.Exists)
        {
            return Failure(
                plan,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                "检测到未完成的普通文件事务。请重新启动 ZZZSwitch 完成自动恢复。");
        }

        BackupRecord? record = null;
        var successfulReplace = 0;
        var failedReplace = 0;
        var successfulDelete = 0;
        var failedDelete = 0;
        var successfulCacheRestore = 0;
        var failedCacheRestore = 0;
        var failedFiles = new List<string>();
        var stagingRoot = Path.Combine(_paths.TempRoot, plan.OperationId);
        HotUpdateTransaction? hotUpdateTransaction = null;
        FileTransactionJournal? fileTransaction = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report("正在保存来源服 version/revision 缓存快照", false);
            var sourceSnapshot = _snapshots.Capture(
                ProfileIds.ToResourceProfile(plan.Manifest.SourceProfile),
                plan.Manifest.GameVersion,
                plan.GamePath);

            Report("正在备份受影响文件", false);
            record = _backups.CreateBackup(plan, plan.TargetSnapshot?.Files.Select(x => x.RelativePath));
            record.SourceSnapshotPath = sourceSnapshot.SnapshotPath;
            record.TargetSnapshotPath = plan.TargetSnapshot?.SnapshotPath;
            record.CacheRestoreCount = plan.TargetSnapshot?.Files.Count ?? 0;
            _backups.SaveRecord(plan.BackupPath, record);
            fileTransaction = new FileTransactionJournal
            {
                OperationId = plan.OperationId,
                CreatedAt = DateTimeOffset.Now,
                BackupPath = plan.BackupPath,
                GamePath = plan.GamePath,
                GameVersion = plan.Manifest.GameVersion,
                SourceProfile = plan.Manifest.SourceProfile,
                TargetProfile = plan.Manifest.TargetProfile,
                Stage = FileTransactionStage.Prepared
            };
            _fileTransactions.Save(fileTransaction);

            Report("正在复制差异文件到应用临时目录", false);
            _files.CreateDirectory(stagingRoot);
            foreach (var entry in plan.Manifest.ReplaceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = PackageFileResolver.ResolveOrThrow(
                    plan.PackageRoot,
                    plan.PackageDirectory,
                    entry);
                var staged = PathSafety.ResolveOrThrow(stagingRoot, entry.Target);
                var parent = Path.GetDirectoryName(staged);
                if (parent is not null)
                {
                    _files.CreateDirectory(parent);
                }

                _files.CopyFile(source, staged, true);
                if (entry.Length.HasValue || !string.IsNullOrWhiteSpace(entry.Sha256))
                {
                    var integrity = _integrity.Validate(staged, entry.Length, entry.Sha256);
                    if (!integrity.IsValid)
                    {
                        throw new IOException($"临时复制完整性校验失败：{entry.Source}；{integrity.Message}");
                    }
                }
                else if (!_files.FileExists(staged) || _files.GetLength(staged) != _files.GetLength(source))
                {
                    throw new IOException($"临时复制校验失败：{entry.Source}");
                }
            }

            if (plan.HotUpdateTransition is not null)
            {
                if (_hotUpdateCaches is null)
                {
                    throw new InvalidOperationException("切换计划需要热更新缓存服务，但服务未初始化。");
                }

                Report(
                    plan.HotUpdateTransition.Mode == HotUpdateTransitionMode.Swap
                        ? "正在交换国服/国际服 Blocks 缓存"
                        : "正在保存来源服 Blocks，并准备目标服首次初始化",
                    false);
                hotUpdateTransaction = _hotUpdateCaches.BeginTransition(plan.HotUpdateTransition);
                fileTransaction!.Stage = FileTransactionStage.BlocksTransitioned;
                _fileTransactions.Save(fileTransaction);
            }

            Report("正在替换目标文件", false);
            foreach (var entry in plan.Manifest.ReplaceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var staged = PathSafety.ResolveOrThrow(stagingRoot, entry.Target);
                    var target = PathSafety.ResolveOrThrow(plan.GamePath, entry.Target);
                    var parent = Path.GetDirectoryName(target);
                    if (parent is not null)
                    {
                        _files.CreateDirectory(parent);
                    }

                    _files.CopyFile(staged, target, true);
                    if (!_files.FileExists(target) || _files.GetLength(target) != _files.GetLength(staged))
                    {
                        throw new IOException("替换后文件校验失败。");
                    }

                    successfulReplace++;
                    Report($"已替换 {entry.Target}", false);
                }
                catch
                {
                    failedReplace++;
                    failedFiles.Add(entry.Target);
                    throw;
                }
            }

            foreach (var patch in plan.Manifest.IniPatches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var target = PathSafety.ResolveOrThrow(plan.GamePath, patch.Target);
                    _iniFiles.Apply(target, patch);
                    if (!_iniFiles.Matches(target, patch))
                    {
                        throw new IOException("INI 修改后的复核未通过。");
                    }

                    successfulReplace++;
                    Report($"已更新 {patch.Target}", false);
                }
                catch
                {
                    failedReplace++;
                    failedFiles.Add(patch.Target);
                    throw;
                }
            }

            Report("正在删除清单指定文件", false);
            foreach (var entry in plan.Manifest.DeleteFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var target = PathSafety.ResolveOrThrow(plan.GamePath, entry.Target);
                    if (!_files.FileExists(target))
                    {
                        throw new IOException("必需删除的文件在执行删除前已不存在。");
                    }

                    _files.DeleteFile(target);
                    if (_files.FileExists(target))
                    {
                        throw new IOException("删除后文件仍然存在。");
                    }

                    successfulDelete++;
                    Report($"已删除 {entry.Target}", false);
                }
                catch
                {
                    failedDelete++;
                    failedFiles.Add(entry.Target);
                    throw;
                }
            }

            foreach (var entry in plan.Manifest.OptionalDeleteFiles)
            {
                var target = PathSafety.ResolveOrThrow(plan.GamePath, entry.Target);
                if (_files.FileExists(target))
                {
                    _files.DeleteFile(target);
                    if (_files.FileExists(target))
                    {
                        throw new IOException($"可选删除失败：{entry.Target}");
                    }
                }
            }

            fileTransaction!.Stage = FileTransactionStage.FilesApplied;
            _fileTransactions.Save(fileTransaction);

            if (plan.TargetSnapshot is not null)
            {
                Report("正在恢复目标服 version/revision 缓存快照", false);
                try
                {
                    successfulCacheRestore = _snapshots.Restore(plan.TargetSnapshot, plan.GamePath);
                }
                catch
                {
                    failedCacheRestore = Math.Max(1, plan.TargetSnapshot.Files.Count - successfulCacheRestore);
                    failedFiles.Add("目标服缓存快照");
                    throw;
                }
            }

            fileTransaction.Stage = FileTransactionStage.MetadataRestored;
            _fileTransactions.Save(fileTransaction);

            Report("正在执行最终数量与文件状态校验", false);
            if (successfulReplace != plan.Manifest.ExpectedReplaceCount || failedReplace != 0 ||
                successfulDelete != plan.Manifest.ExpectedDeleteCount || failedDelete != 0 ||
                successfulCacheRestore != (plan.TargetSnapshot?.Files.Count ?? 0) || failedCacheRestore != 0)
            {
                throw new InvalidOperationException("实际成功数量与清单声明数量不一致。");
            }

            // A version/revision snapshot is restored after the package files are applied.
            // When both operations target the same path, the snapshot is the intentional
            // final owner of that file, so final verification must use its metadata rather
            // than the package's pre-hot-update hash.
            var restoredSnapshotTargets = new Dictionary<string, SnapshotFileRecord>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var snapshotFile in plan.TargetSnapshot?.Files ?? [])
            {
                var snapshotTarget = PathSafety.ResolveOrThrow(plan.GamePath, snapshotFile.RelativePath);
                restoredSnapshotTargets[snapshotTarget] = snapshotFile;
            }

            foreach (var entry in plan.Manifest.ReplaceFiles)
            {
                var target = PathSafety.ResolveOrThrow(plan.GamePath, entry.Target);
                var expectedLength = entry.Length;
                var expectedSha256 = entry.Sha256;
                if (restoredSnapshotTargets.TryGetValue(target, out var restoredSnapshot))
                {
                    expectedLength = restoredSnapshot.Length;
                    expectedSha256 = restoredSnapshot.Sha256;
                }

                if (expectedLength.HasValue || !string.IsNullOrWhiteSpace(expectedSha256))
                {
                    var integrity = _integrity.Validate(target, expectedLength, expectedSha256);
                    if (!integrity.IsValid)
                    {
                        throw new IOException($"最终完整性校验失败：{entry.Target}；{integrity.Message}");
                    }
                }
                else if (!_files.FileExists(target))
                {
                    throw new IOException($"最终校验缺少替换目标：{entry.Target}");
                }
            }

            foreach (var patch in plan.Manifest.IniPatches)
            {
                var target = PathSafety.ResolveOrThrow(plan.GamePath, patch.Target);
                if (!_iniFiles.Matches(target, patch))
                {
                    throw new IOException($"最终校验发现 INI 配置不符：{patch.Target}");
                }
            }

            foreach (var entry in plan.Manifest.DeleteFiles)
            {
                if (_files.FileExists(PathSafety.ResolveOrThrow(plan.GamePath, entry.Target)))
                {
                    throw new IOException($"最终校验发现删除目标仍存在：{entry.Target}");
                }
            }

            record.OperationResult = "success";
            record.RollbackResult = "not_required";
            _backups.SaveRecord(plan.BackupPath, record);

            // Persist the audit log before committing the new state. If logging fails,
            // the operation is rolled back and the target profile is never committed.
            WriteLog(null, "not_required");

            // The state is deliberately the final throwing commit step. No earlier step writes it.
            _stateStore.Save(new AppState
            {
                GamePath = plan.GamePath,
                GameVersion = plan.Manifest.GameVersion,
                CurrentProfile = plan.Manifest.TargetProfile,
                LastSuccessfulSwitch = DateTimeOffset.Now,
                LastOperationId = plan.OperationId,
                LastReplaceCount = successfulReplace,
                LastDeleteCount = successfulDelete,
                LastBackupPath = plan.BackupPath
            });
            if (hotUpdateTransaction is not null)
            {
                _hotUpdateCaches!.Commit(hotUpdateTransaction);
            }

            _fileTransactions.TryDelete();

            // Each game installation keeps one restorable backup for each source
            // profile. A newer switch from the same source replaces that slot only
            // after the new state has been fully committed.
            try
            {
                _backups.PruneRedundantBackups(plan.BackupPath, plan.GamePath);
            }
            catch
            {
                // Retention cleanup is maintenance, not part of the committed switch.
            }

            Report("切换成功", false);
            return new()
            {
                OperationId = plan.OperationId,
                Success = true,
                PlannedReplace = plan.Manifest.ExpectedReplaceCount,
                SuccessfulReplace = successfulReplace,
                FailedReplace = failedReplace,
                PlannedDelete = plan.Manifest.ExpectedDeleteCount,
                SuccessfulDelete = successfulDelete,
                FailedDelete = failedDelete,
                PlannedCacheRestore = plan.TargetSnapshot?.Files.Count ?? 0,
                SuccessfulCacheRestore = successfulCacheRestore,
                FailedCacheRestore = failedCacheRestore,
                BackupPath = plan.BackupPath
            };
        }
        catch (Exception ex)
        {
            var rolledBack = false;
            var rollbackDetail = "未创建完整备份，未执行回滚。";
            var hotUpdateRolledBack = hotUpdateTransaction is null;
            if (hotUpdateTransaction is not null && _hotUpdateCaches is not null)
            {
                hotUpdateRolledBack = _hotUpdateCaches.Rollback(hotUpdateTransaction);
            }

            if (record is not null)
            {
                Report("操作失败，正在回滚", true);
                rolledBack = _backups.Rollback(plan.BackupPath, record, out rollbackDetail);
                rolledBack = rolledBack && hotUpdateRolledBack;
                if (!hotUpdateRolledBack)
                {
                    rollbackDetail += $"{Environment.NewLine}Blocks 缓存回滚未完成。";
                }
                record.OperationResult = "failed";
                record.RollbackResult = rolledBack ? "success" : $"failed: {rollbackDetail}";
                try
                {
                    _backups.SaveRecord(plan.BackupPath, record);
                }
                catch (Exception saveError)
                {
                    rollbackDetail += $"{Environment.NewLine}备份记录更新失败：{saveError.Message}";
                }
            }

            // Backup creation happens before any game or Blocks mutation. If it did
            // not produce a usable record, its partial directory is inert and should
            // not accumulate large duplicate files forever.
            if (record is null)
            {
                _backups.TryDeleteBackup(plan.BackupPath);
            }

            if (record is not null && rolledBack)
            {
                _fileTransactions.TryDelete();
                _backups.TryDeleteCompletedRollback(plan.BackupPath);
            }

            try
            {
                WriteLog(ex.Message, rollbackDetail);
            }
            catch
            {
                // The original operation and rollback result remain the primary outcome.
            }
            return Failure(plan, rolledBack, successfulReplace, failedReplace, successfulDelete, failedDelete, successfulCacheRestore, failedCacheRestore, ex.Message);
        }
        finally
        {
            TryDeleteStaging(stagingRoot);
        }

        void Report(string step, bool rollingBack)
        {
            try
            {
                progress?.Report(new OperationProgress
                {
                    Step = step,
                    PlannedReplace = plan.Manifest.ExpectedReplaceCount,
                    SuccessfulReplace = successfulReplace,
                    FailedReplace = failedReplace,
                    PlannedDelete = plan.Manifest.ExpectedDeleteCount,
                    SuccessfulDelete = successfulDelete,
                    FailedDelete = failedDelete,
                    PlannedCacheRestore = plan.TargetSnapshot?.Files.Count ?? 0,
                    SuccessfulCacheRestore = successfulCacheRestore,
                    FailedCacheRestore = failedCacheRestore,
                    IsRollingBack = rollingBack
                });
            }
            catch
            {
                // UI progress reporting must never influence the file transaction.
            }
        }

        void WriteLog(string? error, string rollback) => _logger.Write(new OperationLogEntry
        {
            Time = DateTimeOffset.Now,
            OperationId = plan.OperationId,
            GamePath = plan.GamePath,
            GameVersion = plan.Manifest.GameVersion,
            SourceProfile = plan.Manifest.SourceProfile,
            TargetProfile = plan.Manifest.TargetProfile,
            PlannedReplace = plan.Manifest.ExpectedReplaceCount,
            SuccessfulReplace = successfulReplace,
            PlannedDelete = plan.Manifest.ExpectedDeleteCount,
            SuccessfulDelete = successfulDelete,
            PlannedCacheRestore = plan.TargetSnapshot?.Files.Count ?? 0,
            SuccessfulCacheRestore = successfulCacheRestore,
            FailedFiles = failedFiles,
            RollbackResult = rollback,
            Error = error
        });
    }

    private static OperationResult Failure(
        SwitchPlan plan,
        bool rolledBack,
        int successfulReplace,
        int failedReplace,
        int successfulDelete,
        int failedDelete,
        int successfulCacheRestore,
        int failedCacheRestore,
        string error) => new()
        {
            OperationId = plan.OperationId,
            Success = false,
            RolledBack = rolledBack,
            PlannedReplace = plan.Manifest.ExpectedReplaceCount,
            SuccessfulReplace = successfulReplace,
            FailedReplace = failedReplace,
            PlannedDelete = plan.Manifest.ExpectedDeleteCount,
            SuccessfulDelete = successfulDelete,
            FailedDelete = failedDelete,
            PlannedCacheRestore = plan.TargetSnapshot?.Files.Count ?? 0,
            SuccessfulCacheRestore = successfulCacheRestore,
            FailedCacheRestore = failedCacheRestore,
            BackupPath = Directory.Exists(plan.BackupPath) ? plan.BackupPath : null,
            Error = error
        };

    private void TryDeleteStaging(string stagingRoot)
    {
        try
        {
            if (Directory.Exists(stagingRoot))
            {
                var root = Path.GetFullPath(_paths.TempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
                {
                    _files.DeleteDirectory(stagingRoot, true);
                }
            }
        }
        catch
        {
            // A stale app-private staging directory is harmless and can be removed later.
        }
    }
}
