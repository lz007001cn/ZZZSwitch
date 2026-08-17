using System.IO;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;

namespace ZZZSwitch.Workflows;

public sealed class CacheManagementWorkflow
{
    private readonly CacheLocationService _cacheLocations;
    private readonly FileTransactionJournalStore _fileTransactions;
    private readonly AppPaths _paths;
    private readonly IProcessMonitor _processMonitor;
    private readonly OperationCoordinator _operations;
    private readonly IMainWindowDialogs _dialogs;
    private readonly MainWindowWorkflowContext _context;

    public CacheManagementWorkflow(
        CacheLocationService cacheLocations,
        FileTransactionJournalStore fileTransactions,
        AppPaths paths,
        IProcessMonitor processMonitor,
        OperationCoordinator operations,
        IMainWindowDialogs dialogs,
        MainWindowWorkflowContext context)
    {
        _cacheLocations = cacheLocations;
        _fileTransactions = fileTransactions;
        _paths = paths;
        _processMonitor = processMonitor;
        _operations = operations;
        _dialogs = dialogs;
        _context = context;
    }

    public async Task ManageAsync()
    {
        if (_context.IsBusy() || _operations.IsBusy)
        {
            _context.ShowOperationInProgress();
            return;
        }

        await _context.RefreshInspection();
        var report = _context.GetInspectionReport();
        var gamePath = report?.Game.GamePath;
        var gameVersion = report?.Game.GameVersion;
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(gameVersion))
        {
            _dialogs.Show(
                T("无法管理缓存", "Unable to manage cache"),
                T("请先选择有效的游戏目录，并确保游戏版本能够正确识别。", "Select a valid game directory and make sure its version can be detected."),
                MessageTone.Warning);
            return;
        }

        CacheUsageSummary usage;
        try
        {
            usage = await Task.Run(() => _cacheLocations.GetUsage(gamePath, gameVersion));
        }
        catch (Exception ex)
        {
            _dialogs.Show(T("无法读取缓存", "Unable to read cache"), ex.Message, MessageTone.Error);
            return;
        }

        switch (await _dialogs.SelectCacheManagementActionAsync(usage))
        {
            case CacheManagementAction.DeleteObsolete:
                await DeleteObsoleteAsync(gamePath, gameVersion, usage);
                break;
            case CacheManagementAction.ChangeLocation:
                var targetRoot = _dialogs.SelectFolder(
                    T("选择 ZZZSwitch 热更新缓存目录", "Select the ZZZSwitch hot-update cache folder"),
                    usage.CacheRootPath);
                if (targetRoot is not null)
                {
                    await ChangeLocationAsync(gamePath, usage, targetRoot);
                }
                break;
            case CacheManagementAction.RestoreDefault:
                await ChangeLocationAsync(gamePath, usage, GameStorageLayout.GetCacheRoot(gamePath));
                break;
        }
    }

    private async Task DeleteObsoleteAsync(
        string gamePath,
        string gameVersion,
        CacheUsageSummary usage)
    {
        if (usage.ObsoleteVersionCount == 0)
        {
            return;
        }

        var blocker = MaintenanceBlocker();
        if (blocker is not null)
        {
            _dialogs.Show(T("暂时无法清理缓存", "Cache cannot be cleaned yet"), blocker, MessageTone.Warning);
            return;
        }

        if (_dialogs.Show(
                T("清理旧版本缓存", "Clean old-version cache"),
                T(
                    $"将永久删除 {usage.ObsoleteVersionCount} 个旧游戏版本的缓存，共 {DisplayFormatting.FormatBytes(usage.ObsoleteBytes)}。\n\n当前版本 {gameVersion} 的缓存不会被删除。此操作不能撤销。",
                    $"Cache for {usage.ObsoleteVersionCount} old game versions ({DisplayFormatting.FormatBytes(usage.ObsoleteBytes)}) will be permanently deleted.\n\nCache for the current version {gameVersion} will be kept. This cannot be undone."),
                MessageTone.Warning,
                showCancel: true,
                primaryText: T("确认清理", "Clean cache")) != true)
        {
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        _context.SetBusy(true, "正在清理旧版本缓存…");
        try
        {
            var result = await Task.Run(() => _cacheLocations.DeleteObsoleteVersions(gamePath, gameVersion));
            _dialogs.Show(
                T("旧版本缓存已清理", "Old-version cache cleaned"),
                T(
                    $"已删除 {result.RemovedVersionCount} 个旧版本、{result.RemovedFileCount} 个文件。\n\n释放空间约 {DisplayFormatting.FormatBytes(result.FreedBytes)}。",
                    $"Deleted {result.RemovedVersionCount} old versions and {result.RemovedFileCount} files.\n\nApproximately {DisplayFormatting.FormatBytes(result.FreedBytes)} was freed."),
                MessageTone.Success);
        }
        catch (Exception ex)
        {
            _dialogs.Show(T("缓存清理失败", "Cache cleanup failed"), ex.Message, MessageTone.Error);
        }
        finally
        {
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "缓存清理结束");
        }
    }

    private async Task ChangeLocationAsync(
        string gamePath,
        CacheUsageSummary usage,
        string targetRoot)
    {
        var blocker = MaintenanceBlocker();
        if (blocker is not null)
        {
            _dialogs.Show(T("暂时无法移动缓存", "Cache cannot be moved yet"), blocker, MessageTone.Warning);
            return;
        }

        if (_dialogs.Show(
                T("更改缓存位置", "Change cache location"),
                T(
                    $"当前位置：\n{usage.CacheRootPath}\n\n目标位置：\n{targetRoot}\n\n将迁移 {usage.FileCount} 个文件，共 {DisplayFormatting.FormatBytes(usage.TotalBytes)}。复制并校验完成后才会启用新位置。" +
                    (usage.ObsoleteBytes > 0 ? $"\n其中包含 {DisplayFormatting.FormatBytes(usage.ObsoleteBytes)} 旧版本缓存，可先返回清理后再迁移。" : string.Empty),
                    $"Current location:\n{usage.CacheRootPath}\n\nTarget location:\n{targetRoot}\n\n{usage.FileCount} files ({DisplayFormatting.FormatBytes(usage.TotalBytes)}) will be moved. The new location is enabled only after verification." +
                    (usage.ObsoleteBytes > 0 ? $"\nThis includes {DisplayFormatting.FormatBytes(usage.ObsoleteBytes)} of old-version cache, which can be cleaned before migration." : string.Empty)),
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
        _context.SetBusy(true, "正在迁移并校验缓存，请勿退出…");
        try
        {
            var result = await Task.Run(() => _cacheLocations.ChangeLocation(gamePath, targetRoot));
            var message = result.ContentMoved
                ? T(
                    $"已迁移 {result.MigratedFileCount} 个文件，共 {DisplayFormatting.FormatBytes(result.MigratedBytes)}。\n\n新位置：\n{result.TargetCacheRoot}",
                    $"Moved {result.MigratedFileCount} files ({DisplayFormatting.FormatBytes(result.MigratedBytes)}).\n\nNew location:\n{result.TargetCacheRoot}")
                : T(
                    $"已将后续缓存位置设置为：\n{result.TargetCacheRoot}",
                    $"Future cache data will be stored at:\n{result.TargetCacheRoot}");
            if (!result.SourceRemoved)
            {
                message += T(
                    $"\n\n新位置已经启用，但旧目录未能自动删除，请确认程序可正常读取缓存后手动删除：\n{result.SourceCacheRoot}",
                    $"\n\nThe new location is active, but the old folder could not be removed. After confirming the cache works, remove it manually:\n{result.SourceCacheRoot}");
            }

            _dialogs.Show(
                result.SourceRemoved
                    ? T("缓存位置已更新", "Cache location updated")
                    : T("缓存已迁移，旧目录仍保留", "Cache migrated; old folder retained"),
                message,
                result.SourceRemoved ? MessageTone.Success : MessageTone.Warning);
        }
        catch (Exception ex)
        {
            _dialogs.Show(
                T("缓存迁移失败", "Cache migration failed"),
                T(
                    $"仍继续使用原缓存位置，未切换到目标目录。\n\n{ex.Message}",
                    $"The original cache location remains active. The target was not enabled.\n\n{ex.Message}"),
                MessageTone.Error);
        }
        finally
        {
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "缓存迁移结束");
        }
    }

    private string? MaintenanceBlocker()
    {
        if (_fileTransactions.Exists || File.Exists(_paths.HotUpdateJournalFile))
        {
            return T(
                "检测到尚未完成的切换事务。请重新启动程序完成自动恢复后再管理缓存。",
                "An unfinished switch transaction was detected. Restart ZZZSwitch to complete automatic recovery before managing cache data.");
        }

        var processes = _processMonitor.FindRelatedProcesses();
        return processes.Count == 0
            ? null
            : T(
                $"请先完全退出游戏和启动器：{string.Join("、", processes)}",
                $"Close the game and launcher first: {string.Join(", ", processes)}");
    }

    private string T(string chinese, string english) => _context.Localize(chinese, english);
}
