using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Dialogs;
using ZZZSwitch.Presentation;
using ZZZSwitch.ViewModels;
using ZZZSwitch.Workflows;

namespace ZZZSwitch;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly AppPaths _paths = new();
    private readonly MainWindowDialogCoordinator _dialogs;
    private readonly ConfigurationRepository _configuration;
    private readonly StateStore _stateStore;
    private readonly InspectionService _inspection;
    private readonly GameDirectoryDiscoveryService _gameDirectoryDiscovery;
    private readonly SwitchPlanner _planner;
    private readonly SwitchEngine _engine;
    private readonly IOnlineDifferenceService _onlineDifferences;
    private readonly BackupService _backups;
    private readonly BackupLocationService _backupLocations;
    private readonly RestoreService _restore;
    private readonly ProfileSnapshotService _snapshots;
    private readonly InspectionPresentationBuilder _inspectionPresentation;
    private readonly HotUpdateCacheService _hotUpdateCaches;
    private readonly FileTransactionJournalStore _fileTransactions;
    private readonly StartupWorkflow _startupWorkflow;
    private readonly ServerSwitchWorkflow _serverSwitchWorkflow;
    private readonly CacheManagementWorkflow _cacheManagementWorkflow;
    private readonly OnlineResourceManagementWorkflow _onlineResourceManagementWorkflow;
    private readonly BackupManagementWorkflow _backupManagementWorkflow;
    private readonly PackageImportWorkflow _packageImportWorkflow;
    private readonly SettingsWorkflow _settingsWorkflow;
    private readonly StorageLayoutService _storageLayout;
    private readonly CacheLocationService _cacheLocations;
    private readonly IProcessMonitor _processMonitor;
    private readonly LegacyRestoreSafetyPolicy _restoreSafetyPolicy;
    private readonly OperationCoordinator _operations;
    private InspectionReport? _lastReport;
    private HotUpdateCacheStatus[] _lastCacheStatuses = [];
    private string? _lastHealthPromptKey;
    private bool _busy;
    private readonly string? _startupStateWarning;
    private readonly ThemeManager _theme;
    private readonly LocalizationManager _localization;
    private readonly UiSettingsService _uiSettingsService;
    private UiSettings _uiSettings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        var app = (App)System.Windows.Application.Current;
        _theme = app.Theme;
        _localization = app.Localization;
        _viewModel.ApplyInitialLanguage(_localization.Language);
        _uiSettingsService = new UiSettingsService(_paths);
        _uiSettings = _uiSettingsService.Load();
        ApplyWindowPlacement(_uiSettings);
        DetailsExpander.IsExpanded = _uiSettings.ShowDetailedStatus;
        Closing += (_, _) => SaveWindowPlacement();
        _dialogs = new MainWindowDialogCoordinator(this);
        var informationalVersion = GetType().Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];
        var displayVersion = $"v{informationalVersion ?? "1.2.6"}";
        _viewModel.AppVersion = displayVersion;
        Title = "ZZZSwitch";
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);
        _operations = new OperationCoordinator(_paths);
        _configuration = new ConfigurationRepository(_paths);
        _stateStore = new StateStore(_paths);
        var gameDirectory = new GameDirectoryService();
        _gameDirectoryDiscovery = new GameDirectoryDiscoveryService(gameDirectory);
        var files = new PhysicalFileOperations();
        _processMonitor = new ProcessMonitor();
        _cacheLocations = new CacheLocationService(_paths);
        _storageLayout = new StorageLayoutService(_cacheLocations);
        _snapshots = new ProfileSnapshotService(_paths, files);
        var onlinePackageCatalog = new OnlineDifferencePackageCatalog(_paths);
        _onlineDifferences = new OnlineDifferenceService(_paths, catalog: onlinePackageCatalog);
        _inspectionPresentation = new InspectionPresentationBuilder(_snapshots, onlinePackageCatalog);
        _hotUpdateCaches = new HotUpdateCacheService(_paths, _processMonitor, _cacheLocations);
        _fileTransactions = new FileTransactionJournalStore(_paths);
        _backupLocations = new BackupLocationService(_paths);
        _backups = new BackupService(files, _paths);
        _inspection = new InspectionService(
            _configuration,
            gameDirectory,
            new ProfileDetector(),
            _stateStore,
            _processMonitor,
            _fileTransactions,
            files,
            _storageLayout,
            inspectLocalPackages: false);
        _planner = new SwitchPlanner(_configuration, gameDirectory, _processMonitor, files, _paths, _snapshots, _hotUpdateCaches, _fileTransactions);
        _engine = new SwitchEngine(files, _paths, _backups, _stateStore, new OperationLogger(_paths), _snapshots, _hotUpdateCaches, _fileTransactions);
        var pendingRecovery = new PendingTransactionRecoveryService(
            _paths,
            _stateStore,
            _backups,
            _hotUpdateCaches,
            _fileTransactions,
            _processMonitor);
        _startupWorkflow = new StartupWorkflow(
            pendingRecovery.RecoverPending,
            _stateStore.Load,
            lastBackupPath => _backups.PruneAllBackups(lastBackupPath));
        _restoreSafetyPolicy = new LegacyRestoreSafetyPolicy(_stateStore, _hotUpdateCaches);
        _restore = new RestoreService(_backups, _processMonitor, files, _stateStore, _restoreSafetyPolicy);
        var workflowContext = new MainWindowWorkflowContext(
            () => _busy,
            () => _viewModel.GamePath,
            () => _lastReport,
            () => RefreshInspectionAsync(),
            () => RefreshInspectionAsync(allowWhileBusy: true),
            SetBusy,
            status => _viewModel.BusyStatus = status,
            ShowOperationInProgress,
            ShowOperationProgress,
            ProfileBrush,
            OpenDirectory);
        _serverSwitchWorkflow = new ServerSwitchWorkflow(
            _planner,
            _engine,
            _operations,
            _onlineDifferences,
            _dialogs,
            workflowContext);
        _cacheManagementWorkflow = new CacheManagementWorkflow(
            _cacheLocations,
            _fileTransactions,
            _paths,
            _processMonitor,
            _operations,
            _dialogs,
            workflowContext);
        _onlineResourceManagementWorkflow = new OnlineResourceManagementWorkflow(
            onlinePackageCatalog,
            _onlineDifferences,
            _operations,
            _dialogs,
            workflowContext);
        _backupManagementWorkflow = new BackupManagementWorkflow(
            _backups,
            _backupLocations,
            _restore,
            _restoreSafetyPolicy,
            _paths,
            _operations,
            _dialogs,
            workflowContext);
        _packageImportWorkflow = new PackageImportWorkflow(
            new PackageImportService(_configuration),
            _processMonitor,
            _operations,
            _dialogs,
            workflowContext);
        _settingsWorkflow = new SettingsWorkflow(
            this,
            _paths,
            _uiSettingsService,
            _theme,
            _localization,
            _cacheLocations,
            _backupLocations,
            new LogMaintenanceService(_paths),
            () => _viewModel.GamePath,
            () => _lastReport?.Game.GameVersion ?? _stateStore.Load()?.GameVersion,
            _cacheManagementWorkflow.ManageAsync,
            _backupManagementWorkflow.ManageDirectoryAsync,
            OpenDirectory,
            ApplyRuntimeSettings);

        var stateLoad = _stateStore.LoadWithStatus();
        _startupStateWarning = stateLoad.Warning;
        var savedPath = stateLoad.State?.GamePath;
        _viewModel.GamePath = _uiSettings.ShowLastGameDirectory && !string.IsNullOrWhiteSpace(savedPath)
            ? savedPath
            : string.Empty;
        _viewModel.ConfigureCommands(new MainWindowCommandHandlers(
            AutoDetectAsync,
            ChooseDirectoryAsync,
            _serverSwitchWorkflow.RunAsync,
            _cacheManagementWorkflow.ManageAsync,
            _onlineResourceManagementWorkflow.ManageAsync,
            _backupManagementWorkflow.ShowHistory,
            _backupManagementWorkflow.ManageDirectoryAsync,
            OpenLogs,
            _packageImportWorkflow.ImportAsync,
            _settingsWorkflow.ShowAsync,
            ShowUnexpectedCommandError));
        Loaded += async (_, _) =>
        {
            var startup = await _startupWorkflow.RunAsync(_startupStateWarning);
            var recovery = startup.Recovery;
            if (recovery.Found)
            {
                _dialogs.Show(
                    recovery.Success ? "上次切换已恢复" : "上次切换需要处理",
                    recovery.Message,
                    recovery.Success ? MessageTone.Success : MessageTone.Warning);
            }

            if (!string.IsNullOrWhiteSpace(startup.StateWarning))
            {
                _dialogs.Show(
                    "本地状态记录不可用",
                    startup.StateWarning,
                    MessageTone.Warning);
            }

            if (_uiSettings.AutoDetectGameDirectory)
            {
                await AutoDetectAsync();
            }
            else if (_uiSettings.AutoInspectOnStartup && !string.IsNullOrWhiteSpace(_viewModel.GamePath))
            {
                await RefreshInspectionAsync(offerStorageRecovery: true);
            }
        };
    }

    private async Task RefreshInspectionAsync(
        bool showReadOnlyBanner = false,
        bool offerStorageRecovery = false,
        bool allowWhileBusy = false)
    {
        // 切换、初始化和恢复完成后的复检复用现有忙碌状态，避免进度浮层闪退后立即重现。
        var managesBusyState = !_busy;
        if (!managesBusyState && !allowWhileBusy)
        {
            return;
        }

        InspectionReport? report = null;
        if (managesBusyState)
        {
            SetBusy(true, "正在只读扫描游戏目录与服务器状态…");
        }
        else
        {
            _viewModel.BusyStatus = "正在重新检查游戏目录与服务器状态…";
            OperationProgress.IsIndeterminate = true;
            OperationProgress.Value = 0;
        }
        try
        {
            var path = _viewModel.GamePath.Trim();
            report = await Task.Run(() => _inspection.Inspect(path));
            _lastReport = report;
            RenderReport(report, showReadOnlyBanner);
        }
        catch (Exception ex)
        {
            _viewModel.HasStatusIssues = true;
            _viewModel.OperationStatus = "检查失败";
            _viewModel.Report = ex.Message;
        }
        finally
        {
            if (managesBusyState)
            {
                SetBusy(false, _viewModel.OperationStatus);
            }
        }

        if (offerStorageRecovery && report is not null)
        {
            await OfferStorageRecoveryAsync(report);
        }
    }

    private void RenderReport(InspectionReport report, bool readOnlyBanner)
    {
        var activeProfile = report.Detection.Profile.ToProfileId();
        _lastCacheStatuses = report.Game.GameVersion is null
            ? []
            : ProfileIds.HotUpdateProfiles.Select(profile => _hotUpdateCaches.GetStatus(
                profile,
                report.Game.GameVersion,
                report.Game.GamePath,
                activeProfile is null ? null : ProfileIds.ToResourceProfile(activeProfile))).ToArray();
        var presentation = _inspectionPresentation.Build(
            report,
            _lastCacheStatuses,
            readOnlyBanner,
            _localization.Language);
        _viewModel.ApplyInspection(presentation);
        _viewModel.ProfileAccent = ProfileBrush(presentation.ActiveProfile);
        OperationProgress.Value = 0;
        DetailsExpander.IsExpanded = presentation.ExpandDetails;
    }

    private async Task OfferStorageRecoveryAsync(InspectionReport report)
    {
        var storage = report.Storage;
        if (storage is null)
        {
            return;
        }

        var configurationErrors = report.Issues
            .Where(x => x.Severity == IssueSeverity.Error &&
                        x.Code.StartsWith("config.", StringComparison.Ordinal))
            .ToArray();
        if (configurationErrors.Length > 0)
        {
            var configurationPromptKey = string.Join(
                "|",
                report.Game.GamePath,
                "configuration",
                string.Join(",", configurationErrors.Select(x => x.Path)));
            if (!string.Equals(_lastHealthPromptKey, configurationPromptKey, StringComparison.OrdinalIgnoreCase))
            {
                _lastHealthPromptKey = configurationPromptKey;
                _dialogs.Show(
                    "ZZZSwitch 配置异常",
                    "检测到内置服务器配置或切换清单损坏。为避免创建不完整目录或执行错误切换，自动修复已停止。\n\n" +
                    "请重新解压完整的软件本体覆盖当前文件；游戏目录中的 .zzzswitch 数据不会被修改。",
                    MessageTone.Error);
            }

            return;
        }

        var invalidCaches = _lastCacheStatuses
            .Where(x => x.IsInitialized && !x.IsAvailable)
            .ToArray();
        if (invalidCaches.Length == 0)
        {
            return;
        }

        var promptKey = string.Join(
            "|",
            report.Game.GamePath,
            report.Game.GameVersion,
            string.Join(",", invalidCaches.Select(x => x.Profile)));
        if (string.Equals(_lastHealthPromptKey, promptKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastHealthPromptKey = promptKey;
        var message = new StringBuilder();
        message.AppendLine("缓存记录异常：");
        foreach (var cache in invalidCaches)
        {
            message.AppendLine($"• {ShortProfileName(cache.Profile)}：{cache.Detail}");
        }

        message.AppendLine();
        message.AppendLine("无需手动初始化按钮。下一次切换会先自动保存当前服务器缓存；已丢失的目标服缓存将在进入游戏后重新下载。 ");
        _dialogs.Show(
            "检测到缓存异常",
            message.ToString().TrimEnd(),
            MessageTone.Warning);
        await Task.CompletedTask;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        // BusyIndicator 是根网格上的浮层，不参与主 StackPanel 测量；显示进度不会推动页面内容。
        _viewModel.BusyStatus = status;
        _viewModel.IsBusy = busy;
        OperationProgress.IsIndeterminate = busy;
        if (!busy)
        {
            OperationProgress.Value = 0;
        }
        _viewModel.SetInspectionCapabilities(
            _lastReport?.Game.IsValid == true && _lastReport.Game.GameVersion is not null,
            _lastReport?.Detection.Profile.ToProfileId() is not null &&
            _lastReport.Game.GameVersion is not null);
    }

    private void ShowOperationProgress(OperationProgress progress)
    {
        _viewModel.BusyStatus = progress.IsRollingBack
            ? $"\u56de\u6eda\u4e2d\uff1a{progress.Step}"
            : progress.Step;
        OperationProgress.IsIndeterminate = false;
        OperationProgress.Maximum = Math.Max(
            1,
            progress.PlannedReplace + progress.PlannedDelete + progress.PlannedCacheRestore);
        OperationProgress.Value =
            progress.SuccessfulReplace +
            progress.SuccessfulDelete +
            progress.SuccessfulCacheRestore;
        _viewModel.Report =
            $"\u5f53\u524d\u6b65\u9aa4\uff1a{progress.Step}\n" +
            $"\u66ff\u6362\uff1a{progress.SuccessfulReplace}/{progress.PlannedReplace}\uff0c\u5931\u8d25 {progress.FailedReplace}\n" +
            $"\u5220\u9664\uff1a{progress.SuccessfulDelete}/{progress.PlannedDelete}\uff0c\u5931\u8d25 {progress.FailedDelete}\n" +
            $"\u7f13\u5b58\u6062\u590d\uff1a{progress.SuccessfulCacheRestore}/{progress.PlannedCacheRestore}\uff0c\u5931\u8d25 {progress.FailedCacheRestore}\n" +
            $"\u6b63\u5728\u56de\u6eda\uff1a{(progress.IsRollingBack ? "\u662f" : "\u5426")}";
    }

    private void SaveSelectedPath(string path)
    {
        var state = _stateStore.Load() ?? new AppState();
        state.GamePath = path;
        _stateStore.Save(state);
    }

    private static string ShortProfileName(string profileId) =>
        DisplayFormatting.ShortProfileName(profileId);

    private System.Windows.Media.Brush ProfileBrush(string? profileId)
    {
        var resourceKey = profileId switch
        {
            ProfileIds.Global => "GlobalProfileBrush",
            ProfileIds.CnOfficial => "CnProfileBrush",
            ProfileIds.Bilibili => "BilibiliProfileBrush",
            _ => "NeutralBrush"
        };
        return (System.Windows.Media.Brush)FindResource(resourceKey);
    }

    private async Task AutoDetectAsync()
    {
        if (_busy)
        {
            return;
        }

        IReadOnlyList<GameDirectoryCandidate> candidates;
        SetBusy(true, "正在检测绝区零游戏目录…");
        try
        {
            var savedPath = _stateStore.Load()?.GamePath;
            var currentPath = _viewModel.GamePath.Trim();
            candidates = await Task.Run(() =>
                _gameDirectoryDiscovery.Discover([currentPath, savedPath]));
        }
        catch (Exception ex)
        {
            _dialogs.Show(
                "自动检测失败",
                $"自动检测未能完成。\n\n{ex.Message}\n\n请点击“选择”手动指定游戏目录。",
                MessageTone.Warning);
            return;
        }
        finally
        {
            SetBusy(false, "目录检测完成");
        }

        if (candidates.Count == 0)
        {
            _dialogs.Show(
                "未找到游戏",
                "未找到有效的绝区零游戏目录。\n\n请点击“选择”，手动选择包含 ZenlessZoneZero.exe 的游戏根目录。",
                MessageTone.Information);
            return;
        }

        var selected = _dialogs.SelectGameDirectory(candidates);

        if (selected is null)
        {
            return;
        }

        _viewModel.GamePath = selected.Path;
        SaveSelectedPath(selected.Path);
        await RefreshInspectionAsync(offerStorageRecovery: true);
    }

    private async Task ChooseDirectoryAsync()
    {
        var selectedPath = _dialogs.SelectFolder(
            "选择绝区零游戏根目录",
            _viewModel.GamePath,
            showNewFolderButton: false);
        if (selectedPath is not null)
        {
            _viewModel.GamePath = selectedPath;
            SaveSelectedPath(selectedPath);
            await RefreshInspectionAsync(offerStorageRecovery: true);
        }
    }

    private void ShowOperationInProgress() =>
        _dialogs.Show(
            "操作正在进行",
            _operations.LastFailure ?? "请等待当前操作完成后再试。",
            MessageTone.Information);

    private void ShowUnexpectedCommandError(Exception exception) =>
        _dialogs.Show(
            "操作未能完成",
            exception.Message,
            MessageTone.Error);

    private void OpenLogs() => OpenDirectory(_paths.LogsRoot, false);

    private void ApplyRuntimeSettings(UiSettings settings)
    {
        _uiSettings = settings;
        DetailsExpander.IsExpanded = settings.ShowDetailedStatus;
        if (_lastReport is not null)
        {
            RenderReport(_lastReport, readOnlyBanner: false);
        }
    }

    private void ApplyWindowPlacement(UiSettings settings)
    {
        if (!settings.RememberWindowPlacement ||
            settings.WindowWidth is null ||
            settings.WindowHeight is null)
        {
            return;
        }

        Width = settings.WindowWidth.Value;
        Height = settings.WindowHeight.Value;
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(settings.WindowLeft ?? area.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(settings.WindowTop ?? area.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        if (!IsLoaded || !_uiSettings.RememberWindowPlacement)
        {
            return;
        }

        var bounds = RestoreBounds;
        _uiSettings.WindowLeft = bounds.Left;
        _uiSettings.WindowTop = bounds.Top;
        _uiSettings.WindowWidth = bounds.Width;
        _uiSettings.WindowHeight = bounds.Height;
        _uiSettings.WindowMaximized = WindowState == WindowState.Maximized;
        try
        {
            _uiSettingsService.Save(_uiSettings);
        }
        catch
        {
            // Window placement persistence must never block application shutdown.
        }
    }

    private void OpenDirectory(string path, bool mustExist)
    {
        if (!Directory.Exists(path))
        {
            if (mustExist)
            {
                _dialogs.Show(
                    "无法打开目录",
                    $"目录不存在：{path}",
                    MessageTone.Warning);
                return;
            }

            Directory.CreateDirectory(path);
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
