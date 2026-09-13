using System.IO;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.ViewModels;
using ZZZSwitch.Workflows;

namespace ZZZSwitch.Ui.Smoke;

internal static partial class Program
{
    private static async Task VerifyInspectionMaintenanceWorkflows(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "ExtractedWorkflows");
        var game = Path.Combine(root, "Game");
        Directory.CreateDirectory(Path.Combine(game, "ZenlessZoneZero_Data"));
        foreach (var name in new[] { "ZenlessZoneZero.exe", "GameAssembly.dll" })
            File.WriteAllText(Path.Combine(game, name), "fixture");
        File.WriteAllText(Path.Combine(game, "version_info"), "3.2.0");
        File.WriteAllText(Path.Combine(game, "config.ini"), "[General]\ngame_version=3.2.0\n");
        var paths = new AppPaths(Path.Combine(root, "state"), Path.Combine(AppContext.BaseDirectory, "config"));
        var files = new PhysicalFileOperations();
        var store = new StateStore(paths);
        store.Save(new AppState { GamePath = game, GameVersion = "3.2.0", LastBackupPath = "retained-backup", LastOperationId = "retained-operation" });
        var stateBefore = File.ReadAllText(paths.StateFile);
        var configBefore = File.ReadAllText(Path.Combine(game, "config.ini"));
        var configuration = new ConfigurationRepository(paths);
        var monitor = new WorkflowProcessMonitor();
        var journals = new FileTransactionJournalStore(paths);
        var inspection = new InspectionService(configuration, new GameDirectoryService(), new ProfileDetector(paths), store,
            monitor, journals, inspectLocalPackages: false, reuseConfirmedDetection: true);
        var online = new WorkflowOnlineService();
        var archiveReads = 0;
        var bundle = new BundledBilibiliPackageService(configuration, () =>
        {
            archiveReads++;
            return typeof(App).Assembly.GetManifestResourceStream("ZZZSwitch.BundledPackages.Bilibili.3.1.0.zip")!;
        }, "3.1.0", "bilibili-3.1.0-v1");
        var operations = new OperationCoordinator(paths);
        var vm = new MainWindowViewModel { GamePath = game };
        InspectionReport? published = null;
        var publications = 0;
        var failPresentation = false;
        var ui = new InspectionUiContext(vm, (report, readOnly) =>
        {
            if (failPresentation && report is not null) throw new IOException("presentation-failure");
            published = report;
            publications++;
            vm.Report = report is null ? "" : "inspected";
        }, (busy, status) => vm.SetBusy(busy, status, true), (cn, en) => cn);
        var flow = new InspectionWorkflow(inspection, online, bundle, journals, paths, operations, ui);
        var backups = new BackupService(files, paths);
        var recovery = new PendingTransactionRecoveryService(paths, store, backups,
            new HotUpdateCacheService(paths, monitor), journals, monitor);
        var resets = 0;
        var maintenance = new MaintenanceWorkflow(operations, inspection, recovery,
            new ClientMaintenanceService(paths, inspection), new GameDirectoryService(), bundle, online, ui,
            () => { flow.ResetManifestAttempts(); resets++; });

        await maintenance.RunAsync(CheckAction.Detect);
        Assert(published is not null && !vm.IsBusy && !operations.IsBusy, "只读检查结束应复位。");
        Assert(online.RefreshCalls == 0 && archiveReads == 0 && File.ReadAllText(paths.StateFile) == stateBefore &&
            File.ReadAllText(Path.Combine(game, "config.ini")) == configBefore, "只读检查不能获取清单、安装组件或改写状态/INI。");
        vm.SetBusy(true, "outer-operation", true);
        var before = publications;
        Assert(await flow.RunAsync() is null && publications == before, "重复检查应被忙碌状态拒绝。");
        await flow.RunAsync(showReadOnlyBanner: true, allowWhileBusy: true);
        Assert(vm.IsBusy && vm.ShowCompactStatus, "内部复检不能解除外层忙碌。");
        vm.SetBusy(false, "done", true);
        await flow.RunAsync();
        Assert(online.RefreshCalls == 1 && archiveReads == 1, "首次普通检查应仅尝试一次清单和组件准备。");
        await flow.RunAsync();
        Assert(online.RefreshCalls == 1 && archiveReads == 1, "重复检查不能增加下载或归档读取。");
        await maintenance.RunAsync(CheckAction.Reset);
        Assert(resets == 1 && store.Load()!.LastBackupPath == "retained-backup" &&
            store.Load()!.LastOperationId == "retained-operation", "重置必须保留备份和操作关联。");
        await flow.RunAsync();
        Assert(online.RefreshCalls == 2 && archiveReads == 1, "重置后清单允许重试，已准备组件继续复用。");

        failPresentation = true;
        await flow.RunAsync(showReadOnlyBanner: true);
        Assert(!vm.IsBusy && published is null && vm.HasStatusIssues && vm.Report == "presentation-failure", "检查异常后应清除旧报告并复位。");
        try { await maintenance.RunAsync(CheckAction.Detect); throw new Exception("Missing failure"); }
        catch (IOException ex) when (ex.Message == "presentation-failure") { }
        Assert(!vm.IsBusy && !operations.IsBusy, "维护异常不得留下操作锁或忙碌状态。");
        failPresentation = false;
        await maintenance.RunAsync(CheckAction.Detect);
        Assert(published is not null, "异常后应能再次检查。");

        online.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        online.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = maintenance.RunAsync(CheckAction.Repair);
        await online.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refreshes = online.RefreshCalls;
        await maintenance.RunAsync(CheckAction.Repair);
        Assert(vm.IsBusy && operations.IsBusy && online.RefreshCalls == refreshes, "重复修复不能进入第二个操作或提前释放锁。");
        online.Gate.SetResult();
        var repairMessage = await repair;
        Assert(repairMessage.Contains("可再次重试") && !vm.IsBusy && !operations.IsBusy, "网络修复失败必须可重试且复位。");
        online.Gate = null;
        await maintenance.RunAsync(CheckAction.Repair);
        Assert(online.RefreshCalls == refreshes + 1, "再次修复必须实际重试清单请求。");

        File.WriteAllText(paths.FileTransactionJournalFile, "{broken");
        var retained = File.ReadAllText(paths.FileTransactionJournalFile);
        var oldResets = resets;
        refreshes = online.RefreshCalls;
        await maintenance.RunAsync(CheckAction.Reset);
        await maintenance.RunAsync(CheckAction.Repair);
        Assert(resets == oldResets && online.RefreshCalls == refreshes, "待恢复事务必须阻止重置和修复写入。");
        await maintenance.RunAsync(CheckAction.Detect);
        var failedRecovery = await maintenance.RunAsync(CheckAction.Recover);
        Assert(!string.IsNullOrWhiteSpace(failedRecovery) && File.ReadAllText(paths.FileTransactionJournalFile) == retained && !vm.IsBusy,
            "恢复依据损坏应保留日志并恢复界面可用状态。");
        // Simulate corrected fixture evidence; actual rollback permutations remain covered by core tests.
        File.Delete(paths.FileTransactionJournalFile);
        await maintenance.RunAsync(CheckAction.Recover);
        Assert(!operations.IsBusy && published is not null, "恢复依据修复后入口应能重试。");

        vm.ApplyInlineSwitchResult("成功", "Success", true, (cn, en) => cn);
        vm.RefreshInlineLanguage((cn, en) => en);
        Assert(vm.BusyStatus == "Success" && vm.ProgressValue == 1, "行内结果语言切换应保留状态。");
        vm.SetBusy(true, "next", false);
        vm.RefreshInlineLanguage((cn, en) => en);
        Assert(vm.BusyStatus == "next" && !vm.ShowCompactStatus, "新操作不能复用旧结果。");
        vm.ApplyOperationProgress(new OperationProgress { Step = "rollback", IsRollingBack = true }, "rollback", (cn, en) => en);
        Assert(vm.IsProgressIndeterminate && vm.BusyStatus.Contains("Rolling back"), "回滚进度状态应统一更新。");
        vm.SetBusy(false, "done", false);
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        vm.SetBusy(false, "done", false);
        vm.SetInspectionCapabilities(false, false);
        Assert(notifications == 0, "相同状态不能重复通知界面。");
        Console.WriteLine("PASS  检查与维护流程脱离窗口验证只读、重复点击、异常复位、重试和缓存调用次数。");
    }

    private sealed class WorkflowProcessMonitor : IProcessMonitor
    {
        public IReadOnlyList<string> FindRelatedProcesses() => [];
    }

    private sealed class WorkflowOnlineService : IOnlineDifferenceService
    {
        public int RefreshCalls;
        public TaskCompletionSource? Gate;
        public TaskCompletionSource? Entered;
        public OnlineDifferenceInventory GetInventory() => new() { Packages = [] };
        public bool TryGetReadyMaterialization(string sourceProfile, string targetProfile, string gameVersion,
            out OnlineDifferenceMaterialization? materialization) { materialization = null; return false; }
        public (OnlineDifferenceMaterialization? Forward, OnlineDifferenceMaterialization? Reverse) GetReadyMaterializations(
            string sourceProfile, string targetProfile, string gameVersion) => (null, null);
        public Task<OnlineDifferencePlan> AnalyzeAsync(string sourceProfile, string targetProfile, string gameVersion,
            string? localGamePath = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<OnlineManifestRefreshResult> RefreshManifestsAsync(string gameVersion, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            Entered?.TrySetResult();
            if (Gate is not null) await Gate.Task;
            throw new IOException("fixture-network-failure");
        }
        public Task<OnlineManifestBrowserData> GetManifestBrowserAsync(string gameVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OnlineDifferenceMaterialization> MaterializeAsync(OnlineDifferencePlan plan,
            IProgress<OnlineDifferenceProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
