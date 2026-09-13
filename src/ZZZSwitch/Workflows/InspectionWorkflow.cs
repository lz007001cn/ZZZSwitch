using System.IO;
using System.Net.Http;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch.Workflows;

public sealed class InspectionWorkflow(
    InspectionService _inspection,
    IOnlineDifferenceService _onlineDifferences,
    BundledBilibiliPackageService _bundledBilibiliPackage,
    FileTransactionJournalStore _fileTransactions,
    AppPaths _paths,
    OperationCoordinator _operations,
    InspectionUiContext _ui)
{
    private readonly HashSet<string> _attemptedDetectionManifests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preparedBilibiliPackages = new(StringComparer.OrdinalIgnoreCase);

    public void ResetManifestAttempts() => _attemptedDetectionManifests.Clear();

    public async Task<InspectionReport?> RunAsync(
        bool showReadOnlyBanner = false,
        bool allowWhileBusy = false)
    {
        // 切换、初始化和恢复完成后的复检复用现有忙碌状态，避免进度浮层闪退后立即重现。
        var managesBusyState = !_ui.State.IsBusy;
        if (!managesBusyState && !allowWhileBusy)
        {
            return null;
        }

        InspectionReport? report = null;
        if (managesBusyState)
        {
            _ui.SetBusy(true, _ui.Localize("正在读取客户端状态…", "Loading client status…"));
        }
        else
        {
            _ui.State.BusyStatus = _ui.Localize("正在读取客户端状态…", "Loading client status…");
            _ui.State.IsProgressIndeterminate = true;
            _ui.State.ProgressValue = 0;
        }
        try
        {
            var path = _ui.State.GamePath.Trim();
            report = await Task.Run(() => _inspection.Inspect(path, readOnly: showReadOnlyBanner));
            if (!showReadOnlyBanner && report.Game.IsValid && report.Detection.NeedsManifestRefresh &&
                report.Game.GameVersion is { } detectionVersion && !_fileTransactions.Exists &&
                _attemptedDetectionManifests.Add(detectionVersion))
            {
                _ui.State.BusyStatus = _ui.Localize(
                    $"正在获取 {detectionVersion} 客户端识别清单…",
                    $"Fetching client identification manifests for {detectionVersion}…");
                try
                {
                    await _onlineDifferences.RefreshManifestsAsync(detectionVersion);
                    report = await Task.Run(() => _inspection.Inspect(path));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
                {
                    report.Issues.Add(new(IssueSeverity.Warning, "detection.manifest.failed",
                        _ui.Localize(
                            $"识别清单获取失败，可在差异包管理中更新 Manifest 后重试：{ex.Message}",
                            $"Could not fetch identification manifests. Retry Refresh Manifest in client package management: {ex.Message}")));
                }
            }
            if (!showReadOnlyBanner && report.Detection.Profile == DetectedProfile.Bilibili)
            {
                IDisposable? versionLease = null;
                // Pre-switch refresh already runs under the workflow's lease.
                if (_operations.IsBusy || _operations.TryBegin(out versionLease))
                {
                    using (versionLease)
                        await Task.Run(() => _inspection.SynchronizeBilibiliVersion(report));
                }
            }
            if (!showReadOnlyBanner && !_fileTransactions.Exists && !File.Exists(_paths.HotUpdateJournalFile) && report.Game.IsValid && report.Game.GameVersion is { } gameVersion)
            {
                var packageKey = $"{Path.GetFullPath(path)}|{gameVersion}";
                if (!_preparedBilibiliPackages.Contains(packageKey))
                {
                    _ui.State.BusyStatus = _ui.Localize(
                        "正在准备 B 服组件…",
                        "Preparing Bilibili components…");
                    try
                    {
                        await Task.Run(() =>
                            _bundledBilibiliPackage.EnsureInstalled(path, gameVersion));
                        _preparedBilibiliPackages.Add(packageKey);
                    }
                    catch (Exception ex) when (
                        ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                    {
                        report.Issues.Add(new(
                            IssueSeverity.Warning,
                            "bilibili.bundle.install.failed",
                            _ui.Localize(
                                $"B 服组件未能自动准备：{ex.Message}",
                                $"Bilibili components could not be prepared automatically: {ex.Message}")));
                    }
                }
            }

            _ui.PublishReport(report, showReadOnlyBanner);
        }
        catch (Exception ex)
        {
            _ui.PublishReport(null, showReadOnlyBanner);
            _inspection.InvalidateDetection();
            _ui.State.HasStatusIssues = true;
            _ui.State.OperationStatus = _ui.Localize("检查失败", "Inspection failed");
            _ui.State.Report = ex.Message;
        }
        finally
        {
            if (managesBusyState)
            {
                _ui.SetBusy(false, _ui.State.OperationStatus);
            }
        }

        return report;
    }
}
