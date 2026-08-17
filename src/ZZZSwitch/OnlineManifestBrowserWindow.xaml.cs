using System.Windows;
using System.Windows.Controls;
using ZZZSwitch.Core.Models;
using ZZZSwitch.ManifestTool.Classification;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.Presentation;

namespace ZZZSwitch;

public partial class OnlineManifestBrowserWindow : Window
{
    private readonly string _gameVersion;

    public OnlineManifestBrowserWindow(OnlineManifestBrowserData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _gameVersion = data.GameVersion;
        InitializeComponent();
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);

        DirectionComboBox.ItemsSource = new[]
        {
            new DirectionOption("国际服 → 国服", data.GlobalToCn),
            new DirectionOption("国服 → 国际服", data.CnToGlobal)
        };
        ScopeComboBox.ItemsSource = new[]
        {
            new ScopeOption("全部资源", ManifestBrowseScope.AllResources),
            new ScopeOption("剧情 / 视频资源", ManifestBrowseScope.StoryMedia),
            new ScopeOption("音频资源", ManifestBrowseScope.Audio),
            new ScopeOption("Streaming Blocks", ManifestBrowseScope.StreamingBlocks),
            new ScopeOption("状态元数据", ManifestBrowseScope.StateMetadata),
            new ScopeOption("客户端差异", ManifestBrowseScope.ClientDifference)
        };
        DirectionComboBox.SelectedIndex = 0;
        ScopeComboBox.SelectedIndex = 0;
        RefreshView();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshView();

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => RefreshView();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshView()
    {
        if (DirectionComboBox.SelectedItem is not DirectionOption direction ||
            ScopeComboBox.SelectedItem is not ScopeOption scope)
        {
            return;
        }

        var search = SearchTextBox.Text.Trim();
        var filtered = direction.Direction.Files
            .Where(file => Includes(scope.Scope, file))
            .Where(file => search.Length == 0 || file.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ResourceList.ItemsSource = filtered.Select(file => new ResourceRow(
            file.Path,
            Category(file),
            Change(file.ChangeType),
            DisplayFormatting.FormatBytes(file.Size),
            file.Md5)).ToArray();

        var manifest = direction.Direction.TargetManifest;
        ManifestText.Text =
            $"{_gameVersion} · 目标 {DisplayFormatting.ShortProfileName(direction.Direction.TargetProfile)} · " +
            $"Manifest {ShortId(manifest.ManifestId)} · 共 {manifest.FileCount:N0} 项";
        ResultText.Text =
            $"当前 {filtered.Length:N0} 项 · " +
            DisplayFormatting.FormatBytes(filtered.Aggregate(0L, (sum, file) => checked(sum + file.Size)));
    }

    private static bool Includes(ManifestBrowseScope scope, OnlineManifestBrowseFile file) => scope switch
    {
        ManifestBrowseScope.ClientDifference => file.IsClientDifference,
        ManifestBrowseScope.StoryMedia => file.IsStoryMedia,
        ManifestBrowseScope.Audio => file.IsAudio,
        ManifestBrowseScope.StreamingBlocks => file.IsStreamingBlocks,
        ManifestBrowseScope.StateMetadata => file.IsStateMetadata,
        _ => true
    };

    private static string Category(OnlineManifestBrowseFile file)
    {
        if (file.IsStoryMedia)
        {
            return "剧情 / 视频";
        }

        if (file.IsAudio)
        {
            return "音频";
        }

        if (file.IsStreamingBlocks)
        {
            return "Streaming Blocks";
        }

        return file.FileClass switch
        {
            ManifestFileClass.BaseClient => "基础客户端",
            ManifestFileClass.BaseResource => "基础资源",
            ManifestFileClass.RuntimeHotUpdate => "运行时热更新",
            ManifestFileClass.StateMetadata => "状态元数据",
            ManifestFileClass.NeedsObservation => "待观察",
            _ => "其他资源"
        };
    }

    private static string Change(ManifestChangeType? change) => change switch
    {
        ManifestChangeType.Modified => "修改",
        ManifestChangeType.Added => "新增",
        ManifestChangeType.Removed => "缺少",
        _ => "相同"
    };

    private static string ShortId(string value) => value.Length <= 18 ? value : value[..18] + "…";

    private enum ManifestBrowseScope
    {
        ClientDifference,
        AllResources,
        StoryMedia,
        Audio,
        StreamingBlocks,
        StateMetadata
    }

    private sealed record DirectionOption(string Name, OnlineManifestDirection Direction)
    {
        public override string ToString() => Name;
    }

    private sealed record ScopeOption(string Name, ManifestBrowseScope Scope)
    {
        public override string ToString() => Name;
    }

    private sealed record ResourceRow(string Path, string Category, string Change, string Size, string Md5);
}
