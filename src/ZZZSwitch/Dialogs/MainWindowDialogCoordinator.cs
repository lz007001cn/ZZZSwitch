using System.IO;
using System.Windows;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using MediaBrush = System.Windows.Media.Brush;

namespace ZZZSwitch.Dialogs;

public sealed record SwitchConfirmationRequest(
    string SourceProfile,
    string SourceName,
    string TargetProfile,
    string TargetName,
    string GameVersion,
    int ReplaceCount,
    int DeleteCount,
    string SnapshotSummary,
    string BlocksSummary,
    string BackupPath);

public interface IMainWindowDialogs
{
    bool? Show(
        string title,
        string message,
        MessageTone tone = MessageTone.Information,
        string? subtitle = null,
        bool showCancel = false,
        string primaryText = "知道了",
        MediaBrush? accentBrush = null);

    GameDirectoryCandidate? SelectGameDirectory(IReadOnlyList<GameDirectoryCandidate> candidates);
    string? SelectFolder(string description, string? currentPath = null, bool showNewFolderButton = true);
    CacheManagementAction SelectCacheManagementAction(CacheUsageSummary usage);
    BackupLocationAction SelectBackupLocationAction(BackupLocationUsage usage);
    bool ConfirmSwitch(SwitchConfirmationRequest request);
    void ShowBackupHistory(
        BackupService backups,
        RestoreService restore,
        LegacyRestoreSafetyPolicy safetyPolicy,
        OperationCoordinator operations,
        string gamePath);
}

public sealed class MainWindowDialogCoordinator : IMainWindowDialogs
{
    private readonly Window _owner;

    public MainWindowDialogCoordinator(Window owner) => _owner = owner;

    public bool? Show(
        string title,
        string message,
        MessageTone tone = MessageTone.Information,
        string? subtitle = null,
        bool showCancel = false,
        string primaryText = "知道了",
        MediaBrush? accentBrush = null) =>
        ThemedMessageWindow.Show(
            _owner,
            title,
            message,
            tone,
            subtitle,
            showCancel,
            primaryText,
            accentBrush);

    public GameDirectoryCandidate? SelectGameDirectory(
        IReadOnlyList<GameDirectoryCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var dialog = new GameDirectorySelectionWindow(candidates) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.SelectedCandidate : null;
    }

    public string? SelectFolder(
        string description,
        string? currentPath = null,
        bool showNewFolderButton = true)
    {
        var existingPath = !string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath)
            ? currentPath
            : string.Empty;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = showNewFolderButton,
            SelectedPath = existingPath,
            InitialDirectory = existingPath
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
               !string.IsNullOrWhiteSpace(dialog.SelectedPath)
            ? dialog.SelectedPath
            : null;
    }

    public CacheManagementAction SelectCacheManagementAction(CacheUsageSummary usage)
    {
        var dialog = new CacheManagementWindow(usage) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.SelectedAction : CacheManagementAction.None;
    }

    public BackupLocationAction SelectBackupLocationAction(BackupLocationUsage usage)
    {
        var dialog = new BackupLocationWindow(usage) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.SelectedAction : BackupLocationAction.None;
    }

    public bool ConfirmSwitch(SwitchConfirmationRequest request)
    {
        var dialog = new SwitchConfirmationWindow(
            request.SourceProfile,
            request.SourceName,
            request.TargetProfile,
            request.TargetName,
            request.GameVersion,
            request.ReplaceCount,
            request.DeleteCount,
            request.SnapshotSummary,
            request.BlocksSummary,
            request.BackupPath)
        {
            Owner = _owner
        };
        return dialog.ShowDialog() == true;
    }

    public void ShowBackupHistory(
        BackupService backups,
        RestoreService restore,
        LegacyRestoreSafetyPolicy safetyPolicy,
        OperationCoordinator operations,
        string gamePath) =>
        new BackupWindow(backups, restore, safetyPolicy, operations, gamePath)
        {
            Owner = _owner
        }.ShowDialog();
}
