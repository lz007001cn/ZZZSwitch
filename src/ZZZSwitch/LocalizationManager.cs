using System.Windows;
using ZZZSwitch.Core.Models;
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

    public string Choose(string chinese, string english) =>
        ResourcesUseEnglish ? english : chinese;

    public string ProfileName(string profileId) => profileId switch
    {
        ProfileIds.Global => Choose("国际服", "Global"),
        ProfileIds.CnOfficial => Choose("国服", "CN Official"),
        ProfileIds.Bilibili => Choose("B服", "Bilibili"),
        _ => profileId
    };

    public string TranslateKnown(string text)
    {
        if (!ResourcesUseEnglish || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (KnownChineseToEnglish.TryGetValue(text, out var translated))
        {
            return translated;
        }

        const string downloadPrefix = "正在下载 ";
        const string downloadSuffix = " 国际服/国服 Manifest…";
        if (text.StartsWith(downloadPrefix, StringComparison.Ordinal) &&
            text.EndsWith(downloadSuffix, StringComparison.Ordinal))
        {
            var value = text[downloadPrefix.Length..(text.Length - downloadSuffix.Length)];
            return $"Downloading {value} Global/CN manifests…";
        }

        const string cachePrefix = "正在读取 ";
        const string cacheSuffix = " Manifest 缓存…";
        if (text.StartsWith(cachePrefix, StringComparison.Ordinal) &&
            text.EndsWith(cacheSuffix, StringComparison.Ordinal))
        {
            var value = text[cachePrefix.Length..(text.Length - cacheSuffix.Length)];
            return $"Reading cached {value} manifests…";
        }

        return TranslatePrefix(text, "已替换 ", "Replaced ")
            ?? TranslatePrefix(text, "已更新 ", "Updated ")
            ?? TranslatePrefix(text, "已删除 ", "Deleted ")
            ?? text;
    }

    private void Apply()
    {
        var strings = Language == AppLanguage.English ? English : Chinese;
        foreach (var (key, value) in strings)
        {
            _application.Resources[key] = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool ResourcesUseEnglish =>
        string.Equals(
            _application.Resources["L.Main.AutoDetect"] as string,
            English["L.Main.AutoDetect"],
            StringComparison.Ordinal);

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
            ["L.Main.CompactMode"] = "精简版",
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
            ["L.Settings.StartCompact"] = "启动时使用精简窗口",
            ["L.Settings.ExitOnClose"] = "关闭窗口时退出程序",
            ["L.Settings.RunOnboarding"] = "重新运行设置向导",
            ["L.Settings.Logs"] = "日志",
            ["L.Settings.LogRetention"] = "自动保留最近的日志",
            ["L.Settings.SevenDays"] = "7 天",
            ["L.Settings.ThirtyDays"] = "30 天",
            ["L.Settings.OpenLogs"] = "打开目录",
            ["L.Common.Save"] = "保存",
            ["L.Common.Close"] = "关闭",
            ["L.Common.Cancel"] = "取消",
            ["L.Common.OK"] = "知道了",
            ["L.Common.Refresh"] = "刷新",
            ["L.Common.OpenFolder"] = "打开目录",
            ["L.Common.ChangeLocation"] = "更改位置",
            ["L.Common.RestoreDefault"] = "恢复默认位置",
            ["L.Common.Browse"] = "浏览",
            ["L.Tray.ShowFull"] = "打开完整版",
            ["L.Tray.ShowCompact"] = "打开精简版",
            ["L.Tray.Exit"] = "退出",
            ["L.Compact.FullMode"] = "完整版",
            ["L.Onboarding.Title"] = "首次设置",
            ["L.Onboarding.Appearance"] = "界面",
            ["L.Onboarding.GameDirectory"] = "游戏目录",
            ["L.Onboarding.Behavior"] = "窗口行为",
            ["L.Onboarding.StepFormat"] = "第 {0} 步，共 3 步",
            ["L.Onboarding.Path"] = "绝区零游戏根目录",
            ["L.Onboarding.Detect"] = "自动检测",
            ["L.Onboarding.Valid"] = "游戏目录有效",
            ["L.Onboarding.Invalid"] = "请选择包含 ZenlessZoneZero.exe 的有效游戏目录",
            ["L.Onboarding.StartMode"] = "启动窗口",
            ["L.Onboarding.FullMode"] = "完整版",
            ["L.Onboarding.CompactMode"] = "精简版",
            ["L.Onboarding.CloseBehavior"] = "关闭窗口",
            ["L.Onboarding.CloseToTray"] = "隐藏到系统托盘",
            ["L.Onboarding.ExitOnClose"] = "退出程序",
            ["L.Onboarding.Back"] = "上一步",
            ["L.Onboarding.Next"] = "下一步",
            ["L.Onboarding.Finish"] = "完成设置",
            ["L.BackupLocation.Title"] = "备份目录",
            ["L.BackupLocation.CurrentDirectory"] = "当前备份目录",
            ["L.Cache.Title"] = "缓存管理",
            ["L.Cache.CurrentDirectory"] = "当前缓存目录",
            ["L.Cache.CurrentGame"] = "当前游戏缓存",
            ["L.Cache.OldVersions"] = "旧版本缓存",
            ["L.Cache.CleanOldVersions"] = "清理旧版本缓存",
            ["L.Backup.Title"] = "备份历史",
            ["L.Backup.Time"] = "时间",
            ["L.Backup.Source"] = "来源",
            ["L.Backup.Target"] = "目标",
            ["L.Backup.Result"] = "结果",
            ["L.Backup.Restored"] = "恢复时间",
            ["L.Backup.Directory"] = "目录",
            ["L.Backup.RestoreLatest"] = "恢复上次状态",
            ["L.Backup.RestoreSelected"] = "恢复选中备份",
            ["L.Backup.DeleteSelected"] = "删除选中备份",
            ["L.GameDirectory.Title"] = "选择游戏目录",
            ["L.GameDirectory.UseDirectory"] = "使用此目录",
            ["L.Resources.Title"] = "客户端差异包管理",
            ["L.Resources.PackageUsage"] = "差异包占用",
            ["L.Resources.SavedVersions"] = "保存版本",
            ["L.Resources.ManifestCache"] = "清单缓存",
            ["L.Resources.ManifestCacheTip"] = "Sophon Manifest 元数据缓存",
            ["L.Resources.RefreshTip"] = "重新扫描本地差异包与清单缓存",
            ["L.Resources.UpdateManifest"] = "更新 Manifest",
            ["L.Resources.UpdateManifestTip"] = "从 Sophon 重新下载当前游戏版本的国际服与国服清单；不会下载游戏文件",
            ["L.Resources.BrowseManifest"] = "Manifest 浏览",
            ["L.Resources.BrowseManifestTip"] = "浏览已缓存的国际服/国服 Manifest，并按资源范围与路径筛选",
            ["L.Resources.Preview"] = "差异包预览",
            ["L.Resources.PreviewTip"] = "查看选中成品差异包的文件、大小和完整性清单",
            ["L.Resources.Verify"] = "校验",
            ["L.Resources.VerifyTip"] = "按差异包清单重新校验所有文件",
            ["L.Resources.UpdatePackage"] = "更新差异包",
            ["L.Resources.UpdatePackageTip"] = "重新分析选中方向并下载新增或变化的文件；已有文件与断点会复用",
            ["L.Resources.Version"] = "版本",
            ["L.Resources.TargetClient"] = "目标客户端",
            ["L.Resources.State"] = "状态",
            ["L.Resources.Size"] = "大小",
            ["L.Resources.Content"] = "内容",
            ["L.Resources.OpenDirectory"] = "打开差异包目录",
            ["L.Resources.DeleteSelected"] = "删除选中差异包",
            ["L.ManifestBrowser.Title"] = "Manifest 资源浏览器",
            ["L.ManifestBrowser.Scope"] = "资源范围",
            ["L.ManifestBrowser.Direction"] = "目标方向",
            ["L.ManifestBrowser.Search"] = "路径搜索",
            ["L.ManifestBrowser.SearchTip"] = "输入文件名或相对路径；不区分大小写",
            ["L.ManifestBrowser.Path"] = "路径",
            ["L.ManifestBrowser.DataType"] = "数据类型",
            ["L.ManifestBrowser.ChangeState"] = "差异状态",
            ["L.Preview.Title"] = "客户端差异包清单预览",
            ["L.Preview.Files"] = "文件",
            ["L.Preview.Usage"] = "占用",
            ["L.SwitchConfirm.Title"] = "确认切换服务器",
            ["L.SwitchConfirm.CurrentServer"] = "当前服务器",
            ["L.SwitchConfirm.TargetServer"] = "目标服务器",
            ["L.SwitchConfirm.GameVersion"] = "游戏版本",
            ["L.SwitchConfirm.FileOperations"] = "文件操作",
            ["L.SwitchConfirm.RollbackBackup"] = "回滚备份",
            ["L.SwitchConfirm.Confirm"] = "确认切换",
            ["L.SwitchConfirm.FileOperationFormat"] = "替换 {0} 个文件 · 删除 {1} 个文件",
            ["L.Download.Title"] = "获取客户端差异包",
            ["L.Download.Files"] = "差异包文件",
            ["L.Download.Maximum"] = "下载上限",
            ["L.Download.Integrity"] = "完整性",
            ["L.Download.AutomaticVerification"] = "自动校验",
            ["L.Download.Start"] = "开始下载",
            ["L.Download.Cancel"] = "取消下载",
            ["L.Download.Retry"] = "重试"
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
            ["L.Main.CompactMode"] = "Compact window",
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
            ["L.Settings.StartCompact"] = "Use the compact window at startup",
            ["L.Settings.ExitOnClose"] = "Exit when closing a window",
            ["L.Settings.RunOnboarding"] = "Run setup guide again",
            ["L.Settings.Logs"] = "Logs",
            ["L.Settings.LogRetention"] = "Automatically retain recent logs",
            ["L.Settings.SevenDays"] = "7 days",
            ["L.Settings.ThirtyDays"] = "30 days",
            ["L.Settings.OpenLogs"] = "Open folder",
            ["L.Common.Save"] = "Save",
            ["L.Common.Close"] = "Close",
            ["L.Common.Cancel"] = "Cancel",
            ["L.Common.OK"] = "OK",
            ["L.Common.Refresh"] = "Refresh",
            ["L.Common.OpenFolder"] = "Open folder",
            ["L.Common.ChangeLocation"] = "Change location",
            ["L.Common.RestoreDefault"] = "Restore default",
            ["L.Common.Browse"] = "Browse",
            ["L.Tray.ShowFull"] = "Open full window",
            ["L.Tray.ShowCompact"] = "Open compact window",
            ["L.Tray.Exit"] = "Exit",
            ["L.Compact.FullMode"] = "Full window",
            ["L.Onboarding.Title"] = "Initial setup",
            ["L.Onboarding.Appearance"] = "Appearance",
            ["L.Onboarding.GameDirectory"] = "Game directory",
            ["L.Onboarding.Behavior"] = "Window behavior",
            ["L.Onboarding.StepFormat"] = "Step {0} of 3",
            ["L.Onboarding.Path"] = "Zenless Zone Zero game root",
            ["L.Onboarding.Detect"] = "Auto detect",
            ["L.Onboarding.Valid"] = "Game directory is valid",
            ["L.Onboarding.Invalid"] = "Select a valid game directory containing ZenlessZoneZero.exe",
            ["L.Onboarding.StartMode"] = "Startup window",
            ["L.Onboarding.FullMode"] = "Full window",
            ["L.Onboarding.CompactMode"] = "Compact window",
            ["L.Onboarding.CloseBehavior"] = "Close window",
            ["L.Onboarding.CloseToTray"] = "Hide to system tray",
            ["L.Onboarding.ExitOnClose"] = "Exit the application",
            ["L.Onboarding.Back"] = "Back",
            ["L.Onboarding.Next"] = "Next",
            ["L.Onboarding.Finish"] = "Finish setup",
            ["L.BackupLocation.Title"] = "Backup location",
            ["L.BackupLocation.CurrentDirectory"] = "Current backup location",
            ["L.Cache.Title"] = "Cache management",
            ["L.Cache.CurrentDirectory"] = "Current cache location",
            ["L.Cache.CurrentGame"] = "Current game cache",
            ["L.Cache.OldVersions"] = "Old-version cache",
            ["L.Cache.CleanOldVersions"] = "Clean old-version cache",
            ["L.Backup.Title"] = "Backup history",
            ["L.Backup.Time"] = "Time",
            ["L.Backup.Source"] = "Source",
            ["L.Backup.Target"] = "Target",
            ["L.Backup.Result"] = "Result",
            ["L.Backup.Restored"] = "Restored at",
            ["L.Backup.Directory"] = "Directory",
            ["L.Backup.RestoreLatest"] = "Restore last state",
            ["L.Backup.RestoreSelected"] = "Restore selected",
            ["L.Backup.DeleteSelected"] = "Delete selected",
            ["L.GameDirectory.Title"] = "Select game directory",
            ["L.GameDirectory.UseDirectory"] = "Use this directory",
            ["L.Resources.Title"] = "Client difference packages",
            ["L.Resources.PackageUsage"] = "Package usage",
            ["L.Resources.SavedVersions"] = "Saved versions",
            ["L.Resources.ManifestCache"] = "Manifest cache",
            ["L.Resources.ManifestCacheTip"] = "Sophon manifest metadata cache",
            ["L.Resources.RefreshTip"] = "Rescan local packages and manifest cache",
            ["L.Resources.UpdateManifest"] = "Update manifest",
            ["L.Resources.UpdateManifestTip"] = "Download the Global and CN manifests for the current game version from Sophon; no game files are downloaded",
            ["L.Resources.BrowseManifest"] = "Browse manifest",
            ["L.Resources.BrowseManifestTip"] = "Browse cached Global/CN manifests and filter by scope or path",
            ["L.Resources.Preview"] = "Preview package",
            ["L.Resources.PreviewTip"] = "View files, sizes, and integrity data in the selected completed package",
            ["L.Resources.Verify"] = "Verify",
            ["L.Resources.VerifyTip"] = "Verify all files against the package manifest",
            ["L.Resources.UpdatePackage"] = "Update package",
            ["L.Resources.UpdatePackageTip"] = "Reanalyze this direction and download changed files; existing files and checkpoints are reused",
            ["L.Resources.Version"] = "Version",
            ["L.Resources.TargetClient"] = "Target client",
            ["L.Resources.State"] = "State",
            ["L.Resources.Size"] = "Size",
            ["L.Resources.Content"] = "Contents",
            ["L.Resources.OpenDirectory"] = "Open package folder",
            ["L.Resources.DeleteSelected"] = "Delete selected package",
            ["L.ManifestBrowser.Title"] = "Manifest resource browser",
            ["L.ManifestBrowser.Scope"] = "Resource scope",
            ["L.ManifestBrowser.Direction"] = "Target direction",
            ["L.ManifestBrowser.Search"] = "Path search",
            ["L.ManifestBrowser.SearchTip"] = "Enter a file name or relative path; search is case-insensitive",
            ["L.ManifestBrowser.Path"] = "Path",
            ["L.ManifestBrowser.DataType"] = "Data type",
            ["L.ManifestBrowser.ChangeState"] = "Change",
            ["L.Preview.Title"] = "Client package manifest preview",
            ["L.Preview.Files"] = "Files",
            ["L.Preview.Usage"] = "Usage",
            ["L.SwitchConfirm.Title"] = "Confirm server switch",
            ["L.SwitchConfirm.CurrentServer"] = "Current server",
            ["L.SwitchConfirm.TargetServer"] = "Target server",
            ["L.SwitchConfirm.GameVersion"] = "Game version",
            ["L.SwitchConfirm.FileOperations"] = "File operations",
            ["L.SwitchConfirm.RollbackBackup"] = "Rollback backup",
            ["L.SwitchConfirm.Confirm"] = "Confirm switch",
            ["L.SwitchConfirm.FileOperationFormat"] = "Replace {0} files · delete {1} files",
            ["L.Download.Title"] = "Get client difference package",
            ["L.Download.Files"] = "Package files",
            ["L.Download.Maximum"] = "Maximum download",
            ["L.Download.Integrity"] = "Integrity",
            ["L.Download.AutomaticVerification"] = "Automatic verification",
            ["L.Download.Start"] = "Start download",
            ["L.Download.Cancel"] = "Cancel download",
            ["L.Download.Retry"] = "Retry"
        };

    private static readonly IReadOnlyDictionary<string, string> KnownChineseToEnglish =
        new Dictionary<string, string>
        {
            ["正在处理…"] = "Working…",
            ["正在只读扫描游戏目录与服务器状态…"] = "Scanning the game directory and server state…",
            ["正在重新检查游戏目录与服务器状态…"] = "Rechecking the game directory and server state…",
            ["正在检测绝区零游戏目录…"] = "Detecting the Zenless Zone Zero game directory…",
            ["目录检测完成"] = "Directory detection complete",
            ["正在清理旧版本缓存…"] = "Cleaning old-version caches…",
            ["缓存清理结束"] = "Cache cleanup complete",
            ["正在迁移并校验缓存，请勿退出…"] = "Migrating and verifying the cache. Do not exit…",
            ["缓存迁移结束"] = "Cache migration complete",
            ["正在迁移并校验备份，请勿退出…"] = "Migrating and verifying backups. Do not exit…",
            ["备份迁移结束"] = "Backup migration complete",
            ["正在解压并校验三服差异包，请勿退出…"] = "Extracting and verifying the client packages. Do not exit…",
            ["差异包导入结束"] = "Package import complete",
            ["Manifest 更新结束"] = "Manifest update complete",
            ["Manifest 浏览准备结束"] = "Manifest browser ready",
            ["正在校验客户端差异包…"] = "Verifying the client difference package…",
            ["差异包校验结束"] = "Package verification complete",
            ["正在重新分析跨服客户端差异…"] = "Reanalyzing cross-region client differences…",
            ["客户端差异包更新结束"] = "Client difference package update complete",
            ["正在删除选中的客户端差异包…"] = "Deleting the selected client difference package…",
            ["客户端差异包管理结束"] = "Client difference package operation complete",
            ["正在校验本地 B 服差异包…"] = "Verifying the local Bilibili package…",
            ["B 服差异包校验结束"] = "Bilibili package verification complete",
            ["正在读取 Sophon 清单并计算差异…"] = "Reading Sophon manifests and calculating differences…",
            ["客户端差异分析结束"] = "Client difference analysis complete",
            ["正在校验本地版本差异包…"] = "Verifying the local version package…",
            ["正在执行切换前完整性检查…"] = "Running pre-switch integrity checks…",
            ["准备执行切换…"] = "Preparing the server switch…",
            ["操作结束，正在重新检查…"] = "Operation complete. Rechecking…",
            ["操作结束"] = "Operation complete",
            ["正在保存来源服 version/revision 缓存快照"] = "Saving the source server version/revision snapshot",
            ["正在备份受影响文件"] = "Backing up affected files",
            ["正在复制差异文件到应用临时目录"] = "Copying difference files to the staging directory",
            ["正在交换国服/国际服 Blocks 缓存"] = "Swapping CN/Global Blocks caches",
            ["正在保存来源服 Blocks，并准备目标服首次初始化"] = "Saving source Blocks and preparing the target cache",
            ["正在替换目标文件"] = "Replacing target files",
            ["正在删除清单指定文件"] = "Deleting manifest-selected files",
            ["正在恢复目标服 version/revision 缓存快照"] = "Restoring the target version/revision snapshot",
            ["正在执行最终数量与文件状态校验"] = "Running final file and count verification",
            ["切换成功"] = "Switch complete",
            ["操作失败，正在回滚"] = "Operation failed. Rolling back…"
        };

    private static string? TranslatePrefix(string text, string chinesePrefix, string englishPrefix) =>
        text.StartsWith(chinesePrefix, StringComparison.Ordinal)
            ? englishPrefix + text[chinesePrefix.Length..]
            : null;
}
