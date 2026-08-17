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
                "无法管理缓存",
                "请先选择有效的游戏目录，并确保游戏版本能够正确识别。",
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
            _dialogs.Show("无法读取缓存", ex.Message, MessageTone.Error);
            return;
        }

        switch (await _dialogs.SelectCacheManagementActionAsync(usage))
        {
            case CacheManagementAction.DeleteObsolete:
                await DeleteObsoleteAsync(gamePath, gameVersion, usage);
                break;
            case CacheManagementAction.ChangeLocation:
                var targetRoot = _dialogs.SelectFolder(
                    "选择 ZZZSwitch 热更新缓存目录",
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
            _dialogs.Show("暂时无法清理缓存", blocker, MessageTone.Warning);
            return;
        }

        if (_dialogs.Show(
                "清理旧版本缓存",
                $"将永久删除 {usage.ObsoleteVersionCount} 个旧游戏版本的缓存，共 {DisplayFormatting.FormatBytes(usage.ObsoleteBytes)}。\n\n" +
                $"当前版本 {gameVersion} 的缓存不会被删除。此操作不能撤销。",
                MessageTone.Warning,
                showCancel: true,
                primaryText: "确认清理") != true)
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
                "旧版本缓存已清理",
                $"已删除 {result.RemovedVersionCount} 个旧版本、{result.RemovedFileCount} 个文件。\n\n" +
                $"释放空间约 {DisplayFormatting.FormatBytes(result.FreedBytes)}。",
                MessageTone.Success);
        }
        catch (Exception ex)
        {
            _dialogs.Show("缓存清理失败", ex.Message, MessageTone.Error);
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
            _dialogs.Show("暂时无法移动缓存", blocker, MessageTone.Warning);
            return;
        }

        if (_dialogs.Show(
                "更改缓存位置",
                $"当前位置：\n{usage.CacheRootPath}\n\n目标位置：\n{targetRoot}\n\n" +
                $"将迁移 {usage.FileCount} 个文件，共 {DisplayFormatting.FormatBytes(usage.TotalBytes)}。复制并校验完成后才会启用新位置。" +
                (usage.ObsoleteBytes > 0
                    ? $"\n其中包含 {DisplayFormatting.FormatBytes(usage.ObsoleteBytes)} 旧版本缓存，可先返回清理后再迁移。"
                    : string.Empty),
                MessageTone.Information,
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
        _context.SetBusy(true, "正在迁移并校验缓存，请勿退出…");
        try
        {
            var result = await Task.Run(() => _cacheLocations.ChangeLocation(gamePath, targetRoot));
            var message = result.ContentMoved
                ? $"已迁移 {result.MigratedFileCount} 个文件，共 {DisplayFormatting.FormatBytes(result.MigratedBytes)}。\n\n新位置：\n{result.TargetCacheRoot}"
                : $"已将后续缓存位置设置为：\n{result.TargetCacheRoot}";
            if (!result.SourceRemoved)
            {
                message += $"\n\n新位置已经启用，但旧目录未能自动删除，请确认程序可正常读取缓存后手动删除：\n{result.SourceCacheRoot}";
            }

            _dialogs.Show(
                result.SourceRemoved ? "缓存位置已更新" : "缓存已迁移，旧目录仍保留",
                message,
                result.SourceRemoved ? MessageTone.Success : MessageTone.Warning);
        }
        catch (Exception ex)
        {
            _dialogs.Show(
                "缓存迁移失败",
                $"仍继续使用原缓存位置，未切换到目标目录。\n\n{ex.Message}",
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
            return "检测到尚未完成的切换事务。请重新启动程序完成自动恢复后再管理缓存。";
        }

        var processes = _processMonitor.FindRelatedProcesses();
        return processes.Count == 0
            ? null
            : $"请先完全退出游戏和启动器：{string.Join("、", processes)}";
    }
}
