using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class InspectionService
{
    private readonly ConfigurationRepository _configuration;
    private readonly GameDirectoryService _gameDirectory;
    private readonly ProfileDetector _detector;
    private readonly StateStore _stateStore;
    private readonly IProcessMonitor _processMonitor;
    private readonly FileTransactionJournalStore? _fileTransactions;
    private readonly FileIntegrityService _integrity;
    private readonly StorageLayoutService _storageLayout;
    private readonly bool _inspectLocalPackages;

    public InspectionService(
        ConfigurationRepository configuration,
        GameDirectoryService gameDirectory,
        ProfileDetector detector,
        StateStore stateStore,
        IProcessMonitor processMonitor,
        FileTransactionJournalStore? fileTransactions = null,
        IFileOperations? files = null,
        StorageLayoutService? storageLayout = null,
        bool inspectLocalPackages = true)
    {
        _configuration = configuration;
        _gameDirectory = gameDirectory;
        _detector = detector;
        _stateStore = stateStore;
        _processMonitor = processMonitor;
        _fileTransactions = fileTransactions;
        _integrity = new FileIntegrityService(files ?? new PhysicalFileOperations());
        _storageLayout = storageLayout ?? new StorageLayoutService();
        _inspectLocalPackages = inspectLocalPackages;
    }

    public InspectionReport Inspect(string gamePath)
    {
        var game = _gameDirectory.Validate(gamePath);
        var issues = new List<ValidationIssue>(game.Issues);
        var profileLoad = _configuration.LoadProfilesWithStatus();
        var transitionLoad = _inspectLocalPackages
            ? _configuration.LoadTransitionsWithStatus()
            : new ConfigurationLoadResult<TransitionManifest>();
        // 单个配置损坏不应让整个检查页退出；有效配置继续检查，错误作为阻止项聚合显示。
        AddConfigurationErrors("profile", profileLoad.Errors, issues);
        if (_inspectLocalPackages)
        {
            AddConfigurationErrors("transition", transitionLoad.Errors, issues);
        }
        var stateLoad = _stateStore.LoadWithStatus();
        if (!string.IsNullOrWhiteSpace(stateLoad.Warning))
        {
            issues.Add(new(
                IssueSeverity.Warning,
                "state.invalid",
                stateLoad.Warning));
        }

        if (_fileTransactions?.Exists == true)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "transaction.file.pending",
                "检测到未完成的文件事务。程序将阻止新的切换，直到启动恢复完成。"));
        }
        var profiles = profileLoad.Items;
        var transitions = transitionLoad.Items;
        var configuredVersions = transitions.Where(x => x.Enabled)
            .Select(x => x.GameVersion)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var packageVersion = game.GameVersion ??
                             (configuredVersions.Length == 1 ? configuredVersions[0] : null);
        if (game.GameVersion is null && configuredVersions.Length > 1)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "manifest.version.ambiguous",
                $"配置中存在多个启用版本：{string.Join("、", configuredVersions)}"));
        }
        var packageRoot = packageVersion is null
            ? GameStorageLayout.GetPackagesRoot(gamePath)
            : GameStorageLayout.GetPackageRoot(gamePath, packageVersion);
        var storage = game.IsValid && packageVersion is not null
            ? _storageLayout.Inspect(game.GamePath, packageVersion, profiles)
            : null;
        if (storage is not null)
        {
            AddStorageIssues(storage, issues, _inspectLocalPackages);
        }

        var packages = _inspectLocalPackages
            ? profiles.Select(profile => InspectPackage(
                packageRoot, packageVersion, profile, profiles, transitions)).ToList()
            : [];
        if (_inspectLocalPackages)
        {
            foreach (var package in packages.Where(x => !x.IsAvailable))
            {
                issues.Add(new(
                    IssueSeverity.Error,
                    "package.unavailable",
                    package.Detail ?? $"{package.DisplayName}差异包不可用。",
                    package.Path));
            }

            ValidateManifestSet(transitions, issues);
            foreach (var transition in transitions.Where(x => x.Enabled))
            {
                ValidateManifest(transition, profiles, packageRoot, gamePath, issues);
            }
        }

        var detection = game.IsValid
            ? _detector.Detect(game.GamePath, profiles, stateLoad.State)
            : new DetectionResult { Profile = DetectedProfile.Unknown };

        var running = _processMonitor.FindRelatedProcesses();
        if (running.Count > 0)
        {
            issues.Add(new(IssueSeverity.Warning, "process.running", $"检测到相关进程：{string.Join("、", running)}"));
        }

        return new()
        {
            Game = game,
            Detection = detection,
            Storage = storage,
            Packages = packages,
            Issues = issues,
            RunningProcesses = running.ToList()
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

    private static void AddStorageIssues(
        StorageLayoutStatus storage,
        List<ValidationIssue> issues,
        bool requirePackages)
    {
        if (!storage.RootExists)
        {
            issues.Add(new(
                requirePackages ? IssueSeverity.Error : IssueSeverity.Information,
                "storage.root.missing",
                requirePackages
                    ? "ZZZSwitch 存储根目录未检测到；差异包和已保存的双服缓存可能已被删除。"
                    : "ZZZSwitch 本地缓存目录尚未建立；在线切换不需要预置差异包，首次切换时会自动创建。",
                storage.RootPath));
            return;
        }

        if (requirePackages && !storage.PackagesRootExists)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "storage.packages.missing",
                "差异包存储目录未检测到。",
                storage.PackagesRootPath));
        }
        else if (requirePackages && !storage.PackageVersionExists)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "storage.package-version.missing",
                "当前游戏版本的差异包目录未检测到。",
                storage.PackageVersionPath));
        }

        if (!storage.CacheRootExists)
        {
            issues.Add(new(
                IssueSeverity.Information,
                "storage.cache.not-created",
                "缓存仓库目录尚未建立；首次初始化前属于正常状态。",
                storage.CacheRootPath));
        }
    }

    private PackageStatus InspectPackage(
        string packageRoot,
        string? packageVersion,
        ProfileDefinition profile,
        IReadOnlyList<ProfileDefinition> profiles,
        IReadOnlyList<TransitionManifest> transitions)
    {
        var path = Path.Combine(packageRoot, profile.PackageDirectoryName);
        var directoryExists = Directory.Exists(path);
        var count = 0;
        string? enumerationFailure = null;
        if (directoryExists)
        {
            try
            {
                // 枚举失败和目录不存在是不同故障：前者通常意味着权限或文件系统异常。
                count = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                enumerationFailure = ex.Message;
            }
        }
        var expectedFiles = transitions
            .Where(x => x.Enabled && string.Equals(x.GameVersion, packageVersion, StringComparison.Ordinal))
            .SelectMany(transition =>
            {
                var defaultDirectoryName = profiles.FirstOrDefault(x =>
                    string.Equals(x.Id, transition.TargetProfile, StringComparison.Ordinal))?.PackageDirectoryName;
                return defaultDirectoryName is null
                    ? []
                    : transition.ReplaceFiles.Where(entry => string.Equals(
                        PackageFileResolver.EffectiveDirectoryName(defaultDirectoryName, entry),
                        profile.PackageDirectoryName,
                        StringComparison.OrdinalIgnoreCase));
            })
            .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
        var missingFiles = 0;
        var invalidFiles = 0;
        string? firstFailure = null;
        if (directoryExists)
        {
            foreach (var entry in expectedFiles)
            {
                if (!PathSafety.TryResolveUnderRoot(path, entry.Source, out var source, out var pathError))
                {
                    invalidFiles++;
                    firstFailure ??= $"{entry.Source}：{pathError}";
                    continue;
                }

                var integrity = _integrity.Validate(source, entry.Length, entry.Sha256);
                if (integrity.Status == FileIntegrityStatus.FileMissing)
                {
                    missingFiles++;
                    firstFailure ??= entry.Source;
                }
                else if (!integrity.IsValid)
                {
                    invalidFiles++;
                    firstFailure ??= $"{entry.Source}：{integrity.Message}";
                }
            }
        }
        else
        {
            missingFiles = expectedFiles.Length;
        }

        var available = profile.Enabled &&
                        directoryExists &&
                        enumerationFailure is null &&
                        expectedFiles.Length > 0 &&
                        missingFiles == 0 &&
                        invalidFiles == 0;
        var detail = available
            ? $"完整性校验通过（{count} 个文件）"
            : profile.DisabledReason ??
              (!directoryExists
                  ? "差异包目录未检测到。"
                  : enumerationFailure is not null
                      ? $"无法读取差异包目录：{enumerationFailure}"
                  : expectedFiles.Length == 0
                      ? "没有找到对应版本的差异包清单。"
                      : count == 0
                          ? $"目录为空；需要手动放入 {expectedFiles.Length} 个差异文件。"
                          : invalidFiles > 0
                              ? $"差异包完整性校验失败 {invalidFiles}/{expectedFiles.Length}；{firstFailure}"
                              : $"差异包不完整；缺少 {missingFiles}/{expectedFiles.Length} 个清单文件。首个：{firstFailure}");
        return new()
        {
            ProfileId = profile.Id,
            DisplayName = profile.DisplayName,
            Path = path,
            IsAvailable = available,
            FileCount = count,
            Detail = detail
        };
    }

    private static void ValidateManifestSet(IReadOnlyList<TransitionManifest> manifests, List<ValidationIssue> issues)
    {
        foreach (var source in ProfileIds.All)
        {
            foreach (var target in ProfileIds.All.Where(x => x != source))
            {
                var count = manifests.Count(x => x.SourceProfile == source && x.TargetProfile == target);
                if (count != 1)
                {
                    issues.Add(new(IssueSeverity.Error, "manifest.set.invalid", $"切换方向 {source} → {target} 应有且仅有一个清单，当前为 {count} 个。"));
                }
            }
        }
    }

    private static void ValidateManifest(
        TransitionManifest manifest,
        IReadOnlyList<ProfileDefinition> profiles,
        string packageRoot,
        string gamePath,
        List<ValidationIssue> issues)
    {
        if (manifest.PlannedReplaceCount != manifest.ExpectedReplaceCount)
        {
            issues.Add(new(IssueSeverity.Error, "manifest.replace.count", $"{manifest.SourceProfile} → {manifest.TargetProfile} 的替换清单数量不符。"));
        }

        if (manifest.DeleteFiles.Count != manifest.ExpectedDeleteCount)
        {
            issues.Add(new(IssueSeverity.Error, "manifest.delete.count", $"{manifest.SourceProfile} → {manifest.TargetProfile} 的删除清单数量不符。"));
        }

        var missingIntegrity = manifest.ReplaceFiles.Count(x =>
            !x.Length.HasValue || string.IsNullOrWhiteSpace(x.Sha256));
        if (missingIntegrity > 0)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "manifest.integrity.missing",
                $"{manifest.SourceProfile} → {manifest.TargetProfile} 有 {missingIntegrity} 个文件缺少 length/sha256 完整性数据。"));
        }

        var invalidIntegrity = manifest.ReplaceFiles.Count(x =>
            x.Length.HasValue &&
            !string.IsNullOrWhiteSpace(x.Sha256) &&
            (x.Length.Value < 0 || !FileIntegrityService.IsValidSha256(x.Sha256)));
        if (invalidIntegrity > 0)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "manifest.integrity.invalid",
                $"{manifest.SourceProfile} → {manifest.TargetProfile} 有 {invalidIntegrity} 个文件的完整性数据格式无效。"));
        }

        var targetProfiles = profiles
            .Where(x => string.Equals(x.Id, manifest.TargetProfile, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (targetProfiles.Length != 1)
        {
            issues.Add(new(
                IssueSeverity.Error,
                targetProfiles.Length == 0 ? "manifest.profile.missing" : "manifest.profile.duplicate",
                targetProfiles.Length == 0
                    ? $"未知目标 profile：{manifest.TargetProfile}"
                    : $"目标 profile 重复：{manifest.TargetProfile}"));
            return;
        }

        var targetProfile = targetProfiles[0];
        var packageDirectory = Path.Combine(packageRoot, targetProfile.PackageDirectoryName);
        foreach (var entry in manifest.ReplaceFiles)
        {
            var sourceDirectoryName = PackageFileResolver.EffectiveDirectoryName(
                targetProfile.PackageDirectoryName,
                entry);
            if (!profiles.Any(x => string.Equals(
                    x.PackageDirectoryName,
                    sourceDirectoryName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new(
                    IssueSeverity.Error,
                    "manifest.source-package.unknown",
                    $"未知的差异包来源目录：{sourceDirectoryName}",
                    entry.Source));
                continue;
            }

            string source;
            try
            {
                source = PackageFileResolver.ResolveOrThrow(packageRoot, packageDirectory, entry);
            }
            catch (InvalidDataException ex)
            {
                issues.Add(new(IssueSeverity.Error, "manifest.source.unsafe", ex.Message, entry.Source));
                continue;
            }

            if (!PathSafety.TryResolveUnderRoot(gamePath, entry.Target, out _, out var targetError))
            {
                issues.Add(new(IssueSeverity.Error, "manifest.target.unsafe", targetError, entry.Target));
            }
        }

        foreach (var patch in manifest.IniPatches)
        {
            if (!PathSafety.TryResolveUnderRoot(gamePath, patch.Target, out _, out var targetError))
            {
                issues.Add(new(IssueSeverity.Error, "manifest.ini-target.unsafe", targetError, patch.Target));
            }
        }

        foreach (var entry in manifest.DeleteFiles.Concat(manifest.OptionalDeleteFiles))
        {
            if (!PathSafety.TryResolveUnderRoot(gamePath, entry.Target, out _, out var targetError))
            {
                issues.Add(new(IssueSeverity.Error, "manifest.delete.unsafe", targetError, entry.Target));
            }
        }
    }
}
