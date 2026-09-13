using System.IO;
using System.Text.Json;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.ViewModels;
using ZZZSwitch.Workflows;
using ZZZSwitch.Presentation;

namespace ZZZSwitch.Ui.Smoke;

internal static partial class Program
{
    private static async Task VerifyBackupRestoreWorkflow(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "BackupRestoreWorkflow");
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "version_info"), "3.2.0");
        File.WriteAllText(Path.Combine(game, "a.bin"), "after-switch");
        var paths = new AppPaths(Path.Combine(root, "state"), Path.Combine(root, "config"));
        var files = new PhysicalFileOperations();
        var backups = new BackupService(files, paths);
        var backup = Path.Combine(paths.BackupsRoot, "operation");
        Directory.CreateDirectory(Path.Combine(backup, "files"));
        File.WriteAllText(Path.Combine(backup, "files", "a.bin"), "before-switch");
        var record = new BackupRecord { OperationId = "operation", GamePath = game, GameVersion = "3.2.0",
            SourceProfile = ProfileIds.Global, TargetProfile = ProfileIds.CnOfficial, OperationResult = "success", BackedUpFiles = ["a.bin"] };
        backups.SaveRecord(backup, record);
        var store = new StateStore(paths);
        store.Save(new AppState { GamePath = game, GameVersion = "3.2.0", CurrentProfile = ProfileIds.CnOfficial,
            LastOperationId = "operation", LastBackupPath = backup });
        var monitor = new NoRunningProcesses();
        var policy = new LegacyRestoreSafetyPolicy(store, new HotUpdateCacheService(paths, monitor));
        var service = new RestoreService(backups, monitor, files, store, policy);
        var operations = new OperationCoordinator(paths);
        var vm = new MainWindowViewModel { GamePath = game, ActiveProfile = ProfileIds.CnOfficial };
        var refreshes = 0; var invalidations = 0; var failRefresh = false;
        var context = new MainWindowWorkflowContext(() => vm.IsBusy, () => game, () => null,
            () => Task.CompletedTask,
            () =>
            {
                Assert(!operations.IsBusy && vm.IsBusy, "复检必须在释放事务锁后且保持界面忙碌时执行。");
                refreshes++;
                if (failRefresh) throw new IOException("refresh-failure");
                vm.ActiveProfile = store.Load()!.CurrentProfile;
                return Task.CompletedTask;
            }, (busy, status) => vm.SetBusy(busy, status, true), status => vm.BusyStatus = status,
            () => { }, _ => { }, (_, _, _) => { }, _ => null!, (_, _) => { }, (cn, en) => cn,
            () => invalidations++);
        var dialogs = new TestMainWindowDialogs();
        new BackupManagementWorkflow(backups, new BackupLocationService(paths), service, policy, paths, operations, dialogs, context).ShowHistory();
        Assert(ReferenceEquals(dialogs.BackupContext, context), "备份窗口必须收到主界面的共享流程上下文。");
        var flow = new BackupRestoreWorkflow(operations, context);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = flow.RunAsync(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return service.RestoreLatest(game); });
        Assert(entered.Wait(TimeSpan.FromSeconds(10)), "恢复未启动。");
        try
        {
            Assert(vm.IsBusy && !vm.IsInteractionEnabled && vm.ShowCompactStatus && operations.IsBusy, "恢复期间主界面必须禁用且展示忙碌。");
            var second = await flow.RunAsync(() => throw new Exception("must not enter"));
            Assert(!second.Result.Success && refreshes == 0 && vm.IsBusy, "被拒绝的重复操作不能复检或释放外层忙碌。");
        }
        finally { release.Set(); }
        var restored = await task;
        Assert(restored.Result.Success && File.ReadAllText(Path.Combine(game, "a.bin")) == "before-switch", "应实际恢复隔离文件。");
        Assert(vm.ActiveProfile == ProfileIds.Global && !vm.IsBusy && !operations.IsBusy && refreshes == 1 && invalidations == 1,
            "恢复结果必须反映到主界面并完整复位。");
        var failed = await flow.RunAsync(() => throw new IOException("restore-failure"));
        Assert(!failed.Result.Success && !vm.IsBusy && refreshes == 2, "异常恢复尝试也必须复检并复位。");
        failRefresh = true;
        var refreshFailed = await flow.RunAsync(() => new OperationResult { OperationId = "test", Success = true });
        Assert(refreshFailed.Result.Success && refreshFailed.RefreshWarning?.Contains("refresh-failure") == true && !vm.IsBusy,
            "复检失败不能改变已完成恢复的结果，必须另报提示并复位。");
        foreach (var english in new[] { false, true })
        {
            var cleanup = new OperationResult { OperationId = "test", Success = true, Error = "恢复已提交，收尾清理未完成；请在检查中重试恢复。" };
            var display = BackupRestorePresentation.From(cleanup, (cn, en) => english ? en : cn);
            Assert(display.Tone == MessageTone.Warning && display.Message.Contains(cleanup.Error) &&
                display.Title == (english ? "Restored; attention required" : "恢复已完成，仍需处理"), "两种恢复入口必须显示完整收尾警告。");
            var failure = BackupRestorePresentation.From(new() { OperationId = "test", Error = "failed", RolledBack = true }, (cn, en) => english ? en : cn);
            Assert(failure.Tone == MessageTone.Error && failure.Message.Contains(english ? "undone" : "已撤销"), "失败结果应保留撤销状态。");
        }
        Console.WriteLine("PASS  备份恢复共享主界面忙碌状态、锁后复检、异常复位及完整收尾警告。");
    }
}
