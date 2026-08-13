using System.Windows;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public enum CacheManagementAction
{
    None,
    ChangeLocation,
    RestoreDefault,
    DeleteObsolete
}

public partial class CacheManagementWindow : Window
{
    public CacheManagementWindow(CacheUsageSummary usage)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);
        CachePathTextBox.Text = usage.CacheRootPath;
        LocationModeText.Text = usage.IsCustomLocation ? "自定义位置" : "默认位置（游戏目录同级）";
        TotalCacheText.Text = FormatBytes(usage.TotalBytes);
        TotalFilesText.Text = $"{usage.FileCount} 个文件";
        ObsoleteCacheText.Text = FormatBytes(usage.ObsoleteBytes);
        ObsoleteVersionsText.Text = usage.ObsoleteVersionCount == 0
            ? "没有可清理的旧版本"
            : $"{usage.ObsoleteVersionCount} 个版本 · {usage.ObsoleteFileCount} 个文件";
        DeleteObsoleteButton.IsEnabled = usage.ObsoleteVersionCount > 0;
        RestoreDefaultButton.IsEnabled = usage.IsCustomLocation;
    }

    public CacheManagementAction SelectedAction { get; private set; }

    private void ChangeLocation_Click(object sender, RoutedEventArgs e) =>
        Complete(CacheManagementAction.ChangeLocation);

    private void RestoreDefault_Click(object sender, RoutedEventArgs e) =>
        Complete(CacheManagementAction.RestoreDefault);

    private void DeleteObsolete_Click(object sender, RoutedEventArgs e) =>
        Complete(CacheManagementAction.DeleteObsolete);

    private void Complete(CacheManagementAction action)
    {
        SelectedAction = action;
        DialogResult = true;
    }

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
