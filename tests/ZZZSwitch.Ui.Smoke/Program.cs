using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZZZSwitch.Commands;
using ZZZSwitch.Controls;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;
using ZZZSwitch.ViewModels;
using ZZZSwitch.Workflows;
using ZZZSwitch.ManifestTool.Classification;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.Ui.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        var tempRoot = Path.Combine(Path.GetTempPath(), "ZZZSwitch.Ui.Smoke", Guid.NewGuid().ToString("N"));
        var main = new MainWindow();
        CacheManagementWindow? cache = null;
        BackupLocationWindow? backupLocation = null;
        BackupWindow? backupHistory = null;
        OnlineDifferenceDownloadWindow? onlineDownload = null;
        OnlineResourceManagementWindow? onlineResources = null;
        OnlineDifferencePreviewWindow? onlinePreview = null;
        OnlineManifestBrowserWindow? onlineManifestBrowser = null;
        CompactModeWindow? compact = null;
        OnboardingWindow? onboarding = null;
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
            Assert(bilibiliButton.ServerName is "B服" or "Bilibili" && bilibiliButton.IconSource is not null,
                "可复用服务器卡片未正确接收 B服展示属性。");
            foreach (var cardName in new[] { "SwitchGlobalButton", "SwitchCnButton", "SwitchBilibiliButton" })
            {
                var card = Require<ServerSwitchCard>(main, cardName);
                var icon = card.FindName("ServerIcon") as Image;
                Assert(icon?.Clip is RectangleGeometry clip && clip.RadiusX == 9 && clip.RadiusY == 9,
                    $"{cardName} 未使用圆角图标遮罩。" );
                Assert(card.FindName("DescriptionText") is null,
                    $"{cardName} 不应继续显示标题下说明。" );
            }
            Assert(bilibiliColumn.Width.IsStar,
                "B服服务器列在普通 Release 界面中未启用。");
            var expectedVersion = typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0]
                ?? throw new InvalidOperationException("程序集缺少信息版本。");
            Assert(versionText.Text == $"v{expectedVersion}",
                $"界面版本号不正确：{versionText.Text}");
            using (var bundledBilibili = typeof(MainWindow).Assembly.GetManifestResourceStream(
                       "ZZZSwitch.BundledPackages.Bilibili.3.1.0.zip"))
            {
                Assert(bundledBilibili is not null,
                    "正式程序没有包含 B 服组件归档。");
            }
            var bundledInstallGame = Path.Combine(tempRoot, "BundledInstall", "Game");
            Directory.CreateDirectory(bundledInstallGame);
            var bundledInstaller = new BundledBilibiliPackageService(
                new ConfigurationRepository(new AppPaths(
                    Path.Combine(tempRoot, "BundledInstallData"),
                    Path.Combine(AppContext.BaseDirectory, "config"))),
                () => typeof(MainWindow).Assembly.GetManifestResourceStream(
                          "ZZZSwitch.BundledPackages.Bilibili.3.1.0.zip")
                      ?? throw new InvalidDataException("内置 B 服组件资源不存在。"),
                "3.1.0",
                "ui-smoke-bilibili-3.1.0");
            var bundledInstall = bundledInstaller.EnsureInstalled(bundledInstallGame, "3.1.0");
            Assert(bundledInstall.Status == BundledBilibiliPackageStatus.Installed &&
                   bundledInstall.FileCount > 0 &&
                   bundledInstall.TotalBytes > 0,
                "正式程序内置 B 服组件没有完整解压并通过切换清单校验。");
            var upgradedInstall = bundledInstaller.EnsureInstalled(bundledInstallGame, "3.2.0");
            Assert(upgradedInstall.Status == BundledBilibiliPackageStatus.Installed &&
                   upgradedInstall.FileCount == bundledInstall.FileCount &&
                   upgradedInstall.TotalBytes == bundledInstall.TotalBytes &&
                   upgradedInstall.PackageDirectory != bundledInstall.PackageDirectory &&
                   bundledInstaller.EnsureInstalled(bundledInstallGame, "3.2.0", true).Status ==
                       BundledBilibiliPackageStatus.AlreadyInstalled &&
                   bundledInstaller.SupportsVersion("3.3.0") && !bundledInstaller.SupportsVersion("2.9.0"),
                "正式内置归档必须在 3.2 安装并校验通过，保留旧版目录且支持后续版本复用。");
            Assert(main.Title == "ZZZSwitch",
                "Window title should not include the version number.");
            Assert(Require<ScrollViewer>(main, "MainScrollViewer").FocusVisualStyle is null,
                "主内容滚动区仍会在窗口获得焦点时绘制虚线焦点框。");
            AssertStableLoadingLayout(main, viewModel, 980, 760, "默认窗口");
            AssertStableLoadingLayout(main, viewModel, 820, 680, "最窄窗口");
            AssertScaledLayout(main, viewModel, 1.25);
            AssertScaledLayout(main, viewModel, 1.5);
            AssertScaledLayout(main, viewModel, 2.0);
            // The MainWindow creates the application's normal theme manager. Disable its
            // Windows-event subscription before isolated theme managers exercise the same
            // Application resources, otherwise two managers can race on a preference event.
            var activeTheme = typeof(App)
                .GetProperty("Theme", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(app) as ThemeManager
                ?? throw new InvalidOperationException("无法取得应用主题管理器。");
            activeTheme.Dispose();
            VerifyThemeSwitching(app, main, viewModel, tempRoot);
            VerifyLocalization(app, main, tempRoot);

            viewModel.IsBusy = true;
            main.UpdateLayout();
            Assert(bilibiliButton.Command is not null && !bilibiliButton.Command.CanExecute(null),
                "忙碌状态未通过 ViewModel 禁用服务器切换命令。");
            viewModel.IsBusy = false;
            main.UpdateLayout();
            VerifyCommandRouting();
            VerifyStartupWorkflowAndDialogRouting(main, tempRoot);
            VerifyMainWindowWorkflows(tempRoot);
            VerifyPackageDeletion(tempRoot);
            Assert(main.FindName("RestoreLatestButton") is null,
                "主界面不应继续显示独立的恢复上次状态按钮。");
            Require<Button>(main, "BackupDirectoryButton");
            var compactModeButton = Require<Button>(main, "CompactModeButton");
            var settingsButton = Require<Button>(main, "SettingsButton");
            Assert(Grid.GetColumn(settingsButton) == 2 && Grid.GetColumn(compactModeButton) == 3,
                "工具栏应先显示设置按钮，再显示精简版按钮。");
            Assert(new[]
                   {
                       Require<TextBlock>(main, "BackupHistoryIcon"),
                       Require<TextBlock>(main, "BackupDirectoryIcon"),
                       Require<TextBlock>(main, "CompactModeIcon"),
                       Require<TextBlock>(main, "SettingsIcon")
                   }.All(icon => icon.VerticalAlignment == VerticalAlignment.Center && icon.FontSize == 15),
                "工具栏图标未与文字按统一基线居中。" );
            Assert(main.FindName("ImportPackageIcon") is null,
                "在线测试版主页不应继续显示手动导入差异包入口。");
            Assert(main.FindName("ThemeButton") is null,
                "主窗口右上角不应继续显示旧主题按钮。");

            var messageWindow = new ThemedMessageWindow("测试标题", "测试内容");
            var messageHeader = Require<Grid>(messageWindow, "HeaderGrid");
            Assert(messageHeader.Children.OfType<TextBlock>().Count() == 1,
                "通用提示弹窗不应继续显示标题下说明。");
            Assert(messageWindow.Title == string.Empty,
                "二级窗口标题栏仍显示重复标题。");
            AssertOverlayWindow(messageWindow, "通用提示");
            Assert(OverlayWindowDragBehavior.CanStartDragFrom(messageHeader, messageWindow) &&
                   !OverlayWindowDragBehavior.CanStartDragFrom(
                       Require<Button>(messageWindow, "PrimaryButton"),
                       messageWindow),
                "浮层窗口不能从普通内容区拖动，或点击按钮时会误触拖动。");
            messageWindow.Close();

            var originalShutdownMode = app.ShutdownMode;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var modelessOwner = new Window { Width = 240, Height = 160, ShowInTaskbar = false };
            modelessOwner.Show();
            var modelessChild = new Window
            {
                Owner = modelessOwner,
                Width = 180,
                Height = 120,
                ShowInTaskbar = false
            };
            var modelessTask = ModelessWindowPresenter.ShowAsync(modelessChild);
            Assert(modelessOwner.IsEnabled && modelessChild.IsVisible && !modelessTask.IsCompleted,
                "二级窗口仍以模态方式禁用底层页面。");
            modelessChild.Close();
            Assert(modelessTask.IsCompleted,
                "二级窗口关闭后异步等待没有结束。");
            modelessOwner.Close();
            app.ShutdownMode = originalShutdownMode;

            var switchConfirmation = new SwitchConfirmationWindow(
                ProfileIds.Global,
                "国际服",
                ProfileIds.Bilibili,
                "B服",
                "3.1.0",
                105,
                0,
                Path.Combine(tempRoot, "Backups", "global-to-bilibili"));
            var confirmationRoot = Require<Grid>(switchConfirmation, "RootGrid");
            var confirmationDetails = Require<Grid>(switchConfirmation, "DetailGrid");
            AssertOverlayWindow(switchConfirmation, "切换确认");
            Assert(confirmationRoot.RowDefinitions.Count == 4 &&
                   confirmationRoot.Children.OfType<Border>().Count() == 2 &&
                   confirmationDetails.RowDefinitions.Count == 3 &&
                   switchConfirmation.FindName("FileSourceText") is null &&
                   switchConfirmation.FindName("SnapshotText") is null &&
                   switchConfirmation.FindName("BlocksText") is null,
                "切换确认窗口仍显示文件来源、资源快照、Blocks 缓存或黄色提示区块。");
            switchConfirmation.Close();

            onlineDownload = new OnlineDifferenceDownloadWindow(
                new OnlineDifferencePlan
                {
                    SourceProfile = ProfileIds.Global,
                    TargetProfile = ProfileIds.CnOfficial,
                    GameVersion = "3.1.0",
                    SourceRegion = SophonRegion.OS,
                    TargetRegion = SophonRegion.CN,
                    TargetManifestId = "manifest-test",
                    TargetCategory = new ManifestCategory(
                        "game", "game", "manifest-test", "https://example.test/manifest", "",
                        "https://example.test/chunks", ""),
                    DownloadFiles = [],
                    DeleteFiles = [],
                    ExcludedStreamingBlocksCount = 2067,
                    ExcludedStreamingBlocksBytes = 9_995_031_492
                },
                new FailingOnlineDifferenceService());
            Assert(Require<ProgressBar>(onlineDownload, "DownloadProgressBar").Maximum == 100 &&
                   onlineDownload.FindName("ScopeText") is null &&
                   Require<TextBlock>(onlineDownload, "IntegrityText").Text == "自动校验" &&
                   Require<Grid>(onlineDownload, "DownloadRootGrid").RowDefinitions.Count == 4,
                "在线差异下载窗口仍显示测试范围或技术校验名称。");
            Assert(onlineDownload.Title == string.Empty &&
                   OverlayShell(onlineDownload).Background is SolidColorBrush downloadBackground &&
                   downloadBackground.Color == Color.FromRgb(27, 27, 27) &&
                   onlineDownload.Foreground is SolidColorBrush downloadForeground &&
                   downloadForeground.Color == Color.FromRgb(242, 242, 242),
                "客户端差异包下载窗口没有完整接入当前深浅主题资源。");
            AssertOverlayWindow(onlineDownload, "客户端差异包下载");
            Assert(string.IsNullOrEmpty(Require<TextBlock>(onlineDownload, "CurrentFileText").Text) &&
                   Require<TextBlock>(onlineDownload, "CurrentFileText").Visibility == Visibility.Collapsed &&
                   string.IsNullOrEmpty(Require<TextBlock>(onlineDownload, "DetailText").Text) &&
                   Require<TextBlock>(onlineDownload, "DetailText").Visibility == Visibility.Collapsed,
                "在线差异下载窗口空闲状态仍显示教程式说明文字。");
            Assert(Require<TextBlock>(onlineDownload, "DetailText").TextWrapping == TextWrapping.Wrap &&
                   Require<TextBlock>(onlineDownload, "ErrorText").TextWrapping == TextWrapping.Wrap,
                "在线差异下载窗口的进度或错误信息不能完整换行显示。");
            var percent = Require<TextBlock>(onlineDownload, "PercentText");
            var progressHeader = VisualTreeHelper.GetParent(percent) as DockPanel;
            Assert(progressHeader is not null && DockPanel.GetDock(percent) == Dock.Right &&
                   ReferenceEquals(progressHeader.Children[^1], Require<TextBlock>(onlineDownload, "StatusText")),
                "下载状态与百分比没有分列，可能再次显示成“下载失败29%”。");
            var retryButton = Require<Button>(onlineDownload, "StartButton");
            retryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(retryButton.IsEnabled && retryButton.Content?.ToString() == "重试" &&
                   Require<TextBlock>(onlineDownload, "ErrorText").Visibility == Visibility.Visible,
                "在线差异下载失败后重试按钮没有恢复可用状态。");

            onlineResources = new OnlineResourceManagementWindow(
                new OnlineDifferenceInventory
                {
                    Packages =
                    [
                        new OnlineDifferencePackageInfo
                        {
                            GameVersion = "3.1.0",
                            SourceProfile = ProfileIds.Global,
                            TargetProfile = ProfileIds.CnOfficial,
                            ManifestId = "manifest-cn",
                            WorkspacePath = Path.Combine(tempRoot, "cn-package"),
                            State = OnlineDifferencePackageState.Ready,
                            FileCount = 60,
                            ContentBytes = 1_113_028_690,
                            LastUpdated = DateTimeOffset.UtcNow
                        },
                        new OnlineDifferencePackageInfo
                        {
                            GameVersion = "3.1.0",
                            SourceProfile = ProfileIds.CnOfficial,
                            TargetProfile = ProfileIds.Global,
                            ManifestId = "manifest-global",
                            WorkspacePath = Path.Combine(tempRoot, "global-package"),
                            State = OnlineDifferencePackageState.Ready,
                            FileCount = 60,
                            ContentBytes = 1_113_085_220,
                            LastUpdated = DateTimeOffset.UtcNow
                        }
                    ],
                    ManifestCacheFileCount = 2,
                    ManifestCacheBytes = 36_024_104
                },
                "3.1.0");
            Assert(Require<ListBox>(onlineResources, "PackageList").Items.Count == 2 &&
                   Require<TextBlock>(onlineResources, "PackageBytesText").Text.Contains("GiB", StringComparison.Ordinal) &&
                   Require<TextBlock>(onlineResources, "ManifestCacheText").Text.Contains("2 个", StringComparison.Ordinal),
                "版本资源管理窗口没有显示双向差异包和 Sophon Manifest 占用。");
            Assert(onlineResources.Title == string.Empty &&
                   OverlayShell(onlineResources).Background is SolidColorBrush managementBackground &&
                   managementBackground.Color == Color.FromRgb(27, 27, 27) &&
                   onlineResources.Foreground is SolidColorBrush managementForeground &&
                   managementForeground.Color == Color.FromRgb(242, 242, 242),
                "客户端差异包管理窗口没有完整接入当前深浅主题资源。");
            AssertOverlayWindow(onlineResources, "客户端差异包管理");
            var packageList = Require<ListBox>(onlineResources, "PackageList");
            Assert(Require<Button>(onlineResources, "RefreshButton").IsEnabled &&
                   Require<Button>(onlineResources, "RefreshManifestButton").IsEnabled &&
                   Require<Button>(onlineResources, "BrowseManifestButton").IsEnabled &&
                   packageList.SelectedItems.Count == 0 &&
                   !Require<Button>(onlineResources, "DeleteButton").IsEnabled &&
                   !Require<Button>(onlineResources, "PreviewButton").IsEnabled &&
                   Require<TextBlock>(onlineResources, "SelectedCountText").Text == "已选 0 项",
                "差异包管理应等待明确勾选，未选中时禁用删除和单项操作。");
            Layout(onlineResources, 820, 520);
            var firstPackageItem = (ListBoxItem)packageList.ItemContainerGenerator.ContainerFromIndex(0);
            var secondPackageItem = (ListBoxItem)packageList.ItemContainerGenerator.ContainerFromIndex(1);
            var firstCheck = VisualChild<CheckBox>(firstPackageItem);
            var secondCheck = VisualChild<CheckBox>(secondPackageItem);
            Toggle(firstCheck);
            Assert(firstPackageItem.IsSelected && packageList.SelectedItems.Count == 1 &&
                   Require<Button>(onlineResources, "PreviewButton").IsEnabled &&
                   Require<Button>(onlineResources, "VerifyButton").IsEnabled &&
                   Require<Button>(onlineResources, "UpdatePackageButton").IsEnabled &&
                   Require<Button>(onlineResources, "OpenButton").IsEnabled &&
                   Require<Button>(onlineResources, "DeleteButton").IsEnabled,
                "勾选一个差异包后，选择状态与单项操作没有同步。");
            Toggle(secondCheck);
            Assert(firstCheck.IsChecked == true && secondPackageItem.IsSelected &&
                   packageList.SelectedItems.Count == 2 &&
                   Require<TextBlock>(onlineResources, "SelectedCountText").Text == "已选 2 项" &&
                   Require<Button>(onlineResources, "DeleteButton").IsEnabled &&
                   !Require<Button>(onlineResources, "PreviewButton").IsEnabled &&
                   !Require<Button>(onlineResources, "VerifyButton").IsEnabled &&
                   !Require<Button>(onlineResources, "UpdatePackageButton").IsEnabled &&
                   !Require<Button>(onlineResources, "OpenButton").IsEnabled,
                "勾选多个差异包应保留原选择并仅启用批量删除。");
            packageList.UnselectAll();
            Assert(firstCheck.IsChecked == false && secondCheck.IsChecked == false &&
                   !Require<Button>(onlineResources, "DeleteButton").IsEnabled,
                "取消选择没有同步复选框与删除按钮。");
            Toggle(firstCheck);
            var updatePackageButton = Require<Button>(onlineResources, "UpdatePackageButton");
            Assert(updatePackageButton.Background is SolidColorBrush updateBackground &&
                   updateBackground.Color == Color.FromRgb(39, 39, 39) &&
                   updatePackageButton.Foreground is SolidColorBrush updateForeground &&
                   updateForeground.Color == Color.FromRgb(242, 242, 242),
                "更新差异包按钮没有使用与工具栏一致的深色主题按钮样式。");
            Assert(Require<StackPanel>(onlineResources, "HeaderPanel").Children.Count == 1,
                "客户端差异包管理标题下仍有多余说明文字。");
            var testPackageItem = new ListBoxItem
            {
                Style = packageList.ItemContainerStyle,
                Content = new Border()
            };
            testPackageItem.ApplyTemplate();
            var selectionBorder = testPackageItem.Template.FindName("SelectionBorder", testPackageItem) as Border;
            Assert(selectionBorder is not null &&
                   selectionBorder.CornerRadius == new CornerRadius(10) &&
                   testPackageItem.FocusVisualStyle is null &&
                   ScrollViewer.GetHorizontalScrollBarVisibility(packageList) == ScrollBarVisibility.Disabled,
                "差异包列表项仍可能使用系统方形选中背景，导致卡片左右圆角被裁切。");
            var tooltip = new ToolTip
            {
                Content = "从 Sophon 下载 Manifest",
                Style = (Style)app.FindResource(typeof(ToolTip))
            };
            tooltip.ApplyTemplate();
            Assert(tooltip.Background is SolidColorBrush tooltipBackground &&
                   tooltipBackground.Color == Color.FromRgb(39, 39, 39) &&
                   tooltip.Foreground is SolidColorBrush tooltipForeground &&
                   tooltipForeground.Color == Color.FromRgb(242, 242, 242),
                "深色主题 ToolTip 仍可能显示成白色空白条。");
            onlinePreview = new OnlineDifferencePreviewWindow(new OnlineDifferencePackagePreview
            {
                Package = new OnlineDifferencePackageInfo
                {
                    GameVersion = "3.1.0",
                    SourceProfile = ProfileIds.Global,
                    TargetProfile = ProfileIds.CnOfficial,
                    ManifestId = "manifest-cn",
                    WorkspacePath = Path.Combine(tempRoot, "cn-package"),
                    State = OnlineDifferencePackageState.Ready,
                    FileCount = 1,
                    ContentBytes = 1024,
                    LastUpdated = DateTimeOffset.UtcNow
                },
                Files =
                [
                    new OnlineDifferencePreviewFile("GameAssembly.dll", 1024, "ABCDEF", "已就绪")
                ],
                DeleteFiles = [],
                Notes = "测试预览"
            });
            Assert(Require<ListBox>(onlinePreview, "FileList").Items.Count == 1 &&
                   Require<TextBlock>(onlinePreview, "FileCountText").Text == "1 个",
                "客户端差异包预览窗口没有显示文件清单。");
            var manifestFiles = new[]
            {
                new OnlineManifestBrowseFile(
                    "GameAssembly.dll", 1024, "00112233445566778899AABBCCDDEEFF",
                    ManifestChangeType.Modified, ManifestFileClass.BaseClient,
                    true, false, false, false, false),
                new OnlineManifestBrowseFile(
                    @"ZenlessZoneZero_Data\StreamingAssets\Video\HD\MainStory\plot.usm",
                    2048, "00112233445566778899AABBCCDDEEFF",
                    null, null, false, true, false, false, false),
                new OnlineManifestBrowseFile(
                    @"ZenlessZoneZero_Data\StreamingAssets\Blocks\data.blk",
                    4096, "00112233445566778899AABBCCDDEEFF",
                    null, null, false, false, false, true, false)
            };
            static OnlineManifestDirection Direction(
                string source,
                string target,
                SophonRegion region,
                IReadOnlyList<OnlineManifestBrowseFile> files) => new()
            {
                SourceProfile = source,
                TargetProfile = target,
                TargetManifest = new OnlineManifestSummary(
                    region, "manifest-test", files.Count, files.Sum(file => file.Size), DateTimeOffset.UtcNow),
                Files = files
            };
            onlineManifestBrowser = new OnlineManifestBrowserWindow(new OnlineManifestBrowserData
            {
                GameVersion = "3.1.0",
                GlobalToCn = Direction(ProfileIds.Global, ProfileIds.CnOfficial, SophonRegion.CN, manifestFiles),
                CnToGlobal = Direction(ProfileIds.CnOfficial, ProfileIds.Global, SophonRegion.OS, manifestFiles)
            });
            var scopeCombo = Require<ComboBox>(onlineManifestBrowser, "ScopeComboBox");
            var directionCombo = Require<ComboBox>(onlineManifestBrowser, "DirectionComboBox");
            scopeCombo.ApplyTemplate();
            directionCombo.ApplyTemplate();
            var scopeSelection = scopeCombo.Template.FindName("SelectionContent", scopeCombo) as ContentPresenter;
            var directionSelection = directionCombo.Template.FindName("SelectionContent", directionCombo) as ContentPresenter;
            Assert(scopeCombo.Items.Count == 6 &&
                   scopeCombo.Items[0]?.ToString() == "全部资源" &&
                   scopeCombo.Items[5]?.ToString() == "客户端差异" &&
                   directionCombo.SelectedIndex == 0 &&
                   scopeSelection?.Content?.ToString() == "全部资源" &&
                   directionSelection?.Content?.ToString() == "国际服 → 国服" &&
                   Grid.GetColumn(Require<StackPanel>(onlineManifestBrowser, "ScopePanel")) == 0 &&
                   Grid.GetColumn(Require<StackPanel>(onlineManifestBrowser, "DirectionPanel")) == 1 &&
                   Require<ListBox>(onlineManifestBrowser, "ResourceList").Items.Count == 3,
                "Manifest 浏览器的资源范围顺序、选中内容显示、目标方向位置或默认全部资源不正确。");
            Assert(Require<Border>(onlineManifestBrowser, "ResourceHeader").Visibility == Visibility.Visible &&
                   Require<TextBlock>(onlineManifestBrowser, "PathHeaderText").Text == "路径" &&
                   Require<TextBlock>(onlineManifestBrowser, "CategoryHeaderText").Text == "数据类型" &&
                   Require<TextBlock>(onlineManifestBrowser, "ChangeHeaderText").Text == "差异状态" &&
                   Require<TextBlock>(onlineManifestBrowser, "SizeHeaderText").Text == "大小" &&
                   onlineManifestBrowser.FindName("Md5HeaderText") is null,
                "Manifest 资源清单表头不完整或仍显示技术校验字段。");
            scopeCombo.SelectedIndex = 1;
            onlineManifestBrowser.UpdateLayout();
            Assert(Require<ListBox>(onlineManifestBrowser, "ResourceList").Items.Count == 1,
                "Manifest 浏览器没有识别剧情/视频资源。");
            scopeCombo.SelectedIndex = 5;
            onlineManifestBrowser.UpdateLayout();
            Assert(Require<ListBox>(onlineManifestBrowser, "ResourceList").Items.Count == 1,
                "Manifest 浏览器最后一项客户端差异范围不正确。");
            Assert(Require<StackPanel>(onlineManifestBrowser, "HeaderPanel").Children.Count == 1 &&
                   Require<Grid>(onlineManifestBrowser, "FooterGrid").Children.OfType<TextBlock>().Count() == 0,
                "Manifest 浏览器仍显示标题说明或底部无用小字。");
            VerifyDifferenceWindowThemes(
                app, onlineDownload, onlineResources, onlinePreview, onlineManifestBrowser, tempRoot);

            var usage = new CacheUsageSummary(
                @"D:\ZZZSwitchCache", 2, 2048, 1, 1, 1024, true);
            cache = new CacheManagementWindow(usage);
            AssertOverlayWindow(cache, "缓存管理");
            Assert(Require<Button>(cache, "RestoreDefaultButton").IsEnabled,
                "自定义位置状态下应允许恢复默认缓存位置。");
            Assert(Require<Button>(cache, "DeleteObsoleteButton").IsEnabled,
                "存在旧版本缓存时应允许清理。");

            backupLocation = new BackupLocationWindow(new BackupLocationUsage(
                @"D:\ZZZSwitchBackups", 3, 6, 4096, true));
            AssertOverlayWindow(backupLocation, "备份目录管理");
            Assert(Require<Button>(backupLocation, "RestoreDefaultButton").IsEnabled,
                "自定义备份位置状态下应允许恢复默认位置。");

            var settingsWindow = new SettingsWindow(new SettingsViewData(
                new UiSettings(),
                usage,
                null,
                new BackupLocationUsage(@"D:\Backups", 3, 6, 4096, true),
                null));
            Assert(settingsWindow.FindName("LanguageComboBox") is ComboBox &&
                   settingsWindow.FindName("ThemeComboBox") is ComboBox &&
                   settingsWindow.FindName("CachePathTextBlock") is TextBlock &&
                   settingsWindow.FindName("BackupPathTextBlock") is TextBlock &&
                   settingsWindow.FindName("StartCompactCheckBox") is CheckBox &&
                   settingsWindow.FindName("ExitOnCloseCheckBox") is CheckBox &&
                   settingsWindow.FindName("RunOnboardingButton") is Button,
                "设置窗口缺少语言、主题、缓存、备份或后台行为入口。");
            Assert(settingsWindow.Title == string.Empty &&
                   settingsWindow.FindName("GamePathTextBox") is null,
                "设置窗口标题不正确，或仍保留重复的游戏目录区块。");
            AssertOverlayWindow(settingsWindow, "设置");
            Assert(Require<Button>(settingsWindow, "CloseButton").Content?.ToString() == "关闭",
                "设置浮层去掉系统关闭按钮后缺少底部关闭入口。");
            Assert(Require<Button>(settingsWindow, "SaveButton").Content?.ToString() == "保存",
                "设置窗口缺少明确的保存按钮。");
            var themeCombo = Require<ComboBox>(settingsWindow, "ThemeComboBox");
            Assert(themeCombo.Items.Count == 3,
                "主题选择必须包含跟随 Windows、浅色和深色三项。");
            themeCombo.ApplyTemplate();
            Assert(themeCombo.Template.FindName("ComboBorder", themeCombo) is Border,
                "主题选择框未使用项目自定义模板。");
            var retentionCombo = Require<ComboBox>(settingsWindow, "LogRetentionComboBox");
            Assert(retentionCombo.Items.Count == 2,
                "日志保留天数只能提供 7 天和 30 天。");
            var rememberWindow = Require<CheckBox>(settingsWindow, "RememberWindowCheckBox");
            rememberWindow.ApplyTemplate();
            Assert(rememberWindow.Template.FindName("CheckBorder", rememberWindow) is Border,
                "复选框未使用项目自定义模板。");
            AssertSettingsLayout(settingsWindow, 620, 480, 1.0);
            AssertSettingsLayout(settingsWindow, 775, 600, 1.25);
            AssertSettingsLayout(settingsWindow, 1240, 960, 2.0);
            settingsWindow.Close();

            compact = new CompactModeWindow(main.DataContext);
            Assert(ReferenceEquals(compact.DataContext, main.DataContext),
                "精简窗口没有共享主窗口的切换状态与命令。");
            Require<ServerSwitchCard>(compact, "CompactGlobalButton");
            Require<ServerSwitchCard>(compact, "CompactCnButton");
            Require<ServerSwitchCard>(compact, "CompactBilibiliButton");
            Require<Button>(compact, "FullModeButton");
            Assert(compact.Width == 620 && compact.MinWidth == compact.MaxWidth &&
                   compact.SizeToContent == SizeToContent.Height &&
                   compact.ResizeMode == ResizeMode.NoResize,
                "精简窗口应保持固定宽度，并按客户区内容自动计算高度。");
            LayoutCompact(compact);
            var compactGlobal = Require<ServerSwitchCard>(compact, "CompactGlobalButton");
            var compactCn = Require<ServerSwitchCard>(compact, "CompactCnButton");
            var compactBilibili = Require<ServerSwitchCard>(compact, "CompactBilibiliButton");
            var compactStatus = Require<Grid>(compact, "CompactOperationStatus");
            var compactProgress = Require<ProgressBar>(compact, "CompactOperationProgress");
            var fullModeButton = Require<Button>(compact, "FullModeButton");
            Assert(Math.Abs(compactGlobal.ActualHeight - 68) <= 1 &&
                   Math.Abs(compactCn.ActualHeight - 68) <= 1 &&
                   Math.Abs(compactBilibili.ActualHeight - 68) <= 1,
                $"精简窗口的三服卡片没有使用扁平紧凑高度：" +
                $"{compactGlobal.ActualHeight}/{compactCn.ActualHeight}/{compactBilibili.ActualHeight}。");
            Assert(fullModeButton.ActualHeight >= fullModeButton.MinHeight && fullModeButton.ActualHeight >= 36,
                $"精简窗口的完整版按钮被裁切：{fullModeButton.ActualHeight} < 36。");
            viewModel.ActiveProfile = ProfileIds.CnOfficial;
            LayoutCompact(compact);
            Assert(!compactGlobal.IsCurrent && compactCn.IsCurrent && !compactBilibili.IsCurrent,
                "精简窗口未高亮当前国服服务器。");
            Assert(Require<Border>(compactCn, "ActiveOutline").Visibility == Visibility.Visible &&
                   Require<TextBlock>(compactCn, "StateIcon").Text == "\uE73E" &&
                   Require<TextBlock>(compactGlobal, "StateIcon").Text == "\uE72A",
                "精简窗口当前服务器未使用强调描边和勾选标识。");
            viewModel.ActiveProfile = ProfileIds.Global;
            LayoutCompact(compact);
            Assert(compactGlobal.IsCurrent && !compactCn.IsCurrent &&
                   Require<Border>(compactGlobal, "ActiveOutline").Visibility == Visibility.Visible &&
                   Require<Border>(compactCn, "ActiveOutline").Visibility == Visibility.Collapsed,
                "精简窗口当前服务器高亮未随检测结果动态更新。");
            viewModel.BusyStatus = "正在替换文件";
            viewModel.ProgressMaximum = 10;
            viewModel.ProgressValue = 4;
            viewModel.ShowCompactStatus = true;
            LayoutCompact(compact);
            Assert(compactStatus.Visibility == Visibility.Visible &&
                   compactProgress.Maximum == 10 && compactProgress.Value == 4 &&
                   Require<TextBlock>(compact, "CompactStatusText").Text == "正在替换文件",
                "精简窗口左下角没有显示共享的切换进度。");
            viewModel.ShowCompactStatus = false;

            VerifySwitchFeedback(app, main, compact, tempRoot);

            onboarding = new OnboardingWindow(new UiSettings(), string.Empty);
            Require<Border>(onboarding, "StepOneIndicator");
            Require<Border>(onboarding, "StepTwoIndicator");
            Require<Border>(onboarding, "StepThreeIndicator");
            Require<ComboBox>(onboarding, "LanguageComboBox");
            Require<ComboBox>(onboarding, "ThemeComboBox");
            Require<TextBox>(onboarding, "GamePathTextBox");
            Require<Button>(onboarding, "DetectButton");
            Require<Button>(onboarding, "BrowseButton");
            var nextButton = Require<Button>(onboarding, "NextButton");
            var nextButtonText = Require<TextBlock>(onboarding, "NextButtonText");
            Assert(System.Windows.Data.BindingOperations.GetBindingExpression(
                       nextButtonText,
                       TextBlock.ForegroundProperty) is not null,
                "引导主按钮文字没有跟随按钮主题前景色。");
            nextButton.IsEnabled = false;
            Layout(onboarding, 640, 460);
            Assert(nextButton.Background == onboarding.FindResource("RaisedSurfaceBrush") &&
                   nextButton.Foreground == onboarding.FindResource("MutedTextBrush"),
                "深色主题下禁用的主按钮仍使用错误的亮色主题。");
            Assert(Require<RadioButton>(onboarding, "FullModeRadio").IsChecked == true &&
                   Require<RadioButton>(onboarding, "CompactModeRadio").IsChecked == false,
                "首次引导默认启动模式应为完整窗口。");
            Assert(Require<RadioButton>(onboarding, "CloseToTrayRadio").IsChecked == true &&
                   Require<RadioButton>(onboarding, "ExitOnCloseRadio").IsChecked == false,
                "首次引导默认关闭行为应为隐藏到托盘。");

            var paths = new AppPaths(Path.Combine(tempRoot, "AppData"), Path.Combine(tempRoot, "config"));
            var files = new PhysicalFileOperations();
            var monitor = new ProcessMonitor();
            var presentationBuilder = new InspectionPresentationBuilder(
                new ProfileSnapshotService(paths, files),
                new OnlineDifferencePackageCatalog(paths));
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
            Assert(presentation.Packages.Contains("3.1.0", StringComparison.Ordinal) &&
                   presentation.Packages.Contains("未下载", StringComparison.Ordinal) &&
                   presentation.Report.Contains("[仅检查模式]", StringComparison.Ordinal) &&
                   presentation.Report.Contains("Sophon", StringComparison.Ordinal),
                "扫描展示格式化器未生成完整摘要或只读说明。");
            var englishPresentation = presentationBuilder.Build(
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
                readOnlyBanner: true,
                AppLanguage.English);
            Assert(englishPresentation.Profile == "Global" &&
                   englishPresentation.Packages.Contains("not downloaded", StringComparison.Ordinal) &&
                   englishPresentation.CacheSummary.Contains("Global: Active", StringComparison.Ordinal) &&
                   englishPresentation.Report.Contains("[Inspection only]", StringComparison.Ordinal) &&
                   !englishPresentation.Packages.Contains("可用", StringComparison.Ordinal) &&
                   !englishPresentation.CacheSummary.Contains("活动中", StringComparison.Ordinal),
                "English 动态检测结果仍混有中文。" );
            foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
            {
                var upgraded = presentationBuilder.Build(new InspectionReport
                {
                    Game = new GameDirectoryResult { GamePath = Path.Combine(tempRoot, "Game"), IsValid = true, GameVersion = "3.2.0" },
                    Detection = new DetectionResult { Profile = DetectedProfile.Unknown, NeedsManifestRefresh = true },
                    Issues = [new(IssueSeverity.Warning, "detection.manifest.required", "需要更新识别清单。")]
                }, [], false, language);
                Assert(upgraded.CanManageOnlineResources && upgraded.ExpandDetails && upgraded.ActiveProfile is null,
                    "新版本未知状态仍须允许刷新 Manifest，并展开原因。");
                if (language == AppLanguage.English)
                    Assert(upgraded.Report.Contains("refresh Manifest", StringComparison.Ordinal), "英文版未解释重新识别入口。");
            }
            var summary = Require<InspectionSummaryCard>(main, "InspectionSummary");
            Assert(Require<Button>(summary, "CacheManagementButton").IsEnabled,
                "摘要控件未绑定缓存管理可用状态。");
            Assert(summary.FindName("InitializeCacheButton") is null &&
                   summary.FindName("OnlineResourcesButton") is Button,
                "主页应以差异包管理替代旧的手动初始化缓存入口。");
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
            using var trayMenu = typeof(App)
                .GetMethod("BuildTrayMenu", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(app, null) as System.Windows.Forms.ContextMenuStrip
                ?? throw new InvalidOperationException("应用没有创建系统托盘快捷菜单。");
            var trayItems = trayMenu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
            Assert(!trayMenu.ShowImageMargin && !trayMenu.ShowCheckMargin &&
                   trayMenu.MinimumSize.Width == 184 &&
                   Math.Abs(trayMenu.Font.SizeInPoints - 9.0f) < 0.01f &&
                   trayItems.Length == 3 && trayItems.All(item => item.Height == 30),
                "中文系统托盘菜单没有使用紧凑尺寸和较小字号，或仍由独立窗口代替。" );
            var trayLocalization = new LocalizationManager(
                app,
                new AppPaths(
                    Path.Combine(tempRoot, "TrayLanguageData"),
                    Path.Combine(tempRoot, "TrayLanguageConfig")));
            trayLocalization.SetLanguage(AppLanguage.English);
            using var englishTrayMenu = typeof(App)
                .GetMethod("BuildTrayMenu", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(app, null) as System.Windows.Forms.ContextMenuStrip
                ?? throw new InvalidOperationException("应用没有创建英文系统托盘快捷菜单。");
            var englishTrayItems = englishTrayMenu.Items
                .OfType<System.Windows.Forms.ToolStripMenuItem>()
                .ToArray();
            Assert(englishTrayMenu.MinimumSize.Width == 220 &&
                   Math.Abs(englishTrayMenu.Font.SizeInPoints - 9.5f) < 0.01f &&
                   englishTrayItems.Length == 3 &&
                   englishTrayItems.All(item =>
                       System.Windows.Forms.TextRenderer.MeasureText(
                           item.Text,
                           englishTrayMenu.Font,
                           new System.Drawing.Size(int.MaxValue, item.Height),
                           System.Windows.Forms.TextFormatFlags.SingleLine |
                           System.Windows.Forms.TextFormatFlags.NoPrefix |
                           System.Windows.Forms.TextFormatFlags.NoPadding).Width <= item.Width - 24),
                "英文系统托盘菜单宽度不足，仍可能截断菜单文字。" );
            trayLocalization.SetLanguage(AppLanguage.Chinese);
            var shouldOpenFromTray = typeof(App).GetMethod(
                "ShouldOpenFromTray",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("应用缺少托盘单击打开策略。");
            Assert((bool)shouldOpenFromTray.Invoke(null, [System.Windows.Forms.MouseButtons.Left])! &&
                   !(bool)shouldOpenFromTray.Invoke(null, [System.Windows.Forms.MouseButtons.Right])!,
                "托盘图标应仅在左键单击时打开窗口，右键必须保留给菜单。" );

            Toggle(secondCheck);
            Require<Button>(onlineResources, "DeleteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(onlineResources.Selection.Action == OnlineResourceManagementAction.Delete &&
                   onlineResources.Selection.Package is null && onlineResources.Selection.Packages.Count == 2 &&
                   onlineResources.Selection.Packages.Select(p => p.TargetProfile).Distinct().Count() == 2,
                "批量删除没有传递全部勾选包。");
            Console.WriteLine("PASS  正式版显示国际服/国服/B服。");
            Console.WriteLine("PASS  默认与窄窗口在扫描前后宽高保持不变。");
            Console.WriteLine("PASS  125%/150%/200% 布局缩放压力验证通过。");
            Console.WriteLine("PASS  跟随 Windows / 深色 / 浅色主题资源可动态切换。");
            Console.WriteLine("PASS  托盘菜单与应用深浅主题保持一致。");
            Console.WriteLine("PASS  中文 / English 主界面资源可动态切换。");
            Console.WriteLine("PASS  设置窗口在最窄尺寸及 125%/200% 缩放下可滚动且布局有效。");
            Console.WriteLine("PASS  精简窗口共享三服切换命令，首次引导默认关闭到托盘。");
            Console.WriteLine("PASS  切换失败状态、预检路径与阶段进度在中英文和精简窗口中正确显示。");
            Console.WriteLine("PASS  主窗口展示状态由 ViewModel 驱动，服务器卡片可复用。");
            Console.WriteLine("PASS  Command 路由、忙碌禁用和异步异常处理通过。");
            Console.WriteLine("PASS  启动恢复编排和非交互弹窗路由通过。");
            Console.WriteLine("PASS  扫描展示格式化器与独立摘要控件绑定通过。");
            Console.WriteLine("PASS  缓存管理窗口显示自定义位置与旧版本清理操作。");
            Console.WriteLine("PASS  主界面显示备份目录，恢复上次状态已集成到备份历史。");
            Console.WriteLine("PASS  差异包复选框、多选计数、单项操作及批量删除范围、取消、部分失败验证通过。");
            Console.WriteLine("PASS  客户端差异包下载窗口显示断点进度、换行错误，且失败后可重试。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL  {ex.Message}");
            return 1;
        }
        finally
        {
            onboarding?.Close();
            backupHistory?.Close();
            onlineResources?.Close();
            onlinePreview?.Close();
            onlineManifestBrowser?.Close();
            onlineDownload?.Close();
            backupLocation?.Close();
            cache?.Close();
            typeof(App).GetMethod("RequestExit", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(app, null);
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
        viewModel.Packages = "选择服务器后可下载";
        viewModel.CacheSummary = "等待检查";
        viewModel.HasStatusIssues = false;
        Layout(main, width, height);
        var loadingWidth = anchor.ActualWidth;
        var loadingHeight = panel.ActualHeight;

        viewModel.Profile = "国际服";
        viewModel.GameVersion = "3.1.0";
        viewModel.Packages = "用户选择服务器后自动分析并下载 · 无需本地替换包";
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

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in VisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T VisualChild<T>(DependencyObject parent) where T : DependencyObject =>
        VisualChildren<T>(parent).First();

    private static void Toggle(CheckBox checkBox) =>
        ((System.Windows.Automation.Provider.IToggleProvider)
            new System.Windows.Automation.Peers.CheckBoxAutomationPeer(checkBox)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();

    private static void Layout(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        var root = window.Content as FrameworkElement
                   ?? throw new InvalidOperationException("窗口根视觉不存在。");
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void LayoutCompact(CompactModeWindow window)
    {
        const double width = 620;
        window.Width = width;
        var root = window.Content as FrameworkElement
                   ?? throw new InvalidOperationException("精简窗口根视觉不存在。");
        root.Measure(new Size(width, double.PositiveInfinity));
        var desiredHeight = root.DesiredSize.Height;
        Assert(IsFinite(desiredHeight) && desiredHeight > 0,
            $"精简窗口自动高度无效：{desiredHeight}。");
        root.Arrange(new Rect(0, 0, width, desiredHeight));
        root.UpdateLayout();
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void AssertSettingsLayout(
        SettingsWindow window,
        double width,
        double height,
        double scale)
    {
        var root = window.Content as FrameworkElement
                   ?? throw new InvalidOperationException("设置窗口根视觉不存在。");
        var originalTransform = root.LayoutTransform;
        try
        {
            root.LayoutTransform = new ScaleTransform(scale, scale);
            window.Width = width;
            window.Height = height;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            Assert(IsFinite(root.ActualWidth) && IsFinite(root.ActualHeight) &&
                   root.ActualWidth > 0 && root.ActualHeight > 0 &&
                   root.ActualWidth <= width && root.ActualHeight <= height,
                $"设置窗口 {scale:P0} 缩放布局尺寸无效：{root.ActualWidth} × {root.ActualHeight}。");
        }
        finally
        {
            root.LayoutTransform = originalTransform;
        }
    }

    private static void VerifyThemeSwitching(
        App app,
        MainWindow main,
        MainWindowViewModel viewModel,
        string tempRoot)
    {
        using var theme = new ThemeManager(
            app,
            new AppPaths(Path.Combine(tempRoot, "ThemeData"), Path.Combine(tempRoot, "ThemeConfig")));
        theme.SetPreference(ThemePreference.Light);
        Layout(main, 820, 680);
        Assert(main.Background is SolidColorBrush light && light.Color == Color.FromRgb(246, 246, 246),
            $"浅色主题未动态更新已创建的主窗口：{(main.Background as SolidColorBrush)?.Color}。");
        Assert(Require<Border>(main, "HeaderBorder").Background is SolidColorBrush header &&
               header.Color == Color.FromRgb(246, 246, 246),
            "浅色主题的品牌栏仍然是黑色。 ");
        Assert(Require<Button>(main, "CompactModeButton").Background is SolidColorBrush toolButton &&
               toolButton.Color == Color.FromRgb(241, 241, 241),
            "浅色主题工具按钮没有使用略深于纯白背景的表面色。");
        var lightSwitchButton = Require<Button>(
            Require<ServerSwitchCard>(main, "SwitchGlobalButton"),
            "SwitchButton");
        Assert(lightSwitchButton.Background is SolidColorBrush switchButton &&
               switchButton.Color == Color.FromRgb(255, 255, 255),
            "浅色主题三服按钮没有恢复原来的白色表面。");
        Assert(Require<System.Windows.Shapes.Rectangle>(main, "BrandMark").Fill is SolidColorBrush logo &&
               logo.Color == Color.FromRgb(28, 28, 28),
            "浅色主题的品牌标识未切换为深色。 ");
        AssertStableLoadingLayout(main, viewModel, 820, 680, "浅色窄窗口");

        theme.SetPreference(ThemePreference.Dark);
        Layout(main, 820, 680);
        Assert(main.Background is SolidColorBrush dark && dark.Color == Color.FromRgb(27, 27, 27),
            "深色主题未动态更新已创建的主窗口。");
        AssertStableLoadingLayout(main, viewModel, 820, 680, "深色窄窗口");
    }

    private static void VerifyLocalization(App app, MainWindow main, string tempRoot)
    {
        var localization = new LocalizationManager(
            app,
            new AppPaths(Path.Combine(tempRoot, "LanguageData"), Path.Combine(tempRoot, "LanguageConfig")));
        localization.SetLanguage(AppLanguage.English);
        main.UpdateLayout();
        Assert(Require<Button>(main, "AutoDetectButton").Content?.ToString() == "Auto detect",
            "English 语言未更新主界面按钮。");
        Assert(Require<TextBlock>(main, "CompactModeText").Text == "Compact window",
            "English 语言未更新精简窗口入口。");
        Assert(Require<ServerSwitchCard>(main, "SwitchBilibiliButton").ServerName == "Bilibili",
            "English 语言未更新服务器卡片。");
        Assert(localization.TranslateKnown("正在只读扫描游戏目录与服务器状态…") ==
               "Scanning the game directory and server state…",
            "English 语言未覆盖工作流忙碌状态。");
        var setBusy = typeof(MainWindow).GetMethod(
            "SetBusy",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("主窗口缺少 SetBusy 状态入口。");
        setBusy.Invoke(main, [true, "正在只读扫描游戏目录与服务器状态…"]);
        var mainViewModel = main.DataContext as MainWindowViewModel
            ?? throw new InvalidOperationException("主窗口未绑定 MainWindowViewModel。");
        Assert(mainViewModel.BusyStatus == "Scanning the game directory and server state…",
            "英文模式的主窗口忙碌浮层仍显示中文。");
        var showProgress = typeof(MainWindow).GetMethod("ShowOperationProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (step, english) in new[]
                 {
                     ("正在检测实际文件变更", "Checking which files need changes"),
                     ("正在校验并准备实际差异文件", "Verifying and staging changed files"),
                     ("正在备份实际受影响文件", "Backing up changed files")
                 })
        {
            showProgress.Invoke(main, [new OperationProgress { Step = step, IsIndeterminate = true }]);
            Assert(mainViewModel.IsProgressIndeterminate && mainViewModel.BusyStatus == english,
                "准备阶段应显示活动进度及英文阶段说明。");
        }
        setBusy.Invoke(main, [false, "目录检测完成"]);

        localization.SetLanguage(AppLanguage.Chinese);
        var showInlineSwitchResult = typeof(MainWindow).GetMethod(
            "ShowInlineSwitchResult",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("主窗口缺少精简切换结果入口。");
        showInlineSwitchResult.Invoke(main,
            ["切换完成：替换 105，删除 0", "Switch complete: 105 replaced, 0 deleted", true]);
        Assert(mainViewModel.BusyStatus == "切换完成：替换 105，删除 0",
            "中文模式的精简切换结果不正确。");
        localization.SetLanguage(AppLanguage.English);
        var applyRuntimeSettings = typeof(MainWindow).GetMethod(
            "ApplyRuntimeSettings",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("主窗口缺少运行时设置入口。");
        applyRuntimeSettings.Invoke(main, [new UiSettings { Language = AppLanguage.English }]);
        Assert(mainViewModel.BusyStatus == "Switch complete: 105 replaced, 0 deleted",
            "精简切换结果未在切换到 English 后动态更新。");

        var confirmation = new SwitchConfirmationWindow(
            ProfileIds.Global,
            "国际服",
            ProfileIds.CnOfficial,
            "国服",
            "3.1.0",
            60,
            0,
            Path.Combine(tempRoot, "Backups", "global-to-cn"));
        Assert(Require<TextBlock>(confirmation, "HeadingText").Text == "Confirm server switch" &&
               Require<TextBlock>(confirmation, "SourceLabelText").Text == "Current server" &&
               Require<TextBlock>(confirmation, "TargetLabelText").Text == "Target server" &&
               Require<TextBlock>(confirmation, "SourceProfileText").Text == "Global" &&
               Require<TextBlock>(confirmation, "TargetProfileText").Text == "CN Official" &&
               Require<TextBlock>(confirmation, "FileOperationText").Text == "Replace 60 files · delete 0 files" &&
               Require<Button>(confirmation, "CancelButton").Content?.ToString() == "Cancel" &&
               Require<Button>(confirmation, "ConfirmButton").Content?.ToString() == "Confirm switch",
            "英文语言未完整覆盖服务器切换确认窗口。");
        confirmation.Close();

        var englishMessage = new ThemedMessageWindow(
            "Title", "Message", showCancel: true);
        Assert(Require<Button>(englishMessage, "CancelButton").Content?.ToString() == "Cancel" &&
               Require<Button>(englishMessage, "PrimaryButton").Content?.ToString() == "OK",
            "英文语言未覆盖通用提示窗口按钮。");
        englishMessage.Close();

        var englishCache = new CacheManagementWindow(
            new CacheUsageSummary(@"D:\ZZZSwitchCache", 2, 2048, 1, 1, 1024, true));
        Assert(Require<TextBlock>(englishCache, "HeadingText").Text == "Cache management" &&
               Require<TextBlock>(englishCache, "LocationModeText").Text == "Custom location" &&
               Require<TextBlock>(englishCache, "TotalFilesText").Text == "2 files" &&
               Require<Button>(englishCache, "CloseButton").Content?.ToString() == "Close",
            "英文语言未覆盖缓存管理窗口。" );
        englishCache.Close();

        var englishBackupLocation = new BackupLocationWindow(
            new BackupLocationUsage(@"D:\ZZZSwitchBackups", 3, 8, 4096, true));
        Assert(Require<TextBlock>(englishBackupLocation, "HeadingText").Text == "Backup location" &&
               Require<TextBlock>(englishBackupLocation, "LocationModeText").Text == "Custom location" &&
               Require<TextBlock>(englishBackupLocation, "UsageText").Text.Contains("3 backups · 4 KiB", StringComparison.Ordinal) &&
               Require<Button>(englishBackupLocation, "OpenButton").Content?.ToString() == "Open folder",
            "英文语言未覆盖备份目录窗口。" );
        englishBackupLocation.Close();

        var englishDirectory = new GameDirectorySelectionWindow(
            [new GameDirectoryCandidate(@"D:\ZenlessZoneZero Game", "上次使用")]);
        var directoryCandidate = Require<ListBox>(englishDirectory, "PathsList").Items[0] as GameDirectoryCandidate;
        Assert(Require<TextBlock>(englishDirectory, "HeadingText").Text == "Select game directory" &&
               directoryCandidate?.Source == "Last used" &&
               Require<Button>(englishDirectory, "ConfirmButton").Content?.ToString() == "Use this directory",
            "英文语言未覆盖游戏目录选择窗口。" );
        englishDirectory.Close();

        var englishResources = new OnlineResourceManagementWindow(
            new OnlineDifferenceInventory
            {
                Packages = [],
                ManifestCacheFileCount = 2,
                ManifestCacheBytes = 4096
            },
            "3.1.0");
        Assert(Require<TextBlock>(englishResources, "HeadingText").Text == "Client difference packages" &&
               Require<TextBlock>(englishResources, "ManifestCacheText").Text.Contains("2 files", StringComparison.Ordinal) &&
               Require<Button>(englishResources, "RefreshManifestButton").Content?.ToString() == "Update manifest",
            "英文语言未覆盖客户端差异包管理窗口。" );
        englishResources.Close();

        Assert(app.Resources["L.ManifestBrowser.Title"]?.ToString() == "Manifest resource browser" &&
               app.Resources["L.Preview.Title"]?.ToString() == "Client package manifest preview",
            "英文语言资源未覆盖 Manifest 浏览与差异预览窗口。" );
        var initialState = new MainWindowViewModel();
        initialState.ApplyInitialLanguage(AppLanguage.English);
        Assert(initialState.Profile == "Waiting for detection" &&
               initialState.Packages == "Available after server selection" &&
               initialState.CacheSummary == "Waiting for inspection",
            "English 扫描前状态仍混有中文。" );

        localization.SetLanguage(AppLanguage.Chinese);
        main.UpdateLayout();
        Assert(Require<Button>(main, "AutoDetectButton").Content?.ToString() == "自动检测",
            "中文语言未恢复主界面按钮。");
        setBusy.Invoke(main, [false, "目录检测完成"]);
        Assert(mainViewModel.BusyStatus == "目录检测完成",
            "中文语言未恢复主窗口状态文本。");
    }

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
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            exception => handledException = exception));
        viewModel.SetInspectionCapabilities(canManageCache: true, canManageOnlineResources: true);

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

    private static void VerifySwitchFeedback(App app, MainWindow main, CompactModeWindow compact, string tempRoot)
    {
        var viewModel = (MainWindowViewModel)main.DataContext;
        var showProgress = typeof(MainWindow).GetMethod("ShowOperationProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var showResult = typeof(MainWindow).GetMethod("ShowInlineSwitchResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var compactProgress = Require<ProgressBar>(compact, "CompactOperationProgress");
        var status = Require<TextBlock>(compact, "CompactStatusText");
        viewModel.ShowCompactStatus = true;
        showProgress.Invoke(main, [new OperationProgress { Step = "正在备份实际受影响文件", IsIndeterminate = true }]);
        LayoutCompact(compact);
        Assert(viewModel.IsProgressIndeterminate && compactProgress.IsIndeterminate, "精简窗口应共享备份阶段活动进度。");
        showProgress.Invoke(main, [new OperationProgress { Step = "已替换 a.bin", PlannedReplace = 2, SuccessfulReplace = 1 }]);
        LayoutCompact(compact);
        Assert(!compactProgress.IsIndeterminate && compactProgress.Value == 1 && compactProgress.Maximum == 2,
            "实际替换阶段应恢复文件数量进度。");
        showProgress.Invoke(main, [new OperationProgress { Step = "操作失败，正在回滚", IsRollingBack = true }]);
        LayoutCompact(compact);
        Assert(compactProgress.IsIndeterminate, "回滚阶段不能停留在确定进度。");

        var localization = new LocalizationManager(app, new AppPaths(Path.Combine(tempRoot, "FeedbackLanguage")));
        foreach (var (failure, chineseState, englishState) in new[]
                 {
                     ("staging",
                         "本次操作未修改游戏文件", "Game files were not modified"),
                     ("replace",
                         "已恢复到切换前状态", "Restored to the previous state"),
                     ("rollback",
                         "恢复尚未完成，需要处理", "Recovery needs attention")
                 })
        {
            // Exercise the public workflow with real planner/engine/file operations.
            // Only the dialog boundary and the precise I/O fault are substituted.
            var chinese = RunFailedSwitchWorkflow(tempRoot, failure, compact: true, english: false);
            var english = RunFailedSwitchWorkflow(tempRoot, failure, compact: true, english: true);
            foreach (var compactMode in new[] { false, true })
            {
                var chineseMessage = compactMode ? chinese : RunFailedSwitchWorkflow(tempRoot, failure, compact: false, english: false);
                var englishMessage = compactMode ? english : RunFailedSwitchWorkflow(tempRoot, failure, compact: false, english: true);
                Assert(chineseMessage.Contains(chineseState) && englishMessage.Contains(englishState) &&
                       !chineseMessage.Contains("未完成或无需"),
                    "实际切换失败后，弹窗和精简模式都必须区分未修改、已恢复和待处理。");
            }
            foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
            {
                localization.SetLanguage(language);
                showResult.Invoke(main, [chinese, english, false]);
                LayoutCompact(compact);
                var expected = language == AppLanguage.English ? english : chinese;
                Assert(status.Text == expected && status.ToolTip?.ToString() == expected && !compactProgress.IsIndeterminate,
                    "精简窗口应显示相应语言的失败摘要，悬停保留完整详情并停止进度动画。");
                Assert(status.ActualHeight <= 16.1, "错误详情不应撑高精简窗口的单行状态区域。");
            }
        }
        localization.SetLanguage(AppLanguage.Chinese);
        viewModel.ShowCompactStatus = false;
    }

    private static string RunFailedSwitchWorkflow(string tempRoot, string failure, bool compact, bool english)
    {
        var root = Path.Combine(tempRoot, "SwitchFailures", $"{failure}-{compact}-{english}");
        var game = Path.Combine(root, "Game");
        var paths = new AppPaths(Path.Combine(root, "AppData"), Path.Combine(root, "config"));
        var persistent = Path.Combine(game, "ZenlessZoneZero_Data", "Persistent");
        Directory.CreateDirectory(persistent);
        File.WriteAllText(Path.Combine(game, "ZenlessZoneZero.exe"), "fixture");
        File.WriteAllText(Path.Combine(game, "GameAssembly.dll"), "fixture");
        File.WriteAllText(Path.Combine(game, "version_info"), "3.1.0");
        File.WriteAllText(Path.Combine(game, "config.ini"), "[General]\ngame_version=3.1.0\n");
        File.WriteAllText(Path.Combine(persistent, "data_version"), "source-version");
        var package = GameStorageLayout.GetPackageDirectory(game, "3.1.0", ProfileIds.Bilibili);
        Directory.CreateDirectory(package);
        var manifest = new TransitionManifest
        {
            SourceProfile = ProfileIds.CnOfficial,
            TargetProfile = ProfileIds.Bilibili,
            GameVersion = "3.1.0"
        };
        foreach (var name in new[] { "a.bin", "b.bin" })
        {
            File.WriteAllText(Path.Combine(game, name), "original");
            File.WriteAllText(Path.Combine(package, name), "new");
            var bytes = File.ReadAllBytes(Path.Combine(package, name));
            manifest.ReplaceFiles.Add(new ReplaceFileEntry
            {
                Source = name, Target = name, Length = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            });
        }
        Directory.CreateDirectory(Path.Combine(paths.ConfigRoot, "profiles"));
        Directory.CreateDirectory(Path.Combine(paths.ConfigRoot, "transitions"));
        var profile = new ProfileDefinition
        {
            Id = ProfileIds.Bilibili, DisplayName = "Bilibili", PackageDirectoryName = ProfileIds.Bilibili,
            KeyFiles = [new FileSignature { Path = "a.bin", Length = 3 }]
        };
        File.WriteAllText(Path.Combine(paths.ConfigRoot, "profiles", "bilibili.json"), JsonSerializer.Serialize(profile, JsonSupport.Options));
        File.WriteAllText(Path.Combine(paths.ConfigRoot, "transitions", "cn-to-bilibili.json"), JsonSerializer.Serialize(manifest, JsonSupport.Options));

        var files = new SwitchFeedbackFileOperations(game, failure);
        var snapshots = new ProfileSnapshotService(paths, files);
        var planner = new SwitchPlanner(new ConfigurationRepository(paths), new GameDirectoryService(),
            new NoRunningProcesses(), files, paths, snapshots);
        var stateStore = new StateStore(paths);
        var backups = new BackupService(files, paths);
        var engine = new SwitchEngine(files, paths, backups, stateStore, new OperationLogger(paths), snapshots);
        var operations = new OperationCoordinator(paths);
        var dialogs = new TestMainWindowDialogs { ConfirmSwitchResult = true };
        var busy = false;
        var inlineCount = 0;
        var detectionInvalidations = 0;
        string? inlineMessage = null;
        var report = new InspectionReport
        {
            Game = new GameDirectoryResult { GamePath = game, GameVersion = "3.1.0", IsValid = true },
            Detection = new DetectionResult { Profile = DetectedProfile.CnOfficial }
        };
        var context = new MainWindowWorkflowContext(
            () => busy, () => game, () => report,
            () => Task.CompletedTask, () => Task.CompletedTask,
            (value, _) => busy = value, _ => { },
            () => throw new InvalidOperationException("Isolated workflow unexpectedly busy."),
            _ => { },
            (chinese, englishText, success) =>
            {
                Assert(!success, "故障切换不应报告成功。");
                inlineCount++;
                inlineMessage = english ? englishText : chinese;
            },
            _ => Brushes.Transparent, (_, _) => { },
            (chinese, englishText) => english ? englishText : chinese,
            () => detectionInvalidations++);
        var workflow = new ServerSwitchWorkflow(planner, engine, operations, null!, dialogs, context);
        // No WPF callbacks run in this fixture; avoid capturing the UI synchronization context.
        Task.Run(() => workflow.RunAsync(ProfileIds.Bilibili, compact)).GetAwaiter().GetResult();

        Assert(files.FailurePath is not null, "流程未到达预期的文件故障，可能被提前预检拦截。" + dialogs.LastMessage);
        Assert(detectionInvalidations == 1, "切换失败后应使已确认状态失效并重新识别。");
        Assert(!busy && !operations.IsBusy, "失败后应释放界面忙碌状态和操作锁。");
        Assert(dialogs.ConfirmSwitchCalled == !compact && dialogs.ShowCount == (compact ? 0 : 1) &&
               inlineCount == (compact ? 1 : 0), "主窗口应确认并弹出结果；精简模式应只报告行内结果。");
        Assert(compact || dialogs.LastTitle == (english ? "Switch failed" : "切换失败"), "主窗口应显示切换失败结果。");
        var message = (compact ? inlineMessage : dialogs.LastMessage) ?? throw new InvalidOperationException("缺少切换结果。");
        Assert(message.Contains(files.FailurePath!), "错误提示应保留实际失败文件的路径。");
        Assert(File.ReadAllText(Path.Combine(game, "b.bin")) == "original" &&
               File.ReadAllText(Path.Combine(game, "a.bin")) == (failure == "rollback" ? "new" : "original"),
            "失败提示必须与磁盘上的真实恢复结果一致。");
        Assert(stateStore.Load()?.CurrentProfile != ProfileIds.Bilibili, "失败操作不得提交目标服状态。");
        var pending = new FileTransactionJournalStore(paths);
        Assert(pending.Exists == (failure == "rollback"), "仅未完成恢复的切换应保留待恢复事务。");
        if (failure == "rollback")
        {
            var retained = backups.ListBackups().Single();
            Assert(message.Contains(retained.Path) &&
                   message.Contains(english ? "Restart ZZZSwitch" : "重新启动 ZZZSwitch"),
                "恢复未完成时应显示真实保留的备份位置和恢复指引。");
        }
        return message;
    }

    private sealed class NoRunningProcesses : IProcessMonitor
    {
        public IReadOnlyList<string> FindRelatedProcesses() => [];
    }

    private sealed class SwitchFeedbackFileOperations(string game, string failure) : IFileOperations
    {
        private readonly PhysicalFileOperations _inner = new();
        public string? FailurePath { get; private set; }
        public bool FileExists(string path) => _inner.FileExists(path);
        public long GetLength(string path) => _inner.GetLength(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void CopyFile(string source, string target, bool overwrite) => _inner.CopyFile(source, target, overwrite);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public void DeleteDirectory(string path, bool recursive) => _inner.DeleteDirectory(path, recursive);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream OpenExclusive(string path) => _inner.OpenExclusive(path);

        public Stream OpenWrite(string path, bool overwrite)
        {
            if (failure == "staging" && path.StartsWith(GameStorageLayout.GetStagingRoot(game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                FailurePath = path;
                throw new IOException($"Injected staging failure: {path}");
            }
            if (failure == "rollback" && path == Path.Combine(game, "a.bin"))
            {
                throw new IOException($"Injected rollback failure: {path}");
            }
            return _inner.OpenWrite(path, overwrite);
        }

        public void MoveFile(string source, string target, bool overwrite)
        {
            if (target == Path.Combine(game, "b.bin"))
            {
                FailurePath = target;
                throw new IOException($"Injected replacement failure: {target}");
            }
            _inner.MoveFile(source, target, overwrite);
        }
    }

    private static void VerifyPackageDeletion(string tempRoot)
    {
        var paths = new AppPaths(Path.Combine(tempRoot, "PackageDeletion"), Path.Combine(tempRoot, "config"));
        var catalog = new OnlineDifferencePackageCatalog(paths);
        var operations = new OperationCoordinator(paths);
        var dialogs = new TestMainWindowDialogs();
        var busy = false;
        var refreshes = 0;
        var context = new MainWindowWorkflowContext(
            () => busy, () => string.Empty, () => null,
            () => Task.CompletedTask,
            () => { refreshes++; return Task.CompletedTask; },
            (value, _) => busy = value, _ => { }, () => { }, _ => { },
            (_, _, _) => { }, _ => Brushes.Transparent, (_, _) => { }, (chinese, _) => chinese);
        var workflow = new OnlineResourceManagementWorkflow(catalog, null!, operations, dialogs, context);
        foreach (var (version, target) in new[]
                 {
                     ("3.1.0", ProfileIds.CnOfficial), ("3.1.0", ProfileIds.Global),
                     ("3.2.0", ProfileIds.CnOfficial)
                 })
        {
            var content = Path.Combine(paths.OnlineDifferenceFilesRoot, version, target, "fixture", "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "payload.bin"), "preserve unless selected");
        }
        var original = catalog.GetInventory().Packages;
        var old = original.Where(p => p.GameVersion == "3.1.0").ToArray();
        var current = original.Single(p => p.GameVersion == "3.2.0");
        var batch = new OnlineResourceManagementSelection(OnlineResourceManagementAction.Delete, null)
        {
            Packages = [old[0], old[1], old[0]]
        };
        dialogs.ConfirmDeletion = false;
        dialogs.PackageSelections.Enqueue(batch);
        workflow.ManageAsync().GetAwaiter().GetResult();
        Assert(original.All(p => Directory.Exists(p.WorkspacePath)) && refreshes == 0 &&
               dialogs.LastConfirmation?.Contains("选中的 2 个") == true &&
               !dialogs.LastConfirmation.Contains("3.2.0", StringComparison.Ordinal),
            "取消批量删除应保留所有包，确认列表应去重且只列出勾选项。");

        dialogs.ConfirmDeletion = true;
        dialogs.PackageSelections.Enqueue(batch);
        using (var locked = File.Open(Path.Combine(old[0].WorkspacePath, "content", "payload.bin"),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            workflow.ManageAsync().GetAwaiter().GetResult();
            Assert(Directory.Exists(old[0].WorkspacePath) && !Directory.Exists(old[1].WorkspacePath) &&
                   Directory.Exists(current.WorkspacePath) &&
                   dialogs.LastTitle == "部分差异包未能删除" &&
                   dialogs.LastMessage?.Contains("已删除 1 个") == true &&
                   dialogs.LastMessage.Contains("3.1.0", StringComparison.Ordinal) &&
                   refreshes == 1 && !busy && !operations.IsBusy,
                "批量删除应准确报告部分失败、继续处理其他勾选项、保留未选项并释放忙碌状态。");
        }
        dialogs.PackageSelections.Enqueue(new(OnlineResourceManagementAction.Delete, old[0]));
        workflow.ManageAsync().GetAwaiter().GetResult();
        Assert(!Directory.Exists(old[0].WorkspacePath) &&
               File.ReadAllText(Path.Combine(current.WorkspacePath, "content", "payload.bin")) == "preserve unless selected" &&
               catalog.GetInventory().Packages.Count == 1 && dialogs.LastTitle == "客户端差异包已删除" &&
               refreshes == 2 && !busy && !operations.IsBusy,
            "重试删除单项失败包后应刷新列表并完整保留未勾选的新版本包。");
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
            (_, _, _) => { },
            _ => Brushes.Transparent,
            (path, _) => openedPath = path,
            (chinese, _) => chinese);

        var switchWorkflow = new ServerSwitchWorkflow(
            null!,
            null!,
            operations,
            null!,
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

        var files = new PhysicalFileOperations();
        var legacyPlanner = new SwitchPlanner(
            new ConfigurationRepository(paths),
            new GameDirectoryService(),
            new ProcessMonitor(),
            files,
            paths,
            new ProfileSnapshotService(paths, files));
        var onlineRoutingProbe = new FailingOnlineDifferenceService();
        report = new InspectionReport
        {
            Game = new GameDirectoryResult { GamePath = Path.Combine(workflowRoot, "Game"), IsValid = true, GameVersion = "3.2.0" },
            Detection = new DetectionResult { Profile = DetectedProfile.Global }
        };
        var unsupportedBundle = new BundledBilibiliPackageService(new ConfigurationRepository(paths),
            () => throw new InvalidOperationException("Unsupported bundle must not be opened"), "3.1.0", "test");
        new ServerSwitchWorkflow(legacyPlanner, null!, operations, onlineRoutingProbe, dialogs, context, unsupportedBundle)
            .RunAsync(ProfileIds.Bilibili).GetAwaiter().GetResult();
        Assert(dialogs.LastTitle == "当前版本暂不支持 B 服切换" && onlineRoutingProbe.AnalyzeCalls == 0 &&
               onlineRoutingProbe.TryGetReadyCalls == 0 && !operations.IsBusy,
            "新版 B 服方向应在下载基础差异包或打开旧组件前解释版本不支持。");
        var bilibiliWorkflow = new ServerSwitchWorkflow(
            legacyPlanner,
            null!,
            operations,
            onlineRoutingProbe,
            dialogs,
            context);
        report = new InspectionReport
        {
            Game = new GameDirectoryResult
            {
                GamePath = Path.Combine(workflowRoot, "Game"),
                IsValid = true,
                GameVersion = "3.1.0"
            },
            Detection = new DetectionResult { Profile = DetectedProfile.CnOfficial }
        };
        dialogs.Reset();
        bilibiliWorkflow.RunAsync(ProfileIds.Bilibili).GetAwaiter().GetResult();
        Assert(dialogs.LastTitle == "切换前检查未通过" &&
               onlineRoutingProbe.TryGetReadyCalls == 0 &&
               onlineRoutingProbe.AnalyzeCalls == 0,
            "国服切到 B 服没有只走本地覆盖层预检，或错误调用了 Sophon 在线差异。");
        Assert(dialogs.LastMessage?.Contains(report.Game.GamePath) == true && dialogs.LastMessage.Contains("路径:"),
            "预检错误应展示具体问题路径。");
        new ServerSwitchWorkflow(legacyPlanner, null!, operations, onlineRoutingProbe, dialogs,
                context with { Localize = (_, english) => english })
            .RunAsync(ProfileIds.Bilibili).GetAwaiter().GetResult();
        Assert(dialogs.LastTitle == "Pre-switch checks failed" && dialogs.LastMessage?.Contains("Path:") == true &&
               dialogs.LastMessage.Contains(report.Game.GamePath), "英文预检也应展示完整路径。");

        dialogs.Reset();
        report = new InspectionReport
        {
            Game = new GameDirectoryResult
            {
                GamePath = Path.Combine(workflowRoot, "Game"),
                IsValid = true,
                GameVersion = "3.1.0"
            },
            Detection = new DetectionResult { Profile = DetectedProfile.Bilibili }
        };
        bilibiliWorkflow.RunAsync(ProfileIds.CnOfficial).GetAwaiter().GetResult();
        Assert(dialogs.LastTitle == "切换前检查未通过" &&
               onlineRoutingProbe.TryGetReadyCalls == 0 &&
               onlineRoutingProbe.AnalyzeCalls == 0,
            "B 服切回国服没有只走本地覆盖层预检，或错误调用了 Sophon 在线差异。");

        dialogs.Reset();
        report = new InspectionReport
        {
            Game = new GameDirectoryResult
            {
                GamePath = Path.Combine(workflowRoot, "Game"),
                IsValid = true,
                GameVersion = "3.1.0"
            },
            Detection = new DetectionResult { Profile = DetectedProfile.Global }
        };
        var crossRegionBilibiliProbe = new FailingOnlineDifferenceService();
        var crossRegionBilibiliWorkflow = new ServerSwitchWorkflow(
            legacyPlanner,
            null!,
            operations,
            crossRegionBilibiliProbe,
            dialogs,
            context);
        crossRegionBilibiliWorkflow.RunAsync(ProfileIds.Bilibili).GetAwaiter().GetResult();
        Assert(dialogs.LastTitle == "无法获取客户端差异包" &&
               crossRegionBilibiliProbe.AnalyzeRequests.Contains(
                   (ProfileIds.Global, ProfileIds.CnOfficial)),
            "国际服切到 B 服没有请求国际服到国服的在线基础差异。");

        dialogs.Reset();
        report = new InspectionReport
        {
            Game = new GameDirectoryResult
            {
                GamePath = Path.Combine(workflowRoot, "Game"),
                IsValid = true,
                GameVersion = "3.1.0"
            },
            Detection = new DetectionResult { Profile = DetectedProfile.Global }
        };
        var verificationProbe = new FailingOnlineDifferenceService();
        var onlineWorkflow = new ServerSwitchWorkflow(
            legacyPlanner,
            null!,
            operations,
            verificationProbe,
            dialogs,
            context);
        onlineWorkflow.RunAsync(ProfileIds.CnOfficial).GetAwaiter().GetResult();
        Assert(verificationProbe.AnalyzeRequests.Contains(
                (ProfileIds.Global, ProfileIds.CnOfficial)),
            "国际服切国服没有请求正确方向的在线差异。");

        dialogs.Reset();
        report = new InspectionReport
        {
            Game = new GameDirectoryResult
            {
                GamePath = Path.Combine(workflowRoot, "Game"),
                IsValid = true,
                GameVersion = "3.1.0"
            },
            Detection = new DetectionResult { Profile = DetectedProfile.CnOfficial }
        };
        var cnToGlobalProbe = new FailingOnlineDifferenceService();
        new ServerSwitchWorkflow(
                legacyPlanner, null!, operations, cnToGlobalProbe, dialogs, context)
            .RunAsync(ProfileIds.Global).GetAwaiter().GetResult();
        Assert(cnToGlobalProbe.AnalyzeRequests.Contains(
                (ProfileIds.CnOfficial, ProfileIds.Global)),
            "国服切国际服没有请求正确方向的在线差异。");

        dialogs.Reset();
        report = new InspectionReport
        {
            Game = new GameDirectoryResult
            {
                GamePath = Path.Combine(workflowRoot, "Game"),
                IsValid = true,
                GameVersion = "3.1.0"
            },
            Detection = new DetectionResult { Profile = DetectedProfile.Bilibili }
        };
        var bilibiliToGlobalProbe = new FailingOnlineDifferenceService();
        new ServerSwitchWorkflow(
                legacyPlanner, null!, operations, bilibiliToGlobalProbe, dialogs, context)
            .RunAsync(ProfileIds.Global).GetAwaiter().GetResult();
        Assert(bilibiliToGlobalProbe.AnalyzeRequests.Contains(
                (ProfileIds.CnOfficial, ProfileIds.Global)),
            "B 服切国际服没有请求国服资源到国际服的在线差异。");

        dialogs.Reset();
        report = null;
        var cacheWorkflow = new CacheManagementWorkflow(
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
        public string? LastMessage { get; private set; }
        public bool ConfirmSwitchCalled { get; private set; }
        public bool ConfirmSwitchResult { get; init; }
        public int ShowCount { get; private set; }
        public BackupLocationAction BackupAction { get; set; }
        public CacheManagementAction CacheAction { get; set; }
        public bool ConfirmDeletion { get; set; } = true;
        public string? LastConfirmation { get; private set; }
        public Queue<OnlineResourceManagementSelection> PackageSelections { get; } = new();

        public bool? Show(
            string title,
            string message,
            MessageTone tone = MessageTone.Information,
            bool showCancel = false,
            string primaryText = "OK",
            Brush? accentBrush = null)
        {
            LastTitle = title;
            LastMessage = message;
            ShowCount++;
            if (showCancel)
            {
                LastConfirmation = message;
                return ConfirmDeletion;
            }
            return true;
        }

        public GameDirectoryCandidate? SelectGameDirectory(IReadOnlyList<GameDirectoryCandidate> candidates) =>
            candidates.FirstOrDefault();

        public string? SelectFolder(
            string description,
            string? currentPath = null,
            bool showNewFolderButton = true) => null;

        public string? SelectPackageArchive() => null;

        public Task<CacheManagementAction> SelectCacheManagementActionAsync(CacheUsageSummary usage) =>
            Task.FromResult(CacheAction);

        public Task<OnlineResourceManagementSelection> SelectOnlineResourceManagementAsync(
            OnlineDifferenceInventory inventory,
            string? currentGameVersion) =>
            Task.FromResult(PackageSelections.TryDequeue(out var selection) ? selection : new OnlineResourceManagementSelection(
                OnlineResourceManagementAction.None,
                null));

        public Task ShowOnlineDifferencePreviewAsync(OnlineDifferencePackagePreview preview) =>
            Task.CompletedTask;

        public Task ShowManifestBrowserAsync(OnlineManifestBrowserData data) => Task.CompletedTask;

        public Task<BackupLocationAction> SelectBackupLocationActionAsync(BackupLocationUsage usage) =>
            Task.FromResult(BackupAction);

        public bool ConfirmSwitch(SwitchConfirmationRequest request)
        {
            ConfirmSwitchCalled = true;
            return ConfirmSwitchResult;
        }

        public OnlineDifferenceMaterialization? DownloadOnlineDifference(
            OnlineDifferencePlan plan,
            IOnlineDifferenceService service,
            bool continueToSwitch = true) => null;

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
            LastMessage = null;
            ConfirmSwitchCalled = false;
            ShowCount = 0;
            BackupAction = BackupLocationAction.None;
            CacheAction = CacheManagementAction.None;
        }
    }


    private static void VerifyDifferenceWindowThemes(
        App app,
        OnlineDifferenceDownloadWindow download,
        OnlineResourceManagementWindow management,
        OnlineDifferencePreviewWindow preview,
        OnlineManifestBrowserWindow manifestBrowser,
        string tempRoot)
    {
        using var theme = new ThemeManager(
            app,
            new AppPaths(
                Path.Combine(tempRoot, "DifferenceThemeData"),
                Path.Combine(tempRoot, "DifferenceThemeConfig")));

        theme.SetPreference(ThemePreference.Light);
        download.UpdateLayout();
        management.UpdateLayout();
        AssertWindowPalette(download, "HeadingText", "SummaryCard", Color.FromRgb(246, 246, 246),
            Color.FromRgb(28, 28, 28), Color.FromRgb(255, 255, 255), "浅色下载窗口");
        AssertButtonPalette(download, Color.FromRgb(241, 241, 241), Color.FromRgb(28, 28, 28), "浅色下载按钮");
        AssertWindowPalette(management, "HeadingText", "SummaryCard", Color.FromRgb(246, 246, 246),
            Color.FromRgb(28, 28, 28), Color.FromRgb(255, 255, 255), "浅色管理窗口");
        AssertWindowPalette(preview, "HeadingText", "SummaryCard", Color.FromRgb(246, 246, 246),
            Color.FromRgb(28, 28, 28), Color.FromRgb(255, 255, 255), "浅色预览窗口");
        AssertWindowPalette(manifestBrowser, "HeadingText", "FilterCard", Color.FromRgb(246, 246, 246),
            Color.FromRgb(28, 28, 28), Color.FromRgb(255, 255, 255), "浅色 Manifest 浏览器");

        theme.SetPreference(ThemePreference.Dark);
        download.UpdateLayout();
        management.UpdateLayout();
        AssertWindowPalette(download, "HeadingText", "SummaryCard", Color.FromRgb(27, 27, 27),
            Color.FromRgb(242, 242, 242), Color.FromRgb(34, 34, 34), "深色下载窗口");
        AssertButtonPalette(download, Color.FromRgb(39, 39, 39), Color.FromRgb(242, 242, 242), "深色下载按钮");
        AssertWindowPalette(management, "HeadingText", "SummaryCard", Color.FromRgb(27, 27, 27),
            Color.FromRgb(242, 242, 242), Color.FromRgb(34, 34, 34), "深色管理窗口");
        AssertWindowPalette(preview, "HeadingText", "SummaryCard", Color.FromRgb(27, 27, 27),
            Color.FromRgb(242, 242, 242), Color.FromRgb(34, 34, 34), "深色预览窗口");
        AssertWindowPalette(manifestBrowser, "HeadingText", "FilterCard", Color.FromRgb(27, 27, 27),
            Color.FromRgb(242, 242, 242), Color.FromRgb(34, 34, 34), "深色 Manifest 浏览器");
    }

    private static void AssertWindowPalette(
        Window window,
        string headingName,
        string cardName,
        Color expectedWindow,
        Color expectedText,
        Color expectedCard,
        string scenario)
    {
        Assert(OverlayShell(window).Background is SolidColorBrush windowBrush && windowBrush.Color == expectedWindow &&
               Require<TextBlock>(window, headingName).Foreground is SolidColorBrush textBrush &&
               textBrush.Color == expectedText &&
               Require<Border>(window, cardName).Background is SolidColorBrush cardBrush &&
               cardBrush.Color == expectedCard,
            $"{scenario}的窗口、标题或卡片颜色未同步切换。");
    }

    private static void AssertButtonPalette(
        OnlineDifferenceDownloadWindow window,
        Color expectedBackground,
        Color expectedForeground,
        string scenario)
    {
        var button = Require<Button>(window, "StartButton");
        Assert(button.Background is SolidColorBrush background && background.Color == expectedBackground &&
               button.Foreground is SolidColorBrush foreground && foreground.Color == expectedForeground,
            $"{scenario}没有同步当前主题。");
    }

    private static void AssertOverlayWindow(Window window, string scenario)
    {
        Assert(window.WindowStyle == WindowStyle.None &&
               window.AllowsTransparency &&
               window.ResizeMode == ResizeMode.NoResize &&
               !window.ShowInTaskbar &&
               window.Background is SolidColorBrush background &&
               background.Color == Colors.Transparent &&
               OverlayShell(window).CornerRadius == new CornerRadius(10),
            $"{scenario}没有使用无系统标题栏的圆角浮层样式。");
    }

    private static Border OverlayShell(Window window)
    {
        window.ApplyTemplate();
        return window.Template.FindName("OverlayShell", window) as Border
               ?? throw new InvalidOperationException($"{window.GetType().Name} 缺少浮层外壳。");
    }

    private sealed class FailingOnlineDifferenceService : IOnlineDifferenceService
    {
        public int TryGetReadyCalls { get; private set; }

        public int AnalyzeCalls { get; private set; }

        public List<(string Source, string Target)> AnalyzeRequests { get; } = [];

        public OnlineDifferenceInventory GetInventory() => new() { Packages = [] };

        public bool TryGetReadyMaterialization(
            string sourceProfile,
            string targetProfile,
            string gameVersion,
            out OnlineDifferenceMaterialization? materialization)
        {
            TryGetReadyCalls++;
            materialization = null;
            return false;
        }

        public (
            OnlineDifferenceMaterialization? Forward,
            OnlineDifferenceMaterialization? Reverse) GetReadyMaterializations(
            string sourceProfile,
            string targetProfile,
            string gameVersion)
        {
            TryGetReadyCalls++;
            return (null, null);
        }

        public Task<OnlineDifferencePlan> AnalyzeAsync(
            string sourceProfile,
            string targetProfile,
            string gameVersion,
            string? localGamePath = null,
            CancellationToken cancellationToken = default)
        {
            AnalyzeCalls++;
            AnalyzeRequests.Add((sourceProfile, targetProfile));
            throw new NotSupportedException();
        }

        public Task<OnlineManifestRefreshResult> RefreshManifestsAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OnlineManifestBrowserData> GetManifestBrowserAsync(
            string gameVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OnlineDifferenceMaterialization> MaterializeAsync(
            OnlineDifferencePlan plan,
            IProgress<OnlineDifferenceProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<OnlineDifferenceMaterialization>(new IOException("transient download failure"));
    }
}
