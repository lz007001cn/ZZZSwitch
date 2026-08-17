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
    private readonly LocalizationManager _localization;

    public OnlineManifestBrowserWindow(OnlineManifestBrowserData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _gameVersion = data.GameVersion;
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        _localization = app.Localization;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);

        DirectionComboBox.ItemsSource = new[]
        {
            new DirectionOption(_localization.Choose("国际服 → 国服", "Global → CN Official"), data.GlobalToCn),
            new DirectionOption(_localization.Choose("国服 → 国际服", "CN Official → Global"), data.CnToGlobal)
        };
        ScopeComboBox.ItemsSource = new[]
        {
            new ScopeOption(_localization.Choose("全部资源", "All resources"), ManifestBrowseScope.AllResources),
            new ScopeOption(_localization.Choose("剧情 / 视频资源", "Story / video resources"), ManifestBrowseScope.StoryMedia),
            new ScopeOption(_localization.Choose("音频资源", "Audio resources"), ManifestBrowseScope.Audio),
            new ScopeOption("Streaming Blocks", ManifestBrowseScope.StreamingBlocks),
            new ScopeOption(_localization.Choose("状态元数据", "State metadata"), ManifestBrowseScope.StateMetadata),
            new ScopeOption(_localization.Choose("客户端差异", "Client differences"), ManifestBrowseScope.ClientDifference)
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
            DisplayFormatting.FormatBytes(file.Size))).ToArray();

        var manifest = direction.Direction.TargetManifest;
        ManifestText.Text =
            _localization.Choose(
                $"{_gameVersion} · 目标 {_localization.ProfileName(direction.Direction.TargetProfile)} · Manifest {ShortId(manifest.ManifestId)} · 共 {manifest.FileCount:N0} 项",
                $"{_gameVersion} · Target {_localization.ProfileName(direction.Direction.TargetProfile)} · Manifest {ShortId(manifest.ManifestId)} · {manifest.FileCount:N0} entries");
        var resultBytes = DisplayFormatting.FormatBytes(
            filtered.Aggregate(0L, (sum, file) => checked(sum + file.Size)));
        ResultText.Text = _localization.Choose(
            $"当前 {filtered.Length:N0} 项 · {resultBytes}",
            $"{filtered.Length:N0} shown · {resultBytes}");
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

    private string Category(OnlineManifestBrowseFile file)
    {
        if (file.IsStoryMedia)
        {
            return _localization.Choose("剧情 / 视频", "Story / video");
        }

        if (file.IsAudio)
        {
            return _localization.Choose("音频", "Audio");
        }

        if (file.IsStreamingBlocks)
        {
            return "Streaming Blocks";
        }

        return file.FileClass switch
        {
            ManifestFileClass.BaseClient => _localization.Choose("基础客户端", "Base client"),
            ManifestFileClass.BaseResource => _localization.Choose("基础资源", "Base resources"),
            ManifestFileClass.RuntimeHotUpdate => _localization.Choose("运行时热更新", "Runtime hot update"),
            ManifestFileClass.StateMetadata => _localization.Choose("状态元数据", "State metadata"),
            ManifestFileClass.NeedsObservation => _localization.Choose("待观察", "Needs review"),
            _ => _localization.Choose("其他资源", "Other resources")
        };
    }

    private string Change(ManifestChangeType? change) => change switch
    {
        ManifestChangeType.Modified => _localization.Choose("修改", "Modified"),
        ManifestChangeType.Added => _localization.Choose("新增", "Added"),
        ManifestChangeType.Removed => _localization.Choose("缺少", "Missing"),
        _ => _localization.Choose("相同", "Same")
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

    private sealed record ResourceRow(string Path, string Category, string Change, string Size);
}
