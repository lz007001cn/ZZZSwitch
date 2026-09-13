using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Presentation;

internal static class OperationProgressFormatting
{
    public static string Report(OperationProgress progress, string step, Func<string, string, string> localize)
    {
        return localize(
            $"当前步骤：{progress.Step}\n" +
            $"替换：{progress.SuccessfulReplace}/{progress.PlannedReplace}，失败 {progress.FailedReplace}\n" +
            $"删除：{progress.SuccessfulDelete}/{progress.PlannedDelete}，失败 {progress.FailedDelete}\n" +
            $"缓存恢复：{progress.SuccessfulCacheRestore}/{progress.PlannedCacheRestore}，失败 {progress.FailedCacheRestore}\n" +
            $"正在回滚：{(progress.IsRollingBack ? "是" : "否")}",
            $"Current step: {step}\n" +
            $"Replaced: {progress.SuccessfulReplace}/{progress.PlannedReplace}, failed {progress.FailedReplace}\n" +
            $"Deleted: {progress.SuccessfulDelete}/{progress.PlannedDelete}, failed {progress.FailedDelete}\n" +
            $"Cache restored: {progress.SuccessfulCacheRestore}/{progress.PlannedCacheRestore}, failed {progress.FailedCacheRestore}\n" +
            $"Rolling back: {(progress.IsRollingBack ? "Yes" : "No")}");
    }
}
