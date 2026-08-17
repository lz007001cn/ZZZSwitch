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
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);
        RefreshManifestButton.IsEnabled = !string.IsNullOrWhiteSpace(currentGameVersion);
        BrowseManifestButton.IsEnabled = !string.IsNullOrWhiteSpace(currentGameVersion);
        PackageBytesText.Text = DisplayFormatting.FormatBytes(inventory.PackageBytes);
        VersionCountText.Text = $"{inventory.Packages.Select(item => item.GameVersion).Distinct().Count():N0} 个";
        ManifestCacheText.Text =
            $"{DisplayFormatting.FormatBytes(inventory.ManifestCacheBytes)} · {inventory.ManifestCacheFileCount:N0} 个";
        PackageList.ItemsSource = inventory.Packages.Select(package => new ResourceRow(
            package,
            string.Equals(package.GameVersion, currentGameVersion, StringComparison.Ordinal)
                ? package.GameVersion + "（当前）"
                : package.GameVersion,
            DisplayFormatting.ShortProfileName(package.TargetProfile),
            StateName(package.State),
            DisplayFormatting.FormatBytes(package.TotalBytes),
            Detail(package))).ToArray();
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

    private static string StateName(OnlineDifferencePackageState state) => state switch
    {
        OnlineDifferencePackageState.Ready => "已下载",
        OnlineDifferencePackageState.Incomplete => "未完成",
        _ => "需修复"
    };

    private static string Detail(OnlineDifferencePackageInfo package)
    {
        var detail = $"{package.FileCount:N0} 个文件";
        if (package.CheckpointCount > 0)
        {
            detail += $" · {package.CheckpointCount:N0} 个断点";
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
