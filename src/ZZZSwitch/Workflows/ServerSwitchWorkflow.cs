using System.IO;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;

namespace ZZZSwitch.Workflows;

public sealed class ServerSwitchWorkflow
{
    private readonly SwitchPlanner _planner;
    private readonly SwitchEngine _engine;
    private readonly OperationCoordinator _operations;
    private readonly IOnlineDifferenceService _onlineDifferences;
    private readonly IMainWindowDialogs _dialogs;
    private readonly MainWindowWorkflowContext _context;

    public ServerSwitchWorkflow(
        SwitchPlanner planner,
        SwitchEngine engine,
        OperationCoordinator operations,
        IOnlineDifferenceService onlineDifferences,
        IMainWindowDialogs dialogs,
        MainWindowWorkflowContext context)
    {
        _planner = planner;
        _engine = engine;
        _operations = operations;
        _onlineDifferences = onlineDifferences;
        _dialogs = dialogs;
        _context = context;
    }

    public async Task RunAsync(string targetProfile)
    {
        if (_context.IsBusy() || !_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        await _context.RefreshInspection();
        var inspection = _context.GetInspectionReport();
        var sourceProfile = inspection?.Detection.Profile.ToProfileId();
        if (sourceProfile is null)
        {
            _dialogs.Show(
                T("无法切换", "Unable to switch"),
                T(
                    "当前来源服无法可靠确定。为避免选择错误方向，程序不会自动执行切换。请先查看详细检查信息。",
                    "The current source server could not be identified reliably. Review the detailed status before switching."),
                MessageTone.Warning);
            return;
        }

        if (sourceProfile == targetProfile)
        {
            _dialogs.Show(
                T("无需切换", "No switch needed"),
                T(
                    "当前已经是目标服务器，不会执行重复覆盖或删除。",
                    "The client is already on the selected server. No files will be replaced or deleted."),
                MessageTone.Information,
                accentBrush: _context.ProfileBrush(targetProfile));
            return;
        }

        var gameVersion = inspection?.Game.GameVersion;
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            _dialogs.Show(
                T("无法获取客户端差异包", "Unable to get the client difference package"),
                T(
                    "没有读取到有效游戏版本，无法选择对应的切换文件。",
                    "No valid game version was detected, so the matching switch files cannot be selected."),
                MessageTone.Warning);
            return;
        }

        var usesLegacyBilibiliPackage = UsesLegacyBilibiliPackage(sourceProfile, targetProfile);
        OnlineDifferenceMaterialization? materialization = null;
        SwitchPlan plan;
        var selectedGamePath = _context.GetGamePath().Trim();
        if (usesLegacyBilibiliPackage)
        {
            _context.SetBusy(true, "正在校验本地 B 服差异包…");
            try
            {
                plan = await Task.Run(() =>
                    _planner.CreatePlan(selectedGamePath, sourceProfile, targetProfile));
            }
            finally
            {
                _context.SetBusy(false, "B 服差异包校验结束");
            }
        }
        else
        {
            var hasReadyTarget = _onlineDifferences.TryGetReadyMaterialization(
                sourceProfile, targetProfile, gameVersion, out materialization);
            var hasReadyReverse = _onlineDifferences.TryGetReadyMaterialization(
                targetProfile, sourceProfile, gameVersion, out _);
            if (!hasReadyTarget || !hasReadyReverse)
            {
                OnlineDifferencePlan? onlinePlan = null;
                _context.SetBusy(true, "正在读取 Sophon 清单并计算差异…");
                try
                {
                    onlinePlan = await _onlineDifferences.AnalyzeAsync(
                        sourceProfile,
                        targetProfile,
                        gameVersion,
                        selectedGamePath);
                }
                catch (Exception) when (hasReadyTarget)
                {
                    // The existing target package remains usable even when an optional
                    // reverse-package refresh cannot reach or read the manifests.
                }
                catch (Exception ex)
                {
                    _dialogs.Show(
                        T("无法获取客户端差异包", "Unable to get the client difference package"),
                        T(
                            $"{ex.Message}\n\n国服与国际服不会回退到游戏目录中的旧差异包。",
                            $"{ex.Message}\n\nGlobal/CN switching will not fall back to a legacy package in the game directory."),
                        MessageTone.Error);
                    return;
                }
                finally
                {
                    _context.SetBusy(false, "客户端差异分析结束");
                }

                if (onlinePlan is not null)
                {
                    materialization = _dialogs.DownloadOnlineDifference(onlinePlan, _onlineDifferences);
                    if (materialization is null)
                    {
                        return;
                    }
                }
            }

            _context.SetBusy(true, materialization!.ReusedReadyPackage
                ? "正在校验本地版本差异包…"
                : "正在执行切换前完整性检查…");
            try
            {
                plan = await Task.Run(() =>
                    _planner.CreateOnlinePlan(selectedGamePath, materialization));
            }
            finally
            {
                _context.SetBusy(false, "差异包校验结束");
            }
        }

        var errors = plan.Issues.Where(x => x.Severity == IssueSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            _dialogs.Show(
                T("切换前检查未通过", "Pre-switch checks failed"),
                string.Join(Environment.NewLine, errors.Select(x => "• " + x.Message)),
                MessageTone.Warning);
            return;
        }

        if (!_dialogs.ConfirmSwitch(new SwitchConfirmationRequest(
                sourceProfile,
                DisplayFormatting.ShortProfileName(sourceProfile),
                targetProfile,
                DisplayFormatting.ShortProfileName(targetProfile),
                plan.Manifest.GameVersion,
                plan.Manifest.ExpectedReplaceCount,
                plan.Manifest.ExpectedDeleteCount,
                plan.BackupPath)))
        {
            return;
        }

        _context.SetBusy(true, "准备执行切换…");
        var progress = new Progress<OperationProgress>(_context.ShowOperationProgress);
        try
        {
            var result = await _engine.ExecuteAsync(plan, progress);
            _dialogs.Show(
                result.Success
                    ? T("切换完成", "Switch complete")
                    : T("切换失败", "Switch failed"),
                result.Success
                    ? T(
                        $"服务器资源已切换完成。\n\n替换 {result.SuccessfulReplace}/{result.PlannedReplace} 个文件\n删除 {result.SuccessfulDelete}/{result.PlannedDelete} 个文件\n缓存恢复 {result.SuccessfulCacheRestore}/{result.PlannedCacheRestore}\n\n回滚备份：{result.BackupPath}" +
                        BilibiliLaunchHint(targetProfile, plan.GamePath, english: false),
                        $"Server resources were switched successfully.\n\nReplaced {result.SuccessfulReplace}/{result.PlannedReplace} files\nDeleted {result.SuccessfulDelete}/{result.PlannedDelete} files\nRestored {result.SuccessfulCacheRestore}/{result.PlannedCacheRestore} cache files\n\nRollback backup: {result.BackupPath}" +
                        BilibiliLaunchHint(targetProfile, plan.GamePath, english: true))
                    : T(
                        $"切换未能完成。\n\n{result.Error}\n\n自动回滚：{(result.RolledBack ? "成功" : "未完成或无需")}",
                        $"The switch could not be completed.\n\n{result.Error}\n\nAutomatic rollback: {(result.RolledBack ? "successful" : "not completed or not required")}"),
                result.Success ? MessageTone.Success : MessageTone.Error);
        }
        finally
        {
            _context.SetBusyStatus("操作结束，正在重新检查…");
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "操作结束");
        }
    }

    private static bool UsesLegacyBilibiliPackage(string sourceProfile, string targetProfile) =>
        string.Equals(sourceProfile, ProfileIds.Bilibili, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(targetProfile, ProfileIds.Bilibili, StringComparison.OrdinalIgnoreCase);

    private string T(string chinese, string english) => _context.Localize(chinese, english);

    private static string BilibiliLaunchHint(string targetProfile, string gamePath, bool english)
    {
        if (!string.Equals(targetProfile, ProfileIds.Bilibili, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var launcher = Path.Combine(
            gamePath,
            "ZenlessZoneZero_Data",
            "Plugins",
            "x86_64",
            "BLPlatform64",
            "PCGamePlatform.exe");
        return english
            ? $"\n\nLaunch Bilibili through its dedicated login window:\n{launcher}"
            : $"\n\nB服请通过专用登录窗启动：\n{launcher}";
    }
}
