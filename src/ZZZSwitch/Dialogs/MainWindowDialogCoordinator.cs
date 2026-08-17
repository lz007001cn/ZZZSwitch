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
    string BackupPath);

public interface IMainWindowDialogs
{
    bool? Show(
        string title,
        string message,
        MessageTone tone = MessageTone.Information,
        bool showCancel = false,
        string primaryText = "知道了",
        MediaBrush? accentBrush = null);

    GameDirectoryCandidate? SelectGameDirectory(IReadOnlyList<GameDirectoryCandidate> candidates);
    string? SelectFolder(string description, string? currentPath = null, bool showNewFolderButton = true);
    string? SelectPackageArchive();
    Task<CacheManagementAction> SelectCacheManagementActionAsync(CacheUsageSummary usage);
    Task<OnlineResourceManagementSelection> SelectOnlineResourceManagementAsync(
        OnlineDifferenceInventory inventory,
        string? currentGameVersion);
    Task ShowOnlineDifferencePreviewAsync(OnlineDifferencePackagePreview preview);
    Task ShowManifestBrowserAsync(OnlineManifestBrowserData data);
    Task<BackupLocationAction> SelectBackupLocationActionAsync(BackupLocationUsage usage);
    bool ConfirmSwitch(SwitchConfirmationRequest request);
    OnlineDifferenceMaterialization? DownloadOnlineDifference(
        OnlineDifferencePlan plan,
        IOnlineDifferenceService service,
        bool continueToSwitch = true);
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
        bool showCancel = false,
        string primaryText = "知道了",
        MediaBrush? accentBrush = null) =>
        ThemedMessageWindow.Show(
            _owner,
            title,
            message,
            tone,
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

    public string? SelectPackageArchive()
    {
        var localization = ((App)System.Windows.Application.Current).Localization;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = localization.Choose("选择文件", "Select a file"),
            Filter = localization.Choose(
                "ZZZSwitch 差异包 (*.zip)|*.zip",
                "ZZZSwitch package (*.zip)|*.zip"),
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public async Task<CacheManagementAction> SelectCacheManagementActionAsync(CacheUsageSummary usage)
    {
        var dialog = new CacheManagementWindow(usage) { Owner = _owner };
        await ModelessWindowPresenter.ShowAsync(dialog);
        return dialog.SelectedAction;
    }

    public async Task<BackupLocationAction> SelectBackupLocationActionAsync(BackupLocationUsage usage)
    {
        var dialog = new BackupLocationWindow(usage) { Owner = _owner };
        await ModelessWindowPresenter.ShowAsync(dialog);
        return dialog.SelectedAction;
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
            request.BackupPath)
        {
            Owner = _owner
        };
        return dialog.ShowDialog() == true;
    }

    public async Task<OnlineResourceManagementSelection> SelectOnlineResourceManagementAsync(
        OnlineDifferenceInventory inventory,
        string? currentGameVersion)
    {
        var dialog = new OnlineResourceManagementWindow(inventory, currentGameVersion) { Owner = _owner };
        await ModelessWindowPresenter.ShowAsync(dialog);
        return dialog.Selection;
    }

    public Task ShowOnlineDifferencePreviewAsync(OnlineDifferencePackagePreview preview) =>
        ModelessWindowPresenter.ShowAsync(
            new OnlineDifferencePreviewWindow(preview) { Owner = _owner });

    public Task ShowManifestBrowserAsync(OnlineManifestBrowserData data) =>
        ModelessWindowPresenter.ShowAsync(
            new OnlineManifestBrowserWindow(data) { Owner = _owner });

    public OnlineDifferenceMaterialization? DownloadOnlineDifference(
        OnlineDifferencePlan plan,
        IOnlineDifferenceService service,
        bool continueToSwitch = true)
    {
        var dialog = new OnlineDifferenceDownloadWindow(plan, service, continueToSwitch) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
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
        }.Show();
}
