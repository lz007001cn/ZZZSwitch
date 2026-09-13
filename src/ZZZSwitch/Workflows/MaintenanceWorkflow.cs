using System.IO;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch.Workflows;

public sealed class MaintenanceWorkflow(
    OperationCoordinator _operations,
    InspectionService _inspection,
    PendingTransactionRecoveryService _recovery,
    ClientMaintenanceService _maintenance,
    GameDirectoryService _gameDirectory,
    BundledBilibiliPackageService _bundledBilibiliPackage,
    IOnlineDifferenceService _onlineDifferences,
    InspectionUiContext _ui,
    Action _resetPrompts)
{
    public async Task<string> RunAsync(CheckAction action)
    {
        if (action is not (CheckAction.Detect or CheckAction.Reset or CheckAction.Repair or CheckAction.Recover))
            throw new ArgumentOutOfRangeException(nameof(action));
        if (!_operations.TryBegin(out var lease, allowPendingRecovery: action is CheckAction.Recover or CheckAction.Detect))
            return _operations.LastFailure ?? "Operation in progress.";
        using (lease)
        {
            _ui.SetBusy(true, _ui.Localize("正在检查…", "Checking…"));
            try
            {
                var path = _ui.State.GamePath.Trim();
                var details = new List<string>();
                if (action == CheckAction.Recover)
                {
                    var result = await Task.Run(_recovery.RecoverPending);
                    _inspection.InvalidateDetection();
                    details.Add(result.Message);
                    if (!result.Success) return string.Join(Environment.NewLine, details);
                }
                if (action == CheckAction.Reset)
                {
                    _maintenance.ResetDetection();
                    _resetPrompts();
                    details.Add(_ui.Localize("识别状态已重置，备份、缓存和路径设置已保留。", "Detection state reset. Backups, caches and storage settings were retained."));
                }
                if (action == CheckAction.Repair)
                {
                    var game = _gameDirectory.Validate(path);
                    if (!game.IsValid || game.GameVersion is null)
                        return string.Join(Environment.NewLine, game.Issues.Select(x => x.Message));
                    await Task.Run(() => _bundledBilibiliPackage.EnsureInstalled(path, game.GameVersion, requireFullVerification: true));
                    details.Add(_ui.Localize("B 服缓存组件已检查。", "Cached Bilibili components checked."));
                    try
                    {
                        await _onlineDifferences.RefreshManifestsAsync(game.GameVersion);
                        details.Add(_ui.Localize("当前版本识别清单已更新。", "Current-version identification manifests refreshed."));
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Http.HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
                    { details.Add(_ui.Localize("清单更新未完成，可再次重试：", "Manifest refresh incomplete; retry when ready: ") + ex.Message); }
                    _inspection.InvalidateDetection();
                }
                var report = await Task.Run(() => _inspection.Inspect(path, readOnly: true));
                _ui.PublishReport(report, true);
                details.Add(_ui.State.Report);
                return string.Join(Environment.NewLine + Environment.NewLine, details);
            }
            catch
            {
                _ui.PublishReport(null, true);
                _inspection.InvalidateDetection();
                throw;
            }
            finally { _ui.SetBusy(false, _ui.Localize("检查结束", "Check finished")); }
        }
    }
}
