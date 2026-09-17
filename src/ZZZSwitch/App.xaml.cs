using System.Windows;
using System.Windows.Input;
using System.IO;
using System.Reflection;
using ZZZSwitch.Update;
using ZZZSwitch.Core.Services;
using Forms = System.Windows.Forms;

namespace ZZZSwitch;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private ApplicationInstanceLock? _applicationInstanceLock;
    private ThemeManager? _theme;
    private LocalizationManager? _localization;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private CompactModeWindow? _compactWindow;
    private bool _isExiting;
    private string? _pendingUpdateSession;

    internal ThemeManager Theme => _theme ??= new ThemeManager(this, new AppPaths());
    internal LocalizationManager Localization =>
        _localization ??= new LocalizationManager(this, new AppPaths());
    internal bool IsCompactModeActive => _compactWindow?.IsVisible == true;
    internal bool HasPendingUpdateSession => _pendingUpdateSession is not null;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            // An unsupported update location must not prevent ordinary startup when no update exists.
            var updateJournal = Path.Combine(AppContext.BaseDirectory, ".zzzswitch-update", "journal.json");
            if (e.Args.Length == 2 && e.Args[0] is "--update-session" or "--update-health-probe")
            {
                var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                UpdateHandoff.ValidateStartup(AppContext.BaseDirectory, e.Args[1], version);
                if (e.Args[0] == "--update-health-probe")
                {
                    var confirmed = UpdateHandoff.ConfirmStartup(AppContext.BaseDirectory, e.Args[1], version);
                    StartupUri = null; Shutdown(confirmed ? 0 : 1); return;
                }
                _pendingUpdateSession = e.Args[1];
            }
            else if (File.Exists(updateJournal))
            {
                UpdateHandoff.StartRecovery(AppContext.BaseDirectory);
                StartupUri = null; Shutdown(); return;
            }
        }
        catch (Exception ex)
        {
            StartupUri = null;
            UpdateLog.Write(UpdatePaths.DefaultRoot, ex.ToString());
            if (!e.Args.Contains("--update-health-probe"))
                System.Windows.MessageBox.Show("软件更新需要恢复，请保留 .zzzswitch-update 并重试。\nApplication update recovery required.\n\n" + ex.Message, "ZZZSwitch");
            Shutdown(1); return;
        }
        _ = Theme;
        _ = Localization;
        _singleInstanceMutex = new Mutex(true, @"Local\ZZZSwitch.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            ReleaseNamedMutex();
            ShowStartupFailure(
                Localization.Choose("ZZZSwitch 已经在运行", "ZZZSwitch is already running"),
                Localization.Choose(
                    "请先关闭现有窗口后再启动。",
                    "Close the existing window before starting ZZZSwitch again."),
                MessageTone.Information);
            Shutdown();
            return;
        }

        if (!ApplicationInstanceLock.TryAcquire(
                new AppPaths(),
                out _applicationInstanceLock,
                out var lockError))
        {
            ReleaseNamedMutex();
            ShowStartupFailure(
                lockError is null
                    ? Localization.Choose("ZZZSwitch 已经在运行", "ZZZSwitch is already running")
                    : Localization.Choose("ZZZSwitch 无法启动", "ZZZSwitch could not start"),
                lockError ?? Localization.Choose(
                    "检测到其他 Windows 会话中的 ZZZSwitch 实例，请先关闭后再启动。",
                    "A ZZZSwitch instance was detected in another Windows session. Close it before starting again."),
                lockError is null ? MessageTone.Information : MessageTone.Error);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        EnsureTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.StopUpdateChecks();
        DisposeTrayIcon();
        _theme?.Dispose();
        _theme = null;
        _localization = null;
        _applicationInstanceLock?.Dispose();
        _applicationInstanceLock = null;
        ReleaseNamedMutex();
        base.OnExit(e);
    }

    internal void RegisterMainWindow(MainWindow window)
    {
        _mainWindow = window;
        MainWindow = window;
    }

    internal void HandleWindowClosing(Window window, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        var settings = new UiSettingsService(new AppPaths()).Load();
        if (settings.ExitOnClose)
        {
            Dispatcher.BeginInvoke(RequestExit);
            return;
        }

        window.Hide();
        EnsureTrayIcon();
    }

    internal void ShowFullWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _compactWindow?.Hide();
        ShowAndActivate(_mainWindow);
    }

    internal void ShowCompactWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _compactWindow ??= new CompactModeWindow(_mainWindow.DataContext);
        _mainWindow.Hide();
        ShowAndActivate(_compactWindow);
    }

    internal void ShowConfiguredWindow()
    {
        var settings = new UiSettingsService(new AppPaths()).Load();
        if (settings.StartInCompactMode && settings.OnboardingCompleted)
        {
            ShowCompactWindow();
        }
        else
        {
            ShowFullWindow();
        }
    }

    internal void RequestExit()
    {
        if (_mainWindow?.IsUpdateSessionActive == true && !_updateExitApproved)
        {
            ShowFullWindow();
            return;
        }
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        DisposeTrayIcon();
        _compactWindow?.Close();
        _mainWindow?.Close();
        Shutdown();
    }

    private bool _updateExitApproved;
    internal void ExitForUpdate() { _updateExitApproved = true; RequestExit(); }

    internal async Task<bool> ConfirmUpdateStartupAsync()
    {
        if (_pendingUpdateSession is null) return true;
        try
        {
            var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var confirmed = await Task.Run(() => UpdateHandoff.ConfirmStartup(AppContext.BaseDirectory, _pendingUpdateSession, version));
            _pendingUpdateSession = null;
            if (confirmed) return true;
        }
        catch (Exception ex) { UpdateLog.Write(UpdatePaths.DefaultRoot, ex.ToString()); }
        RequestExit(); return false;
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        var executable = Environment.ProcessPath;
        var icon = string.IsNullOrWhiteSpace(executable)
            ? System.Drawing.SystemIcons.Application
            : System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? System.Drawing.SystemIcons.Application;
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "ZZZSwitch",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.MouseClick += TrayIcon_MouseClick;
        Localization.Changed += Localization_Changed;
        Theme.Changed += Theme_Changed;
    }

    private void TrayIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (ShouldOpenFromTray(e.Button))
        {
            Dispatcher.BeginInvoke(ShowConfiguredWindow);
        }
    }

    private static bool ShouldOpenFromTray(Forms.MouseButtons button) =>
        button == Forms.MouseButtons.Left;

    private Forms.ContextMenuStrip BuildTrayMenu() =>
        TrayContextMenu.Create(
            Theme.IsDark,
            Localization.Text("L.Tray.ShowFull"),
            Localization.Text("L.Tray.ShowCompact"),
            Localization.Text("L.Tray.Exit"),
            () => Dispatcher.BeginInvoke(ShowFullWindow),
            () => Dispatcher.BeginInvoke(ShowCompactWindow),
            () => Dispatcher.BeginInvoke(RequestExit));

    private void Localization_Changed(object? sender, EventArgs e) => RebuildTrayMenu();

    private void Theme_Changed(object? sender, EventArgs e) => RebuildTrayMenu();

    private void RebuildTrayMenu()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var oldMenu = _trayIcon.ContextMenuStrip;
        _trayIcon.ContextMenuStrip = BuildTrayMenu();
        oldMenu?.Dispose();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        if (_localization is not null)
        {
            _localization.Changed -= Localization_Changed;
        }

        if (_theme is not null)
        {
            _theme.Changed -= Theme_Changed;
        }

        _trayIcon.Visible = false;
        _trayIcon.MouseClick -= TrayIcon_MouseClick;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Icon?.Dispose();
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private static void ShowAndActivate(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void OverlayWindow_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Window window ||
            e.ChangedButton != MouseButton.Left ||
            e.LeftButton != MouseButtonState.Pressed ||
            e.ClickCount != 1 ||
            !OverlayWindowDragBehavior.CanStartDragFrom(
                e.OriginalSource as DependencyObject,
                window))
        {
            return;
        }

        e.Handled = true;
        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may have been released between the event and DragMove.
        }
    }

    private static void ShowStartupFailure(
        string title,
        string message,
        MessageTone tone)
    {
        try
        {
            ThemedMessageWindow.Show(null, title, message, tone);
        }
        catch
        {
            System.Windows.MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                tone == MessageTone.Error ? MessageBoxImage.Error : MessageBoxImage.Information);
        }
    }

    private void ReleaseNamedMutex()
    {
        if (_singleInstanceMutex is null)
        {
            return;
        }

        try
        {
            _singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was not owned by this process.
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }
}
