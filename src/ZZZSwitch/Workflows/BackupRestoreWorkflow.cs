using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch.Workflows;

public sealed class BackupRestoreWorkflow(OperationCoordinator operations, MainWindowWorkflowContext? context = null)
{
    public async Task<(OperationResult Result, string? RefreshWarning)> RunAsync(Func<OperationResult> restore)
    {
        if (context?.IsBusy() == true || !operations.TryBegin(out var lease))
            return (new() { OperationId = "restore", Error = operations.LastFailure ?? "当前有操作正在进行。", GameFilesUnchanged = true }, null);
        OperationResult result;
        try
        {
            context?.SetBusy(true, "正在恢复备份，请勿退出…");
            result = await Task.Run(restore);
        }
        catch (Exception ex)
        {
            result = new() { OperationId = "restore", Error = ex.Message };
        }
        finally
        {
            lease!.Dispose();
        }
        string? refreshWarning = null;
        try
        {
            // Refresh only after an actual attempt, and after releasing the lease.
            context?.InvalidateDetection?.Invoke();
            if (context is not null) await context.RefreshInspectionWhileBusy();
        }
        catch (Exception ex)
        {
            refreshWarning = "恢复后状态刷新未完成：" + ex.Message;
        }
        finally
        {
            context?.SetBusy(false, "恢复操作结束");
        }
        return (result, refreshWarning);
    }
}
