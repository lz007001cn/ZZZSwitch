using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Presentation;

public sealed record BackupRestorePresentation(string Title, string Message, MessageTone Tone)
{
    public static BackupRestorePresentation From(OperationResult result, Func<string, string, string> t, string? refreshWarning = null)
    {
        var warning = result.Success && (!string.IsNullOrWhiteSpace(result.Error) || refreshWarning is not null);
        var title = result.Success
            ? warning ? t("恢复已完成，仍需处理", "Restored; attention required") : t("恢复成功", "Restore complete")
            : t("恢复失败", "Restore failed");
        var message = result.Success ? t("备份中的文件已恢复。", "The files in the backup have been restored.")
            : t("恢复操作未完成。", "The restore operation did not complete.");
        if (!string.IsNullOrWhiteSpace(result.Error)) message += "\n\n" + result.Error;
        if (refreshWarning is not null) message += "\n\n" + refreshWarning;
        if (!result.Success)
            message += "\n" + (result.RolledBack ? t("已撤销本次恢复，回到恢复前状态。", "This restore was undone; the previous state is intact.")
                : result.GameFilesUnchanged ? t("本次未修改游戏文件。", "No game files were changed.")
                : t("请在检查中重试恢复。", "Use Check → Retry recovery."));
        return new(title, message, result.Success ? warning ? MessageTone.Warning : MessageTone.Success : MessageTone.Error);
    }
}
