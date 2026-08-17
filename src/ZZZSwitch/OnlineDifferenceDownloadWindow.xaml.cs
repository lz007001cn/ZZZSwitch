using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Presentation;

namespace ZZZSwitch;

public partial class OnlineDifferenceDownloadWindow : Window
{
    private readonly OnlineDifferencePlan _plan;
    private readonly IOnlineDifferenceService _service;
    private CancellationTokenSource? _cancellation;
    private bool _downloading;
    private bool _allowClose;
    private Stopwatch? _stopwatch;
    private readonly bool _continueToSwitch;

    public OnlineDifferenceDownloadWindow(
        OnlineDifferencePlan plan,
        IOnlineDifferenceService service,
        bool continueToSwitch = true)
    {
        _plan = plan;
        _service = service;
        _continueToSwitch = continueToSwitch;
        InitializeComponent();
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);
        Closing += OnClosing;

        DirectionText.Text =
            $"{DisplayFormatting.ShortProfileName(plan.SourceProfile)} → {DisplayFormatting.ShortProfileName(plan.TargetProfile)} · {plan.GameVersion}";
        FileCountText.Text = $"{plan.DownloadFiles.Count:N0} 个";
        DownloadSizeText.Text = FormatBytes(plan.DownloadBytes);
        StatusText.Text = "等待开始";
        CurrentFileText.Text = "开始后将下载到 ZZZSwitch 应用数据缓存，再交给事务切换。";
        DetailText.Text = "已完成的文件会在校验通过后复用。";
        PercentText.Text = "0%";
    }

    public OnlineDifferenceMaterialization? Result { get; private set; }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            return;
        }

        _downloading = true;
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _stopwatch = Stopwatch.StartNew();
        StartButton.IsEnabled = false;
        StartButton.Content = "开始下载";
        CancelButton.IsEnabled = true;
        CancelButton.Content = "取消下载";
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在下载并校验…";
        CurrentFileText.Text = "正在准备第一个文件…";
        DetailText.Text = string.Empty;
        var progress = new Progress<OnlineDifferenceProgress>(ShowProgress);
        try
        {
            Result = await _service.MaterializeAsync(_plan, progress, _cancellation.Token);
            StatusText.Text = "下载与校验完成";
            DetailText.Text =
                $"新下载 {Result.DownloadedFiles:N0} 个，复用 {Result.ReusedFiles:N0} 个；" +
                (_continueToSwitch ? "即将进入切换确认。" : "差异包已保存，可在切换时直接复用。");
            DownloadProgressBar.Value = 100;
            PercentText.Text = "100%";
            _allowClose = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "下载已取消";
            DetailText.Text = "未完成的临时文件已清理；已校验完成的缓存可在下次复用。";
            _allowClose = true;
            DialogResult = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "下载失败";
            DetailText.Text = "已校验的完整文件和分块断点均会保留；可点击“重试”继续。";
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            StartButton.Content = "重试";
            CancelButton.Content = "关闭";
        }
        finally
        {
            _downloading = false;
            if (!_allowClose)
            {
                StartButton.Content = "重试";
                StartButton.IsEnabled = true;
                CancelButton.Content = "关闭";
                CancelButton.IsEnabled = true;
            }
        }
    }

    private void ShowProgress(OnlineDifferenceProgress progress)
    {
        var ratio = progress.TotalBytes == 0
            ? 1d
            : Math.Clamp((double)progress.CompletedBytes / progress.TotalBytes, 0d, 1d);
        DownloadProgressBar.Value = ratio * 100;
        PercentText.Text = $"{ratio:P0}";
        CurrentFileText.Text = progress.CurrentFile;
        StatusText.Text = progress.VerifyingExistingFile
            ? "正在校验已下载文件…"
            : progress.ReusingChunkCache
                ? "正在从断点缓存恢复…"
                : progress.DownloadAttempt > 1
                    ? $"正在自动重试当前分块（第 {progress.DownloadAttempt}/{progress.MaximumDownloadAttempts} 次）…"
                    : "正在下载并校验…";
        var chunk = progress.CurrentFileChunksTotal > 0
            ? $" · 分块 {progress.CurrentFileChunksCompleted}/{progress.CurrentFileChunksTotal}"
            : string.Empty;
        var network = progress.CurrentChunkBytesTotal > 0 && !progress.ReusingChunkCache
            ? $" · 活动分块网络 {FormatBytes(progress.CurrentChunkBytesDownloaded)} / {FormatBytes(progress.CurrentChunkBytesTotal)}"
            : string.Empty;
        DetailText.Text =
            $"文件 {progress.CompletedFiles}/{progress.TotalFiles} · {FormatBytes(progress.CompletedBytes)} / {FormatBytes(progress.TotalBytes)}{chunk}{network}" +
            (progress.ReusingExistingFile ? " · 已复用完整文件" : string.Empty) +
            (progress.ReusingChunkCache ? " · 命中分块断点" : string.Empty);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            CancelButton.IsEnabled = false;
            StatusText.Text = "正在取消…";
            _cancellation?.Cancel();
            return;
        }

        _allowClose = true;
        DialogResult = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_downloading || _allowClose)
        {
            return;
        }

        e.Cancel = true;
        Cancel_Click(this, new RoutedEventArgs());
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
