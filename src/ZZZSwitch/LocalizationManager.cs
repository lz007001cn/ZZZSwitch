using System.Windows;
using ZZZSwitch.Core.Services;
using WpfApplication = System.Windows.Application;

namespace ZZZSwitch;

public sealed class LocalizationManager
{
    private readonly WpfApplication _application;
    private readonly UiSettingsService _settings;

    public LocalizationManager(WpfApplication application, AppPaths paths)
    {
        _application = application;
        _settings = new UiSettingsService(paths);
        Language = _settings.LoadLanguage();
        Apply();
    }

    public AppLanguage Language { get; private set; }

    public event EventHandler? Changed;

    public void SetLanguage(AppLanguage language)
    {
        if (Language == language)
        {
            return;
        }

        _settings.SaveLanguage(language);
        Language = language;
        Apply();
    }

    public string Text(string key) =>
        (_application.Resources[key] as string) ?? key;

    private void Apply()
    {
        var strings = Language == AppLanguage.English ? English : Chinese;
        foreach (var (key, value) in strings)
        {
            _application.Resources[key] = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static readonly IReadOnlyDictionary<string, string> Chinese =
        new Dictionary<string, string>
        {
            ["L.Main.GameDirectory"] = "游戏目录",
            ["L.Main.AutoDetect"] = "自动检测",
            ["L.Common.Select"] = "选择",
            ["L.Main.SwitchServer"] = "切换服务器",
            ["L.Server.Global"] = "国际服",
            ["L.Server.Cn"] = "国服",
            ["L.Server.Bilibili"] = "B服",
            ["L.Main.Details"] = "详细检查信息",
            ["L.Main.Tools"] = "工具",
            ["L.Main.BackupHistory"] = "备份历史",
            ["L.Main.ImportPackage"] = "导入差异包",
            ["L.Main.BackupDirectory"] = "备份目录",
            ["L.Main.Settings"] = "设置",
            ["L.Summary.CurrentClient"] = "当前客户端",
            ["L.Summary.GameVersion"] = "游戏版本",
            ["L.Summary.Packages"] = "客户端差异包",
            ["L.Summary.Cache"] = "双服热更新缓存",
            ["L.Summary.CacheManagement"] = "缓存管理",
            ["L.Summary.VersionResources"] = "差异包管理",
            ["L.Settings.Title"] = "设置",
            ["L.Settings.Interface"] = "界面",
            ["L.Settings.Language"] = "语言",
            ["L.Settings.Theme"] = "主题",
            ["L.Settings.Chinese"] = "中文",
            ["L.Settings.English"] = "English",
            ["L.Settings.FollowWindows"] = "跟随 Windows",
            ["L.Settings.Light"] = "浅色",
            ["L.Settings.Dark"] = "深色",
            ["L.Settings.ShowDetails"] = "默认展开详细状态信息",
            ["L.Settings.RememberWindow"] = "记住窗口位置和大小",
            ["L.Settings.Storage"] = "存储",
            ["L.Settings.Cache"] = "缓存位置",
            ["L.Settings.CacheDefault"] = "默认使用游戏同级 .zzzswitch\\cache；可迁移到其他本地目录。",
            ["L.Settings.ManageCache"] = "管理缓存",
            ["L.Settings.Backup"] = "备份位置",
            ["L.Settings.BackupDefault"] = "默认 %LOCALAPPDATA%\\ZZZSwitch\\Backups；每个来源服保留最新一份可恢复备份。",
            ["L.Settings.ManageBackup"] = "管理备份",
            ["L.Settings.Startup"] = "启动行为",
            ["L.Settings.AutoDetect"] = "启动时自动检测游戏目录",
            ["L.Settings.AutoInspect"] = "启动时自动检查当前服务器状态",
            ["L.Settings.ShowLastGame"] = "显示上次使用的游戏安装",
            ["L.Settings.Logs"] = "日志",
            ["L.Settings.LogRetention"] = "自动保留最近的日志",
            ["L.Settings.SevenDays"] = "7 天",
            ["L.Settings.ThirtyDays"] = "30 天",
            ["L.Settings.OpenLogs"] = "打开目录",
            ["L.Common.Save"] = "保存",
            ["L.Common.Close"] = "关闭"
        };

    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>
        {
            ["L.Main.GameDirectory"] = "Game directory",
            ["L.Main.AutoDetect"] = "Auto detect",
            ["L.Common.Select"] = "Select",
            ["L.Main.SwitchServer"] = "Switch server",
            ["L.Server.Global"] = "Global",
            ["L.Server.Cn"] = "CN Official",
            ["L.Server.Bilibili"] = "Bilibili",
            ["L.Main.Details"] = "Detailed status",
            ["L.Main.Tools"] = "Tools",
            ["L.Main.BackupHistory"] = "Backup history",
            ["L.Main.ImportPackage"] = "Import package",
            ["L.Main.BackupDirectory"] = "Backup location",
            ["L.Main.Settings"] = "Settings",
            ["L.Summary.CurrentClient"] = "Current client",
            ["L.Summary.GameVersion"] = "Game version",
            ["L.Summary.Packages"] = "Client difference packages",
            ["L.Summary.Cache"] = "Hot-update caches",
            ["L.Summary.CacheManagement"] = "Cache management",
            ["L.Summary.VersionResources"] = "Manage packages",
            ["L.Settings.Title"] = "Settings",
            ["L.Settings.Interface"] = "Appearance",
            ["L.Settings.Language"] = "Language",
            ["L.Settings.Theme"] = "Theme",
            ["L.Settings.Chinese"] = "中文",
            ["L.Settings.English"] = "English",
            ["L.Settings.FollowWindows"] = "Follow Windows",
            ["L.Settings.Light"] = "Light",
            ["L.Settings.Dark"] = "Dark",
            ["L.Settings.ShowDetails"] = "Expand detailed status by default",
            ["L.Settings.RememberWindow"] = "Remember window position and size",
            ["L.Settings.Storage"] = "Storage",
            ["L.Settings.Cache"] = "Cache location",
            ["L.Settings.CacheDefault"] = "Default: .zzzswitch\\cache next to the game. It can be migrated to another local folder.",
            ["L.Settings.ManageCache"] = "Manage cache",
            ["L.Settings.Backup"] = "Backup location",
            ["L.Settings.BackupDefault"] = "Default: %LOCALAPPDATA%\\ZZZSwitch\\Backups. One latest restorable backup is kept per source server.",
            ["L.Settings.ManageBackup"] = "Manage backups",
            ["L.Settings.Startup"] = "Startup",
            ["L.Settings.AutoDetect"] = "Automatically detect the game directory at startup",
            ["L.Settings.AutoInspect"] = "Inspect the current server at startup",
            ["L.Settings.ShowLastGame"] = "Show the last-used game installation",
            ["L.Settings.Logs"] = "Logs",
            ["L.Settings.LogRetention"] = "Automatically retain recent logs",
            ["L.Settings.SevenDays"] = "7 days",
            ["L.Settings.ThirtyDays"] = "30 days",
            ["L.Settings.OpenLogs"] = "Open folder",
            ["L.Common.Save"] = "Save",
            ["L.Common.Close"] = "Close"
        };
}
