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
        var app = (App)System.Windows.Application.Current;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);
        CachePathTextBox.Text = usage.CacheRootPath;
        LocationModeText.Text = usage.IsCustomLocation
            ? app.Localization.Choose("自定义位置", "Custom location")
            : app.Localization.Choose("默认位置（游戏目录同级）", "Default location (next to the game directory)");
        TotalCacheText.Text = FormatBytes(usage.TotalBytes);
        TotalFilesText.Text = app.Localization.Choose($"{usage.FileCount} 个文件", $"{usage.FileCount} files");
        ObsoleteCacheText.Text = FormatBytes(usage.ObsoleteBytes);
        ObsoleteVersionsText.Text = usage.ObsoleteVersionCount == 0
            ? app.Localization.Choose("没有可清理的旧版本", "No old versions to clean")
            : app.Localization.Choose(
                $"{usage.ObsoleteVersionCount} 个版本 · {usage.ObsoleteFileCount} 个文件",
                $"{usage.ObsoleteVersionCount} versions · {usage.ObsoleteFileCount} files");
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
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
