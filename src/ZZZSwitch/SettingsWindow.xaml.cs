using System.Windows;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Presentation;

namespace ZZZSwitch;

public enum SettingsAction
{
    None,
    SaveAndClose,
    ManageCache,
    ManageBackup,
    OpenLogs
}

public sealed record SettingsViewData(
    UiSettings Settings,
    CacheUsageSummary? CacheUsage,
    string? CacheError,
    BackupLocationUsage? BackupUsage,
    string? BackupError);

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewData data)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);
        var localization = ((App)System.Windows.Application.Current).Localization;

        LanguageComboBox.ItemsSource = new[]
        {
            new Choice<AppLanguage>(AppLanguage.Chinese, localization.Text("L.Settings.Chinese")),
            new Choice<AppLanguage>(AppLanguage.English, localization.Text("L.Settings.English"))
        };
        ThemeComboBox.ItemsSource = new[]
        {
            new Choice<ThemePreference>(ThemePreference.FollowWindows, localization.Text("L.Settings.FollowWindows")),
            new Choice<ThemePreference>(ThemePreference.Light, localization.Text("L.Settings.Light")),
            new Choice<ThemePreference>(ThemePreference.Dark, localization.Text("L.Settings.Dark"))
        };
        LogRetentionComboBox.ItemsSource = new[]
        {
            new Choice<int>(7, localization.Text("L.Settings.SevenDays")),
            new Choice<int>(30, localization.Text("L.Settings.ThirtyDays"))
        };

        Select(LanguageComboBox, data.Settings.Language);
        Select(ThemeComboBox, data.Settings.Theme);
        Select(LogRetentionComboBox, data.Settings.LogRetentionDays);
        ShowDetailsCheckBox.IsChecked = data.Settings.ShowDetailedStatus;
        RememberWindowCheckBox.IsChecked = data.Settings.RememberWindowPlacement;
        AutoDetectCheckBox.IsChecked = data.Settings.AutoDetectGameDirectory;
        AutoInspectCheckBox.IsChecked = data.Settings.AutoInspectOnStartup;
        ShowLastGameCheckBox.IsChecked = data.Settings.ShowLastGameDirectory;

        CachePathTextBlock.Text = data.CacheUsage?.CacheRootPath ?? "—";
        CachePathTextBlock.ToolTip = data.CacheUsage?.CacheRootPath;
        CacheUsageText.Text = data.CacheUsage is null
            ? data.CacheError ?? "—"
            : localization.Language == AppLanguage.English
                ? $"{data.CacheUsage.FileCount:N0} files · {DisplayFormatting.FormatBytes(data.CacheUsage.TotalBytes)}"
                : $"{data.CacheUsage.FileCount:N0} 个文件 · {DisplayFormatting.FormatBytes(data.CacheUsage.TotalBytes)}";
        BackupPathTextBlock.Text = data.BackupUsage?.BackupRootPath ?? "—";
        BackupPathTextBlock.ToolTip = data.BackupUsage?.BackupRootPath;
        BackupUsageText.Text = data.BackupUsage is null
            ? data.BackupError ?? "—"
            : localization.Language == AppLanguage.English
                ? $"{data.BackupUsage.BackupCount:N0} backups · {DisplayFormatting.FormatBytes(data.BackupUsage.TotalBytes)}"
                : $"{data.BackupUsage.BackupCount:N0} 个备份 · {DisplayFormatting.FormatBytes(data.BackupUsage.TotalBytes)}";

        OriginalSettings = data.Settings;
    }

    public SettingsAction SelectedAction { get; private set; }
    public UiSettings OriginalSettings { get; }
    public UiSettings UpdatedSettings { get; private set; } = new();

    private void ManageCache_Click(object sender, RoutedEventArgs e) => Complete(SettingsAction.ManageCache);
    private void ManageBackup_Click(object sender, RoutedEventArgs e) => Complete(SettingsAction.ManageBackup);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Complete(SettingsAction.OpenLogs);
    private void Save_Click(object sender, RoutedEventArgs e) => Complete(SettingsAction.SaveAndClose);

    private void Complete(SettingsAction action)
    {
        UpdatedSettings = new UiSettings
        {
            Language = Selected(LanguageComboBox, OriginalSettings.Language),
            Theme = Selected(ThemeComboBox, OriginalSettings.Theme),
            AutoDetectGameDirectory = AutoDetectCheckBox.IsChecked == true,
            AutoInspectOnStartup = AutoInspectCheckBox.IsChecked == true,
            ShowLastGameDirectory = ShowLastGameCheckBox.IsChecked == true,
            RememberWindowPlacement = RememberWindowCheckBox.IsChecked == true,
            ShowDetailedStatus = ShowDetailsCheckBox.IsChecked == true,
            LogRetentionDays = Selected(LogRetentionComboBox, OriginalSettings.LogRetentionDays),
            WindowLeft = OriginalSettings.WindowLeft,
            WindowTop = OriginalSettings.WindowTop,
            WindowWidth = OriginalSettings.WindowWidth,
            WindowHeight = OriginalSettings.WindowHeight,
            WindowMaximized = OriginalSettings.WindowMaximized
        };
        SelectedAction = action;
        DialogResult = true;
    }

    private static void Select<T>(System.Windows.Controls.ComboBox comboBox, T value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<Choice<T>>()
            .FirstOrDefault(item => EqualityComparer<T>.Default.Equals(item.Value, value));
        comboBox.SelectedIndex = Math.Max(0, comboBox.SelectedIndex);
    }

    private static T Selected<T>(System.Windows.Controls.ComboBox comboBox, T fallback) =>
        comboBox.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private sealed record Choice<T>(T Value, string DisplayName);
}
