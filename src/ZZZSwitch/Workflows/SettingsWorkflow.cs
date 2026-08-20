using System.Windows;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;

namespace ZZZSwitch.Workflows;

public sealed class SettingsWorkflow
{
    private readonly Window _owner;
    private readonly AppPaths _paths;
    private readonly UiSettingsService _settings;
    private readonly ThemeManager _theme;
    private readonly LocalizationManager _localization;
    private readonly CacheLocationService _cacheLocations;
    private readonly BackupLocationService _backupLocations;
    private readonly LogMaintenanceService _logs;
    private readonly Func<string> _getGamePath;
    private readonly Func<string?> _getGameVersion;
    private readonly Func<Task> _manageCache;
    private readonly Func<Task> _manageBackup;
    private readonly Action<string, bool> _openDirectory;
    private readonly Action<UiSettings> _applySettings;
    private readonly Func<Task> _runOnboarding;

    public SettingsWorkflow(
        Window owner,
        AppPaths paths,
        UiSettingsService settings,
        ThemeManager theme,
        LocalizationManager localization,
        CacheLocationService cacheLocations,
        BackupLocationService backupLocations,
        LogMaintenanceService logs,
        Func<string> getGamePath,
        Func<string?> getGameVersion,
        Func<Task> manageCache,
        Func<Task> manageBackup,
        Action<string, bool> openDirectory,
        Action<UiSettings> applySettings,
        Func<Task> runOnboarding)
    {
        _owner = owner;
        _paths = paths;
        _settings = settings;
        _theme = theme;
        _localization = localization;
        _cacheLocations = cacheLocations;
        _backupLocations = backupLocations;
        _logs = logs;
        _getGamePath = getGamePath;
        _getGameVersion = getGameVersion;
        _manageCache = manageCache;
        _manageBackup = manageBackup;
        _openDirectory = openDirectory;
        _applySettings = applySettings;
        _runOnboarding = runOnboarding;
    }

    public async Task ShowAsync()
    {
        while (true)
        {
            var viewData = await LoadViewDataAsync();
            var window = new SettingsWindow(viewData) { Owner = _owner };
            await ModelessWindowPresenter.ShowAsync(window);
            if (window.SelectedAction == SettingsAction.None)
            {
                return;
            }

            var updated = window.UpdatedSettings;
            _settings.Save(updated);
            _theme.SetPreference(updated.Theme);
            _localization.SetLanguage(updated.Language);
            _applySettings(updated);
            TryCleanExpiredLogs(updated.LogRetentionDays);

            switch (window.SelectedAction)
            {
                case SettingsAction.SaveAndClose:
                case SettingsAction.None:
                    return;
                case SettingsAction.ManageCache:
                    await _manageCache();
                    break;
                case SettingsAction.ManageBackup:
                    await _manageBackup();
                    break;
                case SettingsAction.OpenLogs:
                    _openDirectory(_paths.LogsRoot, false);
                    break;
                case SettingsAction.RunOnboarding:
                    await _runOnboarding();
                    return;
            }
        }
    }

    private async Task<SettingsViewData> LoadViewDataAsync()
    {
        var gamePath = _getGamePath().Trim();
        var gameVersion = _getGameVersion();
        CacheUsageSummary? cacheUsage = null;
        string? cacheError = null;
        BackupLocationUsage? backupUsage = null;
        string? backupError = null;
        if (!string.IsNullOrWhiteSpace(gamePath) && !string.IsNullOrWhiteSpace(gameVersion))
        {
            try
            {
                cacheUsage = await Task.Run(() => _cacheLocations.GetUsage(gamePath, gameVersion));
            }
            catch (Exception ex)
            {
                cacheError = ex.Message;
            }
        }
        else
        {
            cacheError = _localization.Language == AppLanguage.English
                ? "Select a valid game directory first."
                : "请先选择有效的游戏目录。";
        }

        try
        {
            backupUsage = await Task.Run(_backupLocations.GetUsage);
        }
        catch (Exception ex)
        {
            backupError = ex.Message;
        }

        return new(
            _settings.Load(),
            cacheUsage,
            cacheError,
            backupUsage,
            backupError);
    }

    private void TryCleanExpiredLogs(int retentionDays)
    {
        try
        {
            _logs.CleanExpiredLogs(retentionDays);
        }
        catch (Exception)
        {
            // Log retention is maintenance only and must not block saving settings.
        }
    }
}
