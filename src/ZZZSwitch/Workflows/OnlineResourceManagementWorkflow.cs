using System.IO;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;

namespace ZZZSwitch.Workflows;

public sealed class OnlineResourceManagementWorkflow
{
    private readonly OnlineDifferencePackageCatalog _catalog;
    private readonly IOnlineDifferenceService _onlineDifferences;
    private readonly OperationCoordinator _operations;
    private readonly IMainWindowDialogs _dialogs;
    private readonly MainWindowWorkflowContext _context;

    public OnlineResourceManagementWorkflow(
        OnlineDifferencePackageCatalog catalog,
        IOnlineDifferenceService onlineDifferences,
        OperationCoordinator operations,
        IMainWindowDialogs dialogs,
        MainWindowWorkflowContext context)
    {
        _catalog = catalog;
        _onlineDifferences = onlineDifferences;
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
        var currentVersion = _context.GetInspectionReport()?.Game.GameVersion;
        while (true)
        {
            OnlineDifferenceInventory inventory;
            try
            {
                inventory = _catalog.GetInventory();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _dialogs.Show("无法读取客户端差异包", ex.Message, MessageTone.Error);
                return;
            }

            var selection = await _dialogs.SelectOnlineResourceManagementAsync(inventory, currentVersion);
            switch (selection.Action)
            {
                case OnlineResourceManagementAction.None:
                    return;
                case OnlineResourceManagementAction.Refresh:
                    continue;
                case OnlineResourceManagementAction.RefreshManifest:
                    await RefreshManifestsAsync(currentVersion);
                    continue;
                case OnlineResourceManagementAction.BrowseManifest:
                    await BrowseManifestAsync(currentVersion);
                    continue;
                case OnlineResourceManagementAction.Preview when selection.Package is not null:
                    await ShowPreviewAsync(selection.Package);
                    continue;
                case OnlineResourceManagementAction.Verify when selection.Package is not null:
                    await VerifyAsync(selection.Package);
                    continue;
                case OnlineResourceManagementAction.UpdatePackage when selection.Package is not null:
                    await UpdatePackageAsync(selection.Package);
                    continue;
                case OnlineResourceManagementAction.OpenDirectory when selection.Package is not null:
                    _context.OpenDirectory(selection.Package.WorkspacePath, true);
                    continue;
                case OnlineResourceManagementAction.Delete when selection.Package is not null:
                    await DeleteAsync(selection.Package);
                    continue;
                default:
                    return;
            }
        }
    }

    private async Task ShowPreviewAsync(OnlineDifferencePackageInfo package)
    {
        try
        {
            await _dialogs.ShowOnlineDifferencePreviewAsync(_catalog.GetPreview(package));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dialogs.Show("无法预览客户端差异包", ex.Message, MessageTone.Error);
        }
    }

    private async Task RefreshManifestsAsync(string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            _dialogs.Show("无法更新 Manifest", "请先检测有效的游戏目录与版本。", MessageTone.Warning);
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        _context.SetBusy(true, $"正在下载 {currentVersion} 国际服/国服 Manifest…");
        try
        {
            var result = await _onlineDifferences.RefreshManifestsAsync(currentVersion);
            _dialogs.Show(
                "Manifest 已更新",
                $"游戏版本：{result.GameVersion}\n\n" +
                $"国际服：{result.Global.FileCount:N0} 个文件，{DisplayFormatting.FormatBytes(result.Global.ContentBytes)}\n" +
                $"Manifest：{result.Global.ManifestId}\n\n" +
                $"国服：{result.Cn.FileCount:N0} 个文件，{DisplayFormatting.FormatBytes(result.Cn.ContentBytes)}\n" +
                $"Manifest：{result.Cn.ManifestId}\n\n" +
                $"跨服差异：修改 {result.ModifiedFiles:N0} · 新增 {result.AddedFiles:N0} · 缺少 {result.RemovedFiles:N0}",
                MessageTone.Success);
        }
        catch (Exception ex)
        {
            _dialogs.Show("Manifest 更新失败", ex.Message, MessageTone.Error);
        }
        finally
        {
            _context.SetBusy(false, "Manifest 更新结束");
        }
    }

    private async Task BrowseManifestAsync(string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            _dialogs.Show("无法浏览 Manifest", "请先检测有效的游戏目录与版本。", MessageTone.Warning);
            return;
        }

        OnlineManifestBrowserData? data = null;
        _context.SetBusy(true, $"正在读取 {currentVersion} Manifest 缓存…");
        try
        {
            data = await _onlineDifferences.GetManifestBrowserAsync(currentVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dialogs.Show("无法浏览 Manifest", ex.Message, MessageTone.Error);
        }
        finally
        {
            _context.SetBusy(false, "Manifest 浏览准备结束");
        }

        if (data is not null)
        {
        await _dialogs.ShowManifestBrowserAsync(data);
        }
    }

    private async Task VerifyAsync(OnlineDifferencePackageInfo package)
    {
        if (!_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        _context.SetBusy(true, "正在校验客户端差异包…");
        try
        {
            await Task.Run(() => _catalog.VerifyPackage(package));
            _dialogs.Show(
                "客户端差异包校验通过",
                $"{package.GameVersion} {DisplayFormatting.ShortProfileName(package.TargetProfile)}差异包的 " +
                $"{package.FileCount:N0} 个文件均通过完整性校验。",
                MessageTone.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dialogs.Show("客户端差异包校验失败", ex.Message, MessageTone.Error);
        }
        finally
        {
            _context.SetBusy(false, "差异包校验结束");
        }
    }

    private async Task UpdatePackageAsync(OnlineDifferencePackageInfo package)
    {
        if (!_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        _context.SetBusy(true, "正在重新分析跨服客户端差异…");
        try
        {
            var plan = await _onlineDifferences.AnalyzeAsync(
                package.SourceProfile,
                package.TargetProfile,
                package.GameVersion);
            var result = _dialogs.DownloadOnlineDifference(plan, _onlineDifferences, continueToSwitch: false);
            if (result is null)
            {
                return;
            }

            var workspace = Directory.GetParent(result.PackageDirectory)?.FullName
                ?? throw new InvalidDataException("无法确定已更新差异包的工作区。");
            var removed = await Task.Run(() => _catalog.DeleteSupersededPackages(
                package.SourceProfile,
                package.TargetProfile,
                package.GameVersion,
                workspace));
            _dialogs.Show(
                "客户端差异包已更新",
                $"{package.GameVersion} {DisplayFormatting.ShortProfileName(package.TargetProfile)}差异包已保存。\n" +
                $"新下载 {result.DownloadedFiles:N0} 个文件，复用 {result.ReusedFiles:N0} 个文件。" +
                (removed > 0 ? $"\n已清理 {removed:N0} 个同版本同方向的旧工作区。" : string.Empty),
                MessageTone.Success);
        }
        catch (Exception ex)
        {
            _dialogs.Show("客户端差异包更新失败", ex.Message, MessageTone.Error);
        }
        finally
        {
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "客户端差异包更新结束");
        }
    }

    private async Task DeleteAsync(OnlineDifferencePackageInfo package)
    {
        if (_dialogs.Show(
                "删除客户端差异包",
                $"将永久删除以下自动差异包：\n\n" +
                $"版本：{package.GameVersion}\n" +
                $"目标客户端：{DisplayFormatting.ShortProfileName(package.TargetProfile)}\n" +
                $"大小：{DisplayFormatting.FormatBytes(package.TotalBytes)}\n\n" +
                "删除后，下次切换到该目标客户端时需要重新下载。此操作不能撤销。",
                MessageTone.Warning,
                showCancel: true,
                primaryText: "确认删除") != true)
        {
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        _context.SetBusy(true, "正在删除选中的客户端差异包…");
        try
        {
            await Task.Run(() => _catalog.DeletePackage(package.WorkspacePath));
            _dialogs.Show(
                "客户端差异包已删除",
                $"已删除 {package.GameVersion} {DisplayFormatting.ShortProfileName(package.TargetProfile)}自动差异包。",
                MessageTone.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dialogs.Show("删除客户端差异包失败", ex.Message, MessageTone.Error);
        }
        finally
        {
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "客户端差异包管理结束");
        }
    }
}
