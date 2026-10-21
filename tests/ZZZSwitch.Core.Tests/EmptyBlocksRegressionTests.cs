using System.Text.Json;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch.Core.Tests;

internal static partial class Program
{
    private static Task EmptyHotUpdateCacheRoundTrips()
    {
        foreach (var copy in new[] { false, true })
        foreach (var emptyGlobal in new[] { false, true })
        {
            using var fixture = new TempFixture();
            var service = new HotUpdateCacheService(
                fixture.Paths, new FakeProcessMonitor(), forceVerifiedCopyTransfers: copy);
            var active = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
            Directory.CreateDirectory(active);
            if (!emptyGlobal) File.WriteAllText(Path.Combine(active, "global.blk"), "global-block");
            var baseBlocks = Path.Combine(fixture.Game, @"ZenlessZoneZero_Data\StreamingAssets\Blocks");
            Directory.CreateDirectory(baseBlocks);
            File.WriteAllText(Path.Combine(baseBlocks, "base.blk"), "shared-base");

            // A new game version has no cache records. First entry into CN leaves
            // an empty overlay, which must be switchable without launching the game.
            HotUpdateTransitionPlan Plan(string source, string target, HotUpdateTransitionMode mode)
            {
                var issues = new List<ValidationIssue>();
                var plan = service.CreateTransitionPlan(source, target, "3.3.0", fixture.Game, issues);
                True(plan is not null && issues.All(x => x.Severity != IssueSeverity.Error), "空热更新目录应通过预检。");
                Equal(mode, plan!.Mode);
                return plan;
            }

            service.Commit(service.BeginTransition(Plan(
                ProfileIds.Global, ProfileIds.CnOfficial, HotUpdateTransitionMode.InitializeTarget)));
            Equal(0, Directory.EnumerateFiles(active, "*", SearchOption.AllDirectories).Count());
            service.Commit(service.BeginTransition(Plan(
                ProfileIds.CnOfficial, ProfileIds.Global, HotUpdateTransitionMode.Swap)));
            Equal(emptyGlobal ? 0 : 1, Directory.EnumerateFiles(active, "*", SearchOption.AllDirectories).Count());
            var cn = service.GetStatus(ProfileIds.CnOfficial, "3.3.0", fixture.Game, ProfileIds.Global);
            True(cn.IsAvailable && !cn.IsActive && cn.FileCount == 0 && cn.TotalBytes == 0,
                "已存储的空国服覆盖层应作为有效缓存保留。");

            var transaction = service.BeginTransition(Plan(
                ProfileIds.Global, ProfileIds.CnOfficial, HotUpdateTransitionMode.Swap));
            Equal(0, Directory.EnumerateFiles(active, "*", SearchOption.AllDirectories).Count());
            True(service.Rollback(transaction), "恢复空目标缓存后仍应能完整回滚。");
            var global = service.GetStatus(ProfileIds.Global, "3.3.0", fixture.Game, ProfileIds.Global);
            True(global.IsAvailable && global.IsActive, "回滚后应恢复原国际服缓存。");
            Equal(emptyGlobal ? 0 : 1, global.FileCount);
            if (!emptyGlobal) Equal("global-block", File.ReadAllText(Path.Combine(active, "global.blk")));
            Equal("shared-base", File.ReadAllText(Path.Combine(baseBlocks, "base.blk")));
            True(!File.Exists(fixture.Paths.HotUpdateJournalFile), "完整回滚后应清理事务。");
        }
        return Task.CompletedTask;
    }

    private static Task EmptyHotUpdateCacheRecovery()
    {
        foreach (var copy in new[] { false, true })
        foreach (var beforeMove in new[] { false, true })
        {
            using var fixture = new TempFixture();
            var service = new HotUpdateCacheService(
                fixture.Paths, new FakeProcessMonitor(), forceVerifiedCopyTransfers: copy);
            var active = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
            Directory.CreateDirectory(active);
            var manifest = service.InitializeActive(ProfileIds.Global, "3.0.0", fixture.Game);
            Equal(0, manifest.FileCount);
            Equal(0L, manifest.TotalBytes);

            if (beforeMove)
            {
                File.WriteAllText(fixture.Paths.HotUpdateJournalFile, JsonSerializer.Serialize(new HotUpdateTransaction
                {
                    TransactionId = "empty-before-move",
                    Mode = HotUpdateTransitionMode.InitializeTarget,
                    SourceProfile = ProfileIds.Global,
                    TargetProfile = ProfileIds.CnOfficial,
                    GameVersion = "3.0.0",
                    GamePath = fixture.Game,
                    ActiveBlocksPath = active,
                    SourceStoredBlocksPath = manifest.StoredBlocksPath
                }, JsonSupport.Options));
            }
            else
            {
                var plan = service.CreateTransitionPlan(ProfileIds.Global, ProfileIds.CnOfficial,
                    "3.0.0", fixture.Game, new List<ValidationIssue>());
                service.BeginTransition(plan!);
            }

            // Simulate a new process using only the persistent journal and manifests.
            var restarted = new HotUpdateCacheService(
                fixture.Paths, new FakeProcessMonitor(), forceVerifiedCopyTransfers: copy);
            True(restarted.RecoverPending(ProfileIds.Global) is not null, "空覆盖层事务应可在启动时恢复。");
            True(Directory.Exists(active) && !Directory.EnumerateFileSystemEntries(active).Any(),
                "恢复后必须保留原本的空活动目录。");
            True(restarted.GetStatus(ProfileIds.Global, "3.0.0", fixture.Game, ProfileIds.Global).IsAvailable,
                "恢复后的空缓存应有效。");
            True(!Directory.Exists(manifest.StoredBlocksPath), "撤销后来源仓库应已还原为活动目录。");
            True(!File.Exists(fixture.Paths.HotUpdateJournalFile), "恢复成功应清理日志。");
        }
        return Task.CompletedTask;
    }

    private static Task EmptyHotUpdateCacheSafetyGuards()
    {
        using var fixture = new TempFixture();
        var service = new HotUpdateCacheService(fixture.Paths, new FakeProcessMonitor());
        var active = Path.Combine(fixture.Game, HotUpdateCacheService.BlocksRelativePath);
        foreach (var missing in new[] { true, false })
        {
            Directory.CreateDirectory(active);
            var plan = service.CreateTransitionPlan(ProfileIds.Global, ProfileIds.CnOfficial,
                "3.3.0", fixture.Game, new List<ValidationIssue>())!;
            if (missing) Directory.Delete(active);
            else File.WriteAllText(Path.Combine(active, "download.tmp"), "partial");

            var initializeRejected = false;
            try { service.InitializeActive(ProfileIds.Global, "3.3.0", fixture.Game); }
            catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException) { initializeRejected = true; }
            True(initializeRejected, "初始化仍须拒绝目录缺失或未完成下载。");
            var issues = new List<ValidationIssue>();
            True(service.CreateTransitionPlan(ProfileIds.Global, ProfileIds.CnOfficial,
                "3.3.0", fixture.Game, issues) is null, "预检仍须拒绝目录缺失或未完成下载。");
            True(issues.Any(x => x.Severity == IssueSeverity.Error), "预检应报告错误。");

            var transitionRejected = false;
            try { service.BeginTransition(plan); }
            catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException) { transitionRejected = true; }
            True(transitionRejected, "执行时须重新拒绝目录缺失或新出现的未完成下载。");
            True(!File.Exists(fixture.Paths.HotUpdateJournalFile), "检查失败前不得开始目录交换。");
        }
        return Task.CompletedTask;
    }
}
