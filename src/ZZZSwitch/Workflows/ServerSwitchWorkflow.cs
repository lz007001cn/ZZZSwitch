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
    private readonly IMainWindowDialogs _dialogs;
    private readonly MainWindowWorkflowContext _context;

    public ServerSwitchWorkflow(
        SwitchPlanner planner,
        SwitchEngine engine,
        OperationCoordinator operations,
        IMainWindowDialogs dialogs,
        MainWindowWorkflowContext context)
    {
        _planner = planner;
        _engine = engine;
        _operations = operations;
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
        var sourceProfile = _context.GetInspectionReport()?.Detection.Profile.ToProfileId();
        if (sourceProfile is null)
        {
            _dialogs.Show(
                "无法切换",
                "当前来源服无法可靠确定。为避免选择错误方向，程序不会自动执行切换。请先查看详细检查信息。",
                MessageTone.Warning);
            return;
        }

        if (sourceProfile == targetProfile)
        {
            _dialogs.Show(
                "无需切换",
                "当前已经是目标服务器，不会执行重复覆盖或删除。",
                MessageTone.Information,
                accentBrush: _context.ProfileBrush(targetProfile));
            return;
        }

        var plan = _planner.CreatePlan(_context.GetGamePath().Trim(), sourceProfile, targetProfile);
        var errors = plan.Issues.Where(x => x.Severity == IssueSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            _dialogs.Show(
                "切换前检查未通过",
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
                plan.TargetSnapshot is null
                    ? "无可用快照，仅使用本地差异包"
                    : $"可用，将恢复 {plan.TargetSnapshot.Files.Count} 个 version/revision 文件",
                HotUpdatePreview(plan.HotUpdateTransition, sourceProfile, targetProfile),
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
                result.Success ? "切换完成" : "切换失败",
                result.Success
                    ? $"服务器资源已切换完成。\n\n替换 {result.SuccessfulReplace}/{result.PlannedReplace} 个文件\n删除 {result.SuccessfulDelete}/{result.PlannedDelete} 个文件\n缓存恢复 {result.SuccessfulCacheRestore}/{result.PlannedCacheRestore}\n\n回滚备份：{result.BackupPath}" +
                      BilibiliLaunchHint(targetProfile, plan.GamePath)
                    : $"切换未能完成。\n\n{result.Error}\n\n自动回滚：{(result.RolledBack ? "成功" : "未完成或无需")}",
                result.Success ? MessageTone.Success : MessageTone.Error);
        }
        finally
        {
            _context.SetBusyStatus("操作结束，正在重新检查…");
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "操作结束");
        }
    }

    private static string HotUpdatePreview(
        HotUpdateTransitionPlan? plan,
        string sourceProfile,
        string targetProfile)
    {
        if (plan is null && string.Equals(
                ProfileIds.ToResourceProfile(sourceProfile),
                ProfileIds.ToResourceProfile(targetProfile),
                StringComparison.OrdinalIgnoreCase))
        {
            return "国服与B服共用同一套热更新缓存，本次无需交换 Blocks";
        }

        return plan?.Mode switch
        {
            HotUpdateTransitionMode.Swap => "双服缓存均可用，本次自动快速交换",
            HotUpdateTransitionMode.InitializeTarget => "目标服未初始化，本次保存来源服并进入一次性下载模式",
            _ => "未启用"
        };
    }

    private static string BilibiliLaunchHint(string targetProfile, string gamePath)
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
        return $"\n\nB服请通过专用登录窗启动：\n{launcher}";
    }
}
