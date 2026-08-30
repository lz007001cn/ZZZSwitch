using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ZZZSwitch.ManifestTool.Diff;

namespace ZZZManifestDiffDemo;

public partial class MainWindow : Window
{
    private readonly ManifestComparisonService _comparisonService = new();
    private ManifestSnapshot? _source;
    private ManifestSnapshot? _target;
    private ManifestComparisonResult? _result;
    private string? _sourcePath;
    private string? _targetPath;
    private DifferenceState? _filter;
    private bool _isBusy;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _isInitialized = true;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        LoadDemo();
    }

    private async void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectSnapshot(_sourcePath ?? _targetPath);
        if (path is null)
        {
            return;
        }

        await LoadSnapshotAsync(path, isSource: true);
    }

    private async void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectSnapshot(_targetPath ?? _sourcePath);
        if (path is null)
        {
            return;
        }

        await LoadSnapshotAsync(path, isSource: false);
    }

    private void LoadDemo_Click(object sender, RoutedEventArgs e) => LoadDemo();

    private void LoadDemo()
    {
        var demo = ManifestComparisonService.CreateDemoSnapshots();
        _source = demo.Source;
        _target = demo.Target;
        _sourcePath = null;
        _targetPath = null;
        SourcePathText.Text = "内置演示 / 国际服 snapshot";
        TargetPathText.Text = "内置演示 / 国服 snapshot";
        UpdateManifestCards();
        CompareManifests("已载入内置国际服 → 国服演示数据。");
    }

    private void Compare_Click(object sender, RoutedEventArgs e) => CompareManifests();

    private void CompareManifests(string? successMessage = null)
    {
        if (_source is null || _target is null)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在校验 Manifest 差异...");
            _result = _comparisonService.Compare(_source, _target);
            UpdateSummary();
            ApplyFilter();
            StatusText.Text = successMessage ??
                $"校验完成：{_result.Rows.Count:N0} 个文件，发现 {_result.Diff.Summary.Modified + _result.Diff.Summary.Added + _result.Diff.Summary.Removed:N0} 项差异。";
        }
        catch (Exception exception)
        {
            ShowError("Manifest 校验失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        (_source, _target) = (_target, _source);
        (_sourcePath, _targetPath) = (_targetPath, _sourcePath);
        (SourcePathText.Text, TargetPathText.Text) = (TargetPathText.Text, SourcePathText.Text);
        UpdateManifestCards();
        CompareManifests("已交换来源与目标，并重新校验差异。");
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || sender is not RadioButton { IsChecked: true } radioButton)
        {
            return;
        }

        _filter = Enum.TryParse<DifferenceState>(radioButton.Tag?.ToString(), out var state)
            ? state
            : null;
        ApplyFilter();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        if (_result is null)
        {
            DifferenceGrid.ItemsSource = null;
            VisibleCountText.Text = "0 项";
            return;
        }

        IEnumerable<DifferenceRow> rows = _result.Rows;
        if (_filter.HasValue)
        {
            rows = rows.Where(row => row.State == _filter.Value);
        }

        var query = SearchTextBox.Text.Trim();
        if (query.Length > 0)
        {
            rows = rows.Where(row =>
                row.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (row.SourceMd5?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (row.TargetMd5?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var visibleRows = rows.ToArray();
        DifferenceGrid.ItemsSource = visibleRows;
        VisibleCountText.Text = $"{visibleRows.Length:N0} 项";
        SelectedDetailText.Text = string.Empty;
    }

    private void DifferenceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedDetailText.Text = DifferenceGrid.SelectedItem is DifferenceRow row
            ? $"{row.Path}  |  来源 {row.SourceSizeText} / {row.SourceMd5 ?? "-"}  |  目标 {row.TargetSizeText} / {row.TargetMd5 ?? "-"}"
            : string.Empty;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 Manifest 差异报告",
            FileName = $"manifest-diff-{_result.Source.Region}-to-{_result.Target.Region}",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON 报告 (*.json)|*.json|CSV 表格 (*.csv)|*.csv|文本报告 (*.txt)|*.txt",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在导出差异报告...");
            await _comparisonService.ExportAsync(_result, dialog.FileName);
            StatusText.Text = $"报告已导出：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            ShowError("报告导出失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadSnapshotAsync(string path, bool isSource)
    {
        try
        {
            SetBusy(true, $"正在读取 {System.IO.Path.GetFileName(path)}...");
            var snapshot = await _comparisonService.LoadAsync(path);
            if (isSource)
            {
                _source = snapshot;
                _sourcePath = path;
                SourcePathText.Text = path;
            }
            else
            {
                _target = snapshot;
                _targetPath = path;
                TargetPathText.Text = path;
            }

            _result = null;
            ResetResults();
            UpdateManifestCards();
            StatusText.Text = _source is not null && _target is not null
                ? "两个 Manifest 均已就绪，点击“开始校验”。"
                : "已读取 Manifest，请继续选择另一份 snapshot.json。";
        }
        catch (Exception exception)
        {
            ShowError("Manifest 读取失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateManifestCards()
    {
        SourceMetaText.Text = Describe(_source);
        TargetMetaText.Text = Describe(_target);
        UpdateButtonStates();
    }

    private void UpdateSummary()
    {
        if (_result is null)
        {
            ResetResults();
            return;
        }

        ModifiedCountText.Text = _result.Diff.Summary.Modified.ToString("N0");
        AddedCountText.Text = _result.Diff.Summary.Added.ToString("N0");
        RemovedCountText.Text = _result.Diff.Summary.Removed.ToString("N0");
        SameCountText.Text = _result.Diff.Summary.Same.ToString("N0");
        var delta = _result.TargetBytes - _result.SourceBytes;
        SizeDeltaText.Text = $"{ManifestComparisonService.FormatSignedBytes(delta)}  ({ManifestComparisonService.FormatBytes(_result.SourceBytes)} → {ManifestComparisonService.FormatBytes(_result.TargetBytes)})";
        SizeDeltaText.Foreground = delta switch
        {
            > 0 => (Brush)FindResource("AddedBrush"),
            < 0 => (Brush)FindResource("RemovedBrush"),
            _ => (Brush)FindResource("TextBrush")
        };
        UpdateButtonStates();
    }

    private void ResetResults()
    {
        DifferenceGrid.ItemsSource = null;
        ModifiedCountText.Text = "0";
        AddedCountText.Text = "0";
        RemovedCountText.Text = "0";
        SameCountText.Text = "0";
        SizeDeltaText.Text = "—";
        SizeDeltaText.Foreground = (Brush)FindResource("TextBrush");
        VisibleCountText.Text = "0 项";
        SelectedDetailText.Text = string.Empty;
        UpdateButtonStates();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        if (message is not null)
        {
            StatusText.Text = message;
        }

        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var hasBoth = _source is not null && _target is not null;
        CompareButton.IsEnabled = !_isBusy && hasBoth;
        SwapButton.IsEnabled = !_isBusy && hasBoth;
        LoadDemoButton.IsEnabled = !_isBusy;
        ExportButton.IsEnabled = !_isBusy && _result is not null;
    }

    private static string Describe(ManifestSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "未选择";
        }

        var total = snapshot.Entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Size));
        return $"{snapshot.Region} · {snapshot.Version} · {snapshot.Entries.Count:N0} 文件 · {ManifestComparisonService.FormatBytes(total)}";
    }

    private static string? SelectSnapshot(string? existingPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Sophon Manifest 快照",
            FileName = "snapshot.json",
            DefaultExt = ".json",
            Filter = "Manifest 快照 (snapshot.json)|snapshot.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var initialDirectory = ResolveInitialDirectory(existingPath);
        if (initialDirectory is not null)
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? ResolveInitialDirectory(string? existingPath)
    {
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            var directory = System.IO.Path.GetDirectoryName(existingPath);
            if (Directory.Exists(directory))
            {
                return directory;
            }
        }

        var cache = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZZZSwitch",
            "ManifestCache",
            "nap");
        return Directory.Exists(cache) ? cache : null;
    }

    private void ShowError(string title, Exception exception)
    {
        StatusText.Text = $"{title}：{exception.Message}";
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
