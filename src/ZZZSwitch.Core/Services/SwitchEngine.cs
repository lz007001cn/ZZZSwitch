using System.Diagnostics;
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
    private readonly VerifiedFileTransfer _transfers;
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
        _transfers = new VerifiedFileTransfer(files);
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
                GameFilesUnchanged = true,
                PlannedReplace = 0,
                PlannedDelete = 0
            };
        }

        if (!plan.CanExecute)
        {
            return Failure(plan, false, 0, 0, 0, 0, 0, 0,
                string.Join(Environment.NewLine, plan.Issues.Where(x => x.Severity == IssueSeverity.Error).Select(x => x.Message)),
                gameFilesUnchanged: !_fileTransactions.Exists && !File.Exists(_paths.HotUpdateJournalFile));
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
        var stagingRoot = GameStorageLayout.GetOperationStagingDirectory(plan.GamePath, plan.OperationId);
        HotUpdateTransaction? hotUpdateTransaction = null;
        FileTransactionJournal? fileTransaction = null;
        var changesMayHaveStarted = false;
        var stageDurations = new Dictionary<string, long>(StringComparer.Ordinal);
        var totalTimer = Stopwatch.StartNew();
        var stagedBytes = 0L;
        var backupBytes = 0L;
        IReadOnlyList<ReplaceFileEntry> replaceFiles = plan.Manifest.ReplaceFiles;
        IReadOnlyList<IniFilePatch> iniPatches = plan.Manifest.IniPatches;
        IReadOnlyList<DeleteFileEntry> optionalDeleteFiles = plan.Manifest.OptionalDeleteFiles;
        var plannedReplace = plan.Manifest.PlannedReplaceCount;
        var plannedDelete = plan.Manifest.PlannedDeleteCount;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePlanSource(plan);
            var sourceWasConfirmed = ProfileDetector.HasConfirmedSource(_paths, plan, _stateStore.Load());
            Report("正在检测实际文件变更", false, indeterminate: true);
            MeasureAction("detectChanges", () =>
            {
                replaceFiles = plan.Manifest.ReplaceFiles
                    .Where(entry => TargetNeedsReplacement(plan.GamePath, entry))
                    .ToArray();
                iniPatches = plan.Manifest.IniPatches
                    .Where(patch => !_iniFiles.Matches(
                        PathSafety.ResolveOrThrow(plan.GamePath, patch.Target),
                        patch))
                    .ToArray();
                optionalDeleteFiles = plan.Manifest.OptionalDeleteFiles
                    .Where(entry => _files.FileExists(
                        PathSafety.ResolveOrThrow(plan.GamePath, entry.Target)))
                    .ToArray();
                plannedReplace = replaceFiles.Count + iniPatches.Count;
            });

            Report("正在校验并准备实际差异文件", false, indeterminate: true);
            MeasureAction("stageSources", () =>
            {
                EnsureSafeStagingRoot(plan.GamePath);
                // Persist ownership before writing payloads; no game files or Blocks
                // may change until the verified backup advances this to Prepared.
                fileTransaction = new FileTransactionJournal
                {
                    OperationId = plan.OperationId,
                    CreatedAt = DateTimeOffset.Now,
                    BackupPath = plan.BackupPath,
                    GamePath = plan.GamePath,
                    GameVersion = plan.Manifest.GameVersion,
                    SourceProfile = plan.Manifest.SourceProfile,
                    TargetProfile = plan.Manifest.TargetProfile,
                    Stage = FileTransactionStage.Staging
                };
                _fileTransactions.Save(fileTransaction);
                if (replaceFiles.Count > 0)
                {
                    _files.CreateDirectory(stagingRoot);
                }

                foreach (var entry in replaceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = ResolveSource(plan, entry);
                    var staged = PathSafety.ResolveOrThrow(stagingRoot, entry.Target);
                    var parent = Path.GetDirectoryName(staged);
                    if (parent is not null)
                    {
                        _files.CreateDirectory(parent);
                    }

                    var copy = _transfers.CopyAndVerify(
                        source,
                        staged,
                        overwrite: true,
                        entry.Length,
                        entry.Sha256);
                    stagedBytes = checked(stagedBytes + copy.BytesCopied);
                }
            });

            Report("正在保存来源服 version/revision 缓存快照", false, indeterminate: true);
            var sourceSnapshot = Measure("captureSnapshot", () => _snapshots.Capture(
                ProfileIds.ToResourceProfile(plan.Manifest.SourceProfile),
                plan.Manifest.GameVersion,
                plan.GamePath));

            var affectedFiles = replaceFiles.Select(x => x.Target)
                .Concat(iniPatches.Select(x => x.Target))
                .Concat(plan.Manifest.DeleteFiles.Select(x => x.Target))
                .Concat(optionalDeleteFiles.Select(x => x.Target))
                .Concat(plan.TargetSnapshot?.Files.Select(x => x.RelativePath) ?? []);
            Report("正在备份实际受影响文件", false, indeterminate: true);
            record = Measure("createBackup", () =>
                _backups.CreateBackupForAffectedFiles(plan, affectedFiles));
            backupBytes = record.BackedUpFiles.Sum(relative =>
                BackupFileLength(plan.BackupPath, relative));
            record.ReplaceCount = plannedReplace;
            record.DeleteCount = plannedDelete;
            record.SourceSnapshotPath = sourceSnapshot.SnapshotPath;
            record.TargetSnapshotPath = plan.TargetSnapshot?.SnapshotPath;
            record.CacheRestoreCount = plan.TargetSnapshot?.Files.Count ?? 0;
            _backups.SaveRecord(plan.BackupPath, record);
            ValidatePlanSource(plan);
            fileTransaction!.Stage = FileTransactionStage.Prepared;
            _fileTransactions.Save(fileTransaction);
            changesMayHaveStarted = true;

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
                    false, indeterminate: true);
                hotUpdateTransaction = Measure(
                    "transitionBlocks",
                    () => _hotUpdateCaches.BeginTransition(plan.HotUpdateTransition));
                fileTransaction!.Stage = FileTransactionStage.BlocksTransitioned;
                _fileTransactions.Save(fileTransaction);
            }

            MeasureAction("applyFiles", () =>
            {
                Report("正在替换目标文件", false);
                foreach (var entry in replaceFiles)
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

                        _files.MoveFile(staged, target, overwrite: true);
                        if (!_files.FileExists(target) ||
                            (entry.Length.HasValue && _files.GetLength(target) != entry.Length.Value))
                        {
                            throw new IOException("替换后文件长度不匹配。");
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

                foreach (var patch in iniPatches)
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

                foreach (var entry in optionalDeleteFiles)
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
            });

            fileTransaction!.Stage = FileTransactionStage.FilesApplied;
            _fileTransactions.Save(fileTransaction);

            if (plan.TargetSnapshot is not null)
            {
                Report("正在恢复目标服 version/revision 缓存快照", false, indeterminate: true);
                try
                {
                    successfulCacheRestore = Measure(
                        "restoreMetadata",
                        () => _snapshots.Restore(plan.TargetSnapshot, plan.GamePath));
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

            Report("正在执行最终数量与文件状态校验", false, indeterminate: true);
            var finalValidationTimer = Stopwatch.StartNew();
            try
            {
                if (successfulReplace != plannedReplace || failedReplace != 0 ||
                    successfulDelete != plannedDelete || failedDelete != 0 ||
                    successfulCacheRestore != (plan.TargetSnapshot?.Files.Count ?? 0) || failedCacheRestore != 0)
                {
                    throw new InvalidOperationException("实际成功数量与计划操作数量不一致。");
                }

                ValidateFinalFiles(plan);
            }
            finally
            {
                finalValidationTimer.Stop();
                stageDurations["finalValidation"] = finalValidationTimer.ElapsedMilliseconds;
            }

            record.OperationResult = "success";
            record.RollbackResult = "not_required";
            _backups.SaveRecord(plan.BackupPath, record);

            // Persist the audit log before committing the new state. If logging fails,
            // the operation is rolled back and the target profile is never committed.
            WriteLog(null, "not_required");

            // The state is deliberately the final throwing commit step. No earlier step writes it.
            var committedState = new AppState
            {
                GamePath = plan.GamePath,
                GameVersion = plan.Manifest.GameVersion,
                CurrentProfile = plan.Manifest.TargetProfile,
                LastSuccessfulSwitch = DateTimeOffset.Now,
                LastOperationId = plan.OperationId,
                LastReplaceCount = successfulReplace,
                LastDeleteCount = successfulDelete,
                LastBackupPath = plan.BackupPath
            };
            if (sourceWasConfirmed) ProfileDetector.RememberSuccessfulSwitch(_paths, committedState);
            _stateStore.Save(committedState);
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
                PlannedReplace = plannedReplace,
                SuccessfulReplace = successfulReplace,
                FailedReplace = failedReplace,
                PlannedDelete = plannedDelete,
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
            if (ex is SourceIntegrityException sourceError)
            {
                try { new OnlineDifferencePackageCatalog(_paths).MarkInvalidSource(sourceError.SourcePath); }
                catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException)
                { failedFiles.Add("损坏包标记保存失败：" + markerError.Message); }
            }
            var rolledBack = false;
            var rollbackDetail = "未创建完整备份，未执行回滚。";
            if (changesMayHaveStarted)
            {
                Report("操作失败，正在回滚", true);
            }
            var hotUpdateRolledBack = hotUpdateTransaction is null;
            if (hotUpdateTransaction is not null && _hotUpdateCaches is not null)
            {
                hotUpdateRolledBack = _hotUpdateCaches.Rollback(hotUpdateTransaction);
            }

            if (record is not null && changesMayHaveStarted)
            {
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

            // A failure before Prepared has changed no game files or Blocks.
            // Its backup directory is inert and does not need a restore.
            if (!changesMayHaveStarted)
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
            return Failure(
                plan,
                rolledBack,
                successfulReplace,
                failedReplace,
                successfulDelete,
                failedDelete,
                successfulCacheRestore,
                failedCacheRestore,
                ex.Message,
                plannedReplace,
                plannedDelete,
                gameFilesUnchanged: !changesMayHaveStarted);
        }
        finally
        {
            TryDeleteStaging(stagingRoot, plan.GamePath);
            if (fileTransaction is not null && !changesMayHaveStarted &&
                !Directory.Exists(stagingRoot))
            {
                if (_fileTransactions.TryDelete()) _backups.TryDeleteBackup(plan.BackupPath);
            }
        }

        void Report(string step, bool rollingBack, bool indeterminate = false)
        {
            try
            {
                progress?.Report(new OperationProgress
                {
                    Step = step,
                    PlannedReplace = plannedReplace,
                    SuccessfulReplace = successfulReplace,
                    FailedReplace = failedReplace,
                    PlannedDelete = plannedDelete,
                    SuccessfulDelete = successfulDelete,
                    FailedDelete = failedDelete,
                    PlannedCacheRestore = plan.TargetSnapshot?.Files.Count ?? 0,
                    SuccessfulCacheRestore = successfulCacheRestore,
                    FailedCacheRestore = failedCacheRestore,
                    IsRollingBack = rollingBack,
                    IsIndeterminate = indeterminate || rollingBack
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
            PlannedReplace = plannedReplace,
            SuccessfulReplace = successfulReplace,
            PlannedDelete = plannedDelete,
            SuccessfulDelete = successfulDelete,
            PlannedCacheRestore = plan.TargetSnapshot?.Files.Count ?? 0,
            SuccessfulCacheRestore = successfulCacheRestore,
            StagedBytes = stagedBytes,
            BackupBytes = backupBytes,
            TotalDurationMilliseconds = totalTimer.ElapsedMilliseconds,
            StageDurationsMilliseconds = new Dictionary<string, long>(stageDurations, StringComparer.Ordinal),
            FailedFiles = failedFiles,
            RollbackResult = rollback,
            Error = error
        });

        T Measure<T>(string name, Func<T> action)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                timer.Stop();
                stageDurations[name] = timer.ElapsedMilliseconds;
            }
        }

        void MeasureAction(string name, Action action) =>
            Measure<object?>(name, () =>
            {
                action();
                return null;
            });
    }

    private void ValidateFinalFiles(SwitchPlan plan)
    {
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
            if (restoredSnapshotTargets.TryGetValue(target, out var restoredSnapshot))
            {
                if (!_files.FileExists(target) || _files.GetLength(target) != restoredSnapshot.Length)
                {
                    throw new IOException($"最终校验发现快照目标缺失或长度不符：{entry.Target}");
                }

                continue;
            }

            if (!_files.FileExists(target) ||
                (entry.Length.HasValue && _files.GetLength(target) != entry.Length.Value))
            {
                throw new IOException($"最终校验发现替换目标缺失或长度不符：{entry.Target}");
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
    }

    private static void ValidatePlanSource(SwitchPlan plan)
    {
        var paths = PlanFileGuard.CheckPaths(plan.GamePath, plan.Manifest, plan.TargetSnapshot, plan.BackupPath);
        foreach (var entry in plan.Manifest.ReplaceFiles) paths.Ensure(ResolveSource(plan, entry));
        RecoveryGuard.EnsureVersion(plan.GamePath, plan.Manifest.GameVersion);
        PlanFileGuard.Validate(plan.GamePath, plan.SourceFileStamps);
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
        string error,
        int? plannedReplace = null,
        int? plannedDelete = null,
        bool gameFilesUnchanged = false) => new()
        {
            OperationId = plan.OperationId,
            Success = false,
            RolledBack = rolledBack,
            GameFilesUnchanged = gameFilesUnchanged,
            PlannedReplace = plannedReplace ?? plan.Manifest.PlannedReplaceCount,
            SuccessfulReplace = successfulReplace,
            FailedReplace = failedReplace,
            PlannedDelete = plannedDelete ?? plan.Manifest.PlannedDeleteCount,
            SuccessfulDelete = successfulDelete,
            FailedDelete = failedDelete,
            PlannedCacheRestore = plan.TargetSnapshot?.Files.Count ?? 0,
            SuccessfulCacheRestore = successfulCacheRestore,
            FailedCacheRestore = failedCacheRestore,
            BackupPath = Directory.Exists(plan.BackupPath) ? plan.BackupPath : null,
            Error = error
        };

    private static void EnsureSafeStagingRoot(string gamePath)
    {
        foreach (var path in new[]
                 {
                     GameStorageLayout.GetRoot(gamePath),
                     GameStorageLayout.GetStagingRoot(gamePath)
                 })
        {
            if (Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"拒绝使用重解析点切换暂存目录：{path}");
            }
        }
    }

    private static string ResolveSource(SwitchPlan plan, ReplaceFileEntry entry)
    {
        if (plan.ResolvedSourceFiles.TryGetValue(entry.Target, out var resolved))
        {
            if (string.IsNullOrWhiteSpace(resolved) || !Path.IsPathFullyQualified(resolved))
            {
                throw new InvalidDataException($"组合切换源路径无效：{entry.Target}");
            }

            return Path.GetFullPath(resolved);
        }

        return PackageFileResolver.ResolveOrThrow(
            plan.PackageRoot,
            plan.PackageDirectory,
            entry);
    }

    private bool TargetNeedsReplacement(string gamePath, ReplaceFileEntry entry)
    {
        var target = PathSafety.ResolveOrThrow(gamePath, entry.Target);
        if (!_files.FileExists(target))
        {
            return true;
        }

        if (entry.Length.HasValue && _files.GetLength(target) != entry.Length.Value)
        {
            return true;
        }

        return !_integrity.Validate(target, entry.Length, entry.Sha256).IsValid;
    }

    private long BackupFileLength(string backupPath, string relative)
    {
        var path = PathSafety.ResolveOrThrow(Path.Combine(backupPath, "files"), relative);
        return _files.FileExists(path) ? _files.GetLength(path) : 0L;
    }

    private void TryDeleteStaging(string stagingRoot, string gamePath)
    {
        try
        {
            EnsureSafeStagingRoot(gamePath);
            if (Directory.Exists(stagingRoot))
            {
                if ((File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                var root = Path.GetFullPath(GameStorageLayout.GetStagingRoot(gamePath))
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
                {
                    _files.DeleteDirectory(stagingRoot, true);
                    var parent = Path.GetFullPath(GameStorageLayout.GetStagingRoot(gamePath));
                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        _files.DeleteDirectory(parent, false);
                    }
                }
            }
        }
        catch
        {
            // A stale app-private staging directory is harmless and can be removed later.
        }
    }
}
