using System.IO;
using System.Windows;
using System.Windows.Input;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public partial class BackupWindow : Window
{
    private readonly BackupService _backups;
    private readonly RestoreService _restore;
    private readonly LegacyRestoreSafetyPolicy _safetyPolicy;
    private readonly OperationCoordinator _operations;
    private readonly string _currentGamePath;

    public BackupWindow(
        BackupService backups,
        RestoreService restore,
        LegacyRestoreSafetyPolicy safetyPolicy,
        OperationCoordinator operations,
        string currentGamePath)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkWindowHelper.Apply(this);
        _backups = backups;
        _restore = restore;
        _safetyPolicy = safetyPolicy;
        _operations = operations;
        _currentGamePath = currentGamePath;
        LoadRows();
    }

    private void LoadRows()
    {
        BackupsGrid.ItemsSource = _backups.ListBackups().Select(x => new BackupRow
        {
            Time = x.Record.OperationTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            Source = x.Record.SourceProfile,
            Target = x.Record.TargetProfile,
            Result = x.Record.OperationResult,
            Restored = x.Record.RestoredAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "—",
            Path = x.Path,
            Record = x.Record
        }).ToArray();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadRows();

    private async void RestoreLatest_Click(object sender, RoutedEventArgs e)
    {
        var candidate = _restore.FindLatestRecord(_currentGamePath);
        if (candidate is null)
        {
            ThemedMessageWindow.Show(
                this,
                "没有可恢复的上次状态",
                "未找到与状态记录中最后一次切换精确对应的可恢复备份。",
                MessageTone.Information);
            return;
        }

        var safety = _safetyPolicy.Evaluate(_currentGamePath, candidate);
        if (!safety.CanRestore)
        {
            ThemedMessageWindow.Show(
                this,
                "无法恢复",
                safety.Reason ?? "当前备份不能安全恢复。",
                MessageTone.Warning);
            return;
        }

        if (ThemedMessageWindow.Show(
                this,
                "确认恢复上次状态",
                "将使用状态记录精确对应的最后一次切换备份，恢复切换前状态。",
                MessageTone.Warning,
                "请确认游戏与启动器均已退出",
                showCancel: true,
                primaryText: "恢复上次状态") != true)
        {
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            ThemedMessageWindow.Show(
                this,
                "操作正在进行",
                _operations.LastFailure ?? "请等待当前操作完成后再试。",
                MessageTone.Information);
            return;
        }

        using var operation = lease!;
        OperationResult result;
        IsEnabled = false;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            result = await Task.Run(() => _restore.RestoreLatest(_currentGamePath));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ThemedMessageWindow.Show(this, "恢复失败", ex.Message, MessageTone.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }

        ThemedMessageWindow.Show(
            this,
            result.Success ? "恢复成功" : "恢复失败",
            result.Success ? "已恢复最后一次切换前的状态。" : result.Error ?? "恢复操作未完成。",
            result.Success ? MessageTone.Success : MessageTone.Error);
        LoadRows();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRow row)
        {
            ThemedMessageWindow.Show(this, "未选择备份", "请先在列表中选择一项备份。", MessageTone.Information);
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            ThemedMessageWindow.Show(
                this,
                "操作正在进行",
                _operations.LastFailure ?? "请等待当前操作完成后再试。",
                MessageTone.Information);
            return;
        }

        using var operation = lease!;
        var safety = _safetyPolicy.Evaluate(_currentGamePath, row.Record);
        if (!safety.CanRestore)
        {
            ThemedMessageWindow.Show(
                this,
                "无法恢复",
                safety.Reason ?? "当前备份不能安全恢复。",
                MessageTone.Warning);
            return;
        }

        if (ThemedMessageWindow.Show(
                this,
                "确认恢复",
                $"将恢复备份：\n{row.Path}\n\n这会修改对应游戏目录中的文件。",
                MessageTone.Warning,
                "请确认游戏与启动器均已退出",
                showCancel: true,
                primaryText: "恢复备份") != true)
        {
            return;
        }

        OperationResult result;
        IsEnabled = false;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            result = await Task.Run(() => _restore.Restore(row.Path, row.Record, _currentGamePath));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ThemedMessageWindow.Show(
                this,
                "恢复失败",
                ex.Message,
                MessageTone.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }

        ThemedMessageWindow.Show(
            this,
            result.Success ? "恢复成功" : "恢复失败",
            result.Success ? "备份中的文件已恢复。" : result.Error ?? "恢复操作未完成。",
            result.Success ? MessageTone.Success : MessageTone.Error);
        LoadRows();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRow row)
        {
            ThemedMessageWindow.Show(this, "未选择备份", "请先在列表中选择一项备份。", MessageTone.Information);
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            ThemedMessageWindow.Show(
                this,
                "操作正在进行",
                _operations.LastFailure ?? "请等待当前操作完成后再试。",
                MessageTone.Information);
            return;
        }

        using var operation = lease!;

        if (ThemedMessageWindow.Show(
                this,
                "确认删除旧备份",
                $"永久删除以下备份？此操作不可恢复。\n\n{row.Path}",
                MessageTone.Error,
                "删除后无法通过 ZZZSwitch 恢复",
                showCancel: true,
                primaryText: "删除备份") != true)
        {
            return;
        }

        try
        {
            _backups.DeleteBackup(row.Path);
            LoadRows();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ThemedMessageWindow.Show(
                this,
                "删除失败",
                ex.Message,
                MessageTone.Error,
                "备份仍保留在原位置");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class BackupRow
    {
        public required string Time { get; init; }
        public required string Source { get; init; }
        public required string Target { get; init; }
        public required string Result { get; init; }
        public required string Restored { get; init; }
        public required string Path { get; init; }
        public required BackupRecord Record { get; init; }
    }
}
