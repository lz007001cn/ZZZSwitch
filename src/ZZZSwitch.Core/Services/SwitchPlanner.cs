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
    private readonly Func<string, long> _getAvailableFreeSpace;

    public SwitchPlanner(
        ConfigurationRepository configuration,
        GameDirectoryService gameDirectory,
        IProcessMonitor processMonitor,
        IFileOperations files,
        AppPaths paths,
        ProfileSnapshotService snapshots,
        HotUpdateCacheService? hotUpdateCaches = null,
        FileTransactionJournalStore? fileTransactions = null,
        Func<string, long>? getAvailableFreeSpace = null)
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
        _getAvailableFreeSpace = getAvailableFreeSpace ?? (root => new DriveInfo(root).AvailableFreeSpace);
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
        manifest = ConfigurationRepository.ResolveBilibiliVersion(manifest,
            _gameDirectory.Validate(gamePath).GameVersion, profileLoad.Items, transitionLoad.Items);
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

    public SwitchPlan CreateBilibiliCompositePlan(
        string gamePath,
        string sourceProfile,
        string targetProfile,
        OnlineDifferenceMaterialization baseMaterialization)
    {
        ArgumentNullException.ThrowIfNull(baseMaterialization);
        var sourceResourceProfile = ProfileIds.ToResourceProfile(sourceProfile);
        var targetResourceProfile = ProfileIds.ToResourceProfile(targetProfile);
        if ((!string.Equals(sourceProfile, ProfileIds.Bilibili, StringComparison.Ordinal) &&
             !string.Equals(targetProfile, ProfileIds.Bilibili, StringComparison.Ordinal)) ||
            string.Equals(sourceResourceProfile, targetResourceProfile, StringComparison.Ordinal))
        {
            throw new ArgumentException("组合 B 服计划只用于跨国服资源边界的 B 服切换。");
        }

        var operationId = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var backupName =
            $"{DateTimeOffset.Now:yyyy-MM-dd_HHmmss}_{sourceProfile}_to_{targetProfile}_{operationId[^8..]}";
        var setupIssues = new List<ValidationIssue>();
        var transitionLoad = _configuration.LoadTransitionsWithStatus();
        var profileLoad = _configuration.LoadProfilesWithStatus();
        AddConfigurationErrors("transition", transitionLoad.Errors, setupIssues);
        AddConfigurationErrors("profile", profileLoad.Errors, setupIssues);

        var directMatches = transitionLoad.Items.Where(item =>
                string.Equals(item.SourceProfile, sourceProfile, StringComparison.Ordinal) &&
                string.Equals(item.TargetProfile, targetProfile, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var direct = directMatches.Length == 1
            ? directMatches[0]
            : new TransitionManifest
            {
                SourceProfile = sourceProfile,
                TargetProfile = targetProfile,
                GameVersion = baseMaterialization.Manifest.GameVersion,
                Enabled = false,
                DisabledReason = "没有唯一可用的 B 服切换清单。"
            };
        if (directMatches.Length > 1)
        {
            setupIssues.Add(new(
                IssueSeverity.Error,
                "manifest.direction.duplicate",
                $"存在重复的切换清单：{sourceProfile} -> {targetProfile}。"));
        }

        var baseManifest = baseMaterialization.Manifest;
        direct = ConfigurationRepository.ResolveBilibiliVersion(direct, baseManifest.GameVersion,
            profileLoad.Items, transitionLoad.Items, hasOnlineBase: true);
        if (!string.Equals(baseManifest.SourceProfile, sourceResourceProfile, StringComparison.Ordinal) ||
            !string.Equals(baseManifest.TargetProfile, targetResourceProfile, StringComparison.Ordinal) ||
            !string.Equals(baseManifest.GameVersion, direct.GameVersion, StringComparison.Ordinal))
        {
            setupIssues.Add(new(
                IssueSeverity.Error,
                "manifest.composite.direction",
                "在线国服/国际服差异与 B 服组合方向或版本不一致。"));
        }

        var bilibiliDefinitions = profileLoad.Items.Where(item =>
                string.Equals(item.Id, ProfileIds.Bilibili, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var targetDefinitions = profileLoad.Items.Where(item =>
                string.Equals(item.Id, targetProfile, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        var bilibiliDefinition = bilibiliDefinitions.Length == 1 ? bilibiliDefinitions[0] : null;
        var targetDefinition = targetDefinitions.Length == 1 ? targetDefinitions[0] : null;
        if (bilibiliDefinition is null || targetDefinition is null)
        {
            setupIssues.Add(new(
                IssueSeverity.Error,
                "profile.composite.missing",
                "B 服组合切换缺少唯一的目标服或 B 服目录配置。"));
        }

        var packageRoot = GameStorageLayout.GetPackageRoot(gamePath, direct.GameVersion);
        var directDefaultDirectory = targetDefinition is null
            ? packageRoot
            : Path.Combine(packageRoot, targetDefinition.PackageDirectoryName);
        var replacements = new Dictionary<string, (ReplaceFileEntry Entry, string Source)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in baseManifest.ReplaceFiles)
        {
            try
            {
                replacements[entry.Target] = (
                    entry,
                    PackageFileResolver.ResolveOrThrow(
                        baseMaterialization.PackageRoot,
                        baseMaterialization.PackageDirectory,
                        entry));
            }
            catch (InvalidDataException ex)
            {
                setupIssues.Add(new(IssueSeverity.Error, "path.source.unsafe", ex.Message, entry.Source));
            }
        }

        if (bilibiliDefinition is not null && targetDefinition is not null)
        {
            foreach (var entry in direct.ReplaceFiles.Where(entry =>
                         string.Equals(
                             PackageFileResolver.EffectiveDirectoryName(
                                 targetDefinition.PackageDirectoryName,
                                 entry),
                             bilibiliDefinition.PackageDirectoryName,
                             StringComparison.Ordinal)))
            {
                try
                {
                    replacements[entry.Target] = (
                        entry,
                        PackageFileResolver.ResolveOrThrow(packageRoot, directDefaultDirectory, entry));
                }
                catch (InvalidDataException ex)
                {
                    setupIssues.Add(new(IssueSeverity.Error, "path.source.unsafe", ex.Message, entry.Source));
                }
            }
        }

        var replacedTargets = replacements.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredDeletes = baseManifest.DeleteFiles
            .Concat(direct.DeleteFiles)
            .Where(item => !replacedTargets.Contains(item.Target))
            .GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var requiredDeleteTargets = requiredDeletes
            .Select(item => item.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var optionalDeletes = baseManifest.OptionalDeleteFiles
            .Concat(direct.OptionalDeleteFiles)
            .Where(item => !replacedTargets.Contains(item.Target) &&
                           !requiredDeleteTargets.Contains(item.Target))
            .GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var replacementEntries = replacements.Values.Select(item => item.Entry).ToList();
        var composite = new TransitionManifest
        {
            SourceProfile = sourceProfile,
            TargetProfile = targetProfile,
            GameVersion = direct.GameVersion,
            Enabled = direct.Enabled && baseManifest.Enabled,
            DisabledReason = direct.DisabledReason ?? baseManifest.DisabledReason,
            ExpectedReplaceCount = replacementEntries.Count + direct.IniPatches.Count,
            ExpectedDeleteCount = requiredDeletes.Count,
            ReplaceFiles = replacementEntries,
            IniPatches = direct.IniPatches,
            DeleteFiles = requiredDeletes,
            OptionalDeleteFiles = optionalDeletes,
            Notes = "组合切换：Sophon 国服/国际服基础差异 + 软件内置 B 服覆盖层。"
        };
        var targetSnapshot = composite.Enabled
            ? _snapshots.FindLatestValid(targetResourceProfile, composite.GameVersion, gamePath)
            : null;
        var resolvedSources = replacements.ToDictionary(
            item => item.Key,
            item => item.Value.Source,
            StringComparer.OrdinalIgnoreCase);
        var issues = Validate(
            gamePath,
            composite,
            baseMaterialization.PackageRoot,
            baseMaterialization.PackageDirectory,
            targetSnapshot,
            resolvedSources);
        issues.InsertRange(0, setupIssues);
        var hotUpdateTransition = composite.Enabled &&
                                  _hotUpdateCaches is not null &&
                                  !string.Equals(
                                      sourceResourceProfile,
                                      targetResourceProfile,
                                      StringComparison.Ordinal)
            ? _hotUpdateCaches.CreateTransitionPlan(
                sourceResourceProfile,
                targetResourceProfile,
                 composite.GameVersion,
                 gamePath,
                 issues)
            : null;

        return new SwitchPlan
        {
            OperationId = operationId,
            GamePath = Path.GetFullPath(gamePath),
            PackageRoot = baseMaterialization.PackageRoot,
            PackageDirectory = baseMaterialization.PackageDirectory,
            ResolvedSourceFiles = resolvedSources,
            Manifest = composite,
            BackupPath = Path.Combine(_paths.BackupsRoot, backupName),
            TargetSnapshot = targetSnapshot,
            HotUpdateTransition = hotUpdateTransition,
            FileSourceDescription = "Sophon 在线区域差异 + 内置 B 服覆盖层（已校验）",
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
        ProfileSnapshotManifest? targetSnapshot,
        IReadOnlyDictionary<string, string>? resolvedSourceFiles = null)
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

        if (game.GameVersion is not null && !string.Equals(game.GameVersion, manifest.GameVersion, StringComparison.Ordinal))
        {
            issues.Add(new(IssueSeverity.Error, "game.version.mismatch", $"游戏版本 {game.GameVersion} 与清单版本 {manifest.GameVersion} 不一致。"));
        }

        if (manifest.ReplaceFiles.Count > 0 && !Directory.Exists(packageDirectory))
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
        var packageFileFailures = new List<(string Path, string Reason)>();
        var bundledBilibiliDirectory = GameStorageLayout.GetPackageDirectory(
            gamePath,
            manifest.GameVersion,
            ProfileIds.Bilibili);
        foreach (var entry in manifest.ReplaceFiles)
        {
            string source;
            try
            {
                source = ResolveSource(
                    packageRoot,
                    packageDirectory,
                    entry,
                    resolvedSourceFiles);
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
            else if (!entry.Length.HasValue ||
                     entry.Length.Value < 0 ||
                     !FileIntegrityService.IsValidSha256(entry.Sha256))
            {
                packageFileFailures.Add((source, "清单缺少有效的文件长度或 SHA-256。"));
            }
            else if (_files.GetLength(source) != entry.Length.Value)
            {
                packageFileFailures.Add((source, "文件长度与清单不匹配。"));
            }
            else if (IsUnderDirectory(bundledBilibiliDirectory, source))
            {
                var integrity = _integrity.Validate(source, entry.Length, entry.Sha256);
                if (!integrity.IsValid)
                {
                    packageFileFailures.Add((source, integrity.Message));
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

        if (packageFileFailures.Count > 0)
        {
            var first = packageFileFailures[0];
            issues.Add(new(
                IssueSeverity.Error,
                "package.integrity.failed",
                $"切换文件源有 {packageFileFailures.Count} 个文件未通过基础检查。首个问题：{first.Reason}",
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
                var source = ResolveSource(
                    packageRoot,
                    packageDirectory,
                    x,
                    resolvedSourceFiles);
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
            var stagingDrive = Path.GetPathRoot(Path.GetFullPath(gamePath));
            var backupDrive = Path.GetPathRoot(Path.GetFullPath(_paths.BackupsRoot));
            if (!string.IsNullOrWhiteSpace(stagingDrive))
            {
                AddDiskSpaceIssue(
                    issues,
                    stagingDrive,
                    string.Equals(stagingDrive, backupDrive, StringComparison.OrdinalIgnoreCase)
                        ? checked(stagingBytes + backupBytes)
                        : stagingBytes,
                    "游戏数据磁盘");
            }

            if (!string.IsNullOrWhiteSpace(backupDrive) &&
                !string.Equals(stagingDrive, backupDrive, StringComparison.OrdinalIgnoreCase))
            {
                AddDiskSpaceIssue(issues, backupDrive, backupBytes, "备份磁盘");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            issues.Add(new(IssueSeverity.Error, "disk.check.failed", $"无法检查游戏或备份盘空间：{ex.Message}"));
        }

        return issues;
    }

    private static bool IsUnderDirectory(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSource(
        string packageRoot,
        string packageDirectory,
        ReplaceFileEntry entry,
        IReadOnlyDictionary<string, string>? resolvedSourceFiles)
    {
        if (resolvedSourceFiles is not null &&
            resolvedSourceFiles.TryGetValue(entry.Target, out var resolved))
        {
            if (string.IsNullOrWhiteSpace(resolved) || !Path.IsPathFullyQualified(resolved))
            {
                throw new InvalidDataException($"组合切换源路径无效：{entry.Target}");
            }

            return Path.GetFullPath(resolved);
        }

        return PackageFileResolver.ResolveOrThrow(packageRoot, packageDirectory, entry);
    }

    private void AddDiskSpaceIssue(
        ICollection<ValidationIssue> issues,
        string driveRoot,
        long requiredBytes,
        string description)
    {
        const long safetyMargin = 64L * 1024 * 1024;
        var available = _getAvailableFreeSpace(driveRoot);
        var requiredWithMargin = checked(requiredBytes + safetyMargin);
        if (available < requiredWithMargin)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "disk.space",
                $"{description}空间不足。需要约 {ByteSizeFormatter.Format(requiredWithMargin)}（{requiredWithMargin:N0} 字节），" +
                $"当前可用 {ByteSizeFormatter.Format(available)}（{available:N0} 字节）。"));
        }
    }
}
