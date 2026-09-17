using System.IO;
using System.Windows;
using ZZZSwitch.Update;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public partial class UpdateWindow : Window
{
    private readonly IUpdateService _service;
    private readonly UpdateSettingsStore _settings;
    private readonly string _version;
    private readonly string _root;
    private readonly Func<string, UpdateManifest, CancellationToken, Task> _install;
    private readonly Action<UpdateDownloadProgress> _progress;
    private readonly Func<string, string, string> _text;
    private CancellationTokenSource? _cancellation;
    private UpdateManifest? _manifest;
    private string? _job;
    private bool _busy;
    private bool _closeRequested;
    private bool _handedOff;

    public UpdateWindow(IUpdateService service, UpdateSettingsStore settings, string currentVersion, string updatesRoot,
        Func<string, UpdateManifest, CancellationToken, Task> install, Action<UpdateDownloadProgress> progress, UpdateManifest? available = null)
    {
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        _text = app.Localization.Choose;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);
        _service = service; _settings = settings; _version = currentVersion; _root = updatesRoot; _install = install; _progress = progress;
        Heading.Text = _text("软件更新", "Application update");
        CurrentVersionText.Text = _text("当前版本：", "Current version: ") + currentVersion;
        EndpointLabel.Text = _text("更新地址（HTTPS；本机测试可用 HTTP）", "Update endpoint (HTTPS; HTTP allowed on loopback)");
        AutomaticBox.Content = _text("启动后自动检查", "Check automatically after startup");
        CheckButton.Content = _text("保存并检查更新", "Save and check for updates");
        DownloadButton.Content = _text("下载更新", "Download update");
        CancelButton.Content = _text("关闭", "Close");
        try
        {
            var config = settings.Load(); EndpointBox.Text = config.Endpoint;
            ChannelBox.SelectedIndex = config.Channel == "beta" ? 1 : 0; AutomaticBox.IsChecked = config.AutomaticCheck;
        }
        catch (Exception ex) { StatusText.Text = UpdateErrorText.For(ex, _text); ChannelBox.SelectedIndex = 0; }
        if (available is not null) ShowManifest(available);
        Closing += (_, e) =>
        {
            if (_busy) { _closeRequested = true; _cancellation?.Cancel(); e.Cancel = true; }
        };
        Closed += (_, _) =>
        {
            if (!_handedOff && _job is not null)
            {
                try { var zip = UpdatePaths.Under(_job, "package.zip"); if (File.Exists(zip)) File.Delete(zip); }
                catch (Exception ex) { UpdateLog.Write(_root, ex.ToString()); }
            }
        };
    }

    private void ShowManifest(UpdateManifest m)
    {
        _manifest = m;
        VersionText.Text = _text("新版本：", "New version: ") + m.Version;
        DateText.Text = m.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz");
        NotesText.Text = _text(m.Notes("zh-CN"), m.Notes("en-US"));
        StatusText.Text = m.Mandatory ? _text("建议尽快安装此更新。安装仍需你的确认。", "This update is strongly recommended. Installation still requires your confirmation.") : _text("发现新版本，可下载后确认重启安装。", "An update is available. Confirm restart after downloading.");
        DownloadButton.IsEnabled = true;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunAsync(async token =>
        {
            _manifest = null; DownloadButton.IsEnabled = false;
            var settings = new UpdateSettings(EndpointBox.Text.Trim(), ChannelBox.SelectedIndex == 1 ? "beta" : "stable", AutomaticBox.IsChecked == true);
            _settings.Save(settings);
            StatusText.Text = _text("正在检查更新…", "Checking for updates…");
            var result = await _service.CheckForUpdatesAsync(settings.Endpoint, _version, settings.Channel, token);
            if (result.Status == UpdateCheckStatus.Available) ShowManifest(result.Manifest!);
            else
            {
                VersionText.Text = ""; NotesText.Text = ""; DateText.Text = "";
                StatusText.Text = result.Status == UpdateCheckStatus.UpToDate ? _text("已是最新版本。", "You are up to date.")
                    : _text("此版本不支持原位更新，请手动下载并解压新版到独立目录。", "This version requires a manual upgrade. Extract the new release into a separate directory.");
            }
        });
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _manifest is null) return;
        await RunAsync(async token =>
        {
            if (_job is not null)
            {
                var previous = UpdatePaths.Under(_job, "package.zip");
                if (File.Exists(previous)) File.Delete(previous);
            }
            _job = UpdateHandoff.CreateJob(_root);
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                DownloadProgress.IsIndeterminate = p.Verifying;
                DownloadProgress.Value = p.Total == 0 ? 0 : 100d * p.Received / p.Total;
                StatusText.Text = p.Verifying ? _text("正在校验 SHA-256…", "Verifying SHA-256…")
                    : $"{p.Received / 1048576d:F1} / {p.Total / 1048576d:F1} MiB · {p.BytesPerSecond / 1048576d:F1} MiB/s";
                _progress(p);
            });
            await _service.DownloadUpdateAsync(_manifest, _job, progress, token);
            token.ThrowIfCancellationRequested();
            DownloadProgress.IsIndeterminate = false;
            StatusText.Text = _text("校验通过。", "Verification passed.");
            if (ThemedMessageWindow.Show(this, _text("重启并更新", "Restart and update"),
                _text("更新包已验证。现在退出 ZZZSwitch 并安装更新？", "The package is verified. Exit ZZZSwitch and install now?"),
                MessageTone.Information, true, _text("重启安装", "Restart and install")) != true) return;
            await _install(_job, _manifest, token);
            _handedOff = true;
            _busy = false;
            ((App)System.Windows.Application.Current).ExitForUpdate();
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        _busy = true; _cancellation = new();
        SetControls(false);
        try { await operation(_cancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = _text("已取消更新。", "Update cancelled."); }
        catch (Exception ex) { UpdateLog.Write(_root, ex.ToString()); StatusText.Text = UpdateErrorText.For(ex, _text); }
        finally
        {
            _busy = false; _cancellation.Dispose(); _cancellation = null;
            DownloadProgress.IsIndeterminate = false; SetControls(true);
            if (_closeRequested && !_handedOff) Close();
        }
    }
    private void SetControls(bool enabled)
    {
        CheckButton.IsEnabled = EndpointBox.IsEnabled = ChannelBox.IsEnabled = AutomaticBox.IsEnabled = enabled;
        DownloadButton.IsEnabled = enabled && _manifest is not null;
        CancelButton.Content = _text(enabled ? "关闭" : "取消", enabled ? "Close" : "Cancel");
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_busy) _cancellation?.Cancel(); else Close(); }
}

public static class UpdateErrorText
{
    public static string For(Exception error, Func<string, string, string> text)
    {
        var code = (error as UpdateException)?.Error;
        var message = code switch
        {
            UpdateError.NotConfigured => text("尚未配置更新地址。请填写可信的更新 Manifest 地址。", "No update endpoint configured. Enter a trusted manifest URL."),
            UpdateError.Http => text("更新服务器返回错误，请稍后重试。", "The update server returned an error. Try again later."),
            UpdateError.Network or UpdateError.Timeout => text("无法连接更新服务器或请求超时，请检查网络后重试。", "The update server is unreachable or timed out. Check your connection."),
            UpdateError.HashMismatch or UpdateError.SizeMismatch => text("更新包不完整或校验失败，已拒绝安装，请重新下载。", "The package is incomplete or failed verification. Installation was refused; download again."),
            UpdateError.InvalidJson or UpdateError.InvalidManifest or UpdateError.UnsupportedSchema => text("更新信息无效或协议不受支持，请联系发布者。", "Update metadata is invalid or unsupported. Contact the publisher."),
            _ => text("无法完成更新。请检查程序目录权限与可用空间，保留恢复记录后重试。", "Update could not complete. Check permissions and free space; preserve recovery records before retrying.")
        };
        return message + "\n" + error.Message;
    }
}
