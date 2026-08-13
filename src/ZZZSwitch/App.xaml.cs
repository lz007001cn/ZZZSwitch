using System.Windows;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private ApplicationInstanceLock? _applicationInstanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\ZZZSwitch.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            ReleaseNamedMutex();
            ShowStartupFailure(
                "ZZZSwitch 已经在运行",
                "请先关闭现有窗口后再启动。",
                MessageTone.Information,
                "已阻止重复启动");
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
                lockError is null ? MessageTone.Information : MessageTone.Error,
                lockError is null ? "已阻止跨会话重复启动" : "无法取得应用锁");
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _applicationInstanceLock?.Dispose();
        _applicationInstanceLock = null;
        ReleaseNamedMutex();
        base.OnExit(e);
    }

    private static void ShowStartupFailure(
        string title,
        string message,
        MessageTone tone,
        string subtitle)
    {
        try
        {
            ThemedMessageWindow.Show(null, title, message, tone, subtitle);
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
