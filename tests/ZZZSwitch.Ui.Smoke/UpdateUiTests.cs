using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Update;

namespace ZZZSwitch.Ui.Smoke;

internal static partial class Program
{
    private static void VerifyUpdateUi(App app, string tempRoot)
    {
        var savedStrings = app.Resources.Keys.OfType<string>().Where(k => k.StartsWith("L.")).ToDictionary(k => k, k => app.Resources[k]);
        var root = Path.Combine(tempRoot, "application-update-ui");
        var localization = new LocalizationManager(app, new AppPaths(Path.Combine(root, "settings"), root, root));
        var manifest = new UpdateManifest { SchemaVersion = 1, Version = "1.3.8", Channel = "stable", Mandatory = false,
            MinSupportedVersion = "1.3.0", PublishedAt = DateTimeOffset.Now,
            ReleaseNotes = new() { ["zh-CN"] = "这是更新说明，用于验证多行文本与窗口缩放。", ["en-US"] = "Release notes for update layout and scaling verification." },
            Package = new() { Url = "https://example.test/app.zip", Size = 100, Sha256 = new string('0', 64) } };
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        {
            localization.SetLanguage(language);
            var store = new UpdateSettingsStore(Path.Combine(root, "preferences"));
            store.Save(new("https://example.test/latest.json"));
            var service = new PendingUpdateService();
            var window = new UpdateWindow(service, store, "1.3.7", Path.Combine(root, "updates"), (_, _, _) => Task.CompletedTask, _ => { }, manifest);
            try
            {
                Layout(window, 680, 650);
                Assert(Require<TextBlock>(window, "NotesText").Text == manifest.Notes(language == AppLanguage.Chinese ? "zh-CN" : "en-US"), "更新说明未按语言展示。");
                Assert(Require<Button>(window, "DownloadButton").IsEnabled, "可用更新没有启用下载。");
                SaveReviewImage(window, "update-" + language + ".png");
                Layout(window, 580, 500);
                Assert(Require<ScrollViewer>(window, "UpdateScrollViewer").ActualHeight > 0, "更新窗口内容无法滚动。");
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                try
                {
                    var check = Require<Button>(window, "CheckButton");
                    check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(service.Checks == 1 && !check.IsEnabled, "重复检查未被阻止。");
                    Require<Button>(window, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => check.IsEnabled);
                    Assert(service.Cancelled, "取消未传递给更新服务。");
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            }
            finally { window.Close(); }
        }
        VerifyUpdateCard(app, tempRoot, manifest);
        foreach (var (key, value) in savedStrings) app.Resources[key] = value;
        Console.WriteLine("PASS  应用更新窗口双语布局、重复检查防护及取消通过。");
    }
    private static void PumpUntil(Func<bool> complete)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (complete() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Assert(complete(), "异步更新界面未正常完成。");
    }
    private sealed class PendingUpdateService : IUpdateService
    {
        public int Checks; public bool Cancelled;
        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string endpoint, string currentVersion, string channel, CancellationToken token)
        {
            Checks++;
            try { await Task.Delay(10000, token); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        public Task<string> DownloadUpdateAsync(UpdateManifest m, string root, IProgress<UpdateDownloadProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task VerifyPackageAsync(string path, UpdatePackage package, CancellationToken token) => throw new NotSupportedException();
    }
}
