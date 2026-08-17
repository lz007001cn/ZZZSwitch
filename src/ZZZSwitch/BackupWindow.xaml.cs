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
    private readonly LocalizationManager _localization;

    public BackupWindow(
        BackupService backups,
        RestoreService restore,
        LegacyRestoreSafetyPolicy safetyPolicy,
        OperationCoordinator operations,
        string currentGamePath)
    {
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        _localization = app.Localization;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);
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
            Source = _localization.ProfileName(x.Record.SourceProfile),
            Target = _localization.ProfileName(x.Record.TargetProfile),
            Result = ResultName(x.Record.OperationResult),
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
                T("没有可恢复的上次状态", "No last state to restore"),
                T("未找到与状态记录中最后一次切换精确对应的可恢复备份。", "No restorable backup exactly matching the last switch was found."),
                MessageTone.Information);
            return;
        }

        var safety = _safetyPolicy.Evaluate(_currentGamePath, candidate);
        if (!safety.CanRestore)
        {
            ThemedMessageWindow.Show(
                this,
                T("无法恢复", "Unable to restore"),
                safety.Reason ?? T("当前备份不能安全恢复。", "This backup cannot be restored safely."),
                MessageTone.Warning);
            return;
        }

        if (ThemedMessageWindow.Show(
                this,
                T("确认恢复上次状态", "Confirm restoring the last state"),
                T(
                    "将使用状态记录精确对应的最后一次切换备份，恢复切换前状态。\n\n请确认游戏与启动器均已退出。",
                    "The backup exactly matching the last switch will restore the pre-switch state.\n\nMake sure the game and launcher are closed."),
                MessageTone.Warning,
                showCancel: true,
                primaryText: T("恢复上次状态", "Restore last state")) != true)
        {
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            ThemedMessageWindow.Show(
                this,
                T("操作正在进行", "Operation in progress"),
                _operations.LastFailure ?? T("请等待当前操作完成后再试。", "Wait for the current operation to finish and try again."),
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
            ThemedMessageWindow.Show(this, T("恢复失败", "Restore failed"), ex.Message, MessageTone.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }

        ThemedMessageWindow.Show(
            this,
            result.Success ? T("恢复成功", "Restore complete") : T("恢复失败", "Restore failed"),
            result.Success
                ? T("已恢复最后一次切换前的状态。", "The state before the last switch has been restored.")
                : result.Error ?? T("恢复操作未完成。", "The restore operation did not complete."),
            result.Success ? MessageTone.Success : MessageTone.Error);
        LoadRows();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRow row)
        {
            ThemedMessageWindow.Show(
                this,
                T("未选择备份", "No backup selected"),
                T("请先在列表中选择一项备份。", "Select a backup from the list first."),
                MessageTone.Information);
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            ThemedMessageWindow.Show(
                this,
                T("操作正在进行", "Operation in progress"),
                _operations.LastFailure ?? T("请等待当前操作完成后再试。", "Wait for the current operation to finish and try again."),
                MessageTone.Information);
            return;
        }

        using var operation = lease!;
        var safety = _safetyPolicy.Evaluate(_currentGamePath, row.Record);
        if (!safety.CanRestore)
        {
            ThemedMessageWindow.Show(
                this,
                T("无法恢复", "Unable to restore"),
                safety.Reason ?? T("当前备份不能安全恢复。", "This backup cannot be restored safely."),
                MessageTone.Warning);
            return;
        }

        if (ThemedMessageWindow.Show(
                this,
                T("确认恢复", "Confirm restore"),
                T(
                    $"将恢复备份：\n{row.Path}\n\n这会修改对应游戏目录中的文件。请确认游戏与启动器均已退出。",
                    $"The following backup will be restored:\n{row.Path}\n\nThis modifies files in the corresponding game directory. Make sure the game and launcher are closed."),
                MessageTone.Warning,
                showCancel: true,
                primaryText: T("恢复备份", "Restore backup")) != true)
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
                T("恢复失败", "Restore failed"),
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
            result.Success ? T("恢复成功", "Restore complete") : T("恢复失败", "Restore failed"),
            result.Success
                ? T("备份中的文件已恢复。", "The files in the backup have been restored.")
                : result.Error ?? T("恢复操作未完成。", "The restore operation did not complete."),
            result.Success ? MessageTone.Success : MessageTone.Error);
        LoadRows();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupRow row)
        {
            ThemedMessageWindow.Show(
                this,
                T("未选择备份", "No backup selected"),
                T("请先在列表中选择一项备份。", "Select a backup from the list first."),
                MessageTone.Information);
            return;
        }

        if (!_operations.TryBegin(out var lease))
        {
            ThemedMessageWindow.Show(
                this,
                T("操作正在进行", "Operation in progress"),
                _operations.LastFailure ?? T("请等待当前操作完成后再试。", "Wait for the current operation to finish and try again."),
                MessageTone.Information);
            return;
        }

        using var operation = lease!;

        if (ThemedMessageWindow.Show(
                this,
                T("确认删除旧备份", "Confirm backup deletion"),
                T(
                    $"永久删除以下备份？此操作不可恢复，也无法再通过 ZZZSwitch 恢复。\n\n{row.Path}",
                    $"Permanently delete this backup? This cannot be undone or restored through ZZZSwitch.\n\n{row.Path}"),
                MessageTone.Error,
                showCancel: true,
                primaryText: T("删除备份", "Delete backup")) != true)
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
                T("删除失败", "Delete failed"),
                ex.Message,
                MessageTone.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private string T(string chinese, string english) => _localization.Choose(chinese, english);

    private string ResultName(string result) => result switch
    {
        "success" => T("成功", "Success"),
        "failed" => T("失败", "Failed"),
        "interrupted" => T("已中断", "Interrupted"),
        "pending" => T("待处理", "Pending"),
        _ => result
    };

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
