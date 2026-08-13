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
                "无法导入差异包",
                "请先选择有效的游戏目录，并确认游戏版本能够正确识别。",
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
                "暂时无法导入差异包",
                $"请先完全退出游戏和启动器：{string.Join("、", processes)}",
                MessageTone.Warning);
            return;
        }

        var target = GameStorageLayout.GetPackageRoot(gamePath, gameVersion);
        var replacing = Directory.Exists(target);
        if (_dialogs.Show(
                "导入三服差异包",
                $"文件：\n{archive}\n\n游戏版本：{gameVersion}\n导入位置：\n{target}" +
                (replacing ? "\n\n现有同版本差异包会在新内容完整解压并校验通过后替换。" : string.Empty),
                MessageTone.Information,
                "ZIP 只读取；国际服、国服和 B服内容会一起校验",
                showCancel: true,
                primaryText: "开始导入") != true)
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
            var message = $"已导入 {result.FileCount:N0} 个文件，共 {DisplayFormatting.FormatBytes(result.TotalBytes)}。\n\n位置：\n{result.PackageRoot}";
            if (result.ReplacedExisting)
            {
                message += "\n\n原有同版本差异包已安全替换。";
            }

            if (result.RetainedPreviousPath is not null)
            {
                message += $"\n\n旧目录未能自动清理，仍保留在：\n{result.RetainedPreviousPath}";
            }

            _dialogs.Show(
                "差异包导入完成",
                message,
                result.RetainedPreviousPath is null ? MessageTone.Success : MessageTone.Warning);
        }
        catch (Exception ex)
        {
            _dialogs.Show(
                "差异包导入失败",
                $"现有差异包未被不完整内容覆盖。\n\n{ex.Message}",
                MessageTone.Error);
        }
        finally
        {
            await _context.RefreshInspectionWhileBusy();
            _context.SetBusy(false, "差异包导入结束");
        }
    }
}
