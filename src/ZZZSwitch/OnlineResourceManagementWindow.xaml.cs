using System.Windows;
using System.Windows.Controls;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Presentation;

namespace ZZZSwitch;

public enum OnlineResourceManagementAction
{
    None,
    Refresh,
    RefreshManifest,
    BrowseManifest,
    Preview,
    Verify,
    UpdatePackage,
    OpenDirectory,
    Delete
}

public sealed record OnlineResourceManagementSelection(
    OnlineResourceManagementAction Action,
    OnlineDifferencePackageInfo? Package);

public partial class OnlineResourceManagementWindow : Window
{
    public OnlineResourceManagementWindow(
        OnlineDifferenceInventory inventory,
        string? currentGameVersion)
    {
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        var localization = app.Localization;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);
        RefreshManifestButton.IsEnabled = !string.IsNullOrWhiteSpace(currentGameVersion);
        BrowseManifestButton.IsEnabled = !string.IsNullOrWhiteSpace(currentGameVersion);
        PackageBytesText.Text = DisplayFormatting.FormatBytes(inventory.PackageBytes);
        var versionCount = inventory.Packages.Select(item => item.GameVersion).Distinct().Count();
        VersionCountText.Text = localization.Choose($"{versionCount:N0} 个", $"{versionCount:N0}");
        ManifestCacheText.Text = localization.Choose(
            $"{DisplayFormatting.FormatBytes(inventory.ManifestCacheBytes)} · {inventory.ManifestCacheFileCount:N0} 个",
            $"{DisplayFormatting.FormatBytes(inventory.ManifestCacheBytes)} · {inventory.ManifestCacheFileCount:N0} files");
        PackageList.ItemsSource = inventory.Packages.Select(package => new ResourceRow(
            package,
            string.Equals(package.GameVersion, currentGameVersion, StringComparison.Ordinal)
                ? package.GameVersion + localization.Choose("（当前）", " (current)")
                : package.GameVersion,
            localization.ProfileName(package.TargetProfile),
            StateName(package.State, localization),
            DisplayFormatting.FormatBytes(package.TotalBytes),
            Detail(package, localization))).ToArray();
        if (PackageList.Items.Count > 0)
        {
            var currentIndex = inventory.Packages
                .Select((package, index) => new { package, index })
                .Where(item => string.Equals(item.package.GameVersion, currentGameVersion, StringComparison.Ordinal))
                .OrderBy(item => item.package.State == OnlineDifferencePackageState.Ready ? 0 : 1)
                .Select(item => item.index)
                .FirstOrDefault();
            PackageList.SelectedIndex = currentIndex;
        }
    }

    public OnlineResourceManagementSelection Selection { get; private set; } =
        new(OnlineResourceManagementAction.None, null);

    private void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = PackageList.SelectedItem as ResourceRow;
        var selected = row is not null;
        OpenButton.IsEnabled = selected;
        DeleteButton.IsEnabled = selected;
        PreviewButton.IsEnabled = selected;
        VerifyButton.IsEnabled = row?.Package.State == OnlineDifferencePackageState.Ready;
        UpdatePackageButton.IsEnabled = selected;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.Refresh, requiresPackage: false);

    private void RefreshManifest_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.RefreshManifest, requiresPackage: false);

    private void BrowseManifest_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.BrowseManifest, requiresPackage: false);

    private void Preview_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.Preview);

    private void Verify_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.Verify);

    private void UpdatePackage_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.UpdatePackage);

    private void PackageList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PackageList.SelectedItem is ResourceRow)
        {
            Complete(OnlineResourceManagementAction.Preview);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.OpenDirectory);

    private void Delete_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.Delete);

    private void Complete(OnlineResourceManagementAction action, bool requiresPackage = true)
    {
        var package = (PackageList.SelectedItem as ResourceRow)?.Package;
        if (requiresPackage && package is null)
        {
            return;
        }

        Selection = new(action, package);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string StateName(OnlineDifferencePackageState state, LocalizationManager localization) => state switch
    {
        OnlineDifferencePackageState.Ready => localization.Choose("已下载", "Ready"),
        OnlineDifferencePackageState.Incomplete => localization.Choose("未完成", "Incomplete"),
        _ => localization.Choose("需修复", "Needs repair")
    };

    private static string Detail(OnlineDifferencePackageInfo package, LocalizationManager localization)
    {
        var detail = localization.Choose($"{package.FileCount:N0} 个文件", $"{package.FileCount:N0} files");
        if (package.CheckpointCount > 0)
        {
            detail += localization.Choose(
                $" · {package.CheckpointCount:N0} 个断点",
                $" · {package.CheckpointCount:N0} checkpoints");
        }

        return string.IsNullOrWhiteSpace(package.Problem)
            ? detail
            : detail + " · " + package.Problem;
    }

    private sealed record ResourceRow(
        OnlineDifferencePackageInfo Package,
        string Version,
        string Target,
        string State,
        string Size,
        string Detail);
}
