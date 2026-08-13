using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;

namespace ZZZSwitch.Workflows;

public sealed class BackupManagementWorkflow
{
    private readonly BackupService _backups;
    private readonly BackupLocationService _backupLocations;
    private readonly RestoreService _restore;
    private readonly LegacyRestoreSafetyPolicy _restoreSafetyPolicy;
    private readonly AppPaths _paths;
    private readonly OperationCoordinator _operations;
    private readonly IMainWindowDialogs _dialogs;
    private readonly MainWindowWorkflowContext _context;

    public BackupManagementWorkflow(
        BackupService backups,
        BackupLocationService backupLocations,
        RestoreService restore,
        LegacyRestoreSafetyPolicy restoreSafetyPolicy,
        AppPaths paths,
        OperationCoordinator operations,
        IMainWindowDialogs dialogs,
        MainWindowWorkflowContext context)
    {
        _backups = backups;
        _backupLocations = backupLocations;
        _restore = restore;
        _restoreSafetyPolicy = restoreSafetyPolicy;
        _paths = paths;
        _operations = operations;
        _dialogs = dialogs;
        _context = context;
    }

    public void ShowHistory()
    {
        if (_context.IsBusy() || _operations.IsBusy)
        {
            _context.ShowOperationInProgress();
            return;
        }

        try
        {
            _dialogs.ShowBackupHistory(
                _backups,
                _restore,
                _restoreSafetyPolicy,
                _operations,
                _context.GetGamePath().Trim());
        }
        catch (Exception ex)
        {
            _dialogs.Show("无法打开备份历史", ex.Message, MessageTone.Error);
        }
    }

    public async Task ManageDirectoryAsync()
    {
        if (_context.IsBusy() || _operations.IsBusy)
        {
            _context.ShowOperationInProgress();
            return;
        }

        BackupLocationUsage usage;
        try
        {
            usage = _backupLocations.GetUsage();
        }
        catch (Exception ex)
        {
            _dialogs.Show("无法读取备份目录", ex.Message, MessageTone.Error);
            return;
        }

        switch (_dialogs.SelectBackupLocationAction(usage))
        {
            case BackupLocationAction.OpenLocation:
                _context.OpenDirectory(usage.BackupRootPath, false);
                break;
            case BackupLocationAction.ChangeLocation:
                var targetRoot = _dialogs.SelectFolder(
                    "选择 ZZZSwitch 事务备份目录（目标目录必须为空）",
                    usage.BackupRootPath);
                if (targetRoot is not null)
                {
                    await ChangeLocationAsync(usage, targetRoot);
                }
                break;
            case BackupLocationAction.RestoreDefault:
                await ChangeLocationAsync(usage, _paths.DefaultBackupsRoot);
                break;
        }
    }

    private async Task ChangeLocationAsync(BackupLocationUsage usage, string targetRoot)
    {
        if (_dialogs.Show(
                "更改备份位置",
                $"当前位置：\n{usage.BackupRootPath}\n\n目标位置：\n{targetRoot}\n\n" +
                $"将迁移 {usage.FileCount} 个文件，共 {DisplayFormatting.FormatBytes(usage.TotalBytes)}。",
                MessageTone.Information,
                "复制并逐文件校验完成后才会启用新位置",
                showCancel: true,
                primaryText: "开始迁移") != true)
        {
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        _context.SetBusy(true, "正在迁移并校验备份，请勿退出…");
        try
        {
            var result = await Task.Run(() => _backupLocations.ChangeLocation(
                targetRoot,
                _context.GetGamePath().Trim()));
            var message = result.ContentMoved
                ? $"已迁移 {result.MigratedFileCount} 个文件，共 {DisplayFormatting.FormatBytes(result.MigratedBytes)}。\n\n新位置：\n{result.TargetBackupRoot}"
                : $"已将后续备份位置设置为：\n{result.TargetBackupRoot}";
            if (!result.SourceRemoved)
            {
                message += $"\n\n新位置已经启用，但旧目录未能自动删除。确认新位置可用后可手动处理：\n{result.SourceBackupRoot}";
            }

            _dialogs.Show(
                result.SourceRemoved ? "备份位置已更新" : "备份已迁移，旧目录仍保留",
                message,
                result.SourceRemoved ? MessageTone.Success : MessageTone.Warning);
        }
        catch (Exception ex)
        {
            _dialogs.Show(
                "备份迁移失败",
                $"仍继续使用原备份位置，未切换到目标目录。\n\n{ex.Message}",
                MessageTone.Error);
        }
        finally
        {
            _context.SetBusy(false, "备份迁移结束");
        }
    }
}
