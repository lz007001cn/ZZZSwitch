using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.ManifestTool;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.Core.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("识别国际服", () => DetectionExact(ProfileIds.Global, DetectedProfile.Global)),
        ("识别国服", () => DetectionExact(ProfileIds.CnOfficial, DetectedProfile.CnOfficial)),
        ("识别未知状态", DetectionUnknown),
        ("识别混合状态", DetectionMixed),
        ("升级后使用当前版本清单识别并双向切换", VersionedManifestDetectionAndSwitch),
        ("正常启动复用识别结果且版本文件清单异常触发重检", ConfirmedDetectionReuse),
        ("新版游戏拒绝旧版备份恢复", RestoreRejectsDifferentGameVersion),
        ("叠加型B服优先于国服基础匹配", BilibiliOverlayDetection),
        ("B服资源与热更新缓存归一到国服", BilibiliResourceProfileMapping),
        ("拒绝包含 .. 的路径", () => PathRejected(@"a\..\b")),
        ("拒绝绝对路径", () => PathRejected(@"C:\outside.txt")),
        ("拒绝通配符删除路径", () => PathRejected(@"Data\*.dll")),
        ("安全路径保持在根目录内", PathAccepted),
        ("磁盘容量使用可读单位", ReadableByteSizes),
        ("验证游戏目录与版本", GameDirectoryValidation),
        ("自动检测忽略无效路径并返回有效安装", GameDirectoryDiscovery),
        ("清单声明数量不影响实际文件列表", ManifestCountsAreInformational),
        ("差异包缺少源文件时停止", () => PlannerFailure("source")),
        ("游戏版本不匹配时停止", () => PlannerFailure("version")),
        ("游戏进程运行时停止", () => PlannerFailure("process")),
        ("文件被占用时停止", () => PlannerFailure("lock")),
        ("必需删除文件缺失时停止", () => PlannerFailure("delete")),
        ("存在未完成文件事务时停止", () => PlannerFailure("transaction")),
        ("同盘预检合并暂存与备份空间", () => PlannerDiskSpace(false)),
        ("跨盘预检分别检查暂存与备份空间", () => PlannerDiskSpace(true)),
        ("在线差异自动排除 Streaming Blocks 与待观察文件", OnlineDifferenceSelection),
        ("在线切换按 Manifest 复用本地文件并保存反向差异包", OnlineDifferenceReusesLocalFilesAndCapturesReversePackage),
        ("已完成在线差异自动登记为版本差异包", OnlineDifferencePackageCatalogRecognizesReadyPackage),
        ("未完成在线分块在版本资源中保留", OnlineDifferencePackageCatalogKeepsIncompletePackage),
        ("Manifest 浏览器按方向与资源类型建立索引", ManifestBrowserBuildsExtensibleIndex),
        ("在线切换计划不读取现有差异包", OnlinePlannerDoesNotReadExistingPackage),
        ("国际服到B服组合计划只需要在线基础差异和内置覆盖层", GlobalToBilibiliCompositeUsesOnlineBase),
        ("B服到国际服组合计划不需要本地国际服固定包", BilibiliToGlobalCompositeUsesOnlineBase),
        ("缓存快照只收集一级 version/revision 文件", SnapshotFiltersFiles),
        ("缺失或长度异常的缓存快照被忽略", InvalidSnapshotIsIgnored),
        ("缓存快照按安装隔离并只保留两份有效记录", SnapshotRetentionIsGameScoped),
        ("切换时恢复目标服缓存快照", SwitchRestoresTargetSnapshot),
        ("切换后快照覆盖差异包中的基础缓存文件", SnapshotOverridesPackageCacheFile),
        ("初始化当前服 Blocks 缓存", HotUpdateCacheInitialization),
        ("不同游戏目录的 Blocks 清单相互隔离", HotUpdateManifestsAreGameScoped),
        ("旧版 Blocks 清单按身份安全迁移", LegacyHotUpdateManifestMigrates),
        ("其他游戏目录不会认领旧版清单", ForeignGameDoesNotClaimLegacyManifest),
        ("首次初始化与双服 Blocks 交换可回滚", HotUpdateCacheSwapAndRollback),
        ("跨磁盘复制模式可交换并回滚 Blocks", HotUpdateVerifiedCopySwapAndRollback),
        ("首次移动前中断不会删除活动 Blocks", HotUpdateRecoveryBeforeFirstMove),
        ("目标服缓存仓库丢失时进入重建模式", LostTargetCacheUsesInitializationMode),
        ("文件替换失败时 Blocks 与文件共同回滚", HotUpdateEngineFailureRollback),
        ("事务切换数量正确且成功后写状态", SwitchSuccess),
        ("准备阶段使用活动进度且替换阶段报告数量", SwitchProgressReportsPhases),
        ("已满足目标内容的文件不会重复暂存或备份", UnchangedTargetsAreSkipped),
        ("跨差异包目录复用源文件", CrossPackageSourceReuse),
        ("INI按键修改保留其他配置", IniPatchPreservesUnrelatedSettings),
        ("INI修改后的后续故障完整回滚", IniPatchRollback),
        ("复制中途失败后回滚", CopyFailureRollback),
        ("回滚失败不会误报未修改游戏或已恢复", IncompleteRollbackIsReported),
        ("备份创建失败会清理不完整目录", IncompleteBackupIsRemoved),
        ("新增文件在失败回滚时删除", NewFileRollback),
        ("已删除文件在失败回滚时恢复", DeletedFileRollback),
        ("失败时不提交目标状态", FailedSwitchDoesNotCommitState),
        ("目标服相同时不重复操作", SameTargetNoOp),
        ("共享操作协调器阻止并发并可重复使用", OperationCoordinatorMutualExclusion),
        ("跨协调器写操作锁阻止并发", CrossCoordinatorOperationLock),
        ("应用生命周期锁可竞争并在退出后释放", ApplicationInstanceLockLifecycle),
        ("已初始化 Blocks 缓存时阻止旧备份恢复", LegacyRestoreBlockedWhenHotCacheInitialized),
        ("未初始化 Blocks 缓存时允许旧备份恢复", LegacyRestoreAllowedWithoutHotCache),
        ("拒绝恢复其他游戏目录的备份", LegacyRestoreRejectsDifferentGamePath),
        ("恢复服务无法绕过 Blocks 安全策略", RestoreServiceEnforcesLegacySafety),
        ("主页恢复只使用状态精确对应的最后切换备份", RestoreLatestUsesExactStateBackup),
        ("统一存储布局生成稳定安全路径", UnifiedStorageLayout),
        ("C盘应用数据校验迁移到 .zzzswitch", ApplicationDataMigratesToGameStorage),
        ("旧版 data/cache 目录原子迁移为明确命名", LegacyStorageNamesMigrateAtomically),
        ("未完成事务阻止应用数据迁移", PendingTransactionBlocksApplicationDataMigration),
        ("自定义备份目录会校验迁移并持久化设置", CustomBackupLocationMigratesAndPersists),
        ("备份目录拒绝与游戏目录重叠", BackupLocationRejectsUnsafeTarget),
        ("自定义缓存目录会校验迁移并保留现有内容", CustomCacheLocationMigratesContent),
        ("缓存目录拒绝与切换暂存区重叠", CacheLocationRejectsStagingOverlap),
        ("缓存迁移后旧清单自动解析到新位置", MigratedCacheManifestUsesCustomLocation),
        ("旧游戏版本缓存可独立清理", ObsoleteCacheVersionsCanBeCleaned),
        ("只读旧缓存与残留清单均可清理", ReadOnlyCacheAndOrphanManifestCanBeCleaned),
        ("切走当前服时自动保存新增热更新缓存", SwitchCapturesNewHotUpdateFiles),
        ("未手动初始化时切换自动保存当前服缓存", SwitchAutoCapturesUninitializedCurrentCache),
        ("游戏升级后自动建立新版本来源缓存", UpgradeAutoCreatesNewVersionCache),
        ("软件内导入并原子替换三服差异包", PackageArchiveImportsAtomically),
        ("差异包导入可恢复替换中断残留", PackageArchiveRecoversInterruptedReplacement),
        ("差异包导入拒绝跨目录路径", PackageArchiveRejectsTraversal),
        ("差异包导入拒绝错误游戏版本", PackageArchiveRejectsWrongVersion),
        ("首次运行自动安装内置B服组件", BundledBilibiliPackageInstallsOnFirstRun),
        ("升级后复用B服组件并完成四向切换且保留版本", () => BilibiliOverlayReuseAfterUpgrade("3.2.0", false)),
        ("后续版本自动复用B服组件并更新INI版本", () => BilibiliOverlayReuseAfterUpgrade("3.3.0", true)),
        ("损坏的内置B服组件会自动修复", BundledBilibiliPackageRepairsCorruption),
        ("内置B服组件拒绝异常归档路径", BundledBilibiliPackageRejectsUnsafeArchive),
        ("主题偏好可持久化且损坏设置安全回退", ThemePreferencePersistsAndFallsBack),
        ("界面与启动设置整体持久化", UiSettingsPersistAsOneDocument),
        ("日志保留只清理超过设定天数的文件", ExpiredLogsFollowRetention),
        ("检测 .zzzswitch 根目录缺失", StorageRootMissingDetected),
        ("修复仅重建标准目录结构", StorageLayoutRepair),
        ("单个服务器差异包缺失不误报为目录结构损坏", MissingProfilePackageIsNotStructuralDamage),
        ("在线模式不因本地差异包缺失阻止检查", MissingPackagesAreAggregated),
        ("事务备份保留实体文件并可恢复", PhysicalBackupRemainsRestorable),
        ("旧版内容对象备份仍可恢复", LegacyContentObjectBackupRemainsRestorable),
        ("启动时恢复未完成的普通文件事务", PendingFileTransactionRecovery),
        ("暂存前保存事务且中断后只清理对应暂存", PendingStagingRecovery),
        ("暂存清理被占用时保留事务并可重试", LockedStagingRecoveryCanRetry),
        ("启动时共同恢复 Blocks 与普通文件事务", PendingCombinedTransactionRecovery),
        ("已提交事务仅清理遗留日志", CommittedTransactionJournalCleanup),
        ("损坏状态文件被安全忽略", CorruptStateIsSafelyIgnored),
        ("同长度差异文件篡改可被识别", PackageIntegrityRejectsSameLengthTamper),
        ("暂存校验拒绝被篡改的差异文件", EngineRejectsTamperedPackage),
        ("详细检查标记哈希损坏的差异包", InspectionDetectsTamperedPackage),
        ("损坏配置不会中断详细检查", InspectionSurvivesCorruptConfiguration),
        ("结构无效的配置被隔离", StructurallyInvalidConfigurationIsRejected),
        ("损坏切换清单会安全阻止计划", PlannerRejectsCorruptTransition),
        ("损坏 Blocks 清单会安全阻止计划", HotUpdateRejectsCorruptManifest),
        ("备份列表忽略损坏记录", BackupListIgnoresCorruptRecords),
        ("每个游戏安装按来源服只保留最新备份", BackupRotationKeepsLatestPerSourceProfile),
        ("全部游戏安装分别轮换三个来源服备份槽", BackupRotationIsScopedPerGameAndSourceProfile),
        ("状态文件原子写入不遗留临时文件", StateSaveIsAtomic),
        ("字段无效的 Blocks 清单会安全阻止计划", HotUpdateRejectsInvalidManifestFields),
        ("字段无效的快照会被安全忽略", SnapshotWithInvalidFieldsIsIgnored),
        ("重复切换配置会安全阻止计划", PlannerRejectsDuplicateTransition),
        ("字段无效的 Blocks 事务会安全停止恢复", InvalidHotUpdateTransactionStopsRecovery)
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--inspect-live-read-only")
        {
            return InspectLiveReadOnly(args[1], args[2]);
        }

        var passed = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"结果：{passed}/{Tests.Count} 通过");
        return passed == Tests.Count ? 0 : 1;
    }

    private static Task ReadableByteSizes()
    {
        Equal("512 B", ByteSizeFormatter.Format(512));
        Equal("1.5 KiB", ByteSizeFormatter.Format(1536));
        Equal("128 MiB", ByteSizeFormatter.Format(128L * 1024 * 1024));
        Equal("1.25 GiB", ByteSizeFormatter.Format(5L * 1024 * 1024 * 1024 / 4));
        return Task.CompletedTask;
    }

    private static int InspectLiveReadOnly(string gamePath, string configPath)
    {
        // Point state at a deliberately nonexistent temp subdirectory. Inspect() only
        // reads state and never creates it, preserving the read-only contract.
        var dataPath = Path.Combine(Path.GetTempPath(), "ZZZSwitch.ReadOnlyInspection", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dataPath, configPath);
        var service = new InspectionService(
            new ConfigurationRepository(paths),
            new GameDirectoryService(),
            new ProfileDetector(),
            new StateStore(paths),
            new ProcessMonitor());
        var report = service.Inspect(gamePath);
        Console.WriteLine($"游戏目录有效：{report.Game.IsValid}");
        Console.WriteLine($"游戏版本：{report.Game.GameVersion}");
        Console.WriteLine($"识别状态：{report.Detection.Profile}");
        foreach (var package in report.Packages)
        {
            Console.WriteLine($"{package.ProfileId}: available={package.IsAvailable}, files={package.FileCount}");
        }
        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"{issue.Severity}: {issue.Code}: {issue.Message}");
        }
        Console.WriteLine($"应用数据目录被创建：{Directory.Exists(dataPath)}");
        return report.Game.IsValid && report.Detection.Profile == DetectedProfile.Global && !Directory.Exists(dataPath) ? 0 : 1;
    }

    private static Task DetectionExact(string id, DetectedProfile expected)
    {
        using var fixture = new TempFixture();
        var file = Path.Combine(fixture.Game, $"{id}.bin");
        File.WriteAllText(file, id);
        var profiles = ProfileIds.All.Select(profile => new ProfileDefinition
        {
            Id = profile,
            DisplayName = profile,
            PackageDirectoryName = profile,
            KeyFiles = [new FileSignature { Path = $"{profile}.bin", Length = profile.Length }]
        }).ToArray();
        var result = new ProfileDetector().Detect(fixture.Game, profiles);
        Equal(expected, result.Profile);
        return Task.CompletedTask;
    }

    private static async Task VersionedManifestDetectionAndSwitch()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.2.0");
        string[] keys = ["GameAssembly.dll", "ZenlessZoneZero.exe"];
        var profiles = ProfileIds.All.Select(id => new ProfileDefinition
        {
            Id = id, DisplayName = id, PackageDirectoryName = id, GameVersion = "3.1.0",
            KeyFiles = keys.Select(path => new FileSignature { Path = path, Length = 3 })
                .Concat(id == ProfileIds.Bilibili ? [new FileSignature { Path = "bilibili-sdk.bin", Length = 3 }] : []).ToList()
        }).ToArray();
        foreach (var profile in profiles)
            File.WriteAllText(Path.Combine(fixture.Config, "profiles", profile.Id + ".json"), JsonSerializer.Serialize(profile, JsonSupport.Options));
        var stateStore = new StateStore(fixture.Paths);
        stateStore.Save(new AppState { GamePath = fixture.Game, GameVersion = "3.1.0", CurrentProfile = ProfileIds.Global });
        var stateBefore = File.ReadAllText(fixture.Paths.StateFile);
        var cache = new ManifestCache(fixture.Paths.ManifestCacheRoot, JsonSupport.Options);
        var old = Snapshot(SophonRegion.OS, "3.1.0");
        await cache.SaveAsync(old);
        var oldCachePath = cache.GetPath(SophonRegion.OS, "3.1.0", "game");
        var oldBytes = File.ReadAllBytes(oldCachePath);
        var inspector = new InspectionService(new ConfigurationRepository(fixture.Paths), new GameDirectoryService(),
            new ProfileDetector(fixture.Paths), stateStore, new FakeProcessMonitor(), inspectLocalPackages: false);

        var report = inspector.Inspect(fixture.Game);
        True(report.Detection.NeedsManifestRefresh && report.Detection.Profile == DetectedProfile.Unknown,
            "新版本即使碰巧匹配旧文件长度，也不能使用旧签名或旧状态。");
        True(report.Detection.StateHint is null && report.Issues.Any(issue => issue.Code == "game.version.changed"),
            "跨版本状态应被忽略并解释版本变化。");

        await cache.SaveAsync(Snapshot(SophonRegion.OS, "3.2.0"));
        True(inspector.Inspect(fixture.Game).Detection.NeedsManifestRefresh, "只下载一服清单时不能放行。");
        await cache.SaveAsync(Snapshot(SophonRegion.CN, "3.2.0"));
        foreach (var key in keys) File.WriteAllText(Path.Combine(fixture.Game, key), Content(SophonRegion.OS, key));
        report = inspector.Inspect(fixture.Game);
        Equal(DetectedProfile.Global, report.Detection.Profile);
        True(!report.Detection.NeedsManifestRefresh, "有效缓存应支持离线重新识别。");
        Equal(stateBefore, File.ReadAllText(fixture.Paths.StateFile));

        // Exercise the actual planner/engine with the new-version files in both directions.
        var files = new PhysicalFileOperations();
        var planner = new SwitchPlanner(new ConfigurationRepository(fixture.Paths), new GameDirectoryService(),
            new FakeProcessMonitor(), files, fixture.Paths, new ProfileSnapshotService(fixture.Paths, files));
        foreach (var target in new[] { SophonRegion.CN, SophonRegion.OS })
        {
            var targetId = target == SophonRegion.OS ? ProfileIds.Global : ProfileIds.CnOfficial;
            var sourceId = target == SophonRegion.OS ? ProfileIds.CnOfficial : ProfileIds.Global;
            var entries = keys.Select(key =>
            {
                File.WriteAllText(Path.Combine(fixture.Package, key), Content(target, key));
                return new ReplaceFileEntry { Source = key, Target = key,
                    Length = new FileInfo(Path.Combine(fixture.Package, key)).Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Package, key)))) };
            }).ToList();
            var materialization = new OnlineDifferenceMaterialization
            {
                PackageRoot = fixture.Package, PackageDirectory = fixture.Package,
                Manifest = new TransitionManifest { SourceProfile = sourceId, TargetProfile = targetId,
                    GameVersion = "3.2.0", ReplaceFiles = entries }
            };
            var plan = planner.CreateOnlinePlan(fixture.Game, materialization);
            True(plan.Issues.All(issue => issue.Severity != IssueSeverity.Error), "新版在线计划不应受旧清单版本限制。");
            var switched = await fixture.CreateEngine().ExecuteAsync(plan);
            True(switched.Success, switched.Error ?? "新版切换失败。");
            Equal(target == SophonRegion.OS ? DetectedProfile.Global : DetectedProfile.CnOfficial,
                inspector.Inspect(fixture.Game).Detection.Profile);
        }

        File.WriteAllText(Path.Combine(fixture.Game, keys[0]), Content(SophonRegion.CN, keys[0]));
        Equal(DetectedProfile.Mixed, inspector.Inspect(fixture.Game).Detection.Profile);
        File.WriteAllText(Path.Combine(fixture.Game, keys[0]), new string('x', Content(SophonRegion.OS, keys[0]).Length));
        Equal(DetectedProfile.Unknown, inspector.Inspect(fixture.Game).Detection.Profile);
        foreach (var key in keys) File.WriteAllText(Path.Combine(fixture.Game, key), Content(SophonRegion.CN, key));
        File.WriteAllText(Path.Combine(fixture.Game, "bilibili-sdk.bin"), "sdk");
        report = inspector.Inspect(fixture.Game);
        Equal(DetectedProfile.Unknown, report.Detection.Profile);
        True(report.Issues.Any(issue => issue.Code == "detection.bilibili.unsupported"), "新版 B 服不得误认作国服。");
        File.Delete(Path.Combine(fixture.Game, "bilibili-sdk.bin"));

        var currentCache = cache.GetPath(SophonRegion.OS, "3.2.0", "game");
        File.WriteAllBytes(currentCache, oldBytes);
        True(inspector.Inspect(fixture.Game).Detection.NeedsManifestRefresh, "缓存路径正确而内容版本错误时应拒绝。");
        File.WriteAllText(currentCache, "{broken");
        True(inspector.Inspect(fixture.Game).Detection.NeedsManifestRefresh, "损坏缓存应可刷新恢复。");
        True(oldBytes.SequenceEqual(File.ReadAllBytes(oldCachePath)), "新版本检测和切换不得清理旧版 Manifest。");

        static string Content(SophonRegion region, string key) => $"{region}:{key}:new";
        ManifestSnapshot Snapshot(SophonRegion region, string version) => new(
            SophonRegionConfig.Game, region, version, "game", $"{region}-{version}", DateTimeOffset.UtcNow,
            keys.Select(key => new ManifestEntry(key, Encoding.UTF8.GetByteCount(Content(region, key)),
                Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Content(region, key)))))).ToArray());
    }

    private static async Task ConfirmedDetectionReuse()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.2.0");
        const string key = "GameAssembly.dll";
        var stateStore = new StateStore(fixture.Paths);
        stateStore.Save(new AppState { GamePath = fixture.Game, GameVersion = "3.2.0", CurrentProfile = ProfileIds.CnOfficial });
        foreach (var id in ProfileIds.All)
        {
            var profile = new ProfileDefinition
            {
                Id = id, DisplayName = id, PackageDirectoryName = id, GameVersion = "3.1.0",
                KeyFiles = [new FileSignature { Path = key, Length = 3 }]
            };
            File.WriteAllText(Path.Combine(fixture.Config, "profiles", id + ".json"), JsonSerializer.Serialize(profile, JsonSupport.Options));
        }
        var cache = new ManifestCache(fixture.Paths.ManifestCacheRoot, JsonSupport.Options);
        foreach (var region in new[] { SophonRegion.OS, SophonRegion.CN })
        {
            var content = region == SophonRegion.OS ? "global" : "china!";
            await cache.SaveAsync(new ManifestSnapshot(SophonRegionConfig.Game, region, "3.2.0", "game", region.ToString(),
                DateTimeOffset.UtcNow, [new ManifestEntry(key, content.Length,
                    Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))))]));
        }
        var gameKey = Path.Combine(fixture.Game, key);
        File.WriteAllText(gameKey, "global");
        InspectionService Inspector() => new(new ConfigurationRepository(fixture.Paths), new GameDirectoryService(),
            new ProfileDetector(fixture.Paths), new StateStore(fixture.Paths), new FakeProcessMonitor(),
            inspectLocalPackages: false, reuseConfirmedDetection: true);
        var inspector = Inspector();
        var first = inspector.Inspect(fixture.Game).Detection;
        True(!first.ReusedConfirmedState && first.Profile == DetectedProfile.Global, "普通旧状态不能代替首次识别。");
        var persisted = File.ReadAllText(fixture.Paths.StateFile);

        // Exclusive locks prove that a fresh service (a new launch) does not read
        // either the large key-file contents or the regional Manifest contents.
        using (File.Open(gameKey, FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(cache.GetPath(SophonRegion.OS, "3.2.0", "game"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(cache.GetPath(SophonRegion.CN, "3.2.0", "game"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var cached = Inspector().Inspect(fixture.Game).Detection;
            True(cached.ReusedConfirmedState && cached.Profile == DetectedProfile.Global, "启动时应只读取属性并复用已确认的状态。");
        }
        Equal(persisted, File.ReadAllText(fixture.Paths.StateFile));
        True(!inspector.Inspect(fixture.Game, readOnly: true).Detection.ReusedConfirmedState, "显式只读检查应完整识别。");
        Equal(persisted, File.ReadAllText(fixture.Paths.StateFile));
        inspector.InvalidateDetection();
        True(!inspector.Inspect(fixture.Game).Detection.ReusedConfirmedState, "操作异常后必须重检，即使文件属性未变化。");
        True(inspector.Inspect(fixture.Game).Detection.ReusedConfirmedState, "异常重检成功后应恢复复用。");

        File.WriteAllText(gameKey, "broken");
        File.SetLastWriteTimeUtc(gameKey, DateTime.UtcNow.AddSeconds(1));
        var changed = inspector.Inspect(fixture.Game).Detection;
        True(!changed.ReusedConfirmedState && changed.Profile == DetectedProfile.Unknown, "同长度文件变化必须重新校验内容。");
        True(!inspector.Inspect(fixture.Game).Detection.ReusedConfirmedState, "未知结果不得缓存放行。");
        File.Delete(gameKey);
        True(!inspector.Inspect(fixture.Game).Game.IsValid, "缺失关键文件必须报告异常。");
        File.WriteAllText(gameKey, "global");
        inspector.Inspect(fixture.Game);

        File.WriteAllText(Path.Combine(fixture.Game, "config.ini"), "[General]\ngame_version=3.2.0\nchannel=14\n");
        True(!inspector.Inspect(fixture.Game).Detection.ReusedConfirmedState, "外部修改渠道配置应触发重检。");
        var profilePath = Path.Combine(fixture.Config, "profiles", "global.json");
        File.WriteAllText(profilePath, File.ReadAllText(profilePath).Replace("\"length\": 3", "\"length\": 4"));
        True(!inspector.Inspect(fixture.Game).Detection.ReusedConfirmedState, "识别配置改变应触发重检。");

        var manifestPath = cache.GetPath(SophonRegion.OS, "3.2.0", "game");
        var manifestContent = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, "{broken");
        var invalidManifest = inspector.Inspect(fixture.Game).Detection;
        True(!invalidManifest.ReusedConfirmedState && invalidManifest.NeedsManifestRefresh, "损坏 Manifest 不得继续复用旧结果。");
        File.WriteAllText(manifestPath, manifestContent);
        inspector.Inspect(fixture.Game);

        File.WriteAllText(fixture.Paths.HotUpdateJournalFile, "{}");
        True(!inspector.Inspect(fixture.Game).Detection.ReusedConfirmedState, "未完成事务应禁用缓存。");
        File.Delete(fixture.Paths.HotUpdateJournalFile);
        inspector.Inspect(fixture.Game);
        var state = stateStore.Load()!;
        state.ClientDetection = new ClientDetectionSnapshot { Fingerprint = state.ClientDetection!.Fingerprint, Result = null };
        stateStore.Save(state);
        True(!Inspector().Inspect(fixture.Game).Detection.ReusedConfirmedState, "不完整识别缓存应安全重建。");

        File.WriteAllText(Path.Combine(fixture.Game, "version_info"), "3.3.0");
        var upgraded = Inspector().Inspect(fixture.Game).Detection;
        True(!upgraded.ReusedConfirmedState && upgraded.NeedsManifestRefresh, "版本升级必须重新读取匹配清单。");
    }

    private static Task RestoreRejectsDifferentGameVersion()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.2.0");
        var state = new StateStore(fixture.Paths);
        var files = new PhysicalFileOperations();
        var policy = new LegacyRestoreSafetyPolicy(state, new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor()));
        var restore = new RestoreService(new BackupService(files, fixture.Paths), new FakeProcessMonitor(), files, state, policy);
        var result = restore.Restore(Path.Combine(fixture.Root, "nonexistent-backup"), fixture.CreateBackupRecord(), fixture.Game);
        True(!result.Success && result.Error?.Contains("不能跨版本恢复", StringComparison.Ordinal) == true,
            "应在读取备份或修改游戏前拒绝旧版本恢复。");
        Equal("dll", File.ReadAllText(Path.Combine(fixture.Game, "GameAssembly.dll")));
        return Task.CompletedTask;
    }

    private static Task DetectionUnknown()
    {
        using var fixture = new TempFixture();
        var profile = TestProfile(ProfileIds.Global, "missing.bin", 1);
        Equal(DetectedProfile.Unknown, new ProfileDetector().Detect(fixture.Game, [profile]).Profile);
        return Task.CompletedTask;
    }

    private static Task DetectionMixed()
    {
        using var fixture = new TempFixture();
        File.WriteAllText(Path.Combine(fixture.Game, "shared.bin"), "x");
        var profiles = new[]
        {
            TestProfile(ProfileIds.Global, "shared.bin", 1),
            TestProfile(ProfileIds.CnOfficial, "shared.bin", 1)
        };
        Equal(DetectedProfile.Mixed, new ProfileDetector().Detect(fixture.Game, profiles).Profile);
        return Task.CompletedTask;
    }

    private static Task BilibiliOverlayDetection()
    {
        using var fixture = new TempFixture();
        File.WriteAllText(Path.Combine(fixture.Game, "cn-core.bin"), "cn");
        File.WriteAllText(Path.Combine(fixture.Game, "bilibili-sdk.bin"), "sdk");
        var profiles = new[]
        {
            TestProfile(ProfileIds.CnOfficial, "cn-core.bin", 2),
            new ProfileDefinition
            {
                Id = ProfileIds.Bilibili,
                DisplayName = "bilibili",
                PackageDirectoryName = "bilibili",
                KeyFiles =
                [
                    new FileSignature { Path = "cn-core.bin", Length = 2 },
                    new FileSignature { Path = "bilibili-sdk.bin", Length = 3 }
                ]
            }
        };

        Equal(DetectedProfile.Bilibili, new ProfileDetector().Detect(fixture.Game, profiles).Profile);
        return Task.CompletedTask;
    }

    private static Task BilibiliResourceProfileMapping()
    {
        Equal(ProfileIds.CnOfficial, ProfileIds.ToResourceProfile(ProfileIds.Bilibili));
        True(!ProfileIds.HotUpdateProfiles.Contains(ProfileIds.Bilibili, StringComparer.Ordinal),
            "B服不得创建第三套 Blocks 缓存。");
        return Task.CompletedTask;
    }

    private static Task PathRejected(string relative)
    {
        using var fixture = new TempFixture();
        True(!PathSafety.TryResolveUnderRoot(fixture.Game, relative, out _, out _), "危险路径未被拒绝。");
        return Task.CompletedTask;
    }

    private static Task PathAccepted()
    {
        using var fixture = new TempFixture();
        True(PathSafety.TryResolveUnderRoot(fixture.Game, @"Data\safe.bin", out var resolved, out _), "合法路径被拒绝。");
        True(resolved.StartsWith(Path.GetFullPath(fixture.Game), StringComparison.OrdinalIgnoreCase), "解析结果越界。");
        return Task.CompletedTask;
    }

    private static Task GameDirectoryValidation()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        var result = new GameDirectoryService().Validate(fixture.Game);
        True(result.IsValid, "模拟游戏目录应有效。");
        Equal("3.0.0", result.GameVersion);
        return Task.CompletedTask;
    }

    private static Task GameDirectoryDiscovery()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        var invalidPath = Path.Combine(fixture.Root, "not-a-game");
        Directory.CreateDirectory(invalidPath);
        var locator = new FakeGameInstallLocator(
        [
            new(invalidPath, "无效测试路径"),
            new(fixture.Game, "启动器记录"),
            new(fixture.Game + Path.DirectorySeparatorChar, "重复路径")
        ]);
        var service = new GameDirectoryDiscoveryService(
            new GameDirectoryService(),
            locator);

        var results = service.Discover([invalidPath]);

        Equal(1, results.Count);
        Equal(Path.GetFullPath(fixture.Game), results[0].Path);
        return Task.CompletedTask;
    }

    private static Task ManifestCountsAreInformational()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        var package = GameStorageLayout.GetPackageDirectory(fixture.Game, "3.0.0", "target");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "source.bin"), "new");
        File.WriteAllText(Path.Combine(fixture.Game, "target.bin"), "old");
        fixture.WriteConfiguration(new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 999,
            ExpectedDeleteCount = 999,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = "source.bin",
                    Target = "target.bin",
                    Length = 3,
                    Sha256 = Sha256Text("new")
                }
            ]
        });
        var files = new PhysicalFileOperations();
        var planner = new SwitchPlanner(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new FakeProcessMonitor(),
            files,
            fixture.Paths,
            new ProfileSnapshotService(fixture.Paths, files));

        var plan = planner.CreatePlan(fixture.Game, ProfileIds.Global, ProfileIds.CnOfficial);

        True(plan.CanExecute, string.Join(" | ", plan.Issues.Select(issue => issue.Message)));
        Equal(1, plan.Manifest.PlannedReplaceCount);
        Equal(0, plan.Manifest.PlannedDeleteCount);
        return Task.CompletedTask;
    }

    private static Task PlannerFailure(string mode)
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers(mode == "version" ? "3.1.0" : "3.0.0");
        var package = GameStorageLayout.GetPackageDirectory(fixture.Game, "3.0.0", "target");
        Directory.CreateDirectory(package);
        if (mode != "source")
        {
            File.WriteAllText(Path.Combine(package, "source.bin"), "new");
        }

        File.WriteAllText(Path.Combine(fixture.Game, "target.bin"), "old");
        var deletePath = Path.Combine(fixture.Game, "delete.bin");
        if (mode != "delete")
        {
            File.WriteAllText(deletePath, "delete");
        }

        var expectedSourceHash = Sha256Text("new");
        fixture.WriteConfiguration(new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 1,
            ExpectedDeleteCount = 1,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = "source.bin",
                    Target = "target.bin",
                    Length = 3,
                    Sha256 = expectedSourceHash
                }
            ],
            DeleteFiles = [new DeleteFileEntry { Target = "delete.bin" }]
        });

        var monitor = mode == "process" ? new FakeProcessMonitor("ZenlessZoneZero.exe (PID 1)") : new FakeProcessMonitor();
        var paths = new AppPaths(fixture.Data, fixture.Config);
        var files = new PhysicalFileOperations();
        FileStream? locked = null;
        if (mode == "lock")
        {
            locked = new FileStream(Path.Combine(fixture.Game, "target.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        try
        {
            var snapshots = new ProfileSnapshotService(paths, files);
            if (mode == "transaction")
            {
                new FileTransactionJournalStore(paths).Save(new FileTransactionJournal
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    CreatedAt = DateTimeOffset.Now,
                    BackupPath = Path.Combine(paths.BackupsRoot, "pending"),
                    GamePath = fixture.Game,
                    GameVersion = "3.0.0",
                    SourceProfile = ProfileIds.Global,
                    TargetProfile = ProfileIds.CnOfficial,
                    Stage = FileTransactionStage.Prepared
                });
            }

            var planner = new SwitchPlanner(new ConfigurationRepository(paths), new GameDirectoryService(), monitor, files, paths, snapshots);
            var plan = planner.CreatePlan(fixture.Game, ProfileIds.Global, ProfileIds.CnOfficial);
            True(!plan.CanExecute, $"{mode} 场景不应通过预检。");
            if (mode == "transaction")
            {
                True(plan.Issues.Any(x => x.Code == "transaction.file.pending"), "预检应明确报告未完成文件事务。");
            }
        }
        finally
        {
            locked?.Dispose();
        }

        return Task.CompletedTask;
    }

    private static Task PlannerDiskSpace(bool separateBackupDrive)
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        var source = new string('n', 20);
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), source);
        File.WriteAllText(Path.Combine(fixture.Game, "a.bin"), new string('o', 50));
        var gameDrive = Path.GetPathRoot(fixture.Game)!;
        var backupDrive = separateBackupDrive
            ? (string.Equals(gameDrive, @"Z:\", StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\")
            : gameDrive;
        if (separateBackupDrive)
        {
            // Only the isolated settings file is written. No directory on the
            // hypothetical backup drive is accessed; its free space is injected.
            Directory.CreateDirectory(fixture.Data);
            File.WriteAllText(fixture.Paths.BackupLocationFile, JsonSerializer.Serialize(
                new BackupLocationSettings { BackupRootPath = Path.Combine(backupDrive, "ZZZSwitch.Tests", "Backups") },
                JsonSupport.Options));
        }

        var paths = new AppPaths(fixture.Data, fixture.Config);
        var files = new PhysicalFileOperations();
        const long margin = 64L * 1024 * 1024;
        var cases = separateBackupDrive
            ? new[] { (20L, 50L, true), (20L, 49L, false), (19L, 50L, false) }
            : new[] { (70L, 0L, true), (69L, 0L, false) };
        foreach (var (gameFree, backupFree, allowed) in cases)
        {
            var queriedDrives = new List<string>();
            var planner = new SwitchPlanner(
                new ConfigurationRepository(paths), new GameDirectoryService(), new FakeProcessMonitor(),
                files, paths, new ProfileSnapshotService(paths, files),
                getAvailableFreeSpace: root =>
                {
                    queriedDrives.Add(root);
                    return margin + (string.Equals(root, gameDrive, StringComparison.OrdinalIgnoreCase)
                        ? gameFree : backupFree);
                });
            var plan = planner.CreateOnlinePlan(fixture.Game,
                Materialization(fixture.Package, ProfileIds.Global, ProfileIds.CnOfficial, "a.bin", source));
            Equal(allowed, plan.CanExecute);
            Equal(separateBackupDrive ? 2 : 1, queriedDrives.Count);
            True(queriedDrives.Contains(gameDrive, StringComparer.OrdinalIgnoreCase), "应检查游戏盘。");
            True(queriedDrives.Contains(backupDrive, StringComparer.OrdinalIgnoreCase), "应检查实际备份盘。");
            Equal(allowed ? 0 : 1, plan.Issues.Count(issue => issue.Code == "disk.space"));
            True(plan.Issues.All(issue => issue.Code != "disk.check.failed"), "空间模拟不应触碰不存在的磁盘。");
            if (separateBackupDrive && backupFree < 50)
            {
                True(plan.Issues.Any(issue => issue.Code == "disk.space" && issue.Message.Contains("备份磁盘")),
                    "备份盘不足应明确归因于备份盘。");
            }
        }

        return Task.CompletedTask;
    }

    private static Task SnapshotFiltersFiles()
    {
        using var fixture = new TempFixture();
        var persistent = Path.Combine(fixture.Game, "ZenlessZoneZero_Data", "Persistent");
        var streaming = Path.Combine(fixture.Game, "ZenlessZoneZero_Data", "StreamingAssets");
        File.WriteAllText(Path.Combine(persistent, "data_version_persist"), "version");
        File.WriteAllText(Path.Combine(persistent, "not_cache.bin"), "ignore");
        File.WriteAllText(Path.Combine(streaming, "res_revision"), "revision");
        var blocks = Path.Combine(streaming, "Blocks");
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "nested_version"), "ignore nested");

        var snapshots = new ProfileSnapshotService(fixture.Paths, new PhysicalFileOperations());
        var snapshot = snapshots.Capture(ProfileIds.Global, "3.0.0", fixture.Game);
        True(snapshot.Files.Any(x => x.RelativePath.EndsWith("data_version_persist", StringComparison.Ordinal)), "Persistent version 文件未收集。");
        True(snapshot.Files.Any(x => x.RelativePath.EndsWith("res_revision", StringComparison.Ordinal)), "StreamingAssets revision 文件未收集。");
        True(snapshot.Files.All(x => !x.RelativePath.Contains("not_cache", StringComparison.Ordinal)), "无关文件不应进入快照。");
        True(snapshot.Files.All(x => !x.RelativePath.Contains("Blocks", StringComparison.Ordinal)), "子目录文件不应进入快照。");
        return Task.CompletedTask;
    }

    private static Task OnlineDifferenceSelection()
    {
        const string hashA = "00112233445566778899AABBCCDDEEFF";
        const string hashB = "FFEEDDCCBBAA99887766554433221100";
        ManifestEntry Entry(string path, long size, string hash) => new(path, size, hash);
        var source = new ManifestSnapshot(
            SophonRegionConfig.Game,
            SophonRegion.OS,
            "3.1.0",
            "game",
            "os-manifest",
            DateTimeOffset.UnixEpoch,
            [
                Entry("GameAssembly.dll", 10, hashA),
                Entry("obsolete.exe", 7, hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\APMConfig.json", 2, hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\data_version", 3, hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Blocks\a.blk", 100, hashA),
                Entry(@"ZenlessZoneZero_Data\Persistent\Blocks\runtime.blk", 5, hashA),
                Entry("mystery.bin", 4, hashA)
            ]);
        var target = new ManifestSnapshot(
            SophonRegionConfig.Game,
            SophonRegion.CN,
            "3.1.0",
            "game",
            "cn-manifest",
            DateTimeOffset.UnixEpoch,
            [
                Entry("GameAssembly.dll", 11, hashB),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\APMConfig.json", 2, hashB),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\data_version", 3, hashB),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Blocks\a.blk", 120, hashB),
                Entry(@"ZenlessZoneZero_Data\Persistent\Blocks\runtime.blk", 6, hashB),
                Entry("mystery.bin", 4, hashB)
            ]);
        var diff = new ManifestDiffEngine().Compare(source, target);
        var category = new ManifestCategory(
            "game", "game", "cn-manifest", "https://example.test/manifest", "",
            "https://example.test/chunks", "");
        var plan = OnlineDifferenceService.BuildPlan(
            ProfileIds.Global, ProfileIds.CnOfficial, diff, target, category);

        Equal(3, plan.DownloadFiles.Count);
        True(plan.DownloadFiles.Any(item => item.Path == "GameAssembly.dll"), "基础客户端文件应进入下载范围。");
        True(plan.DownloadFiles.All(item => !item.Path.Contains(@"\Blocks\", StringComparison.Ordinal)),
            "Streaming/Persistent Blocks 都不应进入基础差异下载范围。");
        Equal(0, plan.DeleteFiles.Count);
        Equal(1, plan.ExcludedDeletionReviewCount);
        Equal(1, plan.ExcludedStreamingBlocksCount);
        Equal(120L, plan.ExcludedStreamingBlocksBytes);
        True(plan.ExcludedObservationCount >= 1, "未知文件应保留为待观察项。 ");
        return Task.CompletedTask;
    }

    private static async Task OnlineDifferenceReusesLocalFilesAndCapturesReversePackage()
    {
        using var fixture = new TempFixture();
        const string version = "3.1.0";
        const string targetContent = "target-local";
        const string sourceContent = "source-local";
        File.WriteAllText(Path.Combine(fixture.Game, "target.bin"), targetContent);
        File.WriteAllText(Path.Combine(fixture.Game, "source.bin"), sourceContent);

        static ManifestEntry Entry(string path, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            return new ManifestEntry(
                path,
                bytes.LongLength,
                Convert.ToHexString(MD5.HashData(bytes)));
        }

        var plan = new OnlineDifferencePlan
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = version,
            SourceRegion = SophonRegion.OS,
            TargetRegion = SophonRegion.CN,
            TargetManifestId = "cn-manifest",
            TargetCategory = new ManifestCategory(
                "game", "game", "cn-manifest", "https://example.test/manifest", "",
                "https://example.test/chunks", ""),
            DownloadFiles = [Entry("target.bin", targetContent)],
            DeleteFiles = [],
            LocalGamePath = fixture.Game,
            LocalSourceCapture = new OnlineLocalSourceCapturePlan
            {
                SourceProfile = ProfileIds.CnOfficial,
                TargetProfile = ProfileIds.Global,
                TargetManifestId = "os-manifest",
                Files = [Entry("source.bin", sourceContent)]
            }
        };
        var service = new OnlineDifferenceService(
            fixture.Paths,
            () => new RejectingSophonTransport());

        var result = await service.MaterializeAsync(plan);

        Equal(0, result.DownloadedFiles);
        Equal(1, result.ReusedFiles);
        Equal(1, result.PreservedSourceFiles);
        True(result.SourcePackageReady, "来源服文件完整时应生成可直接使用的反向差异包。");
        Equal(targetContent, File.ReadAllText(Path.Combine(result.PackageDirectory, "target.bin")));

        var catalog = new OnlineDifferencePackageCatalog(fixture.Paths);
        True(catalog.TryGetReadyMaterialization(
                ProfileIds.CnOfficial,
                ProfileIds.Global,
                version,
                out var reverse) && reverse is not null,
            "反向切换应直接命中由当前客户端保存的来源服差异包。");
        Equal(sourceContent, File.ReadAllText(Path.Combine(reverse!.PackageDirectory, "source.bin")));
    }

    private static Task OnlinePlannerDoesNotReadExistingPackage()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        var onlineRoot = Path.Combine(fixture.Paths.OnlineDifferenceFilesRoot, "test", "content");
        Directory.CreateDirectory(onlineRoot);
        File.WriteAllText(Path.Combine(onlineRoot, "target.bin"), "new");
        File.WriteAllText(Path.Combine(fixture.Game, "target.bin"), "old");
        var manifest = new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 1,
            ExpectedDeleteCount = 0,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = "target.bin",
                    Target = "target.bin",
                    Length = 3,
                    Sha256 = Sha256Text("new")
                }
            ]
        };
        var materialization = new OnlineDifferenceMaterialization
        {
            PackageRoot = onlineRoot,
            PackageDirectory = onlineRoot,
            Manifest = manifest,
            DownloadedFiles = 1
        };
        var files = new PhysicalFileOperations();
        var activeBlocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(activeBlocks);
        File.WriteAllText(Path.Combine(activeBlocks, "current.blk"), "current-cache");
        var hotUpdateCaches = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        var planner = new SwitchPlanner(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new FakeProcessMonitor(),
            files,
            fixture.Paths,
            new ProfileSnapshotService(fixture.Paths, files),
            hotUpdateCaches);

        var plan = planner.CreateOnlinePlan(fixture.Game, materialization);

        True(plan.CanExecute, string.Join(" | ", plan.Issues.Select(item => item.Message)));
        Equal(Path.GetFullPath(onlineRoot), Path.GetFullPath(plan.PackageDirectory));
        True(!Directory.Exists(GameStorageLayout.GetPackageRoot(fixture.Game, "3.0.0")),
            "在线计划不应创建或读取 .zzzswitch 差异包目录。");
        Equal("Sophon 在线差异缓存（已通过完整性校验）", plan.FileSourceDescription);
        True(plan.HotUpdateTransition is not null &&
             plan.Issues.Any(issue => issue.Code == "hot-cache.source.auto-capture"),
            "在线计划应在无手动初始化清单时自动准备保存当前服 Blocks。");
        return Task.CompletedTask;
    }

    private static async Task GlobalToBilibiliCompositeUsesOnlineBase()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        WriteCompositeProfiles(fixture);
        var direct = new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.Bilibili,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 3,
            ExpectedDeleteCount = 0,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = "base.bin",
                    Target = "base.bin",
                    SourcePackageDirectoryName = ProfileIds.CnOfficial,
                    Length = 15,
                    Sha256 = Sha256Text("unused-cn-base")
                },
                new ReplaceFileEntry
                {
                    Source = "overlay.bin",
                    Target = "overlay.bin",
                    Length = 9,
                    Sha256 = Sha256Text("b-overlay")
                }
            ],
            IniPatches = [ProfilePatch("bilibili")]
        };
        File.WriteAllText(
            Path.Combine(fixture.Config, "transitions", "global-to-bilibili.json"),
            JsonSerializer.Serialize(direct, JsonSupport.Options));
        var bilibiliRoot = GameStorageLayout.GetPackageDirectory(
            fixture.Game,
            "3.0.0",
            ProfileIds.Bilibili);
        Directory.CreateDirectory(bilibiliRoot);
        File.WriteAllText(Path.Combine(bilibiliRoot, "overlay.bin"), "b-overlay");
        var onlineRoot = Path.Combine(fixture.Root, "online-global-to-cn");
        Directory.CreateDirectory(onlineRoot);
        File.WriteAllText(Path.Combine(onlineRoot, "base.bin"), "online-cn-base");
        File.WriteAllText(Path.Combine(fixture.Game, "base.bin"), "global-old");
        File.WriteAllText(Path.Combine(fixture.Game, "overlay.bin"), "overlay-old");
        File.WriteAllText(Path.Combine(fixture.Game, "config.ini"), "[General]\ncps=global\n");
        var materialization = Materialization(
            onlineRoot,
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "base.bin",
            "online-cn-base");
        var planner = CreatePlanner(fixture);

        var plan = planner.CreateBilibiliCompositePlan(
            fixture.Game,
            ProfileIds.Global,
            ProfileIds.Bilibili,
            materialization);

        True(plan.CanExecute, string.Join(" | ", plan.Issues.Select(item => item.Message)));
        Equal(2, plan.Manifest.ReplaceFiles.Count);
        True(!Directory.Exists(GameStorageLayout.GetPackageDirectory(
                fixture.Game,
                "3.0.0",
                ProfileIds.CnOfficial)),
            "干净环境不应要求本地国服固定包目录。");
        var result = await fixture.CreateEngine().ExecuteAsync(plan);
        True(result.Success, result.Error ?? "国际服到 B 服组合切换应成功。");
        Equal("online-cn-base", File.ReadAllText(Path.Combine(fixture.Game, "base.bin")));
        Equal("b-overlay", File.ReadAllText(Path.Combine(fixture.Game, "overlay.bin")));
        True(File.ReadAllText(Path.Combine(fixture.Game, "config.ini")).Contains("cps=bilibili", StringComparison.Ordinal),
            "最终配置应使用 B 服渠道值。");
    }

    private static async Task BilibiliToGlobalCompositeUsesOnlineBase()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        WriteCompositeProfiles(fixture);
        var direct = new TransitionManifest
        {
            SourceProfile = ProfileIds.Bilibili,
            TargetProfile = ProfileIds.Global,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 2,
            ExpectedDeleteCount = 0,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = "base.bin",
                    Target = "base.bin",
                    Length = 18,
                    Sha256 = Sha256Text("unused-global-base")
                }
            ],
            IniPatches = [ProfilePatch("global")],
            OptionalDeleteFiles = [new DeleteFileEntry { Target = "overlay.bin" }]
        };
        File.WriteAllText(
            Path.Combine(fixture.Config, "transitions", "bilibili-to-global.json"),
            JsonSerializer.Serialize(direct, JsonSupport.Options));
        var onlineRoot = Path.Combine(fixture.Root, "online-cn-to-global");
        Directory.CreateDirectory(onlineRoot);
        File.WriteAllText(Path.Combine(onlineRoot, "base.bin"), "online-global-base");
        File.WriteAllText(Path.Combine(fixture.Game, "base.bin"), "cn-old");
        File.WriteAllText(Path.Combine(fixture.Game, "overlay.bin"), "b-overlay");
        File.WriteAllText(Path.Combine(fixture.Game, "config.ini"), "[General]\ncps=bilibili\n");
        var materialization = Materialization(
            onlineRoot,
            ProfileIds.CnOfficial,
            ProfileIds.Global,
            "base.bin",
            "online-global-base");
        var planner = CreatePlanner(fixture);

        var plan = planner.CreateBilibiliCompositePlan(
            fixture.Game,
            ProfileIds.Bilibili,
            ProfileIds.Global,
            materialization);

        True(plan.CanExecute, string.Join(" | ", plan.Issues.Select(item => item.Message)));
        True(!Directory.Exists(GameStorageLayout.GetPackageDirectory(
                fixture.Game,
                "3.0.0",
                ProfileIds.Global)),
            "干净环境不应要求本地国际服固定包目录。");
        var result = await fixture.CreateEngine().ExecuteAsync(plan);
        True(result.Success, result.Error ?? "B 服到国际服组合切换应成功。");
        Equal("online-global-base", File.ReadAllText(Path.Combine(fixture.Game, "base.bin")));
        True(!File.Exists(Path.Combine(fixture.Game, "overlay.bin")), "B 服覆盖层应按清单移除。");
        True(File.ReadAllText(Path.Combine(fixture.Game, "config.ini")).Contains("cps=global", StringComparison.Ordinal),
            "最终配置应使用国际服渠道值。");
    }

    private static SwitchPlanner CreatePlanner(TempFixture fixture)
    {
        var files = new PhysicalFileOperations();
        return new SwitchPlanner(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new FakeProcessMonitor(),
            files,
            fixture.Paths,
            new ProfileSnapshotService(fixture.Paths, files));
    }

    private static OnlineDifferenceMaterialization Materialization(
        string root,
        string sourceProfile,
        string targetProfile,
        string path,
        string content) => new()
    {
        PackageRoot = root,
        PackageDirectory = root,
        Manifest = new TransitionManifest
        {
            SourceProfile = sourceProfile,
            TargetProfile = targetProfile,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 1,
            ExpectedDeleteCount = 0,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = path,
                    Target = path,
                    Length = content.Length,
                    Sha256 = Sha256Text(content)
                }
            ]
        },
        DownloadedFiles = 1
    };

    private static IniFilePatch ProfilePatch(string cps) => new()
    {
        Target = "config.ini",
        Section = "General",
        Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cps"] = cps
        }
    };

    private static void WriteCompositeProfiles(TempFixture fixture)
    {
        foreach (var profile in ProfileIds.All)
        {
            var definition = new ProfileDefinition
            {
                Id = profile,
                DisplayName = profile,
                PackageDirectoryName = profile
            };
            File.WriteAllText(
                Path.Combine(fixture.Config, "profiles", profile + ".json"),
                JsonSerializer.Serialize(definition, JsonSupport.Options));
        }
    }

    private static Task OnlineDifferencePackageCatalogRecognizesReadyPackage()
    {
        using var fixture = new TempFixture();
        var workspace = Path.Combine(
            fixture.Paths.OnlineDifferenceFilesRoot,
            "3.1.0",
            ProfileIds.CnOfficial,
            "manifest-test");
        var content = Path.Combine(workspace, "content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "payload.bin"), "ready");
        var manifest = new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.1.0",
            ExpectedReplaceCount = 1,
            ExpectedDeleteCount = 0,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = "payload.bin",
                    Target = "payload.bin",
                    Length = 5,
                    Sha256 = Sha256Text("ready")
                }
            ]
        };
        File.WriteAllText(
            Path.Combine(workspace, "transition-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonSupport.Options));

        var catalog = new OnlineDifferencePackageCatalog(fixture.Paths);
        var inventory = catalog.GetInventory();
        var package = inventory.Packages.Single();
        Equal(OnlineDifferencePackageState.Ready, package.State);
        Equal(5L, package.ContentBytes);
        True(catalog.TryGetReadyMaterialization(
                ProfileIds.Global,
                ProfileIds.CnOfficial,
                "3.1.0",
                out var materialization) && materialization is not null,
            "同版本已完成资源应直接生成本地切换材料。");
        True(materialization!.ReusedReadyPackage && materialization.ReusedFiles == 1,
            "本地快速路径应标记为复用完整版本差异包。");
        var preview = catalog.GetPreview(package);
        Equal(1, preview.Files.Count);
        Equal("payload.bin", preview.Files[0].Path);
        Equal("已就绪", preview.Files[0].State);
        catalog.VerifyPackage(package);
        var staleWorkspace = Path.Combine(
            fixture.Paths.OnlineDifferenceFilesRoot,
            "3.1.0",
            ProfileIds.CnOfficial,
            "manifest-stale");
        Directory.CreateDirectory(Path.Combine(staleWorkspace, "chunks"));
        File.WriteAllText(Path.Combine(staleWorkspace, "chunks", "stale.part"), "stale");
        Equal(1, catalog.DeleteSupersededPackages(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.1.0",
            workspace));
        True(!Directory.Exists(staleWorkspace), "更新成功后应清理同版本同方向的旧工作区。");
        File.WriteAllText(Path.Combine(content, "payload.bin"), "wrong");
        var rejected = false;
        try
        {
            catalog.VerifyPackage(package);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        True(rejected, "差异包管理的手动校验必须识别同长度 SHA-256 损坏。");
        True(catalog.TryGetReadyMaterialization(
                ProfileIds.Global,
                ProfileIds.CnOfficial,
                "3.1.0",
                out _),
            "就绪查询只负责确认文件存在和长度；实际切换计划会按清单校验内容哈希。");
        return Task.CompletedTask;
    }

    private static Task OnlineDifferencePackageCatalogKeepsIncompletePackage()
    {
        using var fixture = new TempFixture();
        var workspace = Path.Combine(
            fixture.Paths.OnlineDifferenceFilesRoot,
            "3.2.0",
            ProfileIds.Global,
            "manifest-incomplete");
        var chunks = Path.Combine(workspace, "chunks", "aa");
        Directory.CreateDirectory(chunks);
        File.WriteAllText(Path.Combine(chunks, "checkpoint.plain"), "chunk");

        var catalog = new OnlineDifferencePackageCatalog(fixture.Paths);
        var package = catalog.GetInventory().Packages.Single();
        Equal(OnlineDifferencePackageState.Incomplete, package.State);
        Equal(1, package.CheckpointCount);
        True(!catalog.TryGetReadyMaterialization(
                ProfileIds.CnOfficial,
                ProfileIds.Global,
                "3.2.0",
                out _),
            "未完成分块不能进入本地快速切换路径。");
        catalog.DeletePackage(package.WorkspacePath);
        True(!Directory.Exists(workspace), "版本资源管理应只删除明确选择的工作区。");
        return Task.CompletedTask;
    }

    private static async Task ManifestBrowserBuildsExtensibleIndex()
    {
        using var fixture = new TempFixture();
        const string hashA = "00112233445566778899AABBCCDDEEFF";
        const string hashB = "FFEEDDCCBBAA99887766554433221100";
        static ManifestEntry Entry(string path, string hash) => new(path, 10, hash);

        var global = new ManifestSnapshot(
            SophonRegionConfig.Game,
            SophonRegion.OS,
            "3.1.0",
            "10037",
            "manifest-os",
            DateTimeOffset.UtcNow,
            [
                Entry("GameAssembly.dll", hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Video\HD\MainStory\plot.usm", hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Audio\Windows\Full\Patch.pck", hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Blocks\data.blk", hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\data_version", hashA)
            ]);
        var cn = new ManifestSnapshot(
            SophonRegionConfig.Game,
            SophonRegion.CN,
            "3.1.0",
            "10047",
            "manifest-cn",
            DateTimeOffset.UtcNow,
            [
                Entry("GameAssembly.dll", hashB),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Video\HD\MainStory\plot.usm", hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Audio\Windows\Full\Patch.pck", hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\Blocks\data.blk", hashA),
                Entry(@"ZenlessZoneZero_Data\StreamingAssets\data_version", hashB)
            ]);
        var cache = new ManifestCache(fixture.Paths.ManifestCacheRoot, JsonSupport.Options);
        await cache.SaveAsync(global);
        await cache.SaveAsync(cn);

        var browser = await new OnlineDifferenceService(fixture.Paths).GetManifestBrowserAsync("3.1.0");
        Equal(5, browser.GlobalToCn.Files.Count);
        Equal(2, browser.GlobalToCn.Files.Count(file => file.IsClientDifference));
        Equal(1, browser.GlobalToCn.Files.Count(file => file.IsStoryMedia));
        Equal(1, browser.GlobalToCn.Files.Count(file => file.IsAudio));
        Equal(1, browser.GlobalToCn.Files.Count(file => file.IsStreamingBlocks));
        Equal(1, browser.GlobalToCn.Files.Count(file => file.IsStateMetadata));
        Equal("manifest-cn", browser.GlobalToCn.TargetManifest.ManifestId);
        Equal("manifest-os", browser.CnToGlobal.TargetManifest.ManifestId);
    }

    private static Task InvalidSnapshotIsIgnored()
    {
        using var fixture = new TempFixture();
        var snapshots = new ProfileSnapshotService(fixture.Paths, new PhysicalFileOperations());
        var snapshot = snapshots.Capture(ProfileIds.Global, "3.0.0", fixture.Game);
        var first = snapshot.Files[0];
        File.AppendAllText(Path.Combine(snapshot.SnapshotPath, "files", first.RelativePath), "corrupt");
        True(snapshots.FindLatestValid(ProfileIds.Global, "3.0.0", fixture.Game) is null,
            "长度异常的快照不应可用。");
        return Task.CompletedTask;
    }

    private static Task HotUpdateCacheInitialization()
    {
        using var fixture = new TempFixture();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "cn.blk"), "cn-block");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());

        var manifest = service.InitializeActive(ProfileIds.CnOfficial, "3.0.0", fixture.Game);
        Equal(1, manifest.FileCount);
        var status = service.GetStatus(ProfileIds.CnOfficial, "3.0.0", fixture.Game, ProfileIds.CnOfficial);
        True(status.IsInitialized && status.IsAvailable && status.IsActive, "当前国服缓存应初始化并处于活动状态。");
        return Task.CompletedTask;
    }

    private static Task HotUpdateManifestsAreGameScoped()
    {
        using var fixture = new TempFixture();
        var secondGame = Path.Combine(fixture.Root, "Second Game");
        WriteBlocks(fixture.Game, "first.blk", "first");
        WriteBlocks(secondGame, "second.blk", "second-install");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());

        service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);
        service.InitializeActive(ProfileIds.Global, "3.0.0", secondGame);

        var first = service.GetStatus(ProfileIds.Global, "3.0.0", fixture.Game, ProfileIds.Global);
        var second = service.GetStatus(ProfileIds.Global, "3.0.0", secondGame, ProfileIds.Global);
        True(first.IsAvailable && second.IsAvailable, "两份安装的活动缓存都应保持可用。");
        var manifests = Directory.EnumerateFiles(
                fixture.Paths.HotUpdateManifestsRoot,
                "cache.json",
                SearchOption.AllDirectories)
            .ToArray();
        Equal(2, manifests.Length);
        True(
            manifests.Any(x => x.Contains(GameStorageLayout.GetGameIdentity(fixture.Game), StringComparison.OrdinalIgnoreCase)),
            "第一份安装应有独立清单目录。");
        True(
            manifests.Any(x => x.Contains(GameStorageLayout.GetGameIdentity(secondGame), StringComparison.OrdinalIgnoreCase)),
            "第二份安装应有独立清单目录。");
        return Task.CompletedTask;
    }

    private static Task LegacyHotUpdateManifestMigrates()
    {
        using var fixture = new TempFixture();
        WriteBlocks(fixture.Game, "global.blk", "global");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);
        var scopedPath = ScopedManifestPath(fixture, fixture.Game, ProfileIds.Global, "3.0.0");
        var legacyPath = LegacyManifestPath(fixture, ProfileIds.Global, "3.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.Move(scopedPath, legacyPath);

        var status = service.GetStatus(ProfileIds.Global, "3.0.0", fixture.Game, ProfileIds.Global);
        True(status.IsAvailable, status.Detail ?? "迁移后的缓存应可用。");
        True(File.Exists(scopedPath), "旧清单应迁移到游戏身份目录。");
        True(!File.Exists(legacyPath), "迁移成功后应清理旧清单文件。");
        return Task.CompletedTask;
    }

    private static Task ForeignGameDoesNotClaimLegacyManifest()
    {
        using var fixture = new TempFixture();
        var secondGame = Path.Combine(fixture.Root, "Second Game");
        WriteBlocks(fixture.Game, "first.blk", "first");
        WriteBlocks(secondGame, "second.blk", "second");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);
        var scopedPath = ScopedManifestPath(fixture, fixture.Game, ProfileIds.Global, "3.0.0");
        var legacyPath = LegacyManifestPath(fixture, ProfileIds.Global, "3.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.Move(scopedPath, legacyPath);

        var foreign = service.GetStatus(ProfileIds.Global, "3.0.0", secondGame, ProfileIds.Global);
        True(!foreign.IsInitialized && !foreign.IsAvailable, "另一份安装不应认领不属于自己的旧清单。");
        True(File.Exists(legacyPath), "身份不匹配时旧清单必须保留，供原安装迁移。");
        True(!File.Exists(ScopedManifestPath(fixture, secondGame, ProfileIds.Global, "3.0.0")), "不得为另一份安装生成错误清单。");

        var original = service.GetStatus(ProfileIds.Global, "3.0.0", fixture.Game, ProfileIds.Global);
        True(original.IsAvailable, "原安装仍应能迁移并使用旧清单。");
        return Task.CompletedTask;
    }

    private static Task HotUpdateCacheSwapAndRollback()
    {
        using var fixture = new TempFixture();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "cn.blk"), "cn-block");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        service.InitializeActive(ProfileIds.CnOfficial, "3.0.0", fixture.Game);

        var firstIssues = new List<ValidationIssue>();
        var firstPlan = service.CreateTransitionPlan(
            ProfileIds.CnOfficial,
            ProfileIds.Global,
            "3.0.0",
            fixture.Game,
            firstIssues);
        True(firstPlan is not null && firstPlan.Mode == HotUpdateTransitionMode.InitializeTarget, "国际服未初始化时应进入初始化模式。");
        var firstTransaction = service.BeginTransition(firstPlan!);
        True(Directory.Exists(blocks) && !Directory.EnumerateFiles(blocks).Any(), "首次初始化目标服应创建空 Blocks。");
        service.Commit(firstTransaction);

        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);

        var secondIssues = new List<ValidationIssue>();
        var secondPlan = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.0.0",
            fixture.Game,
            secondIssues);
        True(secondPlan is not null && secondPlan.Mode == HotUpdateTransitionMode.Swap, "双服初始化后应使用快速交换模式。");
        var secondTransaction = service.BeginTransition(secondPlan!);
        True(File.Exists(Path.Combine(blocks, "cn.blk")), "交换后活动目录应为国服 Blocks。");
        True(service.Rollback(secondTransaction), "Blocks 交换应可回滚。");
        True(File.Exists(Path.Combine(blocks, "global.blk")), "回滚后活动目录应恢复国际服 Blocks。");

        var cnStatus = service.GetStatus(ProfileIds.CnOfficial, "3.0.0", fixture.Game, ProfileIds.Global);
        True(cnStatus.IsAvailable && !cnStatus.IsActive, "回滚后国服缓存应完整存放在仓库。");
        return Task.CompletedTask;
    }

    private static Task HotUpdateVerifiedCopySwapAndRollback()
    {
        using var fixture = new TempFixture();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "cn.blk"), "cn-block");
        var service = new HotUpdateCacheService(
            fixture.Paths,
            new FakeProcessMonitor(),
            forceVerifiedCopyTransfers: true);
        service.InitializeActive(ProfileIds.CnOfficial, "3.0.0", fixture.Game);

        var initializeGlobal = service.CreateTransitionPlan(
            ProfileIds.CnOfficial,
            ProfileIds.Global,
            "3.0.0",
            fixture.Game,
            [])!;
        service.Commit(service.BeginTransition(initializeGlobal));
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);

        var swap = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.0.0",
            fixture.Game,
            [])!;
        var transaction = service.BeginTransition(swap);
        True(File.Exists(Path.Combine(blocks, "cn.blk")), "复制模式交换后应激活国服 Blocks。");
        True(service.Rollback(transaction), "复制模式 Blocks 交换应可完整回滚。");
        True(File.Exists(Path.Combine(blocks, "global.blk")), "复制模式回滚后应恢复国际服 Blocks。");
        True(
            !Directory.EnumerateDirectories(fixture.Root, "*.moving-*", SearchOption.AllDirectories).Any(),
            "成功或回滚后不应遗留跨磁盘暂存目录。");
        return Task.CompletedTask;
    }

    private static Task HotUpdateRecoveryBeforeFirstMove()
    {
        using var fixture = new TempFixture();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        var manifest = service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);
        fixture.Paths.EnsureWritableDirectories();
        var journal = new HotUpdateTransaction
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            Mode = HotUpdateTransitionMode.InitializeTarget,
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            GamePath = Path.GetFullPath(fixture.Game),
            ActiveBlocksPath = blocks,
            SourceStoredBlocksPath = manifest.StoredBlocksPath
        };
        File.WriteAllText(
            fixture.Paths.HotUpdateJournalFile,
            JsonSerializer.Serialize(journal, JsonSupport.Options));

        var detail = service.RecoverPending(null);
        True(!string.IsNullOrWhiteSpace(detail), "应识别并处理首次移动前遗留的事务日志。");
        True(File.Exists(Path.Combine(blocks, "global.blk")), "恢复不得删除尚未移动的活动 Blocks。");
        True(!File.Exists(fixture.Paths.HotUpdateJournalFile), "确认无需回滚后应清理事务日志。");
        return Task.CompletedTask;
    }

    private static Task LostTargetCacheUsesInitializationMode()
    {
        using var fixture = new TempFixture();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "cn.blk"), "cn-block");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        service.InitializeActive(ProfileIds.CnOfficial, "3.0.0", fixture.Game);

        var firstPlan = service.CreateTransitionPlan(
            ProfileIds.CnOfficial,
            ProfileIds.Global,
            "3.0.0",
            fixture.Game,
            [])!;
        service.Commit(service.BeginTransition(firstPlan));
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);

        var cnStored = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.0.0",
            ProfileIds.CnOfficial);
        Directory.Delete(cnStored, true);

        var issues = new List<ValidationIssue>();
        var recoveryPlan = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.0.0",
            fixture.Game,
            issues);

        True(
            recoveryPlan is not null &&
            recoveryPlan.Mode == HotUpdateTransitionMode.InitializeTarget,
            "目标缓存仓库丢失后应进入一次性重建模式。");
        Equal(
            1,
            issues.Count(x => x.Code == "hot-cache.target.lost" &&
                              x.Severity == IssueSeverity.Warning));
        True(
            issues.All(x => x.Severity != IssueSeverity.Error),
            "已确认丢失的目标缓存不应永久阻止重建。");
        return Task.CompletedTask;
    }

    private static async Task HotUpdateEngineFailureRollback()
    {
        using var fixture = new TempFixture();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "cn.blk"), "cn-block");
        var hotUpdate = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        hotUpdate.InitializeActive(ProfileIds.CnOfficial, "3.0.0", fixture.Game);

        var initIssues = new List<ValidationIssue>();
        var initPlan = hotUpdate.CreateTransitionPlan(
            ProfileIds.CnOfficial,
            ProfileIds.Global,
            "3.0.0",
            fixture.Game,
            initIssues)!;
        hotUpdate.Commit(hotUpdate.BeginTransition(initPlan));
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        hotUpdate.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);

        var issues = new List<ValidationIssue>();
        var hotPlan = hotUpdate.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.0.0",
            fixture.Game,
            issues)!;
        File.WriteAllText(Path.Combine(fixture.Game, "a.bin"), "old");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "new");
        var basePlan = fixture.CreatePlan([Entry("a.bin")], []);
        var plan = new SwitchPlan
        {
            OperationId = basePlan.OperationId,
            GamePath = basePlan.GamePath,
            PackageRoot = basePlan.PackageRoot,
            PackageDirectory = basePlan.PackageDirectory,
            Manifest = basePlan.Manifest,
            BackupPath = basePlan.BackupPath,
            HotUpdateTransition = hotPlan
        };
        var target = Path.Combine(fixture.Game, "a.bin");
        var faulty = new FaultingFileOperations(new PhysicalFileOperations(), copyTargetToFailOnce: target);
        var backups = new BackupService(faulty, fixture.Paths);
        var snapshots = new ProfileSnapshotService(fixture.Paths, faulty);
        var engine = new SwitchEngine(
            faulty,
            fixture.Paths,
            backups,
            new StateStore(fixture.Paths),
            new OperationLogger(fixture.Paths),
            snapshots,
            hotUpdate);

        var result = await engine.ExecuteAsync(plan);
        True(!result.Success && result.RolledBack, "替换失败时应同时回滚 Blocks 和普通文件。");
        Equal("old", File.ReadAllText(target));
        True(File.Exists(Path.Combine(blocks, "global.blk")), "活动 Blocks 未恢复为国际服。");
        var cnStatus = hotUpdate.GetStatus(ProfileIds.CnOfficial, "3.0.0", fixture.Game, ProfileIds.Global);
        True(cnStatus.IsAvailable, "国服缓存应恢复到仓库。");
    }

    private static async Task SwitchRestoresTargetSnapshot()
    {
        using var fixture = new TempFixture();
        var files = new PhysicalFileOperations();
        var snapshots = new ProfileSnapshotService(fixture.Paths, files);
        var cacheFile = Path.Combine(fixture.Game, "ZenlessZoneZero_Data", "Persistent", "data_version_persist");
        File.WriteAllText(cacheFile, "cn-cache");
        var targetSnapshot = snapshots.Capture(ProfileIds.CnOfficial, "3.0.0", fixture.Game);
        File.WriteAllText(cacheFile, "global-cache");
        File.WriteAllText(Path.Combine(fixture.Game, "a.bin"), "old");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "new");
        var plan = fixture.CreatePlan([Entry("a.bin")], [], targetSnapshot: targetSnapshot);

        var result = await fixture.CreateEngine(files).ExecuteAsync(plan);
        True(result.Success, result.Error ?? "带缓存快照的切换应成功。");
        Equal("cn-cache", File.ReadAllText(cacheFile));
        Equal(targetSnapshot.Files.Count, result.SuccessfulCacheRestore);
    }

    private static async Task SnapshotOverridesPackageCacheFile()
    {
        using var fixture = new TempFixture();
        var files = new PhysicalFileOperations();
        var snapshots = new ProfileSnapshotService(fixture.Paths, files);
        var relative = @"ZenlessZoneZero_Data\Persistent\audio_revision";
        var target = Path.Combine(fixture.Game, relative);
        var source = Path.Combine(fixture.Package, relative);

        File.WriteAllText(target, "cn-hot-update");
        var targetSnapshot = snapshots.Capture(ProfileIds.CnOfficial, "3.0.0", fixture.Game);
        File.WriteAllText(target, "global-hot-update");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "cn-package-base");
        var entry = new ReplaceFileEntry
        {
            Source = relative,
            Target = relative,
            Length = new FileInfo(source).Length,
            Sha256 = Sha256Text("cn-package-base")
        };
        var plan = fixture.CreatePlan([entry], [], targetSnapshot: targetSnapshot);

        var result = await fixture.CreateEngine(files).ExecuteAsync(plan);

        True(result.Success, result.Error ?? "快照覆盖差异包同路径文件后切换应成功。");
        Equal("cn-hot-update", File.ReadAllText(target));
        Equal(targetSnapshot.Files.Count, result.SuccessfulCacheRestore);
    }

    private static async Task SwitchSuccess()
    {
        using var fixture = new TempFixture();
        var plan = fixture.CreatePlan(
            [Entry("a.bin")],
            [new DeleteFileEntry { Target = "remove.bin" }]);
        File.WriteAllText(Path.Combine(fixture.Game, "a.bin"), "old");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "new-content");
        File.WriteAllText(Path.Combine(fixture.Game, "remove.bin"), "remove");

        var result = await fixture.CreateEngine().ExecuteAsync(plan);
        True(result.Success, result.Error ?? "切换应成功。");
        Equal("new-content", File.ReadAllText(Path.Combine(fixture.Game, "a.bin")));
        True(!File.Exists(Path.Combine(fixture.Game, "remove.bin")), "删除目标仍存在。");
        Equal(ProfileIds.CnOfficial, new StateStore(fixture.Paths).Load()?.CurrentProfile);
        Equal(1, result.SuccessfulReplace);
        Equal(1, result.SuccessfulDelete);
        var stagingRoot = GameStorageLayout.GetStagingRoot(fixture.Game);
        True(!Directory.Exists(stagingRoot) ||
             !Directory.EnumerateFileSystemEntries(stagingRoot).Any(),
            "切换成功后不应遗留游戏同盘暂存内容。");
        var logPath = Directory.GetFiles(fixture.Paths.LogsRoot, "*.jsonl").Single();
        var log = JsonSerializer.Deserialize<OperationLogEntry>(
                      File.ReadLines(logPath).Single(),
                      JsonSupport.Options)
                  ?? throw new InvalidDataException("切换性能日志无法解析。");
        Equal(11L, log.StagedBytes);
        Equal(9L, log.BackupBytes);
        True(log.StageDurationsMilliseconds.ContainsKey("stageSources") &&
             log.StageDurationsMilliseconds.ContainsKey("createBackup") &&
             log.StageDurationsMilliseconds.ContainsKey("applyFiles") &&
             log.StageDurationsMilliseconds.ContainsKey("finalValidation"),
             "切换日志缺少分阶段耗时。");
    }

    private static async Task UnchangedTargetsAreSkipped()
    {
        using var fixture = new TempFixture();
        var unchanged = new ReplaceFileEntry
        {
            Source = "unchanged.bin",
            Target = "unchanged.bin",
            Length = 7,
            Sha256 = Sha256Text("current")
        };
        var changed = new ReplaceFileEntry
        {
            Source = "changed.bin",
            Target = "changed.bin",
            Length = 3,
            Sha256 = Sha256Text("new")
        };
        File.WriteAllText(Path.Combine(fixture.Game, unchanged.Target), "current");
        File.WriteAllText(Path.Combine(fixture.Package, unchanged.Source), "current");
        File.WriteAllText(Path.Combine(fixture.Game, changed.Target), "old-value");
        File.WriteAllText(Path.Combine(fixture.Package, changed.Source), "new");
        var iniTarget = Path.Combine(fixture.Game, "config.ini");
        File.WriteAllText(iniTarget, "[General]\r\nchannel=14\r\n");
        var optionalMissing = new DeleteFileEntry { Target = "missing-overlay.bin" };
        var manifest = new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            ReplaceFiles = [unchanged, changed],
            IniPatches =
            [
                new IniFilePatch
                {
                    Target = "config.ini",
                    Section = "General",
                    Values = new() { ["channel"] = "14" }
                }
            ],
            OptionalDeleteFiles = [optionalMissing]
        };
        var plan = fixture.CreatePlan([unchanged, changed], [], [optionalMissing], manifest);

        var result = await fixture.CreateEngine().ExecuteAsync(plan);

        True(result.Success, result.Error ?? "仅处理实际变化文件的切换应成功。");
        Equal(1, result.PlannedReplace);
        Equal(1, result.SuccessfulReplace);
        Equal("current", File.ReadAllText(Path.Combine(fixture.Game, unchanged.Target)));
        Equal("new", File.ReadAllText(Path.Combine(fixture.Game, changed.Target)));
        True(!File.Exists(Path.Combine(plan.BackupPath, "files", unchanged.Target)),
            "内容已经满足目标清单的文件不应进入事务备份。");
        True(!File.Exists(Path.Combine(plan.BackupPath, "files", "config.ini")),
            "已经满足目标值的 INI 不应进入事务备份。");
        Equal("old-value", File.ReadAllText(Path.Combine(plan.BackupPath, "files", changed.Target)));
        var backup = new BackupService(new PhysicalFileOperations(), fixture.Paths).LoadRecord(plan.BackupPath);
        True(!backup.OriginallyMissingFiles.Contains(optionalMissing.Target, StringComparer.OrdinalIgnoreCase),
            "不存在的可选删除文件不应写入本次事务记录。");
        var logPath = Directory.GetFiles(fixture.Paths.LogsRoot, "*.jsonl").Single();
        var log = JsonSerializer.Deserialize<OperationLogEntry>(
                      File.ReadLines(logPath).Single(),
                      JsonSupport.Options)
                  ?? throw new InvalidDataException("切换性能日志无法解析。");
        Equal(3L, log.StagedBytes);
        Equal(9L, log.BackupBytes);
        True(log.StageDurationsMilliseconds.ContainsKey("detectChanges"),
            "切换日志应记录实际变更检测耗时。");
    }

    private static async Task CrossPackageSourceReuse()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        var packageRoot = GameStorageLayout.GetPackageRoot(fixture.Game, "3.0.0");
        var sharedPackage = Path.Combine(packageRoot, "source");
        var targetPackage = Path.Combine(packageRoot, "target");
        Directory.CreateDirectory(sharedPackage);
        Directory.CreateDirectory(targetPackage);
        File.WriteAllText(Path.Combine(sharedPackage, "shared.bin"), "shared-content");
        File.WriteAllText(Path.Combine(fixture.Game, "shared.bin"), "old");
        fixture.WriteConfiguration(new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 1,
            ReplaceFiles =
            [
                new ReplaceFileEntry
                {
                    Source = "shared.bin",
                    Target = "shared.bin",
                    SourcePackageDirectoryName = "source",
                    Length = 14,
                    Sha256 = Sha256Text("shared-content")
                }
            ]
        });

        var files = new PhysicalFileOperations();
        var snapshots = new ProfileSnapshotService(fixture.Paths, files);
        var planner = new SwitchPlanner(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new FakeProcessMonitor(),
            files,
            fixture.Paths,
            snapshots);
        var plan = planner.CreatePlan(fixture.Game, ProfileIds.Global, ProfileIds.CnOfficial);
        True(plan.CanExecute, string.Join(" | ", plan.Issues.Select(x => x.Message)));

        var result = await new SwitchEngine(
            files,
            fixture.Paths,
            new BackupService(files, fixture.Paths),
            new StateStore(fixture.Paths),
            new OperationLogger(fixture.Paths),
            snapshots).ExecuteAsync(plan);
        True(result.Success, result.Error ?? "跨包源文件切换应成功。");
        Equal("shared-content", File.ReadAllText(Path.Combine(fixture.Game, "shared.bin")));
    }

    private static Task IniPatchPreservesUnrelatedSettings()
    {
        using var fixture = new TempFixture();
        var path = Path.Combine(fixture.Game, "config.ini");
        var original = "[General]\r\ngame_version=3.1.0\r\nchannel=1\r\nplugin_test=3.1.0\r\n\r\n[Other]\r\nkeep=yes\r\n";
        File.WriteAllText(path, original, new UTF8Encoding(false));
        var patch = new IniFilePatch
        {
            Target = "config.ini",
            Section = "General",
            Values = new()
            {
                ["cps"] = "zzz_bilibili_pc",
                ["channel"] = "14",
                ["sub_channel"] = "0"
            }
        };

        var editor = new IniFileEditor();
        editor.Apply(path, patch);
        var result = File.ReadAllText(path);
        True(editor.Matches(path, patch), "修改后的 INI 键值未通过复核。");
        True(result.Contains("game_version=3.1.0", StringComparison.Ordinal), "游戏版本配置被误删。");
        True(result.Contains("plugin_test=3.1.0", StringComparison.Ordinal), "插件配置被误删。");
        True(result.Contains("[Other]\r\nkeep=yes", StringComparison.Ordinal), "其他段落被误改。");
        return Task.CompletedTask;
    }

    private static async Task IniPatchRollback()
    {
        using var fixture = new TempFixture();
        var configPath = Path.Combine(fixture.Game, "config.ini");
        var overlayPath = Path.Combine(fixture.Game, "bilibili-overlay.bin");
        const string original = "[General]\r\ngame_version=3.1.0\r\ncps=zzz_oversea_gw_pc\r\nchannel=1\r\nsub_channel=0\r\n";
        File.WriteAllText(configPath, original, new UTF8Encoding(false));
        File.WriteAllText(overlayPath, "overlay");
        var manifest = new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            ExpectedReplaceCount = 1,
            IniPatches =
            [
                new IniFilePatch
                {
                    Target = "config.ini",
                    Section = "General",
                    Values = new() { ["cps"] = "zzz_bilibili_pc", ["channel"] = "14" }
                }
            ],
            OptionalDeleteFiles = [new DeleteFileEntry { Target = "bilibili-overlay.bin" }]
        };
        var plan = fixture.CreatePlan([], [], [new DeleteFileEntry { Target = "bilibili-overlay.bin" }], manifest);
        var faulty = new FaultingFileOperations(
            new PhysicalFileOperations(),
            deleteTargetToFailOnce: overlayPath);

        var result = await fixture.CreateEngine(faulty).ExecuteAsync(plan);
        True(!result.Success && result.RolledBack, "INI 修改后的删除故障应触发完整回滚。");
        Equal(original, File.ReadAllText(configPath));
        Equal("overlay", File.ReadAllText(overlayPath));
    }

    private static async Task SwitchProgressReportsPhases()
    {
        using var fixture = new TempFixture();
        File.WriteAllText(Path.Combine(fixture.Game, "a.bin"), "original");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "replacement");
        var progress = new OperationProgressRecorder();
        var result = await fixture.CreateEngine().ExecuteAsync(fixture.CreatePlan([Entry("a.bin")], []), progress);
        True(result.Success, result.Error ?? "切换应成功。");
        var firstReplacement = progress.FindIndex(item => item.Step == "正在替换目标文件");
        True(firstReplacement > 0 && progress.Take(firstReplacement).All(item => item.IsIndeterminate),
            "检测、暂存、快照和备份阶段不能显示停在零的确定进度。");
        True(progress.Any(item => !item.IsIndeterminate && item.SuccessfulReplace == 1),
            "实际替换后应按已完成文件数推进进度。");
        True(progress.Any(item => item.Step == "正在执行最终数量与文件状态校验" && item.IsIndeterminate),
            "最终校验仍在工作时应显示活动进度。");
        True(!progress[^1].IsIndeterminate, "切换结束后应停止活动进度。");
    }

    private static async Task CopyFailureRollback()
    {
        using var fixture = new TempFixture();
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        var target = Path.Combine(fixture.Game, "a.bin");
        File.WriteAllText(target, "original");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "replacement");
        var faulty = new FaultingFileOperations(new PhysicalFileOperations(), copyTargetToFailOnce: target);
        var progress = new OperationProgressRecorder();
        var result = await fixture.CreateEngine(faulty).ExecuteAsync(plan, progress);
        True(!result.Success && result.RolledBack && !result.GameFilesUnchanged, "复制失败后应报告已恢复，而非未修改游戏。");
        True(progress.Any(item => item.IsRollingBack && item.IsIndeterminate), "回滚时应显示活动进度。");
        Equal("original", File.ReadAllText(target));
    }

    private static async Task IncompleteRollbackIsReported()
    {
        using var fixture = new TempFixture();
        var target = Path.Combine(fixture.Game, "a.bin");
        File.WriteAllText(target, "original");
        File.WriteAllText(Path.Combine(fixture.Game, "b.bin"), "original-b");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "replacement");
        File.WriteAllText(Path.Combine(fixture.Package, "b.bin"), "replacement-b");
        var files = new FaultingFileOperations(new PhysicalFileOperations(),
            copyTargetToFailOnce: Path.Combine(fixture.Game, "b.bin"),
            beforeWrite: path =>
            {
                if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("测试注入：恢复写入失败。");
                }
            });
        var result = await fixture.CreateEngine(files).ExecuteAsync(fixture.CreatePlan([Entry("a.bin"), Entry("b.bin")], []));
        True(!result.Success && !result.RolledBack && !result.GameFilesUnchanged,
            "恢复未完成时不能标记未修改游戏或恢复成功。");
        Equal("replacement", File.ReadAllText(target));
        True(new FileTransactionJournalStore(fixture.Paths).Exists && Directory.Exists(result.BackupPath),
            "未完成恢复的事务和备份应继续保留。");
    }

    private static async Task IncompleteBackupIsRemoved()
    {
        using var fixture = new TempFixture();
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        var target = Path.Combine(fixture.Game, "a.bin");
        var package = Path.Combine(fixture.Package, "a.bin");
        File.WriteAllText(target, "original");
        File.WriteAllText(package, "replacement");
        var faulty = new FaultingFileOperations(
            new PhysicalFileOperations(),
            copyTargetPrefixToFailOnce: Path.Combine(
                plan.BackupPath,
                "files"));

        var result = await fixture.CreateEngine(faulty).ExecuteAsync(plan);
        True(!result.Success && !result.RolledBack, "备份阶段失败时不应触碰游戏文件或伪报回滚。");
        True(result.GameFilesUnchanged, "备份失败应明确报告本次未修改游戏。");
        Equal("original", File.ReadAllText(target));
        True(!Directory.Exists(plan.BackupPath), "不完整事务备份目录应自动清理。");
    }

    private static async Task NewFileRollback()
    {
        using var fixture = new TempFixture();
        var entries = new[] { Entry("new.bin"), Entry("fail.bin") };
        var plan = fixture.CreatePlan(entries, []);
        File.WriteAllText(Path.Combine(fixture.Package, "new.bin"), "new");
        File.WriteAllText(Path.Combine(fixture.Package, "fail.bin"), "fail");
        File.WriteAllText(Path.Combine(fixture.Game, "fail.bin"), "original-fail");
        var faulty = new FaultingFileOperations(new PhysicalFileOperations(), copyTargetToFailOnce: Path.Combine(fixture.Game, "fail.bin"));
        var result = await fixture.CreateEngine(faulty).ExecuteAsync(plan);
        True(!result.Success && result.RolledBack, "应执行成功回滚。");
        True(!File.Exists(Path.Combine(fixture.Game, "new.bin")), "切换时新增的文件未在回滚时删除。");
        Equal("original-fail", File.ReadAllText(Path.Combine(fixture.Game, "fail.bin")));
    }

    private static async Task DeletedFileRollback()
    {
        using var fixture = new TempFixture();
        var required = new DeleteFileEntry { Target = "required.bin" };
        var optional = new DeleteFileEntry { Target = "optional.bin" };
        var plan = fixture.CreatePlan([Entry("a.bin")], [required], [optional]);
        File.WriteAllText(Path.Combine(fixture.Game, "a.bin"), "old");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "new");
        File.WriteAllText(Path.Combine(fixture.Game, "required.bin"), "must-restore");
        File.WriteAllText(Path.Combine(fixture.Game, "optional.bin"), "cause-failure");
        var faulty = new FaultingFileOperations(new PhysicalFileOperations(), deleteTargetToFailOnce: Path.Combine(fixture.Game, "optional.bin"));
        var result = await fixture.CreateEngine(faulty).ExecuteAsync(plan);
        True(!result.Success && result.RolledBack, "删除失败后应回滚。");
        Equal("must-restore", File.ReadAllText(Path.Combine(fixture.Game, "required.bin")));
        Equal("old", File.ReadAllText(Path.Combine(fixture.Game, "a.bin")));
    }

    private static async Task FailedSwitchDoesNotCommitState()
    {
        using var fixture = new TempFixture();
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        File.WriteAllText(Path.Combine(fixture.Game, "a.bin"), "old");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "new");
        var target = Path.Combine(fixture.Game, "a.bin");
        var faulty = new FaultingFileOperations(new PhysicalFileOperations(), copyTargetToFailOnce: target);
        var result = await fixture.CreateEngine(faulty).ExecuteAsync(plan);
        True(!result.Success, "故障注入应导致失败。");
        True(new StateStore(fixture.Paths).Load() is null, "失败操作不应写入目标状态。 ");
    }

    private static async Task SameTargetNoOp()
    {
        using var fixture = new TempFixture();
        var manifest = new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.Global,
            GameVersion = "3.0.0"
        };
        var plan = fixture.CreatePlan([], [], manifest: manifest);
        var result = await fixture.CreateEngine().ExecuteAsync(plan);
        True(result.Success && result.WasNoOp, "相同目标应返回成功的无操作结果。");
        True(!Directory.Exists(fixture.Paths.BackupsRoot), "无操作不应创建备份。");
    }

    private static Task OperationCoordinatorMutualExclusion()
    {
        var coordinator = new OperationCoordinator();
        True(coordinator.TryBegin(out var first) && first is not null, "首次操作应成功取得租约。");
        True(coordinator.IsBusy, "持有租约时协调器应处于忙碌状态。");
        True(!coordinator.TryBegin(out var blocked) && blocked is null, "已有操作时必须拒绝第二个操作。");

        first!.Dispose();
        first.Dispose();
        True(!coordinator.IsBusy, "释放租约后协调器应恢复空闲。");
        True(coordinator.TryBegin(out var next) && next is not null, "释放后应允许后续操作。");
        next!.Dispose();
        return Task.CompletedTask;
    }

    private static Task CrossCoordinatorOperationLock()
    {
        using var fixture = new TempFixture();
        var firstCoordinator = new OperationCoordinator(fixture.Paths);
        var secondCoordinator = new OperationCoordinator(fixture.Paths);

        True(firstCoordinator.TryBegin(out var first) && first is not null, "第一个协调器应取得跨进程锁。");
        True(!secondCoordinator.TryBegin(out var blocked) && blocked is null, "第二个协调器不得同时取得同一写操作锁。");
        True(
            secondCoordinator.LastFailure?.Contains("另一个 ZZZSwitch", StringComparison.Ordinal) == true,
            "锁竞争失败应提供明确原因。");

        first!.Dispose();
        True(secondCoordinator.TryBegin(out var second) && second is not null, "原租约释放后第二个协调器应可取得锁。");
        second!.Dispose();
        return Task.CompletedTask;
    }

    private static Task ApplicationInstanceLockLifecycle()
    {
        using var fixture = new TempFixture();
        True(
            ApplicationInstanceLock.TryAcquire(fixture.Paths, out var first, out var firstError) && first is not null,
            firstError ?? "第一个应用实例应取得生命周期锁。");
        True(
            !ApplicationInstanceLock.TryAcquire(fixture.Paths, out var blocked, out var blockedError) && blocked is null,
            "第二个应用实例不得同时取得生命周期锁。");
        True(blockedError is null, "正常锁竞争不应被误报为权限错误。");

        first!.Dispose();
        first.Dispose();
        True(
            ApplicationInstanceLock.TryAcquire(fixture.Paths, out var next, out var nextError) && next is not null,
            nextError ?? "退出后应能重新取得应用生命周期锁。");
        next!.Dispose();
        return Task.CompletedTask;
    }

    private static Task LegacyRestoreBlockedWhenHotCacheInitialized()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.0.0");
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        var hotUpdate = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        hotUpdate.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);
        new StateStore(fixture.Paths).Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = "3.0.0",
            CurrentProfile = ProfileIds.Global
        });

        var result = new LegacyRestoreSafetyPolicy(new StateStore(fixture.Paths), hotUpdate)
            .Evaluate(fixture.Game, fixture.CreateBackupRecord());
        True(!result.CanRestore && result.Reason?.Contains("Blocks", StringComparison.Ordinal) == true,
            "初始化 Blocks 缓存后必须阻止不包含 Blocks 的旧备份恢复。");
        return Task.CompletedTask;
    }

    private static Task LegacyRestoreAllowedWithoutHotCache()
    {
        using var fixture = new TempFixture();
        var result = new LegacyRestoreSafetyPolicy(
                new StateStore(fixture.Paths),
                new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor()))
            .Evaluate(fixture.Game, fixture.CreateBackupRecord());
        True(result.CanRestore && result.Reason is null, "没有 Blocks 缓存时应允许恢复同一游戏目录的旧备份。");
        return Task.CompletedTask;
    }

    private static Task LegacyRestoreRejectsDifferentGamePath()
    {
        using var fixture = new TempFixture();
        var otherGame = Path.Combine(fixture.Root, "OtherGame");
        Directory.CreateDirectory(otherGame);
        var result = new LegacyRestoreSafetyPolicy(
                new StateStore(fixture.Paths),
                new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor()))
            .Evaluate(otherGame, fixture.CreateBackupRecord());
        True(!result.CanRestore && result.Reason?.Contains("其他游戏目录", StringComparison.Ordinal) == true,
            "备份目录与当前目录不一致时必须拒绝恢复。");
        return Task.CompletedTask;
    }

    private static Task RestoreServiceEnforcesLegacySafety()
    {
        using var fixture = new TempFixture();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        var hotUpdate = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        hotUpdate.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);
        var stateStore = new StateStore(fixture.Paths);
        stateStore.Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = "3.0.0",
            CurrentProfile = ProfileIds.Global
        });
        var files = new PhysicalFileOperations();
        var backups = new BackupService(files, fixture.Paths);
        var policy = new LegacyRestoreSafetyPolicy(stateStore, hotUpdate);
        var restore = new RestoreService(backups, new FakeProcessMonitor(), files, stateStore, policy);

        var result = restore.Restore(
            Path.Combine(fixture.Paths.BackupsRoot, "not-used"),
            fixture.CreateBackupRecord(),
            fixture.Game);
        True(!result.Success && result.Error?.Contains("Blocks", StringComparison.Ordinal) == true,
            "恢复服务必须在接触备份文件前强制执行 Blocks 安全策略。");
        return Task.CompletedTask;
    }

    private static Task RestoreLatestUsesExactStateBackup()
    {
        using var fixture = new TempFixture();
        var files = new PhysicalFileOperations();
        var backups = new BackupService(files, fixture.Paths);
        var stateStore = new StateStore(fixture.Paths);
        var policy = new LegacyRestoreSafetyPolicy(
            stateStore,
            new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor()));
        var restore = new RestoreService(backups, new FakeProcessMonitor(), files, stateStore, policy);
        var start = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var exactPath = Path.Combine(fixture.Paths.BackupsRoot, "exact");
        var unrelatedPath = Path.Combine(fixture.Paths.BackupsRoot, "unrelated-newer");
        var exact = CreateRecord(
            fixture.Game,
            start,
            ProfileIds.Global,
            ProfileIds.CnOfficial);
        var unrelated = CreateRecord(
            fixture.Game,
            start.AddHours(1),
            ProfileIds.Bilibili,
            ProfileIds.Global);
        SaveBackup(exactPath, exact);
        SaveBackup(unrelatedPath, unrelated);
        stateStore.Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = exact.GameVersion,
            CurrentProfile = exact.TargetProfile,
            LastOperationId = exact.OperationId,
            LastBackupPath = exactPath
        });

        Equal(exact.OperationId, restore.FindLatestRecord(fixture.Game)?.OperationId);

        stateStore.Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = exact.GameVersion,
            CurrentProfile = ProfileIds.Global,
            LastOperationId = exact.OperationId,
            LastBackupPath = exactPath
        });
        True(restore.FindLatestRecord(fixture.Game) is null, "当前服与备份目标服不一致时必须拒绝主页恢复。");

        stateStore.Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = exact.GameVersion,
            CurrentProfile = exact.TargetProfile,
            LastOperationId = unrelated.OperationId,
            LastBackupPath = exactPath
        });
        True(restore.FindLatestRecord(fixture.Game) is null, "操作 ID 与备份不一致时必须拒绝主页恢复。");
        return Task.CompletedTask;

        void SaveBackup(string path, BackupRecord record)
        {
            Directory.CreateDirectory(path);
            backups.SaveRecord(path, record);
        }
    }

    private static Task UnifiedStorageLayout()
    {
        using var fixture = new TempFixture();
        var root = GameStorageLayout.GetRoot(fixture.Game);
        Equal(Path.Combine(fixture.Root, ".zzzswitch"), root);
        Equal(
            Path.Combine(root, "packages", "3.0.0", "global"),
            GameStorageLayout.GetPackageDirectory(fixture.Game, "3.0.0", "global"));
        var blocks = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.0.0",
            ProfileIds.CnOfficial);
        True(
            blocks.StartsWith(
                Path.Combine(root, GameStorageLayout.BlocksCacheDirectoryName) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "Blocks 仓库必须位于统一根目录的 blocks-cache 下。");
        True(
            blocks.EndsWith(
                Path.Combine("3.0.0", ProfileIds.CnOfficial, "Blocks"),
                StringComparison.OrdinalIgnoreCase),
            "Blocks 仓库尾部结构不正确。");
        return Task.CompletedTask;
    }

    private static Task CustomCacheLocationMigratesContent()
    {
        using var fixture = new TempFixture();
        var locations = new CacheLocationService(fixture.Paths);
        var oldBlocks = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.1.0",
            ProfileIds.Global);
        Directory.CreateDirectory(oldBlocks);
        File.WriteAllText(Path.Combine(oldBlocks, "cache.bin"), "cache-content");
        var targetRoot = Path.Combine(fixture.Root, "CustomCache");

        var result = locations.ChangeLocation(fixture.Game, targetRoot);
        var newBlocks = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.1.0",
            ProfileIds.Global,
            targetRoot);

        True(result.ContentMoved && result.SourceRemoved, "迁移成功后应删除旧位置的同一游戏缓存。");
        Equal("cache-content", File.ReadAllText(Path.Combine(newBlocks, "cache.bin")));
        Equal(Path.GetFullPath(targetRoot), locations.GetCacheRoot(fixture.Game));
        True(!Directory.Exists(Path.Combine(
            GameStorageLayout.GetCacheRoot(fixture.Game),
            GameStorageLayout.GetGameIdentity(fixture.Game))), "旧游戏缓存根目录不应继续占用空间。");
        return Task.CompletedTask;
    }

    private static Task CustomBackupLocationMigratesAndPersists()
    {
        using var fixture = new TempFixture();
        var backups = new BackupService(new PhysicalFileOperations(), fixture.Paths);
        var target = Path.Combine(fixture.Game, "a.bin");
        File.WriteAllText(target, "before");
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        var record = backups.CreateBackup(plan);
        var backupDirectoryName = Path.GetFileName(plan.BackupPath);
        var defaultRoot = fixture.Paths.DefaultBackupsRoot;
        var customRoot = Path.Combine(fixture.Root, "CustomBackups");
        var locations = new BackupLocationService(fixture.Paths);

        var result = locations.ChangeLocation(customRoot, fixture.Game);

        True(result.ContentMoved, "已有备份应被迁移到自定义目录。");
        True(result.SourceRemoved, "验证迁移成功后应移除旧备份目录。");
        Equal(Path.GetFullPath(customRoot), fixture.Paths.BackupsRoot);
        True(File.Exists(Path.Combine(customRoot, backupDirectoryName, "backup.json")),
            "迁移后的备份记录缺失。");
        True(!Directory.Exists(defaultRoot), "旧备份目录不应继续占用空间。");
        Equal(1, backups.ListBackups().Count);

        var reloadedPaths = new AppPaths(fixture.Data, fixture.Config);
        Equal(Path.GetFullPath(customRoot), reloadedPaths.BackupsRoot);
        File.WriteAllText(target, "after");
        var migratedBackupPath = Path.Combine(customRoot, backupDirectoryName);
        True(new BackupService(new PhysicalFileOperations(), reloadedPaths)
            .Rollback(migratedBackupPath, record, out _),
            "自定义目录中的备份索引应继续解析 .zzzswitch 内容对象。");
        Equal("before", File.ReadAllText(target));

        var restored = new BackupLocationService(reloadedPaths).RestoreDefaultLocation(fixture.Game);
        True(restored.ContentMoved && restored.SourceRemoved, "恢复默认位置时也应校验迁移现有备份。");
        Equal(Path.GetFullPath(defaultRoot), reloadedPaths.BackupsRoot);
        True(File.Exists(Path.Combine(defaultRoot, backupDirectoryName, "backup.json")),
            "恢复默认位置后备份记录缺失。");
        return Task.CompletedTask;
    }

    private static Task BackupLocationRejectsUnsafeTarget()
    {
        using var fixture = new TempFixture();
        var locations = new BackupLocationService(fixture.Paths);
        var unsafeTarget = Path.Combine(fixture.Game, "Backups");
        var rejected = false;

        try
        {
            locations.ChangeLocation(unsafeTarget, fixture.Game);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        True(rejected, "与游戏目录重叠的备份位置必须被拒绝。");
        Equal(Path.GetFullPath(fixture.Paths.DefaultBackupsRoot), fixture.Paths.BackupsRoot);
        True(!File.Exists(fixture.Paths.BackupLocationFile), "拒绝迁移时不应写入设置。");
        return Task.CompletedTask;
    }

    private static Task ObsoleteCacheVersionsCanBeCleaned()
    {
        using var fixture = new TempFixture();
        var locations = new CacheLocationService(fixture.Paths);
        var oldBlocks = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.0.0",
            ProfileIds.CnOfficial);
        var currentBlocks = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.1.0",
            ProfileIds.CnOfficial);
        Directory.CreateDirectory(oldBlocks);
        Directory.CreateDirectory(currentBlocks);
        File.WriteAllText(Path.Combine(oldBlocks, "old.bin"), "old");
        File.WriteAllText(Path.Combine(currentBlocks, "current.bin"), "current");

        var before = locations.GetUsage(fixture.Game, "3.1.0");
        var cleanup = locations.DeleteObsoleteVersions(fixture.Game, "3.1.0");

        Equal(1, before.ObsoleteVersionCount);
        Equal(1, cleanup.RemovedVersionCount);
        True(!Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(oldBlocks)!)!), "旧版本目录应被删除。");
        Equal("current", File.ReadAllText(Path.Combine(currentBlocks, "current.bin")));
        return Task.CompletedTask;
    }

    private static Task ReadOnlyCacheAndOrphanManifestCanBeCleaned()
    {
        using var fixture = new TempFixture();
        var locations = new CacheLocationService(fixture.Paths);
        var oldBlocks = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.0.0",
            ProfileIds.Global);
        Directory.CreateDirectory(oldBlocks);
        var readOnly = Path.Combine(oldBlocks, "readonly.bin");
        File.WriteAllText(readOnly, "old");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);

        var orphanManifest = Path.Combine(
            fixture.Paths.HotUpdateManifestsRoot,
            GameStorageLayout.GetGameIdentity(fixture.Game),
            "2.9.0",
            ProfileIds.Global,
            "cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanManifest)!);
        File.WriteAllText(orphanManifest, "{}");
        var legacyManifest = Path.Combine(
            fixture.Paths.HotUpdateManifestsRoot,
            ProfileIds.Global,
            "2.8.0",
            "cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyManifest)!);
        File.WriteAllText(
            legacyManifest,
            JsonSerializer.Serialize(new HotUpdateCacheManifest
            {
                CacheId = "legacy",
                CreatedAt = DateTimeOffset.Now,
                Profile = ProfileIds.Global,
                GameVersion = "2.8.0",
                GamePath = fixture.Game,
                StoredBlocksPath = oldBlocks,
                FileCount = 1,
                TotalBytes = 3
            }, JsonSupport.Options));
        var snapshots = new ProfileSnapshotService(fixture.Paths, new PhysicalFileOperations());
        var oldSnapshot = snapshots.Capture(ProfileIds.Global, "2.7.0", fixture.Game);
        var legacySnapshotRoot = Path.Combine(
            fixture.Paths.ProfileSnapshotsRoot,
            ProfileIds.Global,
            "2.7.0");
        Directory.CreateDirectory(legacySnapshotRoot);
        var legacySnapshot = Path.Combine(legacySnapshotRoot, oldSnapshot.SnapshotId);
        Directory.Move(oldSnapshot.SnapshotPath, legacySnapshot);

        var usage = locations.GetUsage(fixture.Game, "3.1.0");
        var result = locations.DeleteObsoleteVersions(fixture.Game, "3.1.0");

        Equal(4, usage.ObsoleteVersionCount);
        Equal(4, result.RemovedVersionCount);
        True(!Directory.Exists(Path.Combine(
            fixture.Paths.HotUpdateManifestsRoot,
            GameStorageLayout.GetGameIdentity(fixture.Game),
            "2.9.0")), "仅残留清单的旧版本也应删除。");
        True(!Directory.Exists(Path.Combine(
            locations.GetCacheRoot(fixture.Game),
             GameStorageLayout.GetGameIdentity(fixture.Game),
             "3.0.0")), "含只读文件的旧缓存目录应删除。");
        True(!Directory.Exists(Path.GetDirectoryName(legacyManifest)!),
            "属于当前安装的旧式 Blocks 清单应被清理。");
        True(!Directory.Exists(legacySnapshotRoot),
            "属于当前安装的旧版本快照应随缓存清理。");
        return Task.CompletedTask;
    }

    private static Task SwitchCapturesNewHotUpdateFiles()
    {
        using var fixture = new TempFixture();
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        var active = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(active, "base.blk"), "base");
        service.InitializeActive(ProfileIds.Global, "3.1.0", fixture.Game);
        File.WriteAllText(Path.Combine(active, "hot-update.blk"), "new");

        var issues = new List<ValidationIssue>();
        var plan = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.1.0",
            fixture.Game,
            issues) ?? throw new InvalidOperationException("首次切换计划未生成。");
        var transaction = service.BeginTransition(plan);
        var saved = service.GetStatus(
            ProfileIds.Global,
            "3.1.0",
            fixture.Game,
            ProfileIds.CnOfficial);

        True(saved.IsAvailable && saved.FileCount == 2,
            saved.Detail ?? "切走时应把新增热更新文件纳入来源服缓存清单。");
        True(service.Rollback(transaction), "测试结束时应恢复活动 Blocks。");
        return Task.CompletedTask;
    }

    private static Task SwitchAutoCapturesUninitializedCurrentCache()
    {
        using var fixture = new TempFixture();
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        var active = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(active, "current.blk"), "current-server-cache");
        var issues = new List<ValidationIssue>();

        var plan = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.1.0",
            fixture.Game,
            issues) ?? throw new InvalidOperationException("未生成自动缓存切换计划。");
        var transaction = service.BeginTransition(plan);
        var saved = service.GetStatus(
            ProfileIds.Global,
            "3.1.0",
            fixture.Game,
            ProfileIds.CnOfficial);

        True(issues.Any(issue => issue.Code == "hot-cache.source.auto-capture"),
            "未初始化来源服时应明确进入自动保存模式。");
        True(saved.IsAvailable && saved.FileCount == 1,
            saved.Detail ?? "切换前应自动保存当前服 Blocks。");
        True(service.Rollback(transaction), "测试结束时应恢复自动保存的当前服 Blocks。");
        return Task.CompletedTask;
    }

    private static Task UpgradeAutoCreatesNewVersionCache()
    {
        using var fixture = new TempFixture();
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        var active = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(active, "cache.blk"), "old-version");
        service.InitializeActive(ProfileIds.Global, "3.1.0", fixture.Game);

        var issues = new List<ValidationIssue>();
        var plan = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.2.0",
            fixture.Game,
            issues);

        True(plan is not null, "新版本无需手动初始化，应自动建立来源缓存计划。");
        True(issues.Any(issue => issue.Code == "hot-cache.source.auto-capture"),
            "升级后应明确自动保存当前服到新版本缓存槽。");
        Equal("3.2.0", plan!.SourceManifest.GameVersion);
        True(service.GetStatus(ProfileIds.Global, "3.1.0", fixture.Game, ProfileIds.Global).IsAvailable,
            "停止新版本切换时不应破坏旧版本缓存。");
        return Task.CompletedTask;
    }

    private static Task PackageArchiveImportsAtomically()
    {
        using var fixture = new TempFixture();
        const string content = "verified-package";
        PreparePackageImportConfiguration(fixture, content);
        var archive = CreatePackageArchive(fixture, "3.1.0", content);
        var existing = GameStorageLayout.GetPackageRoot(fixture.Game, "3.1.0");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "old.bin"), "old");

        var result = new PackageImportService(new ConfigurationRepository(fixture.Paths))
            .Import(archive, fixture.Game, "3.1.0");

        True(result.ReplacedExisting && result.RetainedPreviousPath is null,
            "完整导入后应替换并清理旧同版本目录。");
        Equal(content, File.ReadAllText(Path.Combine(existing, ProfileIds.CnOfficial, "payload.bin")));
        True(!File.Exists(Path.Combine(existing, "old.bin")), "旧差异包内容不应与新包混合。");
        return Task.CompletedTask;
    }

    private static Task PackageArchiveRejectsTraversal()
    {
        using var fixture = new TempFixture();
        PreparePackageImportConfiguration(fixture, "payload");
        var archivePath = Path.Combine(fixture.Root, "traversal.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(
                ".zzzswitch/packages/3.1.0/../outside.bin").Open());
            writer.Write("bad");
        }

        var rejected = false;
        try
        {
            new PackageImportService(new ConfigurationRepository(fixture.Paths))
                .Import(archivePath, fixture.Game, "3.1.0");
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        True(rejected, "包含 .. 的 ZIP 路径必须在解压前拒绝。");
        True(!File.Exists(Path.Combine(GameStorageLayout.GetPackagesRoot(fixture.Game), "outside.bin")),
            "拒绝的 ZIP 不得写出目标版本目录。");
        return Task.CompletedTask;
    }

    private static Task PackageArchiveRecoversInterruptedReplacement()
    {
        using var fixture = new TempFixture();
        const string content = "recovered-import";
        PreparePackageImportConfiguration(fixture, content);
        var archive = CreatePackageArchive(fixture, "3.1.0", content);
        var packagesRoot = GameStorageLayout.GetPackagesRoot(fixture.Game);
        var previous = Path.Combine(packagesRoot, ".previous-3.1.0-interrupted");
        var importing = Path.Combine(packagesRoot, ".importing-3.1.0-interrupted");
        Directory.CreateDirectory(previous);
        Directory.CreateDirectory(importing);
        File.WriteAllText(Path.Combine(previous, "old.bin"), "recoverable-old");
        File.WriteAllText(Path.Combine(importing, "partial.bin"), "partial");

        var result = new PackageImportService(new ConfigurationRepository(fixture.Paths))
            .Import(archive, fixture.Game, "3.1.0");

        Equal(content, File.ReadAllText(Path.Combine(
            result.PackageRoot,
            ProfileIds.CnOfficial,
            "payload.bin")));
        True(!Directory.GetDirectories(packagesRoot, ".previous-3.1.0-*").Any() &&
             !Directory.GetDirectories(packagesRoot, ".importing-3.1.0-*").Any(),
            "恢复并提交后不应遗留中断导入目录。");
        return Task.CompletedTask;
    }

    private static Task PackageArchiveRejectsWrongVersion()
    {
        using var fixture = new TempFixture();
        PreparePackageImportConfiguration(fixture, "payload");
        var archive = CreatePackageArchive(fixture, "3.0.0", "payload");
        var rejected = false;
        try
        {
            new PackageImportService(new ConfigurationRepository(fixture.Paths))
                .Import(archive, fixture.Game, "3.1.0");
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        True(rejected, "3.0.0 差异包不得导入到 3.1.0 目录。");
        True(!Directory.Exists(GameStorageLayout.GetPackageRoot(fixture.Game, "3.1.0")),
            "版本不匹配时不得创建目标差异包目录。");
        return Task.CompletedTask;
    }

    private static Task SnapshotRetentionIsGameScoped()
    {
        using var fixture = new TempFixture();
        var secondGame = Path.Combine(fixture.Root, "Second Game");
        var secondPersistent = Path.Combine(secondGame, "ZenlessZoneZero_Data", "Persistent");
        var secondStreaming = Path.Combine(secondGame, "ZenlessZoneZero_Data", "StreamingAssets");
        Directory.CreateDirectory(secondPersistent);
        Directory.CreateDirectory(secondStreaming);
        File.WriteAllText(Path.Combine(secondPersistent, "data_version_persist"), "second-version");
        File.WriteAllText(Path.Combine(secondStreaming, "data_revision"), "second-revision");
        var snapshots = new ProfileSnapshotService(fixture.Paths, new PhysicalFileOperations());

        snapshots.Capture(ProfileIds.Global, "3.0.0", fixture.Game);
        snapshots.Capture(ProfileIds.Global, "3.0.0", fixture.Game);
        snapshots.Capture(ProfileIds.Global, "3.0.0", fixture.Game);
        snapshots.Capture(ProfileIds.Global, "3.0.0", secondGame);

        var firstRoot = Path.Combine(
            fixture.Paths.ProfileSnapshotsRoot,
            GameStorageLayout.GetGameIdentity(fixture.Game),
            "3.0.0",
            ProfileIds.Global);
        var secondRoot = Path.Combine(
            fixture.Paths.ProfileSnapshotsRoot,
            GameStorageLayout.GetGameIdentity(secondGame),
            "3.0.0",
            ProfileIds.Global);
        Equal(2, Directory.GetDirectories(firstRoot).Length);
        Equal(1, Directory.GetDirectories(secondRoot).Length);
        True(snapshots.FindLatestValid(ProfileIds.Global, "3.0.0", fixture.Game) is not null,
            "轮换后当前安装应保留有效快照。");
        True(snapshots.FindLatestValid(ProfileIds.Global, "3.0.0", secondGame) is not null,
            "第二份安装的快照不应被第一份安装轮换删除。");
        return Task.CompletedTask;
    }

    private static Task ApplicationDataMigratesToGameStorage()
    {
        using var fixture = new TempFixture();
        fixture.Paths.EnsureWritableDirectories();
        var snapshots = new ProfileSnapshotService(fixture.Paths, new PhysicalFileOperations());
        var snapshot = snapshots.Capture(ProfileIds.Global, "3.1.0", fixture.Game);
        var backup = Path.Combine(fixture.Paths.BackupsRoot, "operation", "files", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "backup-content");
        var online = Path.Combine(fixture.Paths.OnlineDifferenceFilesRoot, "3.1.0", "content", "b.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(online)!);
        File.WriteAllText(online, "online-content");
        File.WriteAllText(fixture.Paths.UiSettingsFile, "{}");
        new StateStore(fixture.Paths).Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = "3.1.0",
            CurrentProfile = ProfileIds.Global,
            LastBackupPath = Path.GetDirectoryName(Path.GetDirectoryName(backup)!)
        });
        var backupRecord = new BackupRecord
        {
            OperationId = "operation",
            OperationTime = DateTimeOffset.Now,
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.1.0",
            GamePath = fixture.Game,
            BackedUpFiles = ["a.bin"],
            SourceSnapshotPath = snapshot.SnapshotPath,
            TargetSnapshotPath = snapshot.SnapshotPath,
            OperationResult = "success"
        };
        File.WriteAllText(
            Path.Combine(fixture.Paths.BackupsRoot, "operation", "backup.json"),
            JsonSerializer.Serialize(backupRecord, JsonSupport.Options));

        var result = new ApplicationDataPlacementService(fixture.Paths)
            .ActivateGameStorage(fixture.Game);
        var targetRoot = GameStorageLayout.GetAppDataRoot(fixture.Game);

        True(result.ContentMoved && result.SourceRemoved, "应用数据应完整迁移并删除旧副本。");
        Equal(Path.GetFullPath(targetRoot), Path.GetFullPath(fixture.Paths.StorageRoot));
        True(Directory.Exists(Path.Combine(
                targetRoot,
                GameStorageLayout.BackupRecordsDirectoryName,
                "operation",
                "files")),
            "事务实体备份应随应用数据一起迁移。");
        Equal("online-content", File.ReadAllText(Path.Combine(
            targetRoot,
            GameStorageLayout.DownloadsDirectoryName,
            "3.1.0",
            "content",
            "b.bin")));
        True(File.Exists(fixture.Paths.UiSettingsFile) && File.Exists(fixture.Paths.StateFile),
            "配置和状态文件必须保留在 AppData。");
        True(!Directory.Exists(Path.Combine(
                 fixture.Data,
                 GameStorageLayout.BackupRecordsDirectoryName)) &&
             !Directory.Exists(Path.Combine(
                 fixture.Data,
                 GameStorageLayout.DownloadsDirectoryName)),
            "AppData 不应继续保留已迁移的大体积数据。");

        var state = new StateStore(fixture.Paths).Load()!;
        Equal(
            Path.Combine(targetRoot, GameStorageLayout.BackupRecordsDirectoryName, "operation"),
            state.LastBackupPath);
        var migratedSnapshot = new ProfileSnapshotService(fixture.Paths, new PhysicalFileOperations())
            .FindLatestValid(ProfileIds.Global, "3.1.0", fixture.Game);
        True(migratedSnapshot is not null, "迁移后快照应继续可用。");
        True(migratedSnapshot!.SnapshotPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase),
            "迁移后的快照路径应重新绑定到 .zzzswitch 数据根。");
        var migratedRecord = JsonSerializer.Deserialize<BackupRecord>(
                                 File.ReadAllText(Path.Combine(
                                     targetRoot,
                                     GameStorageLayout.BackupRecordsDirectoryName,
                                     "operation",
                                     "backup.json")),
                                 JsonSupport.Options)
                             ?? throw new InvalidDataException("迁移后的备份记录无法读取。");
        Equal(migratedSnapshot.SnapshotPath, migratedRecord.SourceSnapshotPath);
        Equal(migratedSnapshot.SnapshotPath, migratedRecord.TargetSnapshotPath);
        Equal("backup-content", File.ReadAllText(Path.Combine(
            targetRoot,
            GameStorageLayout.BackupRecordsDirectoryName,
            "operation",
            "files",
            "a.bin")));

        var backupLocations = new BackupLocationService(fixture.Paths);
        backupLocations.ChangeLocation(Path.Combine(fixture.Root, "CustomBackups"), fixture.Game);
        var restoredBackups = backupLocations.RestoreDefaultLocation(fixture.Game);
        True(restoredBackups.ContentMoved && restoredBackups.SourceRemoved,
            "数据迁移后应允许备份恢复到 .zzzswitch 默认位置。");

        var cacheLocations = new CacheLocationService(fixture.Paths);
        cacheLocations.ChangeLocation(fixture.Game, Path.Combine(fixture.Root, "CustomCache"));
        var restoredCache = cacheLocations.RestoreDefaultLocation(fixture.Game);
        True(restoredCache.SourceRemoved,
            "数据迁移后应允许 Blocks 缓存恢复到 .zzzswitch 默认位置。");

        var reloaded = new AppPaths(fixture.Data, fixture.Config);
        Equal(Path.GetFullPath(targetRoot), Path.GetFullPath(reloaded.StorageRoot));
        Equal(Path.GetFullPath(fixture.Game), Path.GetFullPath(reloaded.StorageGamePath!));
        return Task.CompletedTask;
    }

    private static Task LegacyStorageNamesMigrateAtomically()
    {
        using var fixture = new TempFixture();
        var legacyData = GameStorageLayout.GetLegacyDataRoot(fixture.Game);
        var legacyCache = GameStorageLayout.GetLegacyCacheRoot(fixture.Game);
        var oldBackups = Path.Combine(legacyData, "Backups", "operation");
        var oldDownloads = Path.Combine(legacyData, "OnlineDifferenceFiles", "3.1.0", "content");
        var oldManifestDirectory = Path.Combine(
            legacyData,
            "HotUpdateCaches",
            GameStorageLayout.GetGameIdentity(fixture.Game),
            "3.1.0",
            ProfileIds.Global);
        var oldBlocks = GameStorageLayout.GetStoredBlocksPath(
            fixture.Game,
            "3.1.0",
            ProfileIds.Global,
            legacyCache);
        Directory.CreateDirectory(oldBackups);
        Directory.CreateDirectory(oldDownloads);
        Directory.CreateDirectory(oldManifestDirectory);
        Directory.CreateDirectory(oldBlocks);
        File.WriteAllText(Path.Combine(oldBackups, "backup.json"), "{}");
        File.WriteAllText(Path.Combine(oldDownloads, "payload.bin"), "download");
        File.WriteAllText(Path.Combine(oldBlocks, "block.bin"), "block");
        var oldManifestPath = Path.Combine(oldManifestDirectory, "cache.json");
        File.WriteAllText(oldManifestPath, JsonSerializer.Serialize(new HotUpdateCacheManifest
        {
            CacheId = "legacy-cache",
            CreatedAt = DateTimeOffset.UtcNow,
            Profile = ProfileIds.Global,
            GameVersion = "3.1.0",
            GamePath = fixture.Game,
            StoredBlocksPath = oldBlocks,
            FileCount = 1,
            TotalBytes = 5
        }, JsonSupport.Options));
        Directory.CreateDirectory(fixture.Data);
        File.WriteAllText(fixture.Paths.StateFile, JsonSerializer.Serialize(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = "3.1.0",
            CurrentProfile = ProfileIds.Global,
            LastBackupPath = oldBackups
        }, JsonSupport.Options));
        File.WriteAllText(
            fixture.Paths.StorageLocationFile,
            JsonSerializer.Serialize(
                new StorageLocationSettings { GamePath = fixture.Game },
                JsonSupport.Options));

        var paths = new AppPaths(fixture.Data, fixture.Config);
        var result = new ApplicationDataPlacementService(paths).ActivateGameStorage(fixture.Game);
        var appData = GameStorageLayout.GetAppDataRoot(fixture.Game);
        var blocksCache = GameStorageLayout.GetCacheRoot(fixture.Game);

        True(result.ContentMoved && result.SourceRemoved && result.LayoutRenamed,
            "旧版目录应通过同卷移动完成命名升级。");
        True(!Directory.Exists(legacyData) && !Directory.Exists(legacyCache),
            "升级完成后不应残留 data 或 cache 旧目录。");
        Equal("download", File.ReadAllText(Path.Combine(
            appData,
            GameStorageLayout.DownloadsDirectoryName,
            "3.1.0",
            "content",
            "payload.bin")));
        Equal("block", File.ReadAllText(Path.Combine(
            blocksCache,
            GameStorageLayout.GetGameIdentity(fixture.Game),
            "3.1.0",
            ProfileIds.Global,
            "Blocks",
            "block.bin")));
        var state = new StateStore(paths).Load()!;
        Equal(
            Path.Combine(appData, GameStorageLayout.BackupRecordsDirectoryName, "operation"),
            state.LastBackupPath);
        var migratedManifestPath = Path.Combine(
            appData,
            GameStorageLayout.BlocksManifestsDirectoryName,
            GameStorageLayout.GetGameIdentity(fixture.Game),
            "3.1.0",
            ProfileIds.Global,
            "cache.json");
        var migratedManifest = JsonSerializer.Deserialize<HotUpdateCacheManifest>(
                                   File.ReadAllText(migratedManifestPath),
                                   JsonSupport.Options)
                               ?? throw new InvalidDataException("迁移后的 Blocks 清单无法读取。");
        Equal(
            GameStorageLayout.GetStoredBlocksPath(
                fixture.Game,
                "3.1.0",
                ProfileIds.Global,
                blocksCache),
            migratedManifest.StoredBlocksPath);
        return Task.CompletedTask;
    }

    private static Task PendingTransactionBlocksApplicationDataMigration()
    {
        using var fixture = new TempFixture();
        fixture.Paths.EnsureWritableDirectories();
        File.WriteAllText(fixture.Paths.FileTransactionJournalFile, "pending");
        var rejected = false;
        try
        {
            new ApplicationDataPlacementService(fixture.Paths).ActivateGameStorage(fixture.Game);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        True(rejected, "未完成事务存在时不得迁移备份与恢复数据。");
        True(fixture.Paths.UsesLegacyDataRoot, "拒绝迁移后应继续使用原数据根。");
        True(!File.Exists(fixture.Paths.StorageLocationFile), "拒绝迁移时不应提交新数据位置。");
        return Task.CompletedTask;
    }

    private static Task CacheLocationRejectsStagingOverlap()
    {
        using var fixture = new TempFixture();
        var locations = new CacheLocationService(fixture.Paths);
        var rejected = false;
        try
        {
            locations.ChangeLocation(fixture.Game, GameStorageLayout.GetStagingRoot(fixture.Game));
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        True(rejected, "自定义缓存目录不得与同盘切换暂存区重叠。");
        return Task.CompletedTask;
    }

    private static async Task BilibiliOverlayReuseAfterUpgrade(string version, bool automaticReuse)
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers(version);
        const string payload = "verified-sdk";
        PrepareBundledBilibiliConfiguration(fixture, payload);
        string[] keys = ["GameAssembly.dll", "ZenlessZoneZero.exe"];
        foreach (var id in ProfileIds.All)
        {
            WriteProfile(id, [version]);
        }
        void WriteProfile(string id, List<string> compatibleVersions)
        {
            var profile = new ProfileDefinition
            {
                Id = id, DisplayName = id, PackageDirectoryName = id, GameVersion = "3.1.0",
                OverlayCompatibleGameVersions = id == ProfileIds.Bilibili && !automaticReuse ? compatibleVersions : [],
                ReuseOverlayAcrossGameVersions = id == ProfileIds.Bilibili && automaticReuse && compatibleVersions.Count > 0,
                KeyFiles = keys.Select(key => new FileSignature { Path = key, Length = 3 })
                    .Concat(id == ProfileIds.Bilibili
                        ? [new FileSignature { Path = "payload.bin", Length = payload.Length, Sha256 = Sha256Text(payload) }]
                        : []).ToList()
            };
            File.WriteAllText(Path.Combine(fixture.Config, "profiles", id + ".json"),
                JsonSerializer.Serialize(profile, JsonSupport.Options));
        }

        var configuration = new ConfigurationRepository(fixture.Paths);
        var overlay = configuration.FindTransition(ProfileIds.CnOfficial, ProfileIds.Bilibili)!;
        foreach (var (source, target) in new[]
        {
            (ProfileIds.CnOfficial, ProfileIds.Bilibili), (ProfileIds.Bilibili, ProfileIds.CnOfficial),
            (ProfileIds.Global, ProfileIds.Bilibili), (ProfileIds.Bilibili, ProfileIds.Global)
        })
        {
            var manifest = new TransitionManifest
            {
                SourceProfile = source, TargetProfile = target, GameVersion = "3.1.0",
                ReplaceFiles = target == ProfileIds.Bilibili ? overlay.ReplaceFiles : [],
                IniPatches = [ProfilePatch(target)],
                OptionalDeleteFiles = source == ProfileIds.Bilibili ? [new DeleteFileEntry { Target = "payload.bin" }] : []
            };
            File.WriteAllText(Path.Combine(fixture.Config, "transitions", source + "-to-" + target + ".json"),
                JsonSerializer.Serialize(manifest, JsonSupport.Options));
        }
        // Remove the helper's original differently named direction file.
        File.Delete(Path.Combine(fixture.Config, "transitions", "cn-official-to-bilibili.json"));

        var archive = CreateBundledBilibiliArchive("3.1.0", payload);
        var installer = new BundledBilibiliPackageService(configuration, () => new MemoryStream(archive), "3.1.0", "sdk-fixture");
        var oldInstall = installer.EnsureInstalled(fixture.Game, "3.1.0");
        var oldMarker = File.ReadAllText(Path.Combine(oldInstall.PackageDirectory, ".bundled-package.json"));
        var installed = installer.EnsureInstalled(fixture.Game, version);
        Equal(BundledBilibiliPackageStatus.Installed, installed.Status);
        Equal(BundledBilibiliPackageStatus.AlreadyInstalled, installer.EnsureInstalled(fixture.Game, version).Status);
        using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(installed.PackageDirectory, ".bundled-package.json"))))
            Equal(version, marker.RootElement.GetProperty("gameVersion").GetString());
        File.WriteAllText(Path.Combine(installed.PackageDirectory, "payload.bin"), new string('x', payload.Length));
        Equal(BundledBilibiliPackageStatus.Repaired, installer.EnsureInstalled(fixture.Game, version, true).Status);
        var unsupportedVersion = automaticReuse ? "2.9.0" : "3.3.0";
        Equal(BundledBilibiliPackageStatus.UnsupportedVersion, installer.EnsureInstalled(fixture.Game, unsupportedVersion).Status);
        True(!Directory.Exists(GameStorageLayout.GetPackageRoot(fixture.Game, unsupportedVersion)), "不支持的版本不得创建组件目录。");

        var cache = new ManifestCache(fixture.Paths.ManifestCacheRoot, JsonSupport.Options);
        foreach (var region in new[] { SophonRegion.OS, SophonRegion.CN })
            await cache.SaveAsync(new ManifestSnapshot(SophonRegionConfig.Game, region, version, "game",
                region + "-3.2", DateTimeOffset.UtcNow,
                keys.Select(key => new ManifestEntry(key, Content(region, key).Length,
                    Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Content(region, key)))))).ToArray()));
        var inspector = new InspectionService(configuration, new GameDirectoryService(), new ProfileDetector(fixture.Paths),
            new StateStore(fixture.Paths), new FakeProcessMonitor(), inspectLocalPackages: false, reuseConfirmedDetection: true);
        foreach (var key in keys) File.WriteAllText(Path.Combine(fixture.Game, key), Content(SophonRegion.OS, key));
        File.WriteAllText(Path.Combine(fixture.Game, "config.ini"), "[General]\ngame_version=3.1.0\ncps=global\nkeep=yes\n");
        Equal(DetectedProfile.Global, inspector.Inspect(fixture.Game).Detection.Profile);
        var planner = CreatePlanner(fixture);
        True(!planner.CreatePlan(fixture.Game, ProfileIds.Global, ProfileIds.Bilibili).CanExecute,
            "新版跨区域切换缺少在线核心时不得只应用覆盖层。");
        var sourceId = ProfileIds.Global;
        foreach (var targetId in new[] { ProfileIds.Bilibili, ProfileIds.CnOfficial, ProfileIds.Bilibili, ProfileIds.Global })
        {
            Equal(sourceId, inspector.Inspect(fixture.Game).Detection.Profile.ToProfileId());
            SwitchPlan plan;
            if (ProfileIds.ToResourceProfile(sourceId) == ProfileIds.ToResourceProfile(targetId))
                plan = planner.CreatePlan(fixture.Game, sourceId, targetId);
            else
            {
                var region = targetId == ProfileIds.Global ? SophonRegion.OS : SophonRegion.CN;
                var onlineRoot = Path.Combine(fixture.Root, "online-" + targetId);
                Directory.CreateDirectory(onlineRoot);
                foreach (var key in keys) File.WriteAllText(Path.Combine(onlineRoot, key), Content(region, key));
                plan = planner.CreateBilibiliCompositePlan(fixture.Game, sourceId, targetId, new OnlineDifferenceMaterialization
                {
                    PackageRoot = onlineRoot, PackageDirectory = onlineRoot,
                    Manifest = new TransitionManifest
                    {
                        SourceProfile = ProfileIds.ToResourceProfile(sourceId), TargetProfile = ProfileIds.ToResourceProfile(targetId),
                        GameVersion = version,
                        ReplaceFiles = keys.Select(key => new ReplaceFileEntry
                        {
                            Source = key, Target = key, Length = Content(region, key).Length, Sha256 = Sha256Text(Content(region, key))
                        }).ToList()
                    }
                });
            }
            True(plan.CanExecute, string.Join(" | ", plan.Issues.Select(issue => issue.Message)));
            Equal(version, plan.Manifest.GameVersion);
            var result = await fixture.CreateEngine().ExecuteAsync(plan);
            True(result.Success, result.Error ?? "升级后的 B 服切换失败。");
            var afterSwitch = inspector.Inspect(fixture.Game).Detection;
            Equal(targetId, afterSwitch.Profile.ToProfileId());
            True(afterSwitch.ReusedConfirmedState, "成功切换应直接更新目标状态，无需再次识别。");
            var ini = File.ReadAllText(Path.Combine(fixture.Game, "config.ini"));
            True(ini.Contains("game_version=" + version) && ini.Contains("keep=yes") && ini.Contains("cps=" + targetId),
                "渠道切换必须保留当前游戏版本和无关配置。");
            if (targetId == ProfileIds.Bilibili)
            {
                var iniPath = Path.Combine(fixture.Game, "config.ini");
                var outdatedIni = ini.Replace("game_version=" + version, "game_version=3.1.0");
                File.WriteAllText(iniPath, outdatedIni);
                var versionReport = inspector.Inspect(fixture.Game);
                File.WriteAllText(fixture.Paths.FileTransactionJournalFile, "{}");
                True(!inspector.SynchronizeBilibiliVersion(versionReport), "有待恢复事务时不得自动修改 INI。");
                File.Delete(fixture.Paths.FileTransactionJournalFile);
                var busyInspector = new InspectionService(configuration, new GameDirectoryService(), new ProfileDetector(fixture.Paths),
                    new StateStore(fixture.Paths), new FakeProcessMonitor("ZenlessZoneZero.exe"), inspectLocalPackages: false);
                True(!busyInspector.SynchronizeBilibiliVersion(versionReport), "游戏运行时不得自动修改 INI。");
                inspector.Inspect(fixture.Game, readOnly: true);
                Equal(outdatedIni, File.ReadAllText(iniPath));
                True(inspector.SynchronizeBilibiliVersion(versionReport), "已更新的 B 服应自动同步 INI 游戏版本。");
                Equal(ini, File.ReadAllText(iniPath));
                Equal(outdatedIni, File.ReadAllText(iniPath + ".zzzswitch-version.bak"));
                var writeTime = File.GetLastWriteTimeUtc(iniPath);
                True(!inspector.SynchronizeBilibiliVersion(versionReport), "版本一致时不得重复写入。");
                Equal(writeTime, File.GetLastWriteTimeUtc(iniPath));
                True(inspector.Inspect(fixture.Game).Detection.ReusedConfirmedState, "只同步版本不应触发重复核心识别。");
                File.WriteAllText(Path.Combine(fixture.Game, "payload.bin"), new string('x', payload.Length));
                Equal(DetectedProfile.Unknown, inspector.Inspect(fixture.Game).Detection.Profile);
                File.WriteAllText(Path.Combine(fixture.Game, "payload.bin"), payload);
                File.WriteAllText(Path.Combine(fixture.Game, keys[0]), Content(SophonRegion.OS, keys[0]));
                Equal(DetectedProfile.Unknown, inspector.Inspect(fixture.Game).Detection.Profile);
                File.WriteAllText(Path.Combine(fixture.Game, keys[0]), Content(SophonRegion.CN, keys[0]));
                WriteProfile(ProfileIds.Bilibili, []);
                Equal(DetectedProfile.Unknown, inspector.Inspect(fixture.Game).Detection.Profile);
                True(!planner.CreatePlan(fixture.Game, ProfileIds.Bilibili, ProfileIds.CnOfficial).CanExecute,
                    "没有明确兼容声明时必须拒绝复用。");
                WriteProfile(ProfileIds.Bilibili, [version]);
            }
            sourceId = targetId;
        }
        // Valid overlay with Global core is inconsistent, even with an exact regional match.
        File.WriteAllText(Path.Combine(fixture.Game, "payload.bin"), payload);
        Equal(DetectedProfile.Unknown, inspector.Inspect(fixture.Game).Detection.Profile);
        Equal(oldMarker, File.ReadAllText(Path.Combine(oldInstall.PackageDirectory, ".bundled-package.json")));
        Equal(payload, File.ReadAllText(Path.Combine(oldInstall.PackageDirectory, "payload.bin")));

        string Content(SophonRegion region, string key) => $"{region}:{key}:{version}";
    }

    private static Task BundledBilibiliPackageInstallsOnFirstRun()
    {
        using var fixture = new TempFixture();
        const string content = "bilibili-component";
        PrepareBundledBilibiliConfiguration(fixture, content);
        var archiveBytes = CreateBundledBilibiliArchive("3.1.0", content);
        var service = new BundledBilibiliPackageService(
            new ConfigurationRepository(fixture.Paths),
            () => new MemoryStream(archiveBytes, writable: false),
            "3.1.0",
            "test-bundle");

        var installed = service.EnsureInstalled(fixture.Game, "3.1.0");
        Equal(BundledBilibiliPackageStatus.Installed, installed.Status);
        Equal(content, File.ReadAllText(Path.Combine(installed.PackageDirectory, "payload.bin")));
        True(File.Exists(Path.Combine(installed.PackageDirectory, ".bundled-package.json")),
            "完整校验并提交后应写入内置包标记。");

        var payload = Path.Combine(installed.PackageDirectory, "payload.bin");
        using var locked = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.None);
        var reused = service.EnsureInstalled(fixture.Game, "3.1.0");
        Equal(BundledBilibiliPackageStatus.AlreadyInstalled, reused.Status);
        return Task.CompletedTask;
    }

    private static Task BundledBilibiliPackageRepairsCorruption()
    {
        using var fixture = new TempFixture();
        const string content = "verified-component";
        PrepareBundledBilibiliConfiguration(fixture, content);
        var archiveBytes = CreateBundledBilibiliArchive("3.1.0", content);
        var service = new BundledBilibiliPackageService(
            new ConfigurationRepository(fixture.Paths),
            () => new MemoryStream(archiveBytes, writable: false),
            "3.1.0",
            "test-bundle");
        var installed = service.EnsureInstalled(fixture.Game, "3.1.0");
        var payload = Path.Combine(installed.PackageDirectory, "payload.bin");
        File.WriteAllText(payload, "tampered-component");

        var repaired = service.EnsureInstalled(
            fixture.Game,
            "3.1.0",
            requireFullVerification: true);
        Equal(BundledBilibiliPackageStatus.Repaired, repaired.Status);
        Equal(content, File.ReadAllText(payload));
        True(!Directory.GetDirectories(
                GameStorageLayout.GetPackageRoot(fixture.Game, "3.1.0"),
                ".previous-bilibili-*").Any(),
            "修复成功后不应遗留旧 B 服组件目录。");
        return Task.CompletedTask;
    }

    private static Task BundledBilibiliPackageRejectsUnsafeArchive()
    {
        using var fixture = new TempFixture();
        const string content = "safe-component";
        PrepareBundledBilibiliConfiguration(fixture, content);
        var archiveBytes = CreateBundledBilibiliArchive(
            "3.1.0",
            content,
            ".zzzswitch/packages/3.1.0/bilibili/../payload.bin");
        var service = new BundledBilibiliPackageService(
            new ConfigurationRepository(fixture.Paths),
            () => new MemoryStream(archiveBytes, writable: false),
            "3.1.0",
            "test-bundle");
        var rejected = false;
        try
        {
            service.EnsureInstalled(fixture.Game, "3.1.0");
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        True(rejected, "内置 B 服 ZIP 的跨目录路径必须被拒绝。");
        True(!File.Exists(Path.Combine(
                GameStorageLayout.GetPackageRoot(fixture.Game, "3.1.0"),
                "payload.bin")),
            "拒绝归档后不得把文件写出 B 服组件目录。");
        return Task.CompletedTask;
    }

    private static Task ThemePreferencePersistsAndFallsBack()
    {
        using var fixture = new TempFixture();
        var settings = new UiSettingsService(fixture.Paths);
        Equal(ThemePreference.FollowWindows, settings.LoadThemePreference());
        settings.SaveThemePreference(ThemePreference.Light);
        Equal(ThemePreference.Light, new UiSettingsService(fixture.Paths).LoadThemePreference());
        File.WriteAllText(fixture.Paths.UiSettingsFile, "{broken");
        Equal(ThemePreference.FollowWindows, settings.LoadThemePreference());
        return Task.CompletedTask;
    }

    private static Task UiSettingsPersistAsOneDocument()
    {
        using var fixture = new TempFixture();
        var service = new UiSettingsService(fixture.Paths);
        var defaults = service.Load();
        True(!defaults.OnboardingCompleted && !defaults.StartInCompactMode && !defaults.ExitOnClose,
            "首次启动应进入引导，且默认使用完整窗口并关闭到托盘。");
        service.Save(new UiSettings
        {
            Theme = ThemePreference.Dark,
            Language = AppLanguage.English,
            OnboardingCompleted = true,
            StartInCompactMode = true,
            ExitOnClose = true,
            AutoDetectGameDirectory = true,
            AutoInspectOnStartup = false,
            ShowLastGameDirectory = false,
            RememberWindowPlacement = true,
            ShowDetailedStatus = true,
            LogRetentionDays = 30,
            WindowLeft = 120,
            WindowTop = 80,
            WindowWidth = 1100,
            WindowHeight = 800,
            WindowMaximized = true
        });

        var loaded = service.Load();
        Equal(ThemePreference.Dark, loaded.Theme);
        Equal(AppLanguage.English, loaded.Language);
        True(loaded.AutoDetectGameDirectory && !loaded.AutoInspectOnStartup &&
             !loaded.ShowLastGameDirectory && loaded.RememberWindowPlacement &&
             loaded.ShowDetailedStatus && loaded.WindowMaximized &&
             loaded.OnboardingCompleted && loaded.StartInCompactMode && loaded.ExitOnClose,
            "界面与启动布尔设置未完整持久化。");
        Equal(30, loaded.LogRetentionDays);
        Equal(1100d, loaded.WindowWidth);

        service.SaveThemePreference(ThemePreference.Light);
        loaded = service.Load();
        Equal(AppLanguage.English, loaded.Language);
        Equal(30, loaded.LogRetentionDays);
        return Task.CompletedTask;
    }

    private static Task ExpiredLogsFollowRetention()
    {
        using var fixture = new TempFixture();
        var logger = new OperationLogger(fixture.Paths);
        logger.Write(new OperationLogEntry
        {
            Time = DateTimeOffset.Now,
            OperationId = "retention-test",
            GamePath = fixture.Game,
            GameVersion = "3.1.0",
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            Error = "retained"
        });
        var oldLog = Path.Combine(fixture.Paths.LogsRoot, "old.jsonl");
        File.WriteAllText(oldLog, "old");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-31));
        var logs = new LogMaintenanceService(fixture.Paths);
        var cleanup = logs.CleanExpiredLogs(30);
        Equal(1, cleanup.RemovedFileCount);
        True(!File.Exists(oldLog) && Directory.GetFiles(fixture.Paths.LogsRoot, "*.jsonl").Length == 1,
            "只应删除超过保留天数的日志。");
        return Task.CompletedTask;
    }

    private static Task MigratedCacheManifestUsesCustomLocation()
    {
        using var fixture = new TempFixture();
        var locations = new CacheLocationService(fixture.Paths);
        var service = new HotUpdateCacheService(
            fixture.Paths,
            new FakeProcessMonitor(),
            locations);
        var activeBlocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(activeBlocks);
        File.WriteAllText(Path.Combine(activeBlocks, "cache.bin"), "cache-content");
        var manifest = service.InitializeActive(ProfileIds.Global, "3.1.0", fixture.Game);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest.StoredBlocksPath)!);
        Directory.Move(activeBlocks, manifest.StoredBlocksPath);
        var targetRoot = Path.Combine(fixture.Root, "RelocatedCache");

        locations.ChangeLocation(fixture.Game, targetRoot);
        var status = service.GetStatus(
            ProfileIds.Global,
            "3.1.0",
            fixture.Game,
            ProfileIds.CnOfficial);

        True(status.IsAvailable, status.Detail ?? "迁移后的缓存应保持可用。");
        True(status.Path is not null && status.Path.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase),
            "旧清单中的存储路径应按当前缓存设置重新解析。");
        return Task.CompletedTask;
    }

    private static Task StorageRootMissingDetected()
    {
        using var fixture = new TempFixture();
        var profiles = StorageProfiles();
        var status = new StorageLayoutService().Inspect(
            fixture.Game,
            "3.1.0",
            profiles);

        True(!status.RootExists, "测试环境不应预先存在 .zzzswitch。");
        True(status.NeedsDirectoryRepair, "根目录缺失时应标记为需要修复。");
        Equal(Path.Combine(fixture.Root, ".zzzswitch"), status.RootPath);
        Equal(2, status.MissingProfileDirectories.Count);
        return Task.CompletedTask;
    }

    private static Task StorageLayoutRepair()
    {
        using var fixture = new TempFixture();
        var profiles = StorageProfiles();
        var result = new StorageLayoutService().Repair(
            fixture.Game,
            "3.1.0",
            profiles);

        True(result.After.RootExists, "修复后根目录应存在。");
        True(result.After.PackagesRootExists, "修复后 packages 目录应存在。");
        True(result.After.PackageVersionExists, "修复后版本目录应存在。");
        True(result.After.CacheRootExists, "修复后 blocks-cache 目录应存在。");
        Equal(2, result.After.MissingProfileDirectories.Count);
        True(
            result.After.MissingProfileDirectories.All(path => !Directory.Exists(path)),
            "目录修复不得伪造 global/cn_official 离线包目录。");
        Equal(
            0,
            Directory.EnumerateFiles(
                result.After.RootPath,
                "*",
                SearchOption.AllDirectories).Count());
        True(
            !Directory.Exists(GameStorageLayout.GetStoredBlocksPath(
                fixture.Game,
                "3.1.0",
                ProfileIds.Global)),
            "目录修复不得伪造 Blocks 缓存内容。");
        return Task.CompletedTask;
    }

    private static Task MissingProfilePackageIsNotStructuralDamage()
    {
        using var fixture = new TempFixture();
        var profiles = StorageProfiles();
        var service = new StorageLayoutService();
        service.Repair(fixture.Game, "3.1.0", profiles);
        Directory.CreateDirectory(GameStorageLayout.GetPackageDirectory(
            fixture.Game,
            "3.1.0",
            profiles[0].PackageDirectoryName));
        var missing = GameStorageLayout.GetPackageDirectory(
            fixture.Game,
            "3.1.0",
            profiles[1].PackageDirectoryName);

        var status = service.Inspect(fixture.Game, "3.1.0", profiles);

        True(!status.NeedsDirectoryRepair, "差异包未安装应由差异包检查报告，而不是误报目录结构损坏。");
        Equal(1, status.MissingProfileDirectories.Count);
        return Task.CompletedTask;
    }

    private static Task MissingPackagesAreAggregated()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.1.0");
        var profiles = StorageProfiles();
        foreach (var profile in profiles)
        {
            File.WriteAllText(
                Path.Combine(fixture.Config, "profiles", profile.Id + ".json"),
                JsonSerializer.Serialize(profile, JsonSupport.Options));
        }

        var transitions = new[]
        {
            new TransitionManifest
            {
                SourceProfile = ProfileIds.Global,
                TargetProfile = ProfileIds.CnOfficial,
                GameVersion = "3.1.0",
                ExpectedReplaceCount = 2,
                ExpectedDeleteCount = 0,
                ReplaceFiles = [Entry("cn-a.bin"), Entry("cn-b.bin")]
            },
            new TransitionManifest
            {
                SourceProfile = ProfileIds.CnOfficial,
                TargetProfile = ProfileIds.Global,
                GameVersion = "3.1.0",
                ExpectedReplaceCount = 2,
                ExpectedDeleteCount = 0,
                ReplaceFiles = [Entry("global-a.bin"), Entry("global-b.bin")]
            }
        };
        foreach (var transition in transitions)
        {
            File.WriteAllText(
                Path.Combine(
                    fixture.Config,
                    "transitions",
                    transition.SourceProfile + "-to-" + transition.TargetProfile + ".json"),
                JsonSerializer.Serialize(transition, JsonSupport.Options));
        }

        var service = new InspectionService(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new ProfileDetector(),
            new StateStore(fixture.Paths),
            new FakeProcessMonitor(),
            inspectLocalPackages: false);
        var report = service.Inspect(fixture.Game);

        Equal(
            1,
            report.Issues.Count(x => x.Code == "storage.root.missing"));
        Equal(
            0,
            report.Issues.Count(x => x.Code == "package.unavailable"));
        Equal(0, report.Packages.Count);
        True(report.Issues.Single(x => x.Code == "storage.root.missing").Severity == IssueSeverity.Information,
            "在线模式下本地存储根目录缺失只能作为提示，不能阻止切换。");
        Equal(
            0,
            report.Issues.Count(x => x.Code == "manifest.source.missing"));
        Equal(
            0,
            report.Issues.Count(x => x.Code == "manifest.integrity.missing"));
        var intentionallyMissingDirections =
            ProfileIds.All.Length * (ProfileIds.All.Length - 1) - transitions.Length;
        True(
            report.Issues.Count < 10 + intentionallyMissingDirections,
            "目录整体缺失时不应为每个差异文件重复生成错误。");
        return Task.CompletedTask;
    }

    private static Task PhysicalBackupRemainsRestorable()
    {
        using var fixture = new TempFixture();
        var target = Path.Combine(fixture.Game, "a.bin");
        File.WriteAllText(target, "old-value");
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        var service = new BackupService(new PhysicalFileOperations(), fixture.Paths);
        var record = service.CreateBackup(plan);
        var physicalBackup = Path.Combine(plan.BackupPath, "files", "a.bin");
        True(File.Exists(physicalBackup), "新备份应把受影响文件保存在当前事务的 files 目录中。");
        True(record.LegacyContentObjects is null && record.LegacyContentLengths is null,
            "新备份不应写入旧版内容对象元数据。");
        True(!Directory.Exists(Path.Combine(
                GameStorageLayout.GetAppDataRoot(fixture.Game),
                GameStorageLayout.BackupContentDirectoryName)),
            "新备份不应创建旧版内容寻址目录。");
        File.WriteAllText(target, "new-value");

        var restored = service.Rollback(plan.BackupPath, record, out var detail);

        True(restored, detail);
        Equal("old-value", File.ReadAllText(target));
        return Task.CompletedTask;
    }

    private static Task LegacyContentObjectBackupRemainsRestorable()
    {
        using var fixture = new TempFixture();
        var target = Path.Combine(fixture.Game, "legacy.bin");
        File.WriteAllText(target, "new-value");
        var objectId = new string('A', 64);
        var legacyObject = Path.Combine(
            GameStorageLayout.GetAppDataRoot(fixture.Game),
            GameStorageLayout.BackupContentDirectoryName,
            "sha256",
            objectId[..2],
            objectId + ".blob");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyObject)!);
        File.WriteAllText(legacyObject, "old-value");
        var backupPath = Path.Combine(fixture.Paths.BackupsRoot, "legacy-object");
        Directory.CreateDirectory(backupPath);
        var record = new BackupRecord
        {
            OperationId = "legacy-object",
            OperationTime = DateTimeOffset.Now,
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            GamePath = fixture.Game,
            BackedUpFiles = ["legacy.bin"],
            LegacyContentObjects = new(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy.bin"] = objectId
            },
            LegacyContentLengths = new(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy.bin"] = new FileInfo(legacyObject).Length
            },
            OperationResult = "success"
        };

        var restored = new BackupService(new PhysicalFileOperations(), fixture.Paths)
            .Rollback(backupPath, record, out var detail);

        True(restored, detail);
        Equal("old-value", File.ReadAllText(target));
        return Task.CompletedTask;
    }

    private static Task BackupRotationKeepsLatestPerSourceProfile()
    {
        using var fixture = new TempFixture();
        var service = new BackupService(new PhysicalFileOperations(), fixture.Paths);
        var oldGlobal = Path.Combine(fixture.Paths.BackupsRoot, "old-global");
        var newerGlobal = Path.Combine(fixture.Paths.BackupsRoot, "newer-global");
        var latestCn = Path.Combine(fixture.Paths.BackupsRoot, "latest-cn");
        var latestBilibili = Path.Combine(fixture.Paths.BackupsRoot, "latest-bilibili");
        var rolledBackFailure = Path.Combine(fixture.Paths.BackupsRoot, "rolled-back-failure");
        var recoveredInterruption = Path.Combine(fixture.Paths.BackupsRoot, "recovered-interruption");
        var manuallyRestored = Path.Combine(fixture.Paths.BackupsRoot, "manually-restored");
        var unresolvedFailure = Path.Combine(fixture.Paths.BackupsRoot, "unresolved-failure");
        var incompleteInterruption = Path.Combine(fixture.Paths.BackupsRoot, "incomplete-interruption");
        var retainedWithClockRollback = Path.Combine(fixture.Paths.BackupsRoot, "retained-clock-rollback");
        var foreign = Path.Combine(fixture.Paths.BackupsRoot, "foreign");
        var otherGame = Path.Combine(fixture.Root, "OtherGame");
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        SaveBackup(oldGlobal, CreateRecord(
            fixture.Game,
            start.AddMinutes(1),
            ProfileIds.Global,
            ProfileIds.CnOfficial));
        SaveBackup(newerGlobal, CreateRecord(
            fixture.Game,
            start.AddMinutes(4),
            ProfileIds.Global,
            ProfileIds.Bilibili));
        SaveBackup(latestCn, CreateRecord(fixture.Game, start.AddMinutes(3), ProfileIds.CnOfficial));
        SaveBackup(latestBilibili, CreateRecord(fixture.Game, start.AddMinutes(2), ProfileIds.Bilibili));
        var rolledBack = CreateRecord(fixture.Game, start.AddMinutes(5));
        rolledBack.OperationResult = "failed";
        rolledBack.RollbackResult = "success";
        SaveBackup(rolledBackFailure, rolledBack);
        var recovered = CreateRecord(fixture.Game, start.AddMinutes(6));
        recovered.OperationResult = "interrupted";
        recovered.RollbackResult = "startup_recovery_success";
        SaveBackup(recoveredInterruption, recovered);
        var restored = CreateRecord(fixture.Game, start.AddMinutes(6));
        restored.RestoredAt = start.AddMinutes(7);
        SaveBackup(manuallyRestored, restored);
        var unresolved = CreateRecord(fixture.Game, start.AddMinutes(7));
        unresolved.OperationResult = "failed";
        unresolved.RollbackResult = "failed: test";
        SaveBackup(unresolvedFailure, unresolved);
        var incomplete = CreateRecord(fixture.Game, start.AddMinutes(8));
        incomplete.OperationResult = "interrupted";
        incomplete.RollbackResult = "startup_recovery_incomplete";
        SaveBackup(incompleteInterruption, incomplete);
        SaveBackup(retainedWithClockRollback, CreateRecord(
            fixture.Game,
            start,
            ProfileIds.Global,
            ProfileIds.CnOfficial));
        SaveBackup(foreign, CreateRecord(otherGame, start.AddMinutes(9)));

        var removed = service.PruneRedundantBackups(retainedWithClockRollback, fixture.Game);

        Equal(5, removed);
        True(!Directory.Exists(oldGlobal), "同一来源服的旧成功备份应被轮换清理。");
        True(!Directory.Exists(newerGlobal), "刚提交的同来源服备份必须取代其他时间记录。");
        True(Directory.Exists(latestCn), "国服来源槽的最新备份必须独立保留。");
        True(Directory.Exists(latestBilibili), "B服来源槽的最新备份必须独立保留。");
        True(!Directory.Exists(rolledBackFailure), "已完整回滚的失败备份应被清理。");
        True(!Directory.Exists(recoveredInterruption), "启动时已完整恢复的中断备份应被清理。");
        True(!Directory.Exists(manuallyRestored), "已手动恢复的备份不应占用三份可恢复配额。");
        True(Directory.Exists(retainedWithClockRollback), "刚提交的来源服备份即使系统时钟回拨也必须保留。");
        True(Directory.Exists(unresolvedFailure), "回滚未完成的备份必须保留供排查。");
        True(Directory.Exists(incompleteInterruption), "启动恢复未完成的备份必须保留供排查。");
        True(Directory.Exists(foreign), "其他游戏目录的备份不得被清理。");
        Equal(3, service.ListBackups().Count(x => SameGame(x.Record.GamePath, fixture.Game) &&
                                                  x.Record.OperationResult == "success" &&
                                                  x.Record.RestoredAt is null));
        return Task.CompletedTask;

        void SaveBackup(string path, BackupRecord record)
        {
            Directory.CreateDirectory(path);
            service.SaveRecord(path, record);
        }

        static bool SameGame(string candidate, string expected) =>
            string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static Task BackupRotationIsScopedPerGameAndSourceProfile()
    {
        using var fixture = new TempFixture();
        var service = new BackupService(new PhysicalFileOperations(), fixture.Paths);
        var otherGame = Path.Combine(fixture.Root, "OtherGame");
        var start = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(otherGame);

        foreach (var (profile, offset) in new[]
                 {
                     (ProfileIds.Global, 0),
                     (ProfileIds.CnOfficial, 10),
                     (ProfileIds.Bilibili, 20)
                 })
        {
            SaveBackup($"game-a-{profile}-old", CreateRecord(fixture.Game, start.AddMinutes(offset), profile));
            SaveBackup($"game-a-{profile}-new", CreateRecord(fixture.Game, start.AddMinutes(offset + 1), profile));
            SaveBackup($"game-b-{profile}-old", CreateRecord(otherGame, start.AddMinutes(offset), profile));
            SaveBackup($"game-b-{profile}-new", CreateRecord(otherGame, start.AddMinutes(offset + 1), profile));
        }

        var protectedBackup = Path.Combine(fixture.Paths.BackupsRoot, $"game-a-{ProfileIds.Global}-old");
        var removed = service.PruneAllBackups(protectedBackup);

        Equal(6, removed);
        True(Directory.Exists(protectedBackup), "启动维护必须保护状态记录的来源服备份，避免时钟回拨误删。");
        Equal(3, service.ListBackups().Count(x => SameGame(x.Record.GamePath, fixture.Game)));
        Equal(3, service.ListBackups().Count(x => SameGame(x.Record.GamePath, otherGame)));
        Equal(1, service.ListBackups().Count(x => SameGame(x.Record.GamePath, fixture.Game) &&
                                                  x.Record.SourceProfile == ProfileIds.Global));
        Equal(1, service.ListBackups().Count(x => SameGame(x.Record.GamePath, fixture.Game) &&
                                                  x.Record.SourceProfile == ProfileIds.CnOfficial));
        Equal(1, service.ListBackups().Count(x => SameGame(x.Record.GamePath, fixture.Game) &&
                                                  x.Record.SourceProfile == ProfileIds.Bilibili));
        return Task.CompletedTask;

        void SaveBackup(string name, BackupRecord record)
        {
            var path = Path.Combine(fixture.Paths.BackupsRoot, name);
            Directory.CreateDirectory(path);
            service.SaveRecord(path, record);
        }

        static bool SameGame(string candidate, string expected) =>
            string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static BackupRecord CreateRecord(
        string gamePath,
        DateTimeOffset operationTime,
        string sourceProfile = ProfileIds.Global,
        string? targetProfile = null,
        string operationResult = "success") => new()
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OperationTime = operationTime,
            SourceProfile = sourceProfile,
            TargetProfile = targetProfile ?? (sourceProfile == ProfileIds.Global ? ProfileIds.CnOfficial : ProfileIds.Global),
            GameVersion = "3.0.0",
            GamePath = gamePath,
            OperationResult = operationResult
        };

    private static async Task PendingStagingRecovery()
    {
        using var fixture = new TempFixture();
        var target = Path.Combine(fixture.Game, "a.bin");
        File.WriteAllText(target, "old");
        File.WriteAllText(Path.Combine(fixture.Package, "a.bin"), "new");
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        var staging = GameStorageLayout.GetOperationStagingDirectory(fixture.Game, plan.OperationId);
        var journals = new FileTransactionJournalStore(fixture.Paths);
        FileTransactionJournal? journalAtFirstWrite = null;
        var files = new FaultingFileOperations(new PhysicalFileOperations(),
            copyTargetPrefixToFailOnce: staging,
            beforeWrite: _ => journalAtFirstWrite = journals.Load());
        var result = await fixture.CreateEngine(files).ExecuteAsync(plan);
        True(!result.Success && !result.RolledBack, "暂存失败不应执行游戏回滚。");
        True(journalAtFirstWrite is { Stage: FileTransactionStage.Staging }, "写首个暂存文件前必须已有可恢复记录。");
        True(!journals.Exists && !Directory.Exists(staging), "正常失败收尾应清理暂存及其记录。");

        // Recreate the disk state at interruption using the journal actually
        // observed at the first payload write, without terminating a real process.
        journals.Save(journalAtFirstWrite!);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "partial.bin"), "partial");
        var unrelated = GameStorageLayout.GetOperationStagingDirectory(fixture.Game, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.bin"), "keep");
        Directory.CreateDirectory(plan.BackupPath);
        File.WriteAllText(Path.Combine(plan.BackupPath, "partial-backup.bin"), "keep for inspection");
        var backups = new BackupService(new PhysicalFileOperations(), fixture.Paths);
        var recovery = CreateRecoveryService(fixture, backups, journals).RecoverPending();
        True(recovery.Found && recovery.Success, recovery.Message);
        True(!journals.Exists && !Directory.Exists(staging), "启动恢复应清理对应暂存和事务记录。");
        Equal("old", File.ReadAllText(target));
        True(!File.Exists(fixture.Paths.StateFile), "暂存清理不应提交游戏状态。");
        Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep.bin")));
        True(File.Exists(Path.Combine(plan.BackupPath, "partial-backup.bin")), "中断时的未完成备份保留供检查。");
    }

    private static Task LockedStagingRecoveryCanRetry()
    {
        using var fixture = new TempFixture();
        var plan = fixture.CreatePlan([], []);
        var staging = GameStorageLayout.GetOperationStagingDirectory(fixture.Game, plan.OperationId);
        var journals = new FileTransactionJournalStore(fixture.Paths);
        journals.Save(CreateFileJournal(plan, FileTransactionStage.Staging));
        Directory.CreateDirectory(staging);
        var payload = Path.Combine(staging, "locked.bin");
        File.WriteAllText(payload, "partial");
        var backups = new BackupService(new PhysicalFileOperations(), fixture.Paths);
        var service = CreateRecoveryService(fixture, backups, journals);
        using (var locked = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = service.RecoverPending();
            True(blocked.Found && !blocked.Success, "被占用时应报告未完成清理。");
            True(journals.Exists, "清理失败必须保留记录用于下次重试。");
        }

        var retried = service.RecoverPending();
        True(retried.Found && retried.Success, retried.Message);
        True(!journals.Exists && !Directory.Exists(staging), "解除占用后应完成清理。");
        True(!service.RecoverPending().Found, "清理成功后再次启动不应重复恢复。");
        return Task.CompletedTask;
    }

    private static Task PendingFileTransactionRecovery()
    {
        using var fixture = new TempFixture();
        var files = new PhysicalFileOperations();
        var target = Path.Combine(fixture.Game, "a.bin");
        File.WriteAllText(target, "old");
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        var backups = new BackupService(files, fixture.Paths);
        var record = backups.CreateBackup(plan);
        File.WriteAllText(target, "new");

        var journals = new FileTransactionJournalStore(fixture.Paths);
        journals.Save(CreateFileJournal(plan, FileTransactionStage.FilesApplied));
        var staging = GameStorageLayout.GetOperationStagingDirectory(fixture.Game, plan.OperationId);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "residue.bin"), "temporary");
        new StateStore(fixture.Paths).Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = "3.0.0",
            CurrentProfile = ProfileIds.Global
        });

        var recovery = CreateRecoveryService(fixture, backups, journals).RecoverPending();
        True(recovery.Found && recovery.Success, recovery.Message);
        Equal("old", File.ReadAllText(target));
        True(!journals.Exists, "恢复成功后应清理普通文件事务日志。");
        True(!Directory.Exists(staging), "恢复成功后应清理该事务的暂存残留。");
        var updated = backups.LoadRecord(plan.BackupPath);
        Equal("interrupted", updated.OperationResult);
        Equal("startup_recovery_success", updated.RollbackResult);
        Equal(record.OperationId, updated.OperationId);
        return Task.CompletedTask;
    }

    private static Task PendingCombinedTransactionRecovery()
    {
        using var fixture = new TempFixture();
        var files = new PhysicalFileOperations();
        var blocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, "cn.blk"), "cn-block");
        var hotUpdate = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        hotUpdate.InitializeActive(ProfileIds.CnOfficial, "3.0.0", fixture.Game);

        var initializeGlobal = hotUpdate.CreateTransitionPlan(
            ProfileIds.CnOfficial,
            ProfileIds.Global,
            "3.0.0",
            fixture.Game,
            [])!;
        hotUpdate.Commit(hotUpdate.BeginTransition(initializeGlobal));
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "global-block");
        hotUpdate.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);

        var target = Path.Combine(fixture.Game, "a.bin");
        File.WriteAllText(target, "old");
        var plan = fixture.CreatePlan([Entry("a.bin")], []);
        var backups = new BackupService(files, fixture.Paths);
        backups.CreateBackup(plan);

        var switchBlocks = hotUpdate.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.0.0",
            fixture.Game,
            [])!;
        hotUpdate.BeginTransition(switchBlocks);
        File.WriteAllText(target, "new");
        var journals = new FileTransactionJournalStore(fixture.Paths);
        journals.Save(CreateFileJournal(plan, FileTransactionStage.FilesApplied));
        new StateStore(fixture.Paths).Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = "3.0.0",
            CurrentProfile = ProfileIds.Global
        });

        var recovery = new PendingTransactionRecoveryService(
            fixture.Paths,
            new StateStore(fixture.Paths),
            backups,
            hotUpdate,
            journals,
            new FakeProcessMonitor()).RecoverPending();

        True(recovery.Found && recovery.Success, recovery.Message);
        Equal("old", File.ReadAllText(target));
        True(File.Exists(Path.Combine(blocks, "global.blk")), "活动 Blocks 应恢复为国际服缓存。");
        True(
            !File.Exists(fixture.Paths.HotUpdateJournalFile),
            $"Blocks 事务日志应被清理。DataRoot 内容：{string.Join(", ", Directory.EnumerateFiles(fixture.Data, "*", SearchOption.AllDirectories))}");
        True(!journals.Exists, "普通文件事务日志应被清理。");
        return Task.CompletedTask;
    }

    private static Task CommittedTransactionJournalCleanup()
    {
        using var fixture = new TempFixture();
        var plan = fixture.CreatePlan([], []);
        var journals = new FileTransactionJournalStore(fixture.Paths);
        journals.Save(CreateFileJournal(plan, FileTransactionStage.MetadataRestored));
        var staging = GameStorageLayout.GetOperationStagingDirectory(fixture.Game, plan.OperationId);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "residue.bin"), "temporary");
        new StateStore(fixture.Paths).Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = plan.Manifest.GameVersion,
            CurrentProfile = plan.Manifest.TargetProfile,
            LastOperationId = plan.OperationId
        });
        var backups = new BackupService(new PhysicalFileOperations(), fixture.Paths);

        var recovery = CreateRecoveryService(fixture, backups, journals).RecoverPending();
        True(recovery.Found && recovery.Success, recovery.Message);
        True(!journals.Exists, "已提交事务的遗留日志应被清理。");
        True(!Directory.Exists(staging), "已提交事务的暂存残留应被清理。");
        True(!Directory.Exists(plan.BackupPath), "清理遗留日志不应尝试读取或创建备份。");
        return Task.CompletedTask;
    }

    private static Task CorruptStateIsSafelyIgnored()
    {
        using var fixture = new TempFixture();
        fixture.Paths.EnsureWritableDirectories();
        File.WriteAllText(fixture.Paths.StateFile, "{ this is not valid json");

        var result = new StateStore(fixture.Paths).LoadWithStatus();
        True(result.State is null, "损坏状态文件不应生成状态对象。");
        True(!string.IsNullOrWhiteSpace(result.Warning), "损坏状态文件应返回可展示的警告。");
        return Task.CompletedTask;
    }

    private static Task PackageIntegrityRejectsSameLengthTamper()
    {
        using var fixture = new TempFixture();
        var path = Path.Combine(fixture.Package, "same-length.bin");
        File.WriteAllText(path, "good");
        var expectedHash = Sha256Text("good");
        File.WriteAllText(path, "evil");

        var result = new FileIntegrityService(new PhysicalFileOperations())
            .Validate(path, 4, expectedHash);
        Equal(FileIntegrityStatus.HashMismatch, result.Status);
        return Task.CompletedTask;
    }

    private static async Task EngineRejectsTamperedPackage()
    {
        using var fixture = new TempFixture();
        var target = Path.Combine(fixture.Game, "a.bin");
        var source = Path.Combine(fixture.Package, "a.bin");
        File.WriteAllText(target, "old");
        File.WriteAllText(source, "evil");
        var entry = new ReplaceFileEntry
        {
            Source = "a.bin",
            Target = "a.bin",
            Length = 4,
            Sha256 = Sha256Text("good")
        };
        var plan = fixture.CreatePlan([entry], []);

        var result = await fixture.CreateEngine().ExecuteAsync(plan);
        True(!result.Success && !result.RolledBack, "差异源损坏时应在创建备份和修改游戏文件前停止。");
        True(result.GameFilesUnchanged, "暂存校验失败应明确报告本次未修改游戏。");
        Equal("old", File.ReadAllText(target));
        True(!Directory.Exists(plan.BackupPath), "暂存校验失败不应建立无用事务备份。");
        True(!new FileTransactionJournalStore(fixture.Paths).Exists, "暂存校验失败收尾后不应遗留文件事务日志。");
    }

    private static Task InspectionDetectsTamperedPackage()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.1.0");
        var profiles = StorageProfiles();
        foreach (var profile in profiles)
        {
            File.WriteAllText(
                Path.Combine(fixture.Config, "profiles", profile.Id + ".json"),
                JsonSerializer.Serialize(profile, JsonSupport.Options));
        }

        var globalDirectory = GameStorageLayout.GetPackageDirectory(fixture.Game, "3.1.0", "global");
        var cnDirectory = GameStorageLayout.GetPackageDirectory(fixture.Game, "3.1.0", "cn_official");
        Directory.CreateDirectory(globalDirectory);
        Directory.CreateDirectory(cnDirectory);
        File.WriteAllText(Path.Combine(globalDirectory, "server.bin"), "global");
        File.WriteAllText(Path.Combine(cnDirectory, "server.bin"), "evil");

        var transitions = new[]
        {
            new TransitionManifest
            {
                SourceProfile = ProfileIds.CnOfficial,
                TargetProfile = ProfileIds.Global,
                GameVersion = "3.1.0",
                ExpectedReplaceCount = 1,
                ExpectedDeleteCount = 0,
                ReplaceFiles =
                [
                    new ReplaceFileEntry
                    {
                        Source = "server.bin",
                        Target = "server.bin",
                        Length = 6,
                        Sha256 = Sha256Text("global")
                    }
                ]
            },
            new TransitionManifest
            {
                SourceProfile = ProfileIds.Global,
                TargetProfile = ProfileIds.CnOfficial,
                GameVersion = "3.1.0",
                ExpectedReplaceCount = 1,
                ExpectedDeleteCount = 0,
                ReplaceFiles =
                [
                    new ReplaceFileEntry
                    {
                        Source = "server.bin",
                        Target = "server.bin",
                        Length = 4,
                        Sha256 = Sha256Text("good")
                    }
                ]
            }
        };
        foreach (var transition in transitions)
        {
            var name = transition.SourceProfile + "-to-" + transition.TargetProfile + ".json";
            File.WriteAllText(
                Path.Combine(fixture.Config, "transitions", name),
                JsonSerializer.Serialize(transition, JsonSupport.Options));
        }

        var report = new InspectionService(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new ProfileDetector(),
            new StateStore(fixture.Paths),
            new FakeProcessMonitor()).Inspect(fixture.Game);

        True(report.Packages.Single(x => x.ProfileId == ProfileIds.Global).IsAvailable, "完整的国际服差异包应可用。");
        var cn = report.Packages.Single(x => x.ProfileId == ProfileIds.CnOfficial);
        True(!cn.IsAvailable, "同长度篡改的国服差异包应不可用。");
        True(cn.Detail?.Contains("完整性", StringComparison.Ordinal) == true, "检查详情应说明完整性不匹配。");
        return Task.CompletedTask;
    }

    private static Task InspectionSurvivesCorruptConfiguration()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.1.0");
        var validProfile = StorageProfiles()[0];
        File.WriteAllText(
            Path.Combine(fixture.Config, "profiles", "global.json"),
            JsonSerializer.Serialize(validProfile, JsonSupport.Options));
        File.WriteAllText(
            Path.Combine(fixture.Config, "profiles", "broken.json"),
            "{ not valid json");

        var report = new InspectionService(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new ProfileDetector(),
            new StateStore(fixture.Paths),
            new FakeProcessMonitor()).Inspect(fixture.Game);

        True(report.Packages.Count == 1, "有效配置应在其他配置损坏时继续参与检查。");
        True(report.Issues.Any(x => x.Code == "config.profile.read"), "详细检查应报告损坏的服务器配置。");
        return Task.CompletedTask;
    }

    private static Task StructurallyInvalidConfigurationIsRejected()
    {
        using var fixture = new TempFixture();
        var invalidProfile = new ProfileDefinition
        {
            Id = ProfileIds.Global,
            DisplayName = "global",
            PackageDirectoryName = @"..\outside",
            KeyFiles = []
        };
        File.WriteAllText(
            Path.Combine(fixture.Config, "profiles", "unsafe.json"),
            JsonSerializer.Serialize(invalidProfile, JsonSupport.Options));

        var result = new ConfigurationRepository(fixture.Paths).LoadProfilesWithStatus();
        Equal(0, result.Items.Count);
        Equal(1, result.Errors.Count);
        return Task.CompletedTask;
    }

    private static Task PlannerRejectsCorruptTransition()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.1.0");
        fixture.WriteConfiguration(new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.1.0",
            ExpectedReplaceCount = 0,
            ExpectedDeleteCount = 0
        });
        File.WriteAllText(Path.Combine(fixture.Config, "transitions", "test.json"), "{ broken");

        var files = new PhysicalFileOperations();
        var planner = new SwitchPlanner(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new FakeProcessMonitor(),
            files,
            fixture.Paths,
            new ProfileSnapshotService(fixture.Paths, files));
        var plan = planner.CreatePlan(fixture.Game, ProfileIds.Global, ProfileIds.CnOfficial);

        True(!plan.CanExecute, "损坏清单不应生成可执行计划。");
        True(plan.Issues.Any(x => x.Code == "config.transition.read"), "计划应明确报告损坏清单。");
        return Task.CompletedTask;
    }

    private static Task HotUpdateRejectsCorruptManifest()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.1.0");
        WriteBlocks(fixture.Game, "global.blk", "global");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        service.InitializeActive(ProfileIds.Global, "3.1.0", fixture.Game);
        File.WriteAllText(
            ScopedManifestPath(fixture, fixture.Game, ProfileIds.Global, "3.1.0"),
            "{ damaged cache manifest");
        var issues = new List<ValidationIssue>();

        var plan = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.1.0",
            fixture.Game,
            issues);

        True(plan is null, "损坏缓存清单时不应创建交换计划。");
        True(issues.Any(x => x.Code == "hot-cache.source.manifest.invalid"), "应报告当前服缓存清单损坏。");
        return Task.CompletedTask;
    }

    private static Task BackupListIgnoresCorruptRecords()
    {
        using var fixture = new TempFixture();
        var backups = new BackupService(new PhysicalFileOperations(), fixture.Paths);
        var validPlan = fixture.CreatePlan([], []);
        backups.CreateBackup(validPlan);
        var corruptDirectory = Path.Combine(fixture.Paths.BackupsRoot, "corrupt");
        Directory.CreateDirectory(corruptDirectory);
        File.WriteAllText(Path.Combine(corruptDirectory, "backup.json"), "{ broken");
        var incompleteDirectory = Path.Combine(fixture.Paths.BackupsRoot, "incomplete");
        Directory.CreateDirectory(incompleteDirectory);
        File.WriteAllText(Path.Combine(incompleteDirectory, "backup.json"), "{}");
        var unsafeDirectory = Path.Combine(fixture.Paths.BackupsRoot, "unsafe");
        Directory.CreateDirectory(unsafeDirectory);
        File.WriteAllText(
            Path.Combine(unsafeDirectory, "backup.json"),
            JsonSerializer.Serialize(new BackupRecord
            {
                OperationId = "unsafe",
                OperationTime = DateTimeOffset.Now,
                SourceProfile = ProfileIds.Global,
                TargetProfile = ProfileIds.CnOfficial,
                GameVersion = "3.1.0",
                GamePath = fixture.Game,
                BackedUpFiles = [@"..\outside.bin"]
            }, JsonSupport.Options));

        var records = backups.ListBackups();

        Equal(1, records.Count);
        Equal(validPlan.OperationId, records[0].Record.OperationId);
        return Task.CompletedTask;
    }

    private static Task StateSaveIsAtomic()
    {
        using var fixture = new TempFixture();
        var store = new StateStore(fixture.Paths);
        store.Save(new AppState
        {
            GamePath = fixture.Game,
            GameVersion = "3.1.0",
            CurrentProfile = ProfileIds.Global
        });

        True(File.Exists(fixture.Paths.StateFile), "状态文件应完成写入。");
        True(!File.Exists(fixture.Paths.StateFile + ".tmp"), "成功写入后不应遗留临时文件。");
        Equal(ProfileIds.Global, store.Load()?.CurrentProfile);
        return Task.CompletedTask;
    }

    private static Task HotUpdateRejectsInvalidManifestFields()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.1.0");
        WriteBlocks(fixture.Game, "global.blk", "global");
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        service.InitializeActive(ProfileIds.Global, "3.1.0", fixture.Game);
        File.WriteAllText(
            ScopedManifestPath(fixture, fixture.Game, ProfileIds.Global, "3.1.0"),
            JsonSerializer.Serialize(new
            {
                cacheId = (string?)null,
                createdAt = DateTimeOffset.Now,
                profile = ProfileIds.Global,
                gameVersion = "3.1.0",
                gamePath = fixture.Game,
                storedBlocksPath = GameStorageLayout.GetStoredBlocksPath(fixture.Game, "3.1.0", ProfileIds.Global),
                fileCount = 1,
                totalBytes = 6
            }));
        var issues = new List<ValidationIssue>();

        var plan = service.CreateTransitionPlan(
            ProfileIds.Global,
            ProfileIds.CnOfficial,
            "3.1.0",
            fixture.Game,
            issues);

        True(plan is null, "字段无效的缓存清单不应生成交换计划。");
        True(issues.Any(x => x.Code == "hot-cache.source.manifest.invalid"), "应报告缓存清单字段无效。");
        return Task.CompletedTask;
    }

    private static Task SnapshotWithInvalidFieldsIsIgnored()
    {
        using var fixture = new TempFixture();
        var snapshots = new ProfileSnapshotService(fixture.Paths, new PhysicalFileOperations());
        var snapshot = snapshots.Capture(ProfileIds.Global, "3.1.0", fixture.Game);
        File.WriteAllText(
            Path.Combine(snapshot.SnapshotPath, "snapshot.json"),
            JsonSerializer.Serialize(new
            {
                snapshotId = snapshot.SnapshotId,
                createdAt = snapshot.CreatedAt,
                profile = snapshot.Profile,
                gameVersion = snapshot.GameVersion,
                gamePath = snapshot.GamePath,
                snapshotPath = snapshot.SnapshotPath,
                files = (object?)null
            }));

        var loaded = snapshots.FindLatestValid(ProfileIds.Global, "3.1.0", fixture.Game);

        True(loaded is null, "字段无效的快照不应参与恢复。");
        return Task.CompletedTask;
    }

    private static Task PlannerRejectsDuplicateTransition()
    {
        using var fixture = new TempFixture();
        fixture.CreateGameMarkers("3.1.0");
        fixture.WriteConfiguration(new TransitionManifest
        {
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.1.0",
            ExpectedReplaceCount = 0,
            ExpectedDeleteCount = 0
        });
        File.Copy(
            Path.Combine(fixture.Config, "transitions", "test.json"),
            Path.Combine(fixture.Config, "transitions", "duplicate.json"));

        var files = new PhysicalFileOperations();
        var planner = new SwitchPlanner(
            new ConfigurationRepository(fixture.Paths),
            new GameDirectoryService(),
            new FakeProcessMonitor(),
            files,
            fixture.Paths,
            new ProfileSnapshotService(fixture.Paths, files));
        var plan = planner.CreatePlan(fixture.Game, ProfileIds.Global, ProfileIds.CnOfficial);

        True(!plan.CanExecute, "重复切换清单不应生成可执行计划。");
        True(plan.Issues.Any(x => x.Code == "manifest.direction.duplicate"), "应明确报告重复切换方向。");
        return Task.CompletedTask;
    }

    private static Task InvalidHotUpdateTransactionStopsRecovery()
    {
        using var fixture = new TempFixture();
        fixture.Paths.EnsureWritableDirectories();
        var activeBlocks = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        File.WriteAllText(
            fixture.Paths.HotUpdateJournalFile,
            JsonSerializer.Serialize(new HotUpdateTransaction
            {
                TransactionId = "invalid",
                Mode = (HotUpdateTransitionMode)99,
                SourceProfile = ProfileIds.Global,
                TargetProfile = ProfileIds.CnOfficial,
                GameVersion = "3.1.0",
                GamePath = fixture.Game,
                ActiveBlocksPath = activeBlocks,
                SourceStoredBlocksPath = GameStorageLayout.GetStoredBlocksPath(
                    fixture.Game,
                    "3.1.0",
                    ProfileIds.Global)
            }, JsonSupport.Options));

        var result = new PendingTransactionRecoveryService(
            fixture.Paths,
            new StateStore(fixture.Paths),
            new BackupService(new PhysicalFileOperations(), fixture.Paths),
            new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor()),
            new FileTransactionJournalStore(fixture.Paths),
            new FakeProcessMonitor()).RecoverPending();

        True(result.Found && !result.Success, "字段无效的 Blocks 事务应被报告为待处理恢复失败。");
        True(File.Exists(fixture.Paths.HotUpdateJournalFile), "无法验证的事务记录必须保留供人工检查。");
        return Task.CompletedTask;
    }

    private static FileTransactionJournal CreateFileJournal(
        SwitchPlan plan,
        FileTransactionStage stage) => new()
        {
            OperationId = plan.OperationId,
            CreatedAt = DateTimeOffset.Now,
            BackupPath = plan.BackupPath,
            GamePath = plan.GamePath,
            GameVersion = plan.Manifest.GameVersion,
            SourceProfile = plan.Manifest.SourceProfile,
            TargetProfile = plan.Manifest.TargetProfile,
            Stage = stage
        };

    private static PendingTransactionRecoveryService CreateRecoveryService(
        TempFixture fixture,
        BackupService backups,
        FileTransactionJournalStore journals)
    {
        var monitor = new FakeProcessMonitor();
        return new PendingTransactionRecoveryService(
            fixture.Paths,
            new StateStore(fixture.Paths),
            backups,
            new HotUpdateCacheService(fixture.Paths, monitor),
            journals,
            monitor);
    }

    private static ProfileDefinition[] StorageProfiles() =>
    [
        new()
        {
            Id = ProfileIds.Global,
            DisplayName = "国际服",
            PackageDirectoryName = "global",
            KeyFiles = [new FileSignature { Path = "GameAssembly.dll", Length = 3 }]
        },
        new()
        {
            Id = ProfileIds.CnOfficial,
            DisplayName = "国服",
            PackageDirectoryName = "cn_official",
            KeyFiles = [new FileSignature { Path = "GameAssembly.dll", Length = 4 }]
        }
    ];

    private static void WriteBlocks(
        string gamePath,
        string fileName,
        string content)
    {
        var blocks = Path.Combine(gamePath, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks);
        File.WriteAllText(Path.Combine(blocks, fileName), content);
    }

    private static string ScopedManifestPath(
        TempFixture fixture,
        string gamePath,
        string profile,
        string version) =>
        Path.Combine(
            fixture.Paths.HotUpdateManifestsRoot,
            GameStorageLayout.GetGameIdentity(gamePath),
            version,
            profile,
            "cache.json");

    private static string LegacyManifestPath(
        TempFixture fixture,
        string profile,
        string version) =>
        Path.Combine(
            fixture.Paths.HotUpdateManifestsRoot,
            profile,
            version,
            "cache.json");

    private static ReplaceFileEntry Entry(string path) => new() { Source = path, Target = path };

    private static void PreparePackageImportConfiguration(TempFixture fixture, string content)
    {
        foreach (var profile in ProfileIds.All)
        {
            var definition = new ProfileDefinition
            {
                Id = profile,
                DisplayName = profile,
                PackageDirectoryName = profile,
                KeyFiles = []
            };
            File.WriteAllText(
                Path.Combine(fixture.Config, "profiles", profile + ".json"),
                JsonSerializer.Serialize(definition, JsonSupport.Options));
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        foreach (var source in ProfileIds.All)
        {
            foreach (var target in ProfileIds.All.Where(target => target != source))
            {
                var transition = new TransitionManifest
                {
                    SourceProfile = source,
                    TargetProfile = target,
                    GameVersion = "3.1.0",
                    ExpectedReplaceCount = 1,
                    ExpectedDeleteCount = 0,
                    ReplaceFiles =
                    [
                        new ReplaceFileEntry
                        {
                            Source = "payload.bin",
                            Target = "payload.bin",
                            Length = bytes.Length,
                            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                        }
                    ]
                };
                File.WriteAllText(
                    Path.Combine(fixture.Config, "transitions", $"{source}-to-{target}.json"),
                    JsonSerializer.Serialize(transition, JsonSupport.Options));
            }
        }
    }

    private static string CreatePackageArchive(TempFixture fixture, string version, string content)
    {
        var archivePath = Path.Combine(fixture.Root, $"packages-{version}.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        AddEntry("version.ini", $"version={version}");
        AddEntry(Path.Combine(ProfileIds.Global, "payload.bin"), content);
        AddEntry(Path.Combine(ProfileIds.CnOfficial, "payload.bin"), content);
        AddEntry(Path.Combine(ProfileIds.Bilibili, "payload.bin"), content);
        return archivePath;

        void AddEntry(string relativePath, string value)
        {
            var name = $".zzzswitch/packages/{version}/{relativePath.Replace('\\', '/')}";
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes);
        }
    }

    private static void PrepareBundledBilibiliConfiguration(TempFixture fixture, string content)
    {
        foreach (var profile in ProfileIds.All)
        {
            File.WriteAllText(
                Path.Combine(fixture.Config, "profiles", profile + ".json"),
                JsonSerializer.Serialize(new ProfileDefinition
                {
                    Id = profile,
                    DisplayName = profile,
                    PackageDirectoryName = profile,
                    KeyFiles = []
                }, JsonSupport.Options));
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllText(
            Path.Combine(fixture.Config, "transitions", "cn-official-to-bilibili.json"),
            JsonSerializer.Serialize(new TransitionManifest
            {
                SourceProfile = ProfileIds.CnOfficial,
                TargetProfile = ProfileIds.Bilibili,
                GameVersion = "3.1.0",
                ExpectedReplaceCount = 1,
                ExpectedDeleteCount = 0,
                ReplaceFiles =
                [
                    new ReplaceFileEntry
                    {
                        Source = "payload.bin",
                        Target = "payload.bin",
                        Length = bytes.Length,
                        Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                    }
                ]
            }, JsonSupport.Options));
    }

    private static byte[] CreateBundledBilibiliArchive(
        string version,
        string content,
        string? entryName = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(
                entryName ?? $".zzzswitch/packages/{version}/bilibili/payload.bin",
                CompressionLevel.NoCompression);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
        }

        return output.ToArray();
    }

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ProfileDefinition TestProfile(string id, string path, long length) => new()
    {
        Id = id,
        DisplayName = id,
        PackageDirectoryName = id,
        KeyFiles = [new FileSignature { Path = path, Length = length }]
    };

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"预期：{expected}；实际：{actual}");
        }
    }

    private sealed class TempFixture : IDisposable
    {
        public TempFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "ZZZSwitch.Tests", Guid.NewGuid().ToString("N"));
            Game = Path.Combine(Root, "Game");
            Data = Path.Combine(Root, "AppData");
            Config = Path.Combine(Root, "config");
            Package = Path.Combine(Root, "package");
            Directory.CreateDirectory(Game);
            Directory.CreateDirectory(Package);
            Directory.CreateDirectory(Path.Combine(Config, "profiles"));
            Directory.CreateDirectory(Path.Combine(Config, "transitions"));
            var persistent = Path.Combine(Game, "ZenlessZoneZero_Data", "Persistent");
            var streaming = Path.Combine(Game, "ZenlessZoneZero_Data", "StreamingAssets");
            Directory.CreateDirectory(persistent);
            Directory.CreateDirectory(streaming);
            File.WriteAllText(Path.Combine(persistent, "data_version_persist"), "source-version");
            File.WriteAllText(Path.Combine(streaming, "data_revision"), "source-revision");
            Paths = new AppPaths(Data, Config);
        }

        public string Root { get; }
        public string Game { get; }
        public string Data { get; }
        public string Config { get; }
        public string Package { get; }
        public AppPaths Paths { get; }

        public void CreateGameMarkers(string version)
        {
            File.WriteAllText(Path.Combine(Game, "ZenlessZoneZero.exe"), "exe");
            File.WriteAllText(Path.Combine(Game, "version_info"), $"OSPRODWin{version}");
            Directory.CreateDirectory(Path.Combine(Game, "ZenlessZoneZero_Data"));
            File.WriteAllText(Path.Combine(Game, "GameAssembly.dll"), "dll");
        }

        public void WriteConfiguration(TransitionManifest transition)
        {
            var profiles = new[]
            {
                new ProfileDefinition { Id = ProfileIds.Global, DisplayName = "global", PackageDirectoryName = "source", KeyFiles = [new FileSignature { Path = "GameAssembly.dll", Length = 3 }] },
                new ProfileDefinition { Id = ProfileIds.CnOfficial, DisplayName = "cn", PackageDirectoryName = "target", KeyFiles = [new FileSignature { Path = "GameAssembly.dll", Length = 3 }] }
            };
            foreach (var profile in profiles)
            {
                File.WriteAllText(Path.Combine(Config, "profiles", profile.Id + ".json"), JsonSerializer.Serialize(profile, JsonSupport.Options));
            }

            File.WriteAllText(Path.Combine(Config, "transitions", "test.json"), JsonSerializer.Serialize(transition, JsonSupport.Options));
        }

        public SwitchPlan CreatePlan(
            IReadOnlyCollection<ReplaceFileEntry> replace,
            IReadOnlyCollection<DeleteFileEntry> delete,
            IReadOnlyCollection<DeleteFileEntry>? optional = null,
            TransitionManifest? manifest = null,
            ProfileSnapshotManifest? targetSnapshot = null)
        {
            manifest ??= new TransitionManifest
            {
                SourceProfile = ProfileIds.Global,
                TargetProfile = ProfileIds.CnOfficial,
                GameVersion = "3.0.0",
                ExpectedReplaceCount = replace.Count,
                ExpectedDeleteCount = delete.Count,
                ReplaceFiles = replace.ToList(),
                DeleteFiles = delete.ToList(),
                OptionalDeleteFiles = optional?.ToList() ?? []
            };
            return new()
            {
                OperationId = Guid.NewGuid().ToString("N"),
                GamePath = Game,
                PackageRoot = Package,
                PackageDirectory = Package,
                Manifest = manifest,
                BackupPath = Path.Combine(Paths.BackupsRoot, Guid.NewGuid().ToString("N")),
                TargetSnapshot = targetSnapshot
            };
        }

        public SwitchEngine CreateEngine(IFileOperations? files = null)
        {
            files ??= new PhysicalFileOperations();
            var backups = new BackupService(files, Paths);
            var snapshots = new ProfileSnapshotService(Paths, files);
            return new(files, Paths, backups, new StateStore(Paths), new OperationLogger(Paths), snapshots);
        }

        public BackupRecord CreateBackupRecord() => new()
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OperationTime = DateTimeOffset.Now,
            SourceProfile = ProfileIds.Global,
            TargetProfile = ProfileIds.CnOfficial,
            GameVersion = "3.0.0",
            GamePath = Game,
            OperationResult = "success"
        };

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }

    private sealed class FakeProcessMonitor(params string[] processes) : IProcessMonitor
    {
        public IReadOnlyList<string> FindRelatedProcesses() => processes;
    }

    private sealed class RejectingSophonTransport : ISophonTransport
    {
        public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new InvalidOperationException("测试不允许网络请求。"));

        public Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromException<byte[]>(new InvalidOperationException("测试不允许网络请求。"));
    }

    private sealed class FakeGameInstallLocator(
        IReadOnlyList<GameDirectoryCandidate> candidates) : IGameInstallLocator
    {
        public IReadOnlyList<GameDirectoryCandidate> Locate() => candidates;
    }

    private sealed class OperationProgressRecorder : List<OperationProgress>, IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => Add(value);
    }

    private sealed class FaultingFileOperations : IFileOperations
    {
        private readonly IFileOperations _inner;
        private readonly string? _copyTarget;
        private readonly string? _deleteTarget;
        private readonly string? _copyTargetPrefix;
        private readonly Action<string>? _beforeWrite;
        private bool _copyFailed;
        private bool _deleteFailed;

        public FaultingFileOperations(
            IFileOperations inner,
            string? copyTargetToFailOnce = null,
            string? deleteTargetToFailOnce = null,
            string? copyTargetPrefixToFailOnce = null,
            Action<string>? beforeWrite = null)
        {
            _inner = inner;
            _copyTarget = copyTargetToFailOnce;
            _deleteTarget = deleteTargetToFailOnce;
            _copyTargetPrefix = copyTargetPrefixToFailOnce;
            _beforeWrite = beforeWrite;
        }

        public bool FileExists(string path) => _inner.FileExists(path);
        public long GetLength(string path) => _inner.GetLength(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void DeleteDirectory(string path, bool recursive) => _inner.DeleteDirectory(path, recursive);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream OpenWrite(string path, bool overwrite)
        {
            _beforeWrite?.Invoke(path);
            ThrowIfCopyTarget(path);
            return _inner.OpenWrite(path, overwrite);
        }
        public Stream OpenExclusive(string path) => _inner.OpenExclusive(path);

        public void CopyFile(string source, string target, bool overwrite)
        {
            ThrowIfCopyTarget(target);
            _inner.CopyFile(source, target, overwrite);
        }

        public void MoveFile(string source, string target, bool overwrite)
        {
            ThrowIfCopyTarget(target);
            _inner.MoveFile(source, target, overwrite);
        }

        public void DeleteFile(string path)
        {
            if (!_deleteFailed && _deleteTarget is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_deleteTarget), StringComparison.OrdinalIgnoreCase))
            {
                _deleteFailed = true;
                throw new IOException("测试注入：删除失败。");
            }

            _inner.DeleteFile(path);
        }

        private void ThrowIfCopyTarget(string target)
        {
            if (!_copyFailed &&
                ((_copyTarget is not null &&
                  string.Equals(
                      Path.GetFullPath(target),
                      Path.GetFullPath(_copyTarget),
                      StringComparison.OrdinalIgnoreCase)) ||
                 (_copyTargetPrefix is not null &&
                  Path.GetFullPath(target).StartsWith(
                      Path.GetFullPath(_copyTargetPrefix).TrimEnd(Path.DirectorySeparatorChar) +
                      Path.DirectorySeparatorChar,
                      StringComparison.OrdinalIgnoreCase))))
            {
                _copyFailed = true;
                throw new IOException("测试注入：复制失败。");
            }
        }
    }

}
