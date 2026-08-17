using System.Windows;
using System.Windows.Input;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private ApplicationInstanceLock? _applicationInstanceLock;
    private ThemeManager? _theme;
    private LocalizationManager? _localization;

    internal ThemeManager Theme => _theme ??= new ThemeManager(this, new AppPaths());
    internal LocalizationManager Localization =>
        _localization ??= new LocalizationManager(this, new AppPaths());

    protected override void OnStartup(StartupEventArgs e)
    {
        _ = Theme;
        _ = Localization;
        _singleInstanceMutex = new Mutex(true, @"Local\ZZZSwitch.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            ReleaseNamedMutex();
            ShowStartupFailure(
                "ZZZSwitch 已经在运行",
                "请先关闭现有窗口后再启动。",
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
                lockError is null ? "ZZZSwitch 已经在运行" : "ZZZSwitch 无法启动",
                lockError ?? "检测到其他 Windows 会话中的 ZZZSwitch 实例，请先关闭后再启动。",
                lockError is null ? MessageTone.Information : MessageTone.Error);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _theme?.Dispose();
        _theme = null;
        _localization = null;
        _applicationInstanceLock?.Dispose();
        _applicationInstanceLock = null;
        ReleaseNamedMutex();
        base.OnExit(e);
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
