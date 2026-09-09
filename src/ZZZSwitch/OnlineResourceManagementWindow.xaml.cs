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
    OnlineDifferencePackageInfo? Package)
{
    public IReadOnlyList<OnlineDifferencePackageInfo> Packages { get; init; } = [];
}

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
        UpdateSelection();
    }

    public OnlineResourceManagementSelection Selection { get; private set; } =
        new(OnlineResourceManagementAction.None, null);

    private void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelection();

    private void UpdateSelection()
    {
        var count = PackageList.SelectedItems.Count;
        var row = count == 1 ? PackageList.SelectedItem as ResourceRow : null;
        var selected = row is not null;
        OpenButton.IsEnabled = selected;
        DeleteButton.IsEnabled = count > 0;
        PreviewButton.IsEnabled = selected;
        VerifyButton.IsEnabled = row?.Package.State == OnlineDifferencePackageState.Ready;
        UpdatePackageButton.IsEnabled = selected;
        SelectedCountText.Text = ((App)System.Windows.Application.Current).Localization.Choose(
            $"已选 {count:N0} 项", $"{count:N0} selected");
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

    private void Open_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.OpenDirectory);

    private void Delete_Click(object sender, RoutedEventArgs e) =>
        Complete(OnlineResourceManagementAction.Delete);

    private void Complete(OnlineResourceManagementAction action, bool requiresPackage = true)
    {
        var packages = PackageList.SelectedItems.Cast<ResourceRow>().Select(row => row.Package).ToArray();
        var package = packages.Length == 1 ? packages[0] : null;
        if (requiresPackage && (packages.Length == 0 ||
                                (action != OnlineResourceManagementAction.Delete && package is null)))
        {
            return;
        }

        Selection = new(action, package) { Packages = packages };
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
