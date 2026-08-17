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
            _dialogs.Show(T("无法打开备份历史", "Unable to open backup history"), ex.Message, MessageTone.Error);
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
            _dialogs.Show(T("无法读取备份目录", "Unable to read backup location"), ex.Message, MessageTone.Error);
            return;
        }

        switch (await _dialogs.SelectBackupLocationActionAsync(usage))
        {
            case BackupLocationAction.OpenLocation:
                _context.OpenDirectory(usage.BackupRootPath, false);
                break;
            case BackupLocationAction.ChangeLocation:
                var targetRoot = _dialogs.SelectFolder(
                    T("选择 ZZZSwitch 事务备份目录（目标目录必须为空）", "Select the ZZZSwitch transaction backup folder (the target must be empty)"),
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
                T("更改备份位置", "Change backup location"),
                T(
                    $"当前位置：\n{usage.BackupRootPath}\n\n目标位置：\n{targetRoot}\n\n将迁移 {usage.FileCount} 个文件，共 {DisplayFormatting.FormatBytes(usage.TotalBytes)}。复制并逐文件校验完成后才会启用新位置。",
                    $"Current location:\n{usage.BackupRootPath}\n\nTarget location:\n{targetRoot}\n\n{usage.FileCount} files ({DisplayFormatting.FormatBytes(usage.TotalBytes)}) will be moved. The new location is enabled only after every copied file is verified."),
                MessageTone.Information,
                showCancel: true,
                primaryText: T("开始迁移", "Start migration")) != true)
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
                ? T(
                    $"已迁移 {result.MigratedFileCount} 个文件，共 {DisplayFormatting.FormatBytes(result.MigratedBytes)}。\n\n新位置：\n{result.TargetBackupRoot}",
                    $"Moved {result.MigratedFileCount} files ({DisplayFormatting.FormatBytes(result.MigratedBytes)}).\n\nNew location:\n{result.TargetBackupRoot}")
                : T(
                    $"已将后续备份位置设置为：\n{result.TargetBackupRoot}",
                    $"Future backups will be stored at:\n{result.TargetBackupRoot}");
            if (!result.SourceRemoved)
            {
                message += T(
                    $"\n\n新位置已经启用，但旧目录未能自动删除。确认新位置可用后可手动处理：\n{result.SourceBackupRoot}",
                    $"\n\nThe new location is active, but the old folder could not be removed. After confirming the new location works, remove it manually:\n{result.SourceBackupRoot}");
            }

            _dialogs.Show(
                result.SourceRemoved
                    ? T("备份位置已更新", "Backup location updated")
                    : T("备份已迁移，旧目录仍保留", "Backups migrated; old folder retained"),
                message,
                result.SourceRemoved ? MessageTone.Success : MessageTone.Warning);
        }
        catch (Exception ex)
        {
            _dialogs.Show(
                T("备份迁移失败", "Backup migration failed"),
                T(
                    $"仍继续使用原备份位置，未切换到目标目录。\n\n{ex.Message}",
                    $"The original backup location remains active. The target was not enabled.\n\n{ex.Message}"),
                MessageTone.Error);
        }
        finally
        {
            _context.SetBusy(false, "备份迁移结束");
        }
    }

    private string T(string chinese, string english) => _context.Localize(chinese, english);
}
