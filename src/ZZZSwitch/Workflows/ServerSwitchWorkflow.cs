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
    private readonly BundledBilibiliPackageService? _bundledBilibiliPackage;

    public ServerSwitchWorkflow(
        SwitchPlanner planner,
        SwitchEngine engine,
        OperationCoordinator operations,
        IOnlineDifferenceService onlineDifferences,
        IMainWindowDialogs dialogs,
        MainWindowWorkflowContext context,
        BundledBilibiliPackageService? bundledBilibiliPackage = null)
    {
        _planner = planner;
        _engine = engine;
        _operations = operations;
        _onlineDifferences = onlineDifferences;
        _dialogs = dialogs;
        _context = context;
        _bundledBilibiliPackage = bundledBilibiliPackage;
    }

    public async Task RunAsync(string targetProfile, bool useCompactExperience = false)
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

        var sourceResourceProfile = ProfileIds.ToResourceProfile(sourceProfile);
        var targetResourceProfile = ProfileIds.ToResourceProfile(targetProfile);
        var usesBilibili = UsesBilibili(sourceProfile, targetProfile);
        if (usesBilibili && _bundledBilibiliPackage is not null && !_bundledBilibiliPackage.SupportsVersion(gameVersion))
        {
            _dialogs.Show(
                T("当前版本暂不支持 B 服切换", "Bilibili switching is unavailable for this version"),
                T($"游戏版本 {gameVersion} 没有匹配的 B 服组件和切换清单。请等待适配此版本的组件；无需清空缓存。",
                    $"No matching Bilibili components and transition manifest are available for game version {gameVersion}. Wait for updated components; clearing caches is unnecessary."),
                MessageTone.Warning);
            return;
        }
        var usesLocalBilibiliOverlayOnly = usesBilibili && string.Equals(
            sourceResourceProfile,
            targetResourceProfile,
            StringComparison.Ordinal);
        OnlineDifferenceMaterialization? materialization = null;
        SwitchPlan plan;
        var selectedGamePath = _context.GetGamePath().Trim();
        if (usesLocalBilibiliOverlayOnly)
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
            materialization = await PrepareOnlineMaterializationAsync(
                sourceResourceProfile,
                targetResourceProfile,
                gameVersion,
                selectedGamePath);
            if (materialization is null)
            {
                return;
            }

            _context.SetBusy(true, materialization!.ReusedReadyPackage
                ? "正在校验本地版本差异包…"
                : "正在执行切换前完整性检查…");
            try
            {
                plan = await Task.Run(() => usesBilibili
                    ? _planner.CreateBilibiliCompositePlan(
                        selectedGamePath,
                        sourceProfile,
                        targetProfile,
                        materialization)
                    : _planner.CreateOnlinePlan(selectedGamePath, materialization));
            }
            finally
            {
                _context.SetBusy(false, "差异包校验结束");
            }
        }

        if (usesBilibili &&
            _bundledBilibiliPackage is not null &&
            plan.Issues.Any(issue => issue.Code is
                "package.directory.missing" or
                "package.source.missing" or
                "package.integrity.failed"))
        {
            _context.SetBusy(true, "正在修复 B 服组件并重新检查…");
            try
            {
                await Task.Run(() => _bundledBilibiliPackage.EnsureInstalled(
                    selectedGamePath,
                    gameVersion,
                    requireFullVerification: true));
                plan = await Task.Run(() => usesLocalBilibiliOverlayOnly
                    ? _planner.CreatePlan(selectedGamePath, sourceProfile, targetProfile)
                    : _planner.CreateBilibiliCompositePlan(
                        selectedGamePath,
                        sourceProfile,
                        targetProfile,
                        materialization!));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                _dialogs.Show(
                    T("无法修复 B 服组件", "Unable to repair Bilibili components"),
                    ex.Message,
                    MessageTone.Error);
                return;
            }
            finally
            {
                _context.SetBusy(false, "B 服组件检查结束");
            }
        }

        var errors = plan.Issues.Where(x => x.Severity == IssueSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            _context.InvalidateDetection?.Invoke();
            _dialogs.Show(
                T("切换前检查未通过", "Pre-switch checks failed"),
                string.Join(Environment.NewLine, errors.Select(FormatValidationIssue)),
                MessageTone.Warning);
            return;
        }

        if (UsesModalSwitchFlow(useCompactExperience) && !_dialogs.ConfirmSwitch(new SwitchConfirmationRequest(
                sourceProfile,
                DisplayFormatting.ShortProfileName(sourceProfile),
                targetProfile,
                DisplayFormatting.ShortProfileName(targetProfile),
                plan.Manifest.GameVersion,
                plan.Manifest.PlannedReplaceCount,
                plan.Manifest.PlannedDeleteCount,
                plan.BackupPath)))
        {
            return;
        }

        _context.SetBusy(true, "准备执行切换…");
        var progress = new Progress<OperationProgress>(_context.ShowOperationProgress);
        OperationResult? result = null;
        try
        {
            result = await _engine.ExecuteAsync(plan, progress);
            if (UsesModalSwitchFlow(useCompactExperience))
            {
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
                            FormatFailure(result, english: false),
                            FormatFailure(result, english: true)),
                    result.Success ? MessageTone.Success : MessageTone.Error);
            }
        }
        finally
        {
            if (result?.Success != true) _context.InvalidateDetection?.Invoke();
            _context.SetBusyStatus(T("操作结束，正在更新状态…", "Updating status…"));
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "操作结束");
        }

        if (useCompactExperience && result is not null)
        {
            _context.ShowInlineSwitchResult(
                result.Success
                    ? $"切换完成：替换 {result.SuccessfulReplace}，删除 {result.SuccessfulDelete}"
                    : FormatFailure(result, english: false),
                result.Success
                    ? $"Switch complete: {result.SuccessfulReplace} replaced, {result.SuccessfulDelete} deleted"
                    : FormatFailure(result, english: true),
                result.Success);
        }
    }

    private string FormatValidationIssue(ValidationIssue issue)
    {
        var text = "• " + issue.Message;
        return string.IsNullOrWhiteSpace(issue.Path) || issue.Message.Contains(issue.Path, StringComparison.OrdinalIgnoreCase)
            ? text
            : $"{text}\n  {T("路径", "Path")}: {issue.Path}";
    }

    private static string FormatFailure(OperationResult result, bool english)
    {
        var state = result.RolledBack
            ? (english ? "Restored to the previous state." : "已恢复到切换前状态。")
            : result.GameFilesUnchanged
                ? (english ? "Game files were not modified by this operation." : "本次操作未修改游戏文件。")
                : (english ? "Recovery needs attention." : "恢复尚未完成，需要处理。");
        var message = english
            ? $"Switch failed: {state}\n\n{result.Error}"
            : $"切换失败：{state}\n\n{result.Error}";
        if (!result.RolledBack && !result.GameFilesUnchanged)
        {
            message += english
                ? "\n\nRestart ZZZSwitch to resume recovery. If it still fails, check the operation log."
                : "\n\n请重新启动 ZZZSwitch 完成恢复；若仍失败，请查看操作日志。";
            if (!string.IsNullOrWhiteSpace(result.BackupPath))
            {
                message += $"\n{(english ? "Retained backup" : "保留的备份")}: {result.BackupPath}";
            }
        }

        return message;
    }

    private async Task<OnlineDifferenceMaterialization?> PrepareOnlineMaterializationAsync(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        string selectedGamePath)
    {
        var ready = _onlineDifferences.GetReadyMaterializations(
            sourceProfile,
            targetProfile,
            gameVersion);
        var materialization = ready.Forward;
        var hasReadyTarget = materialization is not null;
        var hasReadyReverse = ready.Reverse is not null;
        if (hasReadyTarget && hasReadyReverse)
        {
            return materialization;
        }

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
            return null;
        }
        finally
        {
            _context.SetBusy(false, "客户端差异分析结束");
        }

        return onlinePlan is null
            ? materialization
            : _dialogs.DownloadOnlineDifference(onlinePlan, _onlineDifferences);
    }

    private static bool UsesBilibili(string sourceProfile, string targetProfile) =>
        string.Equals(sourceProfile, ProfileIds.Bilibili, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(targetProfile, ProfileIds.Bilibili, StringComparison.OrdinalIgnoreCase);

    private static bool UsesModalSwitchFlow(bool useCompactExperience) => !useCompactExperience;

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
