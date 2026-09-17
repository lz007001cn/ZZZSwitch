using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Update;

namespace ZZZSwitch.Ui.Smoke;
internal static partial class Program
{
    private static void VerifyUpdateCard(App app, string tempRoot, UpdateManifest manifest)
    {
        var paths = new AppPaths(Path.Combine(tempRoot, "card-preferences"), tempRoot, tempRoot);
        using var theme = new ThemeManager(app, paths);
        var localization = new LocalizationManager(app, paths);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                localization.SetLanguage(language); theme.SetPreference(preference);
                var service = new CardUpdateService(manifest);
                int installed = 0, exited = 0;
                var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var model = new ApplicationUpdateModel(service, new UpdateSettingsStore(paths.DataRoot), "1.3.7-test.20260917-091546+4ff638fc8b42bd7a1cbe0f81a8d3347db1ce9e3f", Path.Combine(tempRoot, "card-updates"),
                    localization.Choose, async (_, _, token) => { installed++; await handoff.Task.WaitAsync(token); }, action => action(), () => exited++);
                var window = new SettingsWindow(new(new UiSettings(), null, null, null, null), model);
                try
                {
                    Layout(window, 680, 600);
                    var log = Require<Border>(window, "LogsCard"); var card = Require<Border>(window, "UpdateCard");
                    Assert(log.Parent == card.Parent && log.Parent is StackPanel panel && panel.Children.IndexOf(card) == panel.Children.IndexOf(log) + 1,
                        "日志与更新必须同级并相邻，更新位于日志之后。");
                    Assert(ReferenceEquals(log.Style, card.Style), "更新卡片未复用设置样式。");
                    Assert(OverlayWindowDragBehavior.CanStartDragFrom(log, window), "滚动区域内的设置标题应能拖动窗口。");
                    Assert(!OverlayWindowDragBehavior.CanStartDragFrom(Require<Button>(window, "CheckApplicationUpdateButton"), window), "更新按钮不应触发拖动。");
                    Assert(!OverlayWindowDragBehavior.CanStartDragFrom(Require<ComboBox>(window, "ThemeComboBox"), window), "下拉菜单不应触发拖动。");
                    var button = Require<Button>(window, "CheckApplicationUpdateButton");
                    Assert(model.State == ApplicationUpdateState.Idle && button.IsEnabled, "初始检查按钮不可用。");
                    // Simulate settings Loaded: metadata arrives without a check-button click.
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var check = model.EnsureCheckedAsync();
                    Layout(window, 680, 600);
                    Assert(model.State == ApplicationUpdateState.Checking && !button.IsEnabled && !model.ShowProgress && !model.ShowAction && model.StatusText == "", "静默检查不应显示操作或进度。");
                    Assert(model.CurrentText == localization.Choose("当前版本：1.3.7（测试版）", "Current version: 1.3.7 (test build)") &&
                           !model.CurrentText.Contains("4ff638fc") && model.LatestText.EndsWith("—"),
                        "测试构建应隐藏时间戳和提交哈希，并显示最新版本占位。");
                    model.ActAsync().GetAwaiter().GetResult(); Assert(service.Checks == 1, "重复检查未阻止。");
                    service.Check.SetResult(new(UpdateCheckStatus.Available, manifest)); PumpUntil(() => model.State == ApplicationUpdateState.Available); check.GetAwaiter().GetResult();
                    model.EnsureCheckedAsync().GetAwaiter().GetResult();
                    Assert(service.Checks == 1, "已取得版本信息不应在设置打开时重复检查。");
                    Layout(window, 680, 600);
                    Assert(button.Content?.ToString() == localization.Choose("安装更新", "Install update") && button.IsEnabled, "新版按钮错误。");
                    var notes = Require<TextBlock>(window, "UpdateNotesText");
                    var status = Require<TextBlock>(window, "UpdateStatusText");
                    Assert(notes.Parent is ScrollViewer notesScroller && notesScroller.Parent is StackPanel updatePanel &&
                           updatePanel.Children.IndexOf(status) > updatePanel.Children.IndexOf(notesScroller),
                        "发现新版与自动重启提示必须位于更新说明之后。");
                    var scroller = (ScrollViewer)((Grid)window.Content).Children[0]; scroller.ScrollToBottom(); window.UpdateLayout();
                    SaveReviewImage(window, $"settings-update-{language}-{preference}.png");
                    AssertSettingsLayout(window, 620, 480, 1.5);
                    var download = model.ActAsync();
                    service.Progress!.Report(new(50, 100, 10)); PumpUntil(() => model.Percent == 50);
                    Assert(model.ButtonText.Contains("50%") && !model.CanAct, "下载进度或按钮错误。");
                    service.Progress.Report(new(100, 100, 0, true)); PumpUntil(() => model.State == ApplicationUpdateState.Verifying);
                    Assert(model.ButtonText == localization.Choose("正在验证", "Verifying"), "校验按钮错误。");
                    service.Download.SetResult("fixture.zip"); PumpUntil(() => model.State == ApplicationUpdateState.Installing);
                    Assert(installed == 1 && !model.CanAct && exited == 0, "交接前不可退出。");
                    handoff.SetResult(); PumpUntil(() => download.IsCompleted); download.GetAwaiter().GetResult();
                    Assert(exited == 1 && model.HasHandedOff, "Updater 交接后未退出。");
                }
                finally { window.Close(); }
            }
            var failed = new CardUpdateService(manifest);
            var failureModel = new ApplicationUpdateModel(failed, new UpdateSettingsStore(paths.DataRoot), "1.3.7", Path.Combine(tempRoot, "failure-update"),
                localization.Choose, (_, _, _) => throw new Exception("Hash failure must not hand off"), action => action(), () => throw new Exception("Must not exit"));
            var checking = failureModel.CheckAsync(); failed.Check.SetException(new UpdateException(UpdateError.Network, "offline"));
            PumpUntil(() => checking.IsCompleted); Assert(failureModel.State == ApplicationUpdateState.Failed && failureModel.CanAct, "检查失败不可重试。");
            failed.Check = new(TaskCreationOptions.RunContinuationsAsynchronously);
            checking = failureModel.CheckAsync(); failed.Check.SetResult(new(UpdateCheckStatus.UpToDate, manifest));
            PumpUntil(() => checking.IsCompleted); Assert(failureModel.State == ApplicationUpdateState.UpToDate, "最新版本状态错误。");
            failed.Check = new(TaskCreationOptions.RunContinuationsAsynchronously);
            checking = failureModel.CheckAsync(automatic: true); failed.Check.SetException(new UpdateException(UpdateError.Network, "offline"));
            PumpUntil(() => checking.IsCompleted); Assert(failureModel.State == ApplicationUpdateState.UpToDate, "自动检查失败不应覆盖已知状态。");
            failed.Check = new(TaskCreationOptions.RunContinuationsAsynchronously);
            checking = failureModel.CheckAsync(); failureModel.Cancel(); PumpUntil(() => checking.IsCompleted);
            Assert(failureModel.State == ApplicationUpdateState.UpToDate, "检查取消没有恢复状态。");
            failed.Check = new(TaskCreationOptions.RunContinuationsAsynchronously);
            checking = failureModel.CheckAsync(); failed.Check.SetResult(new(UpdateCheckStatus.Available, manifest)); PumpUntil(() => checking.IsCompleted);
            var downloading = failureModel.ActAsync(); failed.Download.SetException(new UpdateException(UpdateError.HashMismatch, "bad hash"));
            PumpUntil(() => downloading.IsCompleted); Assert(failureModel.State == ApplicationUpdateState.Failed && failureModel.CanAct, "校验失败不可重试。");
            failed.Download = new(TaskCreationOptions.RunContinuationsAsynchronously);
            downloading = failureModel.ActAsync(); failureModel.Cancel(); PumpUntil(() => downloading.IsCompleted);
            Assert(failureModel.State == ApplicationUpdateState.Available, "下载取消应恢复安装按钮。");
            failed.Download = new(TaskCreationOptions.RunContinuationsAsynchronously);
            downloading = failureModel.ActAsync(); failed.Download.SetException(new UpdateException(UpdateError.Network, "offline"));
            PumpUntil(() => downloading.IsCompleted); Assert(failureModel.State == ApplicationUpdateState.Failed && failureModel.CanAct, "下载失败应允许重试。");
            Console.WriteLine("PASS  设置更新卡片同级顺序、双语双主题、状态绑定、取消、静默失败及错误哈希禁止交接。");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private sealed class CardUpdateService(UpdateManifest manifest) : IUpdateService
    {
        public int Checks;
        public TaskCompletionSource<UpdateCheckResult> Check = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Download = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<UpdateDownloadProgress>? Progress;
        public Task<UpdateCheckResult> CheckForUpdatesAsync(string endpoint, string version, string channel, CancellationToken token)
        { Checks++; return Check.Task.WaitAsync(token); }
        public Task<string> DownloadUpdateAsync(UpdateManifest m, string root, IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
        { Assert(m == manifest, "下载清单发生变化。"); Progress = progress; return Download.Task.WaitAsync(token); }
        public Task VerifyPackageAsync(string path, UpdatePackage package, CancellationToken token) => Task.CompletedTask;
    }
}
