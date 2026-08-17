using System.IO;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;

namespace ZZZSwitch.Workflows;

public sealed class PackageImportWorkflow
{
    private readonly PackageImportService _packages;
    private readonly IProcessMonitor _processMonitor;
    private readonly OperationCoordinator _operations;
    private readonly IMainWindowDialogs _dialogs;
    private readonly MainWindowWorkflowContext _context;

    public PackageImportWorkflow(
        PackageImportService packages,
        IProcessMonitor processMonitor,
        OperationCoordinator operations,
        IMainWindowDialogs dialogs,
        MainWindowWorkflowContext context)
    {
        _packages = packages;
        _processMonitor = processMonitor;
        _operations = operations;
        _dialogs = dialogs;
        _context = context;
    }

    public async Task ImportAsync()
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
                T("无法导入差异包", "Unable to import package"),
                T("请先选择有效的游戏目录，并确认游戏版本能够正确识别。", "Select a valid game directory and make sure its version can be detected."),
                MessageTone.Warning);
            return;
        }

        var archive = _dialogs.SelectPackageArchive();
        if (archive is null)
        {
            return;
        }

        var processes = _processMonitor.FindRelatedProcesses();
        if (processes.Count > 0)
        {
            _dialogs.Show(
                T("暂时无法导入差异包", "Package cannot be imported yet"),
                T(
                    $"请先完全退出游戏和启动器：{string.Join("、", processes)}",
                    $"Close the game and launcher first: {string.Join(", ", processes)}"),
                MessageTone.Warning);
            return;
        }

        var target = GameStorageLayout.GetPackageRoot(gamePath, gameVersion);
        var replacing = Directory.Exists(target);
        if (_dialogs.Show(
                T("导入三服差异包", "Import three-server package"),
                T(
                    $"文件：\n{archive}\n\n游戏版本：{gameVersion}\n导入位置：\n{target}" +
                    (replacing ? "\n\n现有同版本差异包会在新内容完整解压并校验通过后替换。" : string.Empty) +
                    "\n\n国际服、国服和 B服内容会一起校验。",
                    $"File:\n{archive}\n\nGame version: {gameVersion}\nImport location:\n{target}" +
                    (replacing ? "\n\nThe existing package for this version will be replaced only after the new content is fully extracted and verified." : string.Empty) +
                    "\n\nGlobal, CN Official, and Bilibili content will be verified together."),
                MessageTone.Information,
                showCancel: true,
                primaryText: T("开始导入", "Start import")) != true)
        {
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            _context.ShowOperationInProgress();
            return;
        }

        using var operation = lease!;
        _context.SetBusy(true, "正在解压并校验三服差异包，请勿退出…");
        try
        {
            var result = await Task.Run(() => _packages.Import(archive, gamePath, gameVersion));
            var message = T(
                $"已导入 {result.FileCount:N0} 个文件，共 {DisplayFormatting.FormatBytes(result.TotalBytes)}。\n\n位置：\n{result.PackageRoot}",
                $"Imported {result.FileCount:N0} files ({DisplayFormatting.FormatBytes(result.TotalBytes)}).\n\nLocation:\n{result.PackageRoot}");
            if (result.ReplacedExisting)
            {
                message += T("\n\n原有同版本差异包已安全替换。", "\n\nThe existing package for this version was replaced safely.");
            }

            if (result.RetainedPreviousPath is not null)
            {
                message += T(
                    $"\n\n旧目录未能自动清理，仍保留在：\n{result.RetainedPreviousPath}",
                    $"\n\nThe old folder could not be removed and remains at:\n{result.RetainedPreviousPath}");
            }

            _dialogs.Show(
                T("差异包导入完成", "Package import complete"),
                message,
                result.RetainedPreviousPath is null ? MessageTone.Success : MessageTone.Warning);
        }
        catch (Exception ex)
        {
            _dialogs.Show(
                T("差异包导入失败", "Package import failed"),
                T(
                    $"现有差异包未被不完整内容覆盖。\n\n{ex.Message}",
                    $"The existing package was not overwritten by incomplete content.\n\n{ex.Message}"),
                MessageTone.Error);
        }
        finally
        {
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "差异包导入结束");
        }
    }

    private string T(string chinese, string english) => _context.Localize(chinese, english);
}
