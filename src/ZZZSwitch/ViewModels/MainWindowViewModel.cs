using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ZZZSwitch.Commands;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.Presentation;
using MediaBrush = System.Windows.Media.Brush;

namespace ZZZSwitch.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly ICommand DisabledCommand = new RelayCommand(() => { }, () => false);
    private readonly List<Action> _refreshCommandStates = [];
    private string _appVersion = "v1.2.6";
    private string _gamePath = string.Empty;
    private string _profile = "等待检测";
    private string _gameVersion = "—";
    private string _packages = "等待扫描";
    private string _cacheSummary = "等待检查";
    private string _operationStatus = "检查中";
    private string _issueSummary = string.Empty;
    private string _report = string.Empty;
    private string _busyStatus = "正在处理…";
    private bool _isBusy;
    private bool _hasStatusIssues;
    private bool _inspectionCanManageCache;
    private bool _inspectionCanManageOnlineResources;
    private MediaBrush? _profileAccent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand AutoDetectCommand { get; private set; } = DisabledCommand;
    public ICommand ChooseDirectoryCommand { get; private set; } = DisabledCommand;
    public ICommand SwitchGlobalCommand { get; private set; } = DisabledCommand;
    public ICommand SwitchCnCommand { get; private set; } = DisabledCommand;
    public ICommand SwitchBilibiliCommand { get; private set; } = DisabledCommand;
    public ICommand CacheManagementCommand { get; private set; } = DisabledCommand;
    public ICommand OnlineResourcesCommand { get; private set; } = DisabledCommand;
    public ICommand BackupsCommand { get; private set; } = DisabledCommand;
    public ICommand BackupDirectoryCommand { get; private set; } = DisabledCommand;
    public ICommand LogsCommand { get; private set; } = DisabledCommand;
    public ICommand OpenPackagesCommand { get; private set; } = DisabledCommand;
    public ICommand SettingsCommand { get; private set; } = DisabledCommand;

    public string AppVersion
    {
        get => _appVersion;
        set => SetField(ref _appVersion, value);
    }

    public string GamePath
    {
        get => _gamePath;
        set => SetField(ref _gamePath, value);
    }

    public string Profile
    {
        get => _profile;
        set => SetField(ref _profile, value);
    }

    public MediaBrush? ProfileAccent
    {
        get => _profileAccent;
        set => SetField(ref _profileAccent, value);
    }

    public string GameVersion
    {
        get => _gameVersion;
        set => SetField(ref _gameVersion, value);
    }

    public string Packages
    {
        get => _packages;
        set => SetField(ref _packages, value);
    }

    public string CacheSummary
    {
        get => _cacheSummary;
        set => SetField(ref _cacheSummary, value);
    }

    public string OperationStatus
    {
        get => _operationStatus;
        set => SetField(ref _operationStatus, value);
    }

    public string IssueSummary
    {
        get => _issueSummary;
        set => SetField(ref _issueSummary, value);
    }

    public string Report
    {
        get => _report;
        set => SetField(ref _report, value);
    }

    public string BusyStatus
    {
        get => _busyStatus;
        set => SetField(ref _busyStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetField(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInteractionEnabled));
            OnPropertyChanged(nameof(CanManageCache));
            OnPropertyChanged(nameof(CanManageOnlineResources));
            RefreshCommandStates();
        }
    }

    public bool IsInteractionEnabled => !IsBusy;

    public bool HasStatusIssues
    {
        get => _hasStatusIssues;
        set => SetField(ref _hasStatusIssues, value);
    }

    public bool CanManageCache => !IsBusy && _inspectionCanManageCache;

    public bool CanManageOnlineResources => !IsBusy && _inspectionCanManageOnlineResources;

    public void SetInspectionCapabilities(bool canManageCache, bool canManageOnlineResources)
    {
        if (_inspectionCanManageCache != canManageCache)
        {
            _inspectionCanManageCache = canManageCache;
            OnPropertyChanged(nameof(CanManageCache));
        }

        if (_inspectionCanManageOnlineResources != canManageOnlineResources)
        {
            _inspectionCanManageOnlineResources = canManageOnlineResources;
            OnPropertyChanged(nameof(CanManageOnlineResources));
        }

        RefreshCommandStates();
    }

    public void ApplyInspection(InspectionPresentation presentation)
    {
        Profile = presentation.Profile;
        GameVersion = presentation.GameVersion;
        Packages = presentation.Packages;
        CacheSummary = presentation.CacheSummary;
        OperationStatus = presentation.OperationStatus;
        IssueSummary = presentation.IssueSummary;
        Report = presentation.Report;
        HasStatusIssues = presentation.HasStatusIssues;
        SetInspectionCapabilities(presentation.CanManageCache, presentation.CanManageOnlineResources);
    }

    public void ApplyInitialLanguage(AppLanguage language)
    {
        if (language == AppLanguage.English)
        {
            Profile = "Waiting for detection";
            Packages = "Available after server selection";
            CacheSummary = "Waiting for inspection";
            OperationStatus = "Inspecting";
            BusyStatus = "Working…";
            return;
        }

        Profile = "等待检测";
        Packages = "选择服务器后可下载";
        CacheSummary = "等待检查";
        OperationStatus = "检查中";
        BusyStatus = "正在处理…";
    }

    public void ConfigureCommands(MainWindowCommandHandlers handlers)
    {
        _refreshCommandStates.Clear();
        AutoDetectCommand = Async(handlers.AutoDetect, () => IsInteractionEnabled, handlers.HandleUnexpectedError);
        ChooseDirectoryCommand = Async(handlers.ChooseDirectory, () => IsInteractionEnabled, handlers.HandleUnexpectedError);
        SwitchGlobalCommand = Async(
            () => handlers.Switch(ProfileIds.Global),
            () => IsInteractionEnabled,
            handlers.HandleUnexpectedError);
        SwitchCnCommand = Async(
            () => handlers.Switch(ProfileIds.CnOfficial),
            () => IsInteractionEnabled,
            handlers.HandleUnexpectedError);
        SwitchBilibiliCommand = Async(
            () => handlers.Switch(ProfileIds.Bilibili),
            () => IsInteractionEnabled,
            handlers.HandleUnexpectedError);
        CacheManagementCommand = Async(
            handlers.CacheManagement,
            () => CanManageCache,
            handlers.HandleUnexpectedError);
        OnlineResourcesCommand = Async(
            handlers.ManageOnlineResources,
            () => CanManageOnlineResources,
            handlers.HandleUnexpectedError);
        BackupsCommand = Sync(
            handlers.ShowBackups,
            () => IsInteractionEnabled,
            handlers.HandleUnexpectedError);
        BackupDirectoryCommand = Async(
            handlers.ManageBackupDirectory,
            () => IsInteractionEnabled,
            handlers.HandleUnexpectedError);
        LogsCommand = Sync(handlers.OpenLogs, handleError: handlers.HandleUnexpectedError);
        OpenPackagesCommand = Async(
            handlers.ImportPackages,
            () => IsInteractionEnabled,
            handlers.HandleUnexpectedError);
        SettingsCommand = Async(
            handlers.OpenSettings,
            () => IsInteractionEnabled,
            handlers.HandleUnexpectedError);

        foreach (var propertyName in CommandPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }

        RefreshCommandStates();
    }

    private AsyncRelayCommand Async(
        Func<Task> execute,
        Func<bool> canExecute,
        Action<Exception> handleError)
    {
        var command = new AsyncRelayCommand(execute, canExecute, handleError);
        _refreshCommandStates.Add(command.RaiseCanExecuteChanged);
        return command;
    }

    private RelayCommand Sync(
        Action execute,
        Func<bool>? canExecute = null,
        Action<Exception>? handleError = null)
    {
        var command = new RelayCommand(execute, canExecute, handleError);
        _refreshCommandStates.Add(command.RaiseCanExecuteChanged);
        return command;
    }

    private void RefreshCommandStates()
    {
        foreach (var refresh in _refreshCommandStates)
        {
            refresh();
        }
    }

    private static readonly string[] CommandPropertyNames =
    [
        nameof(AutoDetectCommand),
        nameof(ChooseDirectoryCommand),
        nameof(SwitchGlobalCommand),
        nameof(SwitchCnCommand),
        nameof(SwitchBilibiliCommand),
        nameof(CacheManagementCommand),
        nameof(OnlineResourcesCommand),
        nameof(BackupsCommand),
        nameof(BackupDirectoryCommand),
        nameof(LogsCommand),
        nameof(OpenPackagesCommand),
        nameof(SettingsCommand)
    ];

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record MainWindowCommandHandlers(
    Func<Task> AutoDetect,
    Func<Task> ChooseDirectory,
    Func<string, Task> Switch,
    Func<Task> CacheManagement,
    Func<Task> ManageOnlineResources,
    Action ShowBackups,
    Func<Task> ManageBackupDirectory,
    Action OpenLogs,
    Func<Task> ImportPackages,
    Func<Task> OpenSettings,
    Action<Exception> HandleUnexpectedError);
