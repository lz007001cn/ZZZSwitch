using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class SwitchPlanner
{
    private readonly ConfigurationRepository _configuration;
    private readonly GameDirectoryService _gameDirectory;
    private readonly IProcessMonitor _processMonitor;
    private readonly IFileOperations _files;
    private readonly AppPaths _paths;
    private readonly ProfileSnapshotService _snapshots;
    private readonly HotUpdateCacheService? _hotUpdateCaches;
    private readonly FileTransactionJournalStore _fileTransactions;
    private readonly FileIntegrityService _integrity;

    public SwitchPlanner(
        ConfigurationRepository configuration,
        GameDirectoryService gameDirectory,
        IProcessMonitor processMonitor,
        IFileOperations files,
        AppPaths paths,
        ProfileSnapshotService snapshots,
        HotUpdateCacheService? hotUpdateCaches = null,
        FileTransactionJournalStore? fileTransactions = null)
    {
        _configuration = configuration;
        _gameDirectory = gameDirectory;
        _processMonitor = processMonitor;
        _files = files;
        _paths = paths;
        _snapshots = snapshots;
        _hotUpdateCaches = hotUpdateCaches;
        _fileTransactions = fileTransactions ?? new FileTransactionJournalStore(paths);
        _integrity = new FileIntegrityService(files);
    }

    public SwitchPlan CreatePlan(string gamePath, string sourceProfile, string targetProfile)
    {
        var operationId = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var backupName = $"{DateTimeOffset.Now:yyyy-MM-dd_HHmmss}_{sourceProfile}_to_{targetProfile}_{operationId[^8..]}";
        var setupIssues = new List<ValidationIssue>();
        var transitionLoad = _configuration.LoadTransitionsWithStatus();
        var profileLoad = _configuration.LoadProfilesWithStatus();
        AddConfigurationErrors("transition", transitionLoad.Errors, setupIssues);
        AddConfigurationErrors("profile", profileLoad.Errors, setupIssues);

        var transitionMatches = transitionLoad.Items.Where(x =>
            string.Equals(x.SourceProfile, sourceProfile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.TargetProfile, targetProfile, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        // 绝不在重复清单中“任选一个”。发布目录混入旧配置时必须明确阻止切换。
        if (transitionMatches.Length > 1)
        {
            setupIssues.Add(new(
                IssueSeverity.Error,
                "manifest.direction.duplicate",
                $"存在重复的切换清单：{sourceProfile} -> {targetProfile}。"));
        }

        var manifest = transitionMatches.Length == 1
            ? transitionMatches[0]
            : new TransitionManifest
            {
                SourceProfile = sourceProfile,
                TargetProfile = targetProfile,
                GameVersion = "unknown",
                Enabled = false,
                DisabledReason = "没有唯一可用的对应切换方向清单。"
            };
        var targetDefinitions = profileLoad.Items
            .Where(x => string.Equals(x.Id, targetProfile, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (targetDefinitions.Length != 1)
        {
            setupIssues.Add(new(
                IssueSeverity.Error,
                targetDefinitions.Length == 0 ? "profile.target.missing" : "profile.target.duplicate",
                targetDefinitions.Length == 0
                    ? $"没有找到目标服务器配置：{targetProfile}。"
                    : $"存在重复的目标服务器配置：{targetProfile}。"));
        }

        var targetDefinition = targetDefinitions.Length == 1 ? targetDefinitions[0] : null;
        var packageRoot = GameStorageLayout.GetPackageRoot(gamePath, manifest.GameVersion);
        var packageDirectory = targetDefinition is null ? packageRoot : Path.Combine(packageRoot, targetDefinition.PackageDirectoryName);
        var targetSnapshot = manifest.Enabled
            ? _snapshots.FindLatestValid(ProfileIds.ToResourceProfile(targetProfile), manifest.GameVersion, gamePath)
            : null;
        var issues = Validate(gamePath, manifest, packageRoot, packageDirectory, targetSnapshot);
        issues.InsertRange(0, setupIssues);
        var sourceResourceProfile = ProfileIds.ToResourceProfile(sourceProfile);
        var targetResourceProfile = ProfileIds.ToResourceProfile(targetProfile);
        var hotUpdateTransition = manifest.Enabled &&
                                  _hotUpdateCaches is not null &&
                                  !string.Equals(sourceResourceProfile, targetResourceProfile, StringComparison.OrdinalIgnoreCase)
            ? _hotUpdateCaches.CreateTransitionPlan(
                sourceResourceProfile,
                targetResourceProfile,
                manifest.GameVersion,
                gamePath,
                issues)
            : null;
        if (string.Equals(sourceProfile, targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new(IssueSeverity.Information, "target.same", "当前已经是目标服，不会执行重复覆盖或删除。"));
        }

        return new()
        {
            OperationId = operationId,
            GamePath = Path.GetFullPath(gamePath),
            PackageRoot = packageRoot,
            PackageDirectory = packageDirectory,
            Manifest = manifest,
            BackupPath = Path.Combine(_paths.BackupsRoot, backupName),
            TargetSnapshot = targetSnapshot,
            HotUpdateTransition = hotUpdateTransition,
            Issues = issues
        };
    }

    public SwitchPlan CreateOnlinePlan(
        string gamePath,
        OnlineDifferenceMaterialization materialization)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        var manifest = materialization.Manifest;
        var operationId = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var backupName =
            $"{DateTimeOffset.Now:yyyy-MM-dd_HHmmss}_{manifest.SourceProfile}_to_{manifest.TargetProfile}_{operationId[^8..]}";
        var targetSnapshot = manifest.Enabled
            ? _snapshots.FindLatestValid(
                ProfileIds.ToResourceProfile(manifest.TargetProfile),
                manifest.GameVersion,
                gamePath)
            : null;
        var issues = Validate(
            gamePath,
            manifest,
            materialization.PackageRoot,
            materialization.PackageDirectory,
            targetSnapshot);
        var sourceResourceProfile = ProfileIds.ToResourceProfile(manifest.SourceProfile);
        var targetResourceProfile = ProfileIds.ToResourceProfile(manifest.TargetProfile);
        var hotUpdateTransition = manifest.Enabled &&
                                  _hotUpdateCaches is not null &&
                                  !string.Equals(
                                      sourceResourceProfile,
                                      targetResourceProfile,
                                      StringComparison.OrdinalIgnoreCase)
            ? _hotUpdateCaches.CreateTransitionPlan(
                sourceResourceProfile,
                targetResourceProfile,
                manifest.GameVersion,
                gamePath,
                issues)
            : null;

        return new SwitchPlan
        {
            OperationId = operationId,
            GamePath = Path.GetFullPath(gamePath),
            PackageRoot = materialization.PackageRoot,
            PackageDirectory = materialization.PackageDirectory,
            Manifest = manifest,
            BackupPath = Path.Combine(_paths.BackupsRoot, backupName),
            TargetSnapshot = targetSnapshot,
            HotUpdateTransition = hotUpdateTransition,
            FileSourceDescription = "Sophon 在线差异缓存（已通过完整性校验）",
            Issues = issues
        };
    }

    private static void AddConfigurationErrors(
        string kind,
        IReadOnlyList<ConfigurationLoadError> errors,
        List<ValidationIssue> issues)
    {
        foreach (var error in errors)
        {
            issues.Add(new(
                IssueSeverity.Error,
                $"config.{kind}.read",
                error.Message,
                error.Path));
        }
    }

    private List<ValidationIssue> Validate(
        string gamePath,
        TransitionManifest manifest,
        string packageRoot,
        string packageDirectory,
        ProfileSnapshotManifest? targetSnapshot)
    {
        var issues = new List<ValidationIssue>();
        var game = _gameDirectory.Validate(gamePath);
        issues.AddRange(game.Issues);

        if (_fileTransactions.Exists)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "transaction.file.pending",
                "检测到上次切换留下的文件事务记录。请重启 ZZZSwitch 完成自动恢复后再切换。"));
        }

        if (!manifest.Enabled)
        {
            issues.Add(new(IssueSeverity.Error, "manifest.disabled", manifest.DisabledReason ?? "该切换方向已禁用。"));
        }

        if (manifest.PlannedReplaceCount != manifest.ExpectedReplaceCount)
        {
            issues.Add(new(IssueSeverity.Error, "manifest.replace.count", "计划替换数量与清单声明不一致。"));
        }

        if (manifest.DeleteFiles.Count != manifest.ExpectedDeleteCount)
        {
            issues.Add(new(IssueSeverity.Error, "manifest.delete.count", "计划删除数量与清单声明不一致。"));
        }

        if (game.GameVersion is not null && !string.Equals(game.GameVersion, manifest.GameVersion, StringComparison.Ordinal))
        {
            issues.Add(new(IssueSeverity.Error, "game.version.mismatch", $"游戏版本 {game.GameVersion} 与清单版本 {manifest.GameVersion} 不一致。"));
        }

        if (!Directory.Exists(packageDirectory))
        {
            issues.Add(new(IssueSeverity.Error, "package.directory.missing", "切换文件源目录不存在。", packageDirectory));
        }

        if (manifest.Enabled)
        {
            var sourceCacheFiles = _snapshots.DiscoverCacheMetadataFiles(gamePath);
            if (sourceCacheFiles.Count == 0)
            {
                issues.Add(new(IssueSeverity.Error, "snapshot.source.empty", "没有发现可备份的 Persistent/StreamingAssets 一级 version/revision 文件。"));
            }

            if (targetSnapshot is null)
            {
                issues.Add(new(IssueSeverity.Warning, "snapshot.target.missing", "目标服尚无有效缓存快照；本次仍可使用已校验的切换文件源，但首次切回前无法恢复该服缓存元数据。"));
            }
        }

        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageIntegrityFailures = new List<(string Path, string Reason)>();
        foreach (var entry in manifest.ReplaceFiles)
        {
            string source;
            try
            {
                source = PackageFileResolver.ResolveOrThrow(packageRoot, packageDirectory, entry);
            }
            catch (InvalidDataException ex)
            {
                issues.Add(new(IssueSeverity.Error, "path.source.unsafe", ex.Message, entry.Source));
                continue;
            }

            if (!_files.FileExists(source))
            {
                issues.Add(new(IssueSeverity.Error, "package.source.missing", "切换源文件不存在。", source));
            }
            else
            {
                var integrity = _integrity.Validate(source, entry.Length, entry.Sha256);
                if (!integrity.IsValid)
                {
                    packageIntegrityFailures.Add((source, integrity.Message));
                }
            }

            if (!PathSafety.TryResolveUnderRoot(gamePath, entry.Target, out var target, out var targetError))
            {
                issues.Add(new(IssueSeverity.Error, "path.target.unsafe", targetError, entry.Target));
            }
            else if (!targetPaths.Add(target))
            {
                issues.Add(new(IssueSeverity.Error, "path.target.duplicate", "目标文件在清单中重复。", entry.Target));
            }
        }

        foreach (var patch in manifest.IniPatches)
        {
            if (!PathSafety.TryResolveUnderRoot(gamePath, patch.Target, out var target, out var targetError))
            {
                issues.Add(new(IssueSeverity.Error, "path.ini-target.unsafe", targetError, patch.Target));
            }
            else if (!targetPaths.Add(target))
            {
                issues.Add(new(IssueSeverity.Error, "path.target.duplicate", "INI 目标与其他替换目标重复。", patch.Target));
            }
            else if (!_files.FileExists(target))
            {
                issues.Add(new(IssueSeverity.Error, "ini.target.missing", "需要修改的 INI 文件不存在。", target));
            }
        }

        if (packageIntegrityFailures.Count > 0)
        {
            var first = packageIntegrityFailures[0];
            issues.Add(new(
                IssueSeverity.Error,
                "package.integrity.failed",
                $"切换文件源有 {packageIntegrityFailures.Count} 个文件未通过完整性校验。首个问题：{first.Reason}",
                first.Path));
        }

        foreach (var entry in manifest.DeleteFiles)
        {
            if (!PathSafety.TryResolveUnderRoot(gamePath, entry.Target, out var target, out var error))
            {
                issues.Add(new(IssueSeverity.Error, "path.delete.unsafe", error, entry.Target));
            }
            else if (!_files.FileExists(target))
            {
                issues.Add(new(IssueSeverity.Error, "delete.required.missing", "必需删除的目标在操作前不存在，来源状态不符合预期。", target));
            }
        }

        foreach (var entry in manifest.OptionalDeleteFiles)
        {
            if (!PathSafety.TryResolveUnderRoot(gamePath, entry.Target, out _, out var error))
            {
                issues.Add(new(IssueSeverity.Error, "path.optional-delete.unsafe", error, entry.Target));
            }
        }

        var processes = _processMonitor.FindRelatedProcesses();
        foreach (var process in processes)
        {
            if (process.StartsWith("ZenlessZoneZero", StringComparison.OrdinalIgnoreCase) ||
                process.StartsWith("HYUpdater", StringComparison.OrdinalIgnoreCase) ||
                process.StartsWith("PCGamePlatform", StringComparison.OrdinalIgnoreCase) ||
                process.StartsWith("game_security_protection", StringComparison.OrdinalIgnoreCase) ||
                process.StartsWith("ZZZSwitch", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(IssueSeverity.Error, "process.blocking", $"阻止切换的进程正在运行：{process}"));
            }
            else
            {
                issues.Add(new(IssueSeverity.Warning, "process.launcher", $"检测到启动器后台进程；若文件被占用请关闭 HoYoPlay：{process}"));
            }
        }

        foreach (var target in targetPaths
                     .Concat(manifest.DeleteFiles.Concat(manifest.OptionalDeleteFiles)
                         .Select(x => PathSafety.TryResolveUnderRoot(gamePath, x.Target, out var p, out _) ? p : string.Empty))
                     .Where(x => !string.IsNullOrEmpty(x) && _files.FileExists(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var handle = _files.OpenExclusive(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(IssueSeverity.Error, "file.locked", "目标文件被占用，请关闭游戏和 HoYoPlay。", target));
            }
        }

        try
        {
            var stagingBytes = manifest.ReplaceFiles.Sum(x =>
            {
                var source = PackageFileResolver.ResolveOrThrow(packageRoot, packageDirectory, x);
                return _files.FileExists(source) ? _files.GetLength(source) : 0L;
            });
            var affectedTargets = manifest.ReplaceFiles.Select(x => x.Target)
                .Concat(manifest.IniPatches.Select(x => x.Target))
                .Concat(manifest.DeleteFiles.Select(x => x.Target))
                .Concat(manifest.OptionalDeleteFiles.Select(x => x.Target))
                .Concat(targetSnapshot?.Files.Select(x => x.RelativePath) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var backupBytes = affectedTargets.Sum(x =>
            {
                var target = PathSafety.ResolveOrThrow(gamePath, x);
                return _files.FileExists(target) ? _files.GetLength(target) : 0L;
            });
            var requiredBytes = stagingBytes + backupBytes;
            var root = Path.GetPathRoot(_paths.DataRoot);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var available = new DriveInfo(root).AvailableFreeSpace;
                const long safetyMargin = 64L * 1024 * 1024;
                if (available < requiredBytes + safetyMargin)
                {
                    issues.Add(new(IssueSeverity.Error, "disk.space", $"应用数据盘空间不足，备份和临时预复制至少需要 {requiredBytes + safetyMargin:N0} 字节。"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            issues.Add(new(IssueSeverity.Error, "disk.check.failed", $"无法检查备份盘空间：{ex.Message}"));
        }

        return issues;
    }
}
