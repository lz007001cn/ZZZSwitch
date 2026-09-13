using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using System.Windows.Controls.Primitives;
using ZZZSwitch.Workflows;

namespace ZZZSwitch;

public partial class MainWindow
{
    private void BackupsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = _viewModel;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private Task ShowChecksAsync()
    {
        var window = new CheckWindow(RunCheckAsync, _localization.Choose(
            "选择检查或修复操作。重新检测只读取游戏文件；修复资源仅处理软件缓存。\n\n当前目录：",
            "Choose an action. Re-detection reads game files; resource repair updates application caches.\n\nCurrent directory: ") + _viewModel.GamePath)
        { Owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private async Task<string> RunCheckAsync(CheckAction action)
    {
        if (action == CheckAction.Logs) { OpenLogs(); return _paths.LogsRoot; }
        if (action == CheckAction.Packages)
        {
            await _onlineResourceManagementWorkflow.ManageAsync();
            return _localization.Choose("请选择有问题的包并使用更新/验证；无效清单也可单独删除。", "Select an affected package and update or verify it. Invalid manifests can be deleted individually.");
        }
        return await _maintenanceWorkflow.RunAsync(action);
    }
}
