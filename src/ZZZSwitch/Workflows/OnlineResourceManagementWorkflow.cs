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
                _dialogs.Show(T("无法读取客户端差异包", "Unable to read client packages"), ex.Message, MessageTone.Error);
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
            _dialogs.Show(T("无法预览客户端差异包", "Unable to preview client package"), ex.Message, MessageTone.Error);
        }
    }

    private async Task RefreshManifestsAsync(string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            _dialogs.Show(
                T("无法更新 Manifest", "Unable to update manifest"),
                T("请先检测有效的游戏目录与版本。", "Detect a valid game directory and version first."),
                MessageTone.Warning);
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
                T("Manifest 已更新", "Manifest updated"),
                T(
                    $"游戏版本：{result.GameVersion}\n\n国际服：{result.Global.FileCount:N0} 个文件，{DisplayFormatting.FormatBytes(result.Global.ContentBytes)}\nManifest：{result.Global.ManifestId}\n\n国服：{result.Cn.FileCount:N0} 个文件，{DisplayFormatting.FormatBytes(result.Cn.ContentBytes)}\nManifest：{result.Cn.ManifestId}\n\n跨服差异：修改 {result.ModifiedFiles:N0} · 新增 {result.AddedFiles:N0} · 缺少 {result.RemovedFiles:N0}",
                    $"Game version: {result.GameVersion}\n\nGlobal: {result.Global.FileCount:N0} files, {DisplayFormatting.FormatBytes(result.Global.ContentBytes)}\nManifest: {result.Global.ManifestId}\n\nCN Official: {result.Cn.FileCount:N0} files, {DisplayFormatting.FormatBytes(result.Cn.ContentBytes)}\nManifest: {result.Cn.ManifestId}\n\nCross-region differences: {result.ModifiedFiles:N0} modified · {result.AddedFiles:N0} added · {result.RemovedFiles:N0} missing"),
                MessageTone.Success);
        }
        catch (Exception ex)
        {
            _dialogs.Show(T("Manifest 更新失败", "Manifest update failed"), ex.Message, MessageTone.Error);
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
            _dialogs.Show(
                T("无法浏览 Manifest", "Unable to browse manifest"),
                T("请先检测有效的游戏目录与版本。", "Detect a valid game directory and version first."),
                MessageTone.Warning);
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
            _dialogs.Show(T("无法浏览 Manifest", "Unable to browse manifest"), ex.Message, MessageTone.Error);
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
                T("客户端差异包校验通过", "Client package verified"),
                T(
                    $"{package.GameVersion} {ProfileName(package.TargetProfile)}差异包的 {package.FileCount:N0} 个文件均通过完整性校验。",
                    $"All {package.FileCount:N0} files in the {package.GameVersion} {ProfileName(package.TargetProfile)} package passed integrity verification."),
                MessageTone.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dialogs.Show(T("客户端差异包校验失败", "Client package verification failed"), ex.Message, MessageTone.Error);
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
                ?? throw new InvalidDataException(T(
                    "无法确定已更新差异包的工作区。",
                    "Unable to determine the workspace of the updated package."));
            var removed = await Task.Run(() => _catalog.DeleteSupersededPackages(
                package.SourceProfile,
                package.TargetProfile,
                package.GameVersion,
                workspace));
            _dialogs.Show(
                T("客户端差异包已更新", "Client package updated"),
                T(
                    $"{package.GameVersion} {ProfileName(package.TargetProfile)}差异包已保存。\n新下载 {result.DownloadedFiles:N0} 个文件，复用 {result.ReusedFiles:N0} 个文件。" +
                    (removed > 0 ? $"\n已清理 {removed:N0} 个同版本同方向的旧工作区。" : string.Empty),
                    $"The {package.GameVersion} {ProfileName(package.TargetProfile)} package was saved.\nDownloaded {result.DownloadedFiles:N0} new files and reused {result.ReusedFiles:N0} files." +
                    (removed > 0 ? $"\nRemoved {removed:N0} superseded workspaces for the same version and direction." : string.Empty)),
                MessageTone.Success);
        }
        catch (Exception ex)
        {
            _dialogs.Show(T("客户端差异包更新失败", "Client package update failed"), ex.Message, MessageTone.Error);
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
                T("删除客户端差异包", "Delete client package"),
                T(
                    $"将永久删除以下自动差异包：\n\n版本：{package.GameVersion}\n目标客户端：{ProfileName(package.TargetProfile)}\n大小：{DisplayFormatting.FormatBytes(package.TotalBytes)}\n\n删除后，下次切换到该目标客户端时需要重新下载。此操作不能撤销。",
                    $"The following automatic package will be permanently deleted:\n\nVersion: {package.GameVersion}\nTarget client: {ProfileName(package.TargetProfile)}\nSize: {DisplayFormatting.FormatBytes(package.TotalBytes)}\n\nThe package must be downloaded again next time you switch to this client. This cannot be undone."),
                MessageTone.Warning,
                showCancel: true,
                primaryText: T("确认删除", "Delete package")) != true)
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
                T("客户端差异包已删除", "Client package deleted"),
                T(
                    $"已删除 {package.GameVersion} {ProfileName(package.TargetProfile)}自动差异包。",
                    $"Deleted the {package.GameVersion} {ProfileName(package.TargetProfile)} automatic package."),
                MessageTone.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _dialogs.Show(T("删除客户端差异包失败", "Failed to delete client package"), ex.Message, MessageTone.Error);
        }
        finally
        {
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "客户端差异包管理结束");
        }
    }

    private string T(string chinese, string english) => _context.Localize(chinese, english);

    private string ProfileName(string profileId) => profileId switch
    {
        ProfileIds.Global => T("国际服", "Global"),
        ProfileIds.CnOfficial => T("国服", "CN Official"),
        ProfileIds.Bilibili => T("B服", "Bilibili"),
        _ => profileId
    };
}
