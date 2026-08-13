using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ZZZSwitch.Commands;
using ZZZSwitch.Controls;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;
using ZZZSwitch.ViewModels;
using ZZZSwitch.Workflows;

namespace ZZZSwitch.Ui.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        var main = new MainWindow();
        CacheManagementWindow? cache = null;
        BackupLocationWindow? backupLocation = null;
        BackupWindow? backupHistory = null;
        var tempRoot = Path.Combine(Path.GetTempPath(), "ZZZSwitch.Ui.Smoke", Guid.NewGuid().ToString("N"));
        try
        {
            var bilibiliButton = Require<ServerSwitchCard>(main, "SwitchBilibiliButton");
            var bilibiliColumn = Require<ColumnDefinition>(main, "BilibiliColumn");
            var versionText = Require<TextBlock>(main, "AppVersionText");
            var viewModel = main.DataContext as MainWindowViewModel
                            ?? throw new InvalidOperationException("主窗口未绑定 MainWindowViewModel。");
            Layout(main, 980, 760);
            Assert(bilibiliButton.Visibility == Visibility.Visible,
                "B服切换按钮在普通 Release 界面中不可见。");
            Assert(bilibiliButton.ServerName == "B服" && bilibiliButton.IconSource is not null,
                "可复用服务器卡片未正确接收 B服展示属性。");
            Assert(bilibiliColumn.Width.IsStar,
                "B服服务器列在普通 Release 界面中未启用。");
            Assert(versionText.Text == "v1.2.2",
                $"界面版本号不正确：{versionText.Text}");
            Assert(main.Title == "ZZZSwitch",
                "Window title should not include the version number.");
            AssertStableLoadingLayout(main, viewModel, 980, 760, "默认窗口");
            AssertStableLoadingLayout(main, viewModel, 820, 680, "最窄窗口");
            AssertScaledLayout(main, viewModel, 1.25);
            AssertScaledLayout(main, viewModel, 1.5);
            AssertScaledLayout(main, viewModel, 2.0);

            viewModel.IsBusy = true;
            main.UpdateLayout();
            Assert(bilibiliButton.Command is not null && !bilibiliButton.Command.CanExecute(null),
                "忙碌状态未通过 ViewModel 禁用服务器切换命令。");
            viewModel.IsBusy = false;
            main.UpdateLayout();
            VerifyCommandRouting();
            VerifyStartupWorkflowAndDialogRouting(main, tempRoot);
            VerifyMainWindowWorkflows(tempRoot);
            Assert(main.FindName("RestoreLatestButton") is null,
                "主界面不应继续显示独立的恢复上次状态按钮。");
            Require<Button>(main, "BackupDirectoryButton");

            var usage = new CacheUsageSummary(
                @"D:\ZZZSwitchCache", 2, 2048, 1, 1, 1024, true);
            cache = new CacheManagementWindow(usage);
            Assert(Require<Button>(cache, "RestoreDefaultButton").IsEnabled,
                "自定义位置状态下应允许恢复默认缓存位置。");
            Assert(Require<Button>(cache, "DeleteObsoleteButton").IsEnabled,
                "存在旧版本缓存时应允许清理。");

            backupLocation = new BackupLocationWindow(new BackupLocationUsage(
                @"D:\ZZZSwitchBackups", 3, 6, 4096, true));
            Assert(Require<Button>(backupLocation, "RestoreDefaultButton").IsEnabled,
                "自定义备份位置状态下应允许恢复默认位置。");

            var paths = new AppPaths(Path.Combine(tempRoot, "AppData"), Path.Combine(tempRoot, "config"));
            var files = new PhysicalFileOperations();
            var monitor = new ProcessMonitor();
            var presentationBuilder = new InspectionPresentationBuilder(
                new ProfileSnapshotService(paths, files));
            var presentation = presentationBuilder.Build(
                new InspectionReport
                {
                    Game = new GameDirectoryResult
                    {
                        GamePath = Path.Combine(tempRoot, "Game"),
                        IsValid = true,
                        GameVersion = "3.1.0"
                    },
                    Detection = new DetectionResult { Profile = DetectedProfile.Global },
                    Packages =
                    [
                        Package(ProfileIds.Bilibili, "B服", 72),
                        Package(ProfileIds.CnOfficial, "国服", 32),
                        Package(ProfileIds.Global, "国际服", 24)
                    ]
                },
                [
                    Cache(ProfileIds.Global, isActive: true, 710L * 1024 * 1024),
                    Cache(ProfileIds.CnOfficial, isActive: false, 10L * 1024 * 1024 * 1024)
                ],
                readOnlyBanner: true);
            viewModel.ApplyInspection(presentation);
            viewModel.ProfileAccent = Brushes.CornflowerBlue;
            Layout(main, 980, 760);
            Assert(presentation.Packages.Contains("B服", StringComparison.Ordinal) &&
                   presentation.Report.Contains("[仅检查模式]", StringComparison.Ordinal),
                "扫描展示格式化器未生成完整摘要或只读说明。");
            var summary = Require<InspectionSummaryCard>(main, "InspectionSummary");
            Assert(Require<Button>(summary, "CacheManagementButton").IsEnabled,
                "摘要控件未绑定缓存管理可用状态。");
            Assert(Require<Button>(summary, "InitializeCacheButton").IsEnabled,
                "摘要控件未绑定缓存初始化可用状态。");
            var backups = new BackupService(files, paths);
            var state = new StateStore(paths);
            var hotUpdates = new HotUpdateCacheService(paths, monitor);
            var policy = new LegacyRestoreSafetyPolicy(state, hotUpdates);
            var restore = new RestoreService(backups, monitor, files, state, policy);
            backupHistory = new BackupWindow(
                backups,
                restore,
                policy,
                new OperationCoordinator(paths),
                Path.Combine(tempRoot, "Game"));
            Require<Button>(backupHistory, "RestoreLatestButton");

            Console.WriteLine("PASS  正式版显示国际服/国服/B服。");
            Console.WriteLine("PASS  默认与窄窗口在扫描前后宽高保持不变。");
            Console.WriteLine("PASS  125%/150%/200% 布局缩放压力验证通过。");
            Console.WriteLine("PASS  主窗口展示状态由 ViewModel 驱动，服务器卡片可复用。");
            Console.WriteLine("PASS  Command 路由、忙碌禁用和异步异常处理通过。");
            Console.WriteLine("PASS  启动恢复编排和非交互弹窗路由通过。");
            Console.WriteLine("PASS  扫描展示格式化器与独立摘要控件绑定通过。");
            Console.WriteLine("PASS  缓存管理窗口显示自定义位置与旧版本清理操作。");
            Console.WriteLine("PASS  主界面显示备份目录，恢复上次状态已集成到备份历史。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL  {ex.Message}");
            return 1;
        }
        finally
        {
            backupHistory?.Close();
            backupLocation?.Close();
            cache?.Close();
            main.Close();
            app.Shutdown();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private static T Require<T>(FrameworkElement owner, string name) where T : class =>
        owner.FindName(name) as T ?? throw new InvalidOperationException($"界面元素不存在：{name}");

    private static void AssertStableLoadingLayout(
        MainWindow main,
        MainWindowViewModel viewModel,
        double width,
        double height,
        string scenario)
    {
        var anchor = Require<Border>(main, "ContentWidthAnchor");
        var panel = Require<StackPanel>(main, "MainContentPanel");
        viewModel.Profile = "等待检测";
        viewModel.GameVersion = "—";
        viewModel.Packages = "等待扫描";
        viewModel.CacheSummary = "等待检查";
        viewModel.HasStatusIssues = false;
        Layout(main, width, height);
        var loadingWidth = anchor.ActualWidth;
        var loadingHeight = panel.ActualHeight;

        viewModel.Profile = "国际服";
        viewModel.GameVersion = "3.1.0";
        viewModel.Packages = "B服 可用：72 个文件　国服 可用：32 个文件　国际服 可用：24 个文件";
        viewModel.CacheSummary = "国际服：活动中 · 710.1 MiB　国服：可用 · 9.79 GiB";
        Layout(main, width, height);

        Assert(Math.Abs(anchor.ActualWidth - loadingWidth) < 0.1,
            $"{scenario}扫描前后宽度变化：{loadingWidth} → {anchor.ActualWidth}");
        Assert(Math.Abs(panel.ActualHeight - loadingHeight) < 0.1,
            $"{scenario}扫描前后高度变化：{loadingHeight} → {panel.ActualHeight}");
        Assert(panel.ActualWidth <= width && panel.ActualWidth > 0 && panel.ActualHeight > 0,
            $"{scenario}主内容尺寸越界或无效：{panel.ActualWidth} × {panel.ActualHeight}，窗口 {width} × {height}。");
    }

    private static void AssertScaledLayout(MainWindow main, MainWindowViewModel viewModel, double scale)
    {
        var root = main.Content as FrameworkElement
                   ?? throw new InvalidOperationException("主窗口根视觉不存在。");
        var originalTransform = root.LayoutTransform;
        try
        {
            root.LayoutTransform = new ScaleTransform(scale, scale);
            AssertStableLoadingLayout(main, viewModel, 980 * scale, 760 * scale, $"{scale:P0} 缩放");
            Assert(IsFinite(root.ActualWidth) && IsFinite(root.ActualHeight),
                $"{scale:P0} 缩放产生无效布局尺寸。");
        }
        finally
        {
            root.LayoutTransform = originalTransform;
        }
    }

    private static void Layout(MainWindow main, double width, double height)
    {
        main.Width = width;
        main.Height = height;
        var root = main.Content as FrameworkElement
                   ?? throw new InvalidOperationException("主窗口根视觉不存在。");
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        main.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        root.UpdateLayout();
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void VerifyCommandRouting()
    {
        var switchedProfile = string.Empty;
        Exception? handledException = null;
        var viewModel = new MainWindowViewModel();
        viewModel.ConfigureCommands(new MainWindowCommandHandlers(
            () => Task.FromException(new InvalidOperationException("command-test")),
            () => Task.CompletedTask,
            profile =>
            {
                switchedProfile = profile;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => { },
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("sync-command-test"),
            () => { },
            exception => handledException = exception));
        viewModel.SetInspectionCapabilities(canManageCache: true, canInitializeCache: true);

        ((AsyncRelayCommand)viewModel.SwitchBilibiliCommand).ExecuteAsync().GetAwaiter().GetResult();
        Assert(switchedProfile == ProfileIds.Bilibili, "B服 Command 未路由到正确目标 profile。");
        Assert(viewModel.CacheManagementCommand.CanExecute(null), "有效扫描状态下缓存 Command 应可执行。");

        viewModel.IsBusy = true;
        Assert(!viewModel.SwitchGlobalCommand.CanExecute(null) &&
               !viewModel.CacheManagementCommand.CanExecute(null) &&
               !viewModel.BackupsCommand.CanExecute(null),
            "忙碌状态未统一禁用写操作 Command。");
        viewModel.IsBusy = false;

        ((AsyncRelayCommand)viewModel.AutoDetectCommand).ExecuteAsync().GetAwaiter().GetResult();
        Assert(handledException?.Message == "command-test", "异步 Command 异常未交给统一错误处理器。");
        handledException = null;
        viewModel.LogsCommand.Execute(null);
        Assert(handledException?.Message == "sync-command-test", "同步 Command 异常未交给统一错误处理器。");
    }

    private static void VerifyStartupWorkflowAndDialogRouting(MainWindow main, string tempRoot)
    {
        var protectedBackup = Path.Combine(tempRoot, "Backups", "protected");
        string? prunedWith = null;
        var workflow = new StartupWorkflow(
            () => new PendingRecoveryResult
            {
                Found = true,
                Success = true,
                Message = "recovered"
            },
            () => new AppState { LastBackupPath = protectedBackup },
            path => prunedWith = path);
        var result = workflow.RunAsync("state-warning").GetAwaiter().GetResult();

        Assert(result.Recovery.Found && result.Recovery.Success &&
               result.StateWarning == "state-warning",
            "启动工作流未保留恢复结果或状态警告。");
        Assert(result.BackupPruneAttempted && result.BackupPruneSucceeded &&
               string.Equals(prunedWith, protectedBackup, StringComparison.Ordinal),
            "启动轮换未使用状态中精确关联的最后备份路径。");

        var failedPrune = new StartupWorkflow(
            () => new PendingRecoveryResult
            {
                Found = false,
                Success = true,
                Message = "none"
            },
            () => null,
            _ => throw new IOException("prune-test"));
        var failedResult = failedPrune.RunAsync(null).GetAwaiter().GetResult();
        Assert(failedResult.BackupPruneAttempted && !failedResult.BackupPruneSucceeded,
            "启动备份轮换失败应被记录且不能中断启动流程。");

        var dialogs = new MainWindowDialogCoordinator(main);
        var candidate = new GameDirectoryCandidate(Path.Combine(tempRoot, "Game"), "测试");
        Assert(ReferenceEquals(dialogs.SelectGameDirectory([candidate]), candidate),
            "单个游戏目录候选不应打开选择窗口或改变候选对象。");
        Assert(dialogs.SelectGameDirectory([]) is null,
            "空游戏目录候选列表应直接返回空结果。");
    }

    private static void VerifyMainWindowWorkflows(string tempRoot)
    {
        var workflowRoot = Path.Combine(tempRoot, "Workflows");
        var paths = new AppPaths(Path.Combine(workflowRoot, "AppData"), Path.Combine(workflowRoot, "config"));
        var operations = new OperationCoordinator(paths);
        var dialogs = new TestMainWindowDialogs();
        var busy = false;
        var refreshCount = 0;
        var inProgressCount = 0;
        string? openedPath = null;
        InspectionReport? report = new()
        {
            Game = new GameDirectoryResult
            {
                GamePath = Path.Combine(workflowRoot, "Game"),
                IsValid = true,
                GameVersion = "3.1.0"
            },
            Detection = new DetectionResult { Profile = DetectedProfile.Global }
        };
        var context = new MainWindowWorkflowContext(
            () => busy,
            () => report?.Game.GamePath ?? string.Empty,
            () => report,
            () =>
            {
                refreshCount++;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            (value, _) => busy = value,
            _ => { },
            () => inProgressCount++,
            _ => { },
            _ => Brushes.Transparent,
            (path, _) => openedPath = path);

        var switchWorkflow = new ServerSwitchWorkflow(
            null!,
            null!,
            operations,
            dialogs,
            context);
        switchWorkflow.RunAsync(ProfileIds.Global).GetAwaiter().GetResult();
        Assert(refreshCount == 1 && dialogs.LastTitle == "\u65e0\u9700\u5207\u6362",
            "Same-profile workflow did not stop before planning.");
        Assert(!dialogs.ConfirmSwitchCalled && !operations.IsBusy,
            "Same-profile workflow reached confirmation or retained the operation lock.");

        busy = true;
        switchWorkflow.RunAsync(ProfileIds.Bilibili).GetAwaiter().GetResult();
        Assert(inProgressCount == 1 && refreshCount == 1,
            "Busy workflow did not stop before refresh and planning.");
        busy = false;

        dialogs.Reset();
        report = null;
        var cacheWorkflow = new CacheManagementWorkflow(
            null!,
            null!,
            null!,
            null!,
            paths,
            null!,
            operations,
            dialogs,
            context);
        cacheWorkflow.ManageAsync().GetAwaiter().GetResult();
        Assert(dialogs.LastTitle == "\u65e0\u6cd5\u7ba1\u7406\u7f13\u5b58",
            "Cache workflow did not reject an invalid inspection report.");

        dialogs.Reset();
        dialogs.BackupAction = BackupLocationAction.OpenLocation;
        var backupWorkflow = new BackupManagementWorkflow(
            null!,
            new BackupLocationService(paths),
            null!,
            null!,
            paths,
            operations,
            dialogs,
            context);
        backupWorkflow.ManageDirectoryAsync().GetAwaiter().GetResult();
        Assert(string.Equals(openedPath, paths.DefaultBackupsRoot, StringComparison.OrdinalIgnoreCase),
            "Backup directory workflow did not route OpenLocation to the configured path.");
    }
    private static PackageStatus Package(string profile, string displayName, int fileCount) => new()
    {
        ProfileId = profile,
        DisplayName = displayName,
        Path = Path.Combine(Path.GetTempPath(), profile),
        IsAvailable = true,
        FileCount = fileCount,
        Detail = "可用"
    };

    private static HotUpdateCacheStatus Cache(string profile, bool isActive, long bytes) => new()
    {
        Profile = profile,
        IsInitialized = true,
        IsActive = isActive,
        IsAvailable = true,
        TotalBytes = bytes,
        Detail = "可用"
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
    private sealed class TestMainWindowDialogs : IMainWindowDialogs
    {
        public string? LastTitle { get; private set; }
        public bool ConfirmSwitchCalled { get; private set; }
        public BackupLocationAction BackupAction { get; set; }
        public CacheManagementAction CacheAction { get; set; }

        public bool? Show(
            string title,
            string message,
            MessageTone tone = MessageTone.Information,
            string? subtitle = null,
            bool showCancel = false,
            string primaryText = "OK",
            Brush? accentBrush = null)
        {
            LastTitle = title;
            return true;
        }

        public GameDirectoryCandidate? SelectGameDirectory(IReadOnlyList<GameDirectoryCandidate> candidates) =>
            candidates.FirstOrDefault();

        public string? SelectFolder(
            string description,
            string? currentPath = null,
            bool showNewFolderButton = true) => null;

        public CacheManagementAction SelectCacheManagementAction(CacheUsageSummary usage) => CacheAction;

        public BackupLocationAction SelectBackupLocationAction(BackupLocationUsage usage) => BackupAction;

        public bool ConfirmSwitch(SwitchConfirmationRequest request)
        {
            ConfirmSwitchCalled = true;
            return false;
        }

        public void ShowBackupHistory(
            BackupService backups,
            RestoreService restore,
            LegacyRestoreSafetyPolicy safetyPolicy,
            OperationCoordinator operations,
            string gamePath)
        {
        }

        public void Reset()
        {
            LastTitle = null;
            ConfirmSwitchCalled = false;
            BackupAction = BackupLocationAction.None;
            CacheAction = CacheManagementAction.None;
        }
    }
}
