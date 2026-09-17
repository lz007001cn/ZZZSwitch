using System.ComponentModel;
using System.IO;
using ZZZSwitch.Update;

namespace ZZZSwitch;

public enum ApplicationUpdateState { Idle, Checking, UpToDate, Available, Downloading, Verifying, Installing, Failed, Unavailable }

// One shared presentation model for the settings card and the silent startup check.
public sealed class ApplicationUpdateModel : INotifyPropertyChanged
{
    private readonly IUpdateService _service;
    private readonly UpdateSettingsStore _settings;
    private readonly string _root;
    private readonly Func<string, string, string> _text;
    private readonly Func<string, UpdateManifest, CancellationToken, Task> _install;
    private readonly Func<Func<Task>, Task> _exclusive;
    private readonly Action _exit;
    private CancellationTokenSource? _operation;
    private UpdateManifest? _manifest;
    private string _error = "";
    private bool _silentCheck;
    private bool _hasChecked;
    private long _received, _total;
    public ApplicationUpdateModel(IUpdateService service, UpdateSettingsStore settings, string version, string root,
        Func<string, string, string> text, Func<string, UpdateManifest, CancellationToken, Task> install,
        Func<Func<Task>, Task> exclusive, Action exit)
    { _service = service; _settings = settings; CurrentVersion = version; _root = root; _text = text; _install = install; _exclusive = exclusive; _exit = exit; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ApplicationUpdateState State { get; private set; }
    public string CurrentVersion { get; }
    public UpdateRelease? Release { get; private set; }
    public string Heading => _text("更新", "Updates");
    public string CurrentText
    {
        get
        {
            var version = UpdateVersion.Parse(CurrentVersion);
            var display = version.IsLocalTestBuild
                ? version.CoreVersion + _text("（测试版）", " (test build)")
                : version.PreRelease is null ? version.CoreVersion : version.CoreVersion + "-" + version.PreRelease;
            return _text("当前版本：", "Current version: ") + display;
        }
    }
    public string LatestText => _text("最新版本：", "Latest version: ") + (Release?.Version ?? "—");
    public string DateText => Release is null ? "" : _text("发布日期：", "Published: ") + Release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd");
    public string Notes => (_manifest?.Notes(_text("zh-CN", "en-US")) ?? Release?.Body ?? "") is var notes && notes.Length > 1600 ? notes[..1600] + "…" : notes;
    public bool IsBusy => State is ApplicationUpdateState.Checking or ApplicationUpdateState.Downloading or ApplicationUpdateState.Verifying or ApplicationUpdateState.Installing;
    public bool HasHandedOff { get; private set; }
    public bool CanAct => !IsBusy;
    public bool ShowProgress => IsBusy && !(State == ApplicationUpdateState.Checking && _silentCheck);
    public bool ShowAction => !(State == ApplicationUpdateState.Checking && _silentCheck);
    public Task EnsureCheckedAsync() => _hasChecked || IsBusy ? Task.CompletedTask : CheckAsync(automatic: true);
    public bool IsIndeterminate => State is ApplicationUpdateState.Checking or ApplicationUpdateState.Verifying or ApplicationUpdateState.Installing;
    public double Percent => _total <= 0 ? 0 : 100d * _received / _total;
    public string ButtonText => State switch
    {
        ApplicationUpdateState.Checking => _text("正在检查…", "Checking…"),
        ApplicationUpdateState.Available => _text("安装更新", "Install update"),
        ApplicationUpdateState.Downloading => _text("下载中 ", "Downloading ") + $"{Percent:F0}%",
        ApplicationUpdateState.Verifying => _text("正在验证", "Verifying"),
        ApplicationUpdateState.Installing => _text("正在安装", "Installing"),
        ApplicationUpdateState.Failed => _text("重试", "Retry"),
        _ => _text("检查更新", "Check for updates")
    };
    public string StatusText => State switch
    {
        ApplicationUpdateState.Checking => _silentCheck ? "" : _text("正在检查更新……", "Checking for updates…"),
        ApplicationUpdateState.UpToDate => _text("已是最新版本", "You are up to date"),
        ApplicationUpdateState.Available => _text("发现新版本，安装后将自动重启。", "Update available. Installation will restart the app."),
        ApplicationUpdateState.Downloading => _text("正在下载 v", "Downloading v") + _manifest?.Version + $" · {_received / 1048576d:F1} / {_total / 1048576d:F1} MiB",
        ApplicationUpdateState.Verifying => _text("正在校验 SHA-256…", "Verifying SHA-256…"),
        ApplicationUpdateState.Installing => _text("正在安装，即将重启…", "Installing; restarting shortly…"),
        ApplicationUpdateState.Failed => _error,
        ApplicationUpdateState.Unavailable => _text("发现新版本；该发行包暂不支持自动安装。", "A new version is available; this release cannot be installed automatically yet."),
        _ => ""
    };
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Cancel() => _operation?.Cancel();
    public async Task CheckAsync(bool automatic = false, CancellationToken token = default)
    {
        if (IsBusy) return;
        var previous = State;
        _silentCheck = automatic;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        State = ApplicationUpdateState.Checking; Changed();
        try
        {
            var settings = _settings.Load();
            var result = await _service.CheckForUpdatesAsync(settings.Endpoint, CurrentVersion, settings.Channel, _operation.Token);
            _hasChecked = true;
            Release = result.Release ?? (result.Manifest is { } m ? new(m.Version, m.Version, m.Notes("en-US"), m.PublishedAt, "", m) : null);
            if (result.Release?.UnavailableReason is { } reason) UpdateLog.Write(_root, "Release unavailable: " + reason);
            _manifest = result.Status == UpdateCheckStatus.Available ? result.Manifest : null;
            State = result.Status switch { UpdateCheckStatus.Available => ApplicationUpdateState.Available,
                UpdateCheckStatus.UpToDate => ApplicationUpdateState.UpToDate, _ => ApplicationUpdateState.Unavailable };
        }
        catch (OperationCanceledException) { State = previous; }
        catch (Exception ex)
        {
            UpdateLog.Write(_root, (automatic ? "Automatic check: " : "Check: ") + ex);
            State = automatic ? previous : ApplicationUpdateState.Failed;
            if (!automatic) { _manifest = null; _error = _text("无法检查更新，请稍后重试", "Unable to check for updates. Try again later."); }
        }
        finally { _operation.Dispose(); _operation = null; _silentCheck = false; Changed(); }
    }
    public async Task ActAsync()
    {
        if (IsBusy) return;
        if (_manifest is null) { await CheckAsync(); return; }
        _operation = new();
        var operation = _operation;
        var token = operation.Token;
        string? job = null; bool handedOff = false;
        State = ApplicationUpdateState.Downloading; _received = _total = 0; Changed();
        try
        {
            await _exclusive(async () =>
            {
                job = UpdateHandoff.CreateJob(_root);
                var progress = new Progress<UpdateDownloadProgress>(p =>
                {
                    if (!ReferenceEquals(_operation, operation) || token.IsCancellationRequested) return;
                    if (State is not (ApplicationUpdateState.Downloading or ApplicationUpdateState.Verifying)) return;
                    _received = p.Received; _total = p.Total;
                    State = p.Verifying ? ApplicationUpdateState.Verifying : ApplicationUpdateState.Downloading; Changed();
                });
                await _service.DownloadUpdateAsync(_manifest, job, progress, token);
                token.ThrowIfCancellationRequested();
                State = ApplicationUpdateState.Installing; Changed();
                await _install(job, _manifest, token);
                handedOff = HasHandedOff = true;
                _exit();
            });
        }
        catch (OperationCanceledException) { State = ApplicationUpdateState.Available; }
        catch (Exception ex)
        {
            UpdateLog.Write(_root, ex.ToString());
            _error = ex is UpdateException { Error: UpdateError.HashMismatch or UpdateError.SizeMismatch }
                ? _text("校验失败，已拒绝安装。请重试。", "Verification failed. Installation refused. Retry.")
                : State == ApplicationUpdateState.Installing
                    ? _text("更新安装准备失败，请重试。", "Unable to prepare installation. Retry.")
                    : _text("下载更新失败，请稍后重试。", "Update download failed. Try again later.");
            State = ApplicationUpdateState.Failed;
        }
        finally
        {
            if (!handedOff && job is not null)
            {
                try { var zip = UpdatePaths.Under(job, "package.zip"); if (File.Exists(zip)) File.Delete(zip); }
                catch (Exception ex) { UpdateLog.Write(_root, ex.ToString()); }
            }
            _operation.Dispose(); _operation = null; Changed();
        }
    }
}
