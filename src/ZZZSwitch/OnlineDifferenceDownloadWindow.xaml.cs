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
    private readonly LocalizationManager _localization;
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
        _localization = ((App)System.Windows.Application.Current).Localization;
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);
        Closing += OnClosing;

        DirectionText.Text =
            $"{ProfileName(plan.SourceProfile)} → {ProfileName(plan.TargetProfile)} · {plan.GameVersion}";
        FileCountText.Text = _localization.Choose(
            $"{plan.DownloadFiles.Count:N0} 个",
            $"{plan.DownloadFiles.Count:N0}");
        DownloadSizeText.Text = FormatBytes(plan.DownloadBytes);
        StatusText.Text = _localization.Choose("等待开始", "Ready");
        CurrentFileText.Text = _localization.Choose(
            "开始后将下载到 ZZZSwitch 应用数据缓存，再交给事务切换。",
            "Files will be stored in the ZZZSwitch application cache before the switch.");
        DetailText.Text = _localization.Choose(
            "已完成的文件会在校验通过后复用。",
            "Completed files are reused after verification.");
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
        StartButton.Content = _localization.Text("L.Download.Start");
        CancelButton.IsEnabled = true;
        CancelButton.Content = _localization.Text("L.Download.Cancel");
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = _localization.Choose("正在下载并校验…", "Downloading and verifying…");
        CurrentFileText.Text = _localization.Choose("正在准备第一个文件…", "Preparing the first file…");
        DetailText.Text = string.Empty;
        var progress = new Progress<OnlineDifferenceProgress>(ShowProgress);
        try
        {
            Result = await _service.MaterializeAsync(_plan, progress, _cancellation.Token);
            StatusText.Text = _localization.Choose("下载与校验完成", "Download and verification complete");
            DetailText.Text = _localization.Choose(
                $"新下载 {Result.DownloadedFiles:N0} 个，复用 {Result.ReusedFiles:N0} 个；" +
                (Result.SourcePackageReady
                    ? $"已从当前客户端保存 {Result.PreservedSourceFiles:N0} 个反向切换文件；"
                    : Result.PreservedSourceFiles > 0
                        ? $"已保存 {Result.PreservedSourceFiles:N0} 个可复用来源文件；"
                        : string.Empty) +
                (_continueToSwitch ? "即将进入切换确认。" : "差异包已保存，可在切换时直接复用。"),
                $"Downloaded {Result.DownloadedFiles:N0}, reused {Result.ReusedFiles:N0}; " +
                (Result.SourcePackageReady
                    ? $"preserved {Result.PreservedSourceFiles:N0} files for the reverse switch; "
                    : Result.PreservedSourceFiles > 0
                        ? $"preserved {Result.PreservedSourceFiles:N0} reusable source files; "
                        : string.Empty) +
                (_continueToSwitch ? "opening switch confirmation." : "the package is ready for future switches."));
            DownloadProgressBar.Value = 100;
            PercentText.Text = "100%";
            _allowClose = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Choose("下载已取消", "Download canceled");
            DetailText.Text = _localization.Choose(
                "未完成的临时文件已清理；已校验完成的缓存可在下次复用。",
                "Incomplete temporary files were removed; verified cache files can be reused next time.");
            _allowClose = true;
            DialogResult = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = _localization.Choose("下载失败", "Download failed");
            DetailText.Text = _localization.Choose(
                "已校验的完整文件和分块断点均会保留；可点击“重试”继续。",
                "Verified files and chunk checkpoints were retained. Select Retry to continue.");
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            StartButton.Content = _localization.Text("L.Download.Retry");
            CancelButton.Content = _localization.Text("L.Common.Close");
        }
        finally
        {
            _downloading = false;
            if (!_allowClose)
            {
                StartButton.Content = _localization.Text("L.Download.Retry");
                StartButton.IsEnabled = true;
                CancelButton.Content = _localization.Text("L.Common.Close");
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
        StatusText.Text = _localization.Choose(
            progress.PreservingSourceFiles
                ? "正在保存当前客户端文件…"
                : progress.CheckingLocalFiles
                    ? "正在检查本地可复用文件…"
                    : progress.VerifyingExistingFile
                        ? "正在校验已下载文件…"
                        : progress.ReusingChunkCache
                            ? "正在从断点缓存恢复…"
                            : progress.DownloadAttempt > 1
                                ? $"正在自动重试当前分块（第 {progress.DownloadAttempt}/{progress.MaximumDownloadAttempts} 次）…"
                                : "正在下载并校验…",
            progress.PreservingSourceFiles
                ? "Preserving current client files…"
                : progress.CheckingLocalFiles
                    ? "Checking reusable local files…"
                    : progress.VerifyingExistingFile
                        ? "Verifying downloaded files…"
                        : progress.ReusingChunkCache
                            ? "Restoring from chunk checkpoints…"
                            : progress.DownloadAttempt > 1
                                ? $"Retrying the current chunk ({progress.DownloadAttempt}/{progress.MaximumDownloadAttempts})…"
                                : "Downloading and verifying…");
        var chunk = progress.CurrentFileChunksTotal > 0
            ? _localization.Choose(
                $" · 分块 {progress.CurrentFileChunksCompleted}/{progress.CurrentFileChunksTotal}",
                $" · chunks {progress.CurrentFileChunksCompleted}/{progress.CurrentFileChunksTotal}")
            : string.Empty;
        var network = progress.CurrentChunkBytesTotal > 0 && !progress.ReusingChunkCache
            ? _localization.Choose(
                $" · 活动分块网络 {FormatBytes(progress.CurrentChunkBytesDownloaded)} / {FormatBytes(progress.CurrentChunkBytesTotal)}",
                $" · active network {FormatBytes(progress.CurrentChunkBytesDownloaded)} / {FormatBytes(progress.CurrentChunkBytesTotal)}")
            : string.Empty;
        DetailText.Text = _localization.Choose(
            $"文件 {progress.CompletedFiles}/{progress.TotalFiles} · {FormatBytes(progress.CompletedBytes)} / {FormatBytes(progress.TotalBytes)}{chunk}{network}" +
            (progress.ReusingExistingFile
                ? progress.PreservingSourceFiles ? " · 已保存到反向差异包" : " · 已复用完整文件"
                : string.Empty) +
            (progress.ReusingChunkCache ? " · 命中分块断点" : string.Empty),
            $"Files {progress.CompletedFiles}/{progress.TotalFiles} · {FormatBytes(progress.CompletedBytes)} / {FormatBytes(progress.TotalBytes)}{chunk}{network}" +
            (progress.ReusingExistingFile
                ? progress.PreservingSourceFiles ? " · preserved for reverse switch" : " · reused complete file"
                : string.Empty) +
            (progress.ReusingChunkCache ? " · chunk checkpoint reused" : string.Empty));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            CancelButton.IsEnabled = false;
            StatusText.Text = _localization.Choose("正在取消…", "Canceling…");
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

    private string ProfileName(string profile) => profile switch
    {
        Core.Models.ProfileIds.Global => _localization.Text("L.Server.Global"),
        Core.Models.ProfileIds.CnOfficial => _localization.Text("L.Server.Cn"),
        Core.Models.ProfileIds.Bilibili => _localization.Text("L.Server.Bilibili"),
        _ => profile
    };

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
