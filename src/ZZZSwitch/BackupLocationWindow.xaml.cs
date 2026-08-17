using System.Windows;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public enum BackupLocationAction
{
    None,
    OpenLocation,
    ChangeLocation,
    RestoreDefault
}

public partial class BackupLocationWindow : Window
{
    public BackupLocationWindow(BackupLocationUsage usage)
    {
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);
        BackupPathTextBox.Text = usage.BackupRootPath;
        LocationModeText.Text = usage.IsCustomLocation
            ? app.Localization.Choose("自定义位置", "Custom location")
            : app.Localization.Choose("默认位置（应用数据目录）", "Default location (application data)");
        UsageText.Text = app.Localization.Choose(
            $"{usage.BackupCount} 个备份 · {usage.FileCount} 个文件 · {FormatBytes(usage.TotalBytes)}",
            $"{usage.BackupCount} backups · {usage.FileCount} files · {FormatBytes(usage.TotalBytes)}");
        RestoreDefaultButton.IsEnabled = usage.IsCustomLocation;
    }

    public BackupLocationAction SelectedAction { get; private set; }

    private void OpenLocation_Click(object sender, RoutedEventArgs e) =>
        Complete(BackupLocationAction.OpenLocation);

    private void ChangeLocation_Click(object sender, RoutedEventArgs e) =>
        Complete(BackupLocationAction.ChangeLocation);

    private void RestoreDefault_Click(object sender, RoutedEventArgs e) =>
        Complete(BackupLocationAction.RestoreDefault);

    private void Complete(BackupLocationAction action)
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
