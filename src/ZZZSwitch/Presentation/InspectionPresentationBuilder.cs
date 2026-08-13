using System.Text;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch.Presentation;

public sealed record InspectionPresentation(
    string? ActiveProfile,
    string Profile,
    string GameVersion,
    string Packages,
    string CacheSummary,
    string OperationStatus,
    string IssueSummary,
    string Report,
    bool HasStatusIssues,
    bool CanManageCache,
    bool CanInitializeCache,
    bool ExpandDetails);

public sealed class InspectionPresentationBuilder
{
    private readonly ProfileSnapshotService _snapshots;

    public InspectionPresentationBuilder(ProfileSnapshotService snapshots) => _snapshots = snapshots;

    public InspectionPresentation Build(
        InspectionReport report,
        IReadOnlyList<HotUpdateCacheStatus> cacheStatuses,
        bool readOnlyBanner,
        AppLanguage language = AppLanguage.Chinese)
    {
        var activeProfile = report.Detection.Profile.ToProfileId();
        var cacheIssues = BuildCacheIssues(cacheStatuses, language);
        var displayedIssues = report.Issues.Concat(cacheIssues).ToArray();
        var errors = displayedIssues.Count(x => x.Severity == IssueSeverity.Error);
        var warnings = displayedIssues.Count(x => x.Severity == IssueSeverity.Warning);
        var canSwitch = report.CanSwitch && errors == 0;

        var text = new StringBuilder();
        if (readOnlyBanner)
        {
            text.AppendLine(Text(language,
                "[仅检查模式] 本次检查未创建备份、未复制/删除文件、未修改状态或游戏目录。",
                "[Inspection only] No backup was created, no files were copied or deleted, and no state or game content was changed."));
            text.AppendLine();
        }

        text.AppendLine($"{Text(language, "游戏目录", "Game directory")}：{report.Game.GamePath}");
        text.AppendLine($"{Text(language, "目录有效", "Valid directory")}：{Text(language, report.Game.IsValid ? "是" : "否", report.Game.IsValid ? "Yes" : "No")}");
        text.AppendLine($"{Text(language, "游戏版本", "Game version")}：{report.Game.GameVersion ?? Text(language, "未知", "Unknown")}");
        text.AppendLine($"{Text(language, "当前识别服", "Detected server")}：{DetectedProfileName(report.Detection.Profile, language)}");
        if (!string.IsNullOrWhiteSpace(report.Detection.StateHint))
        {
            text.AppendLine(language == AppLanguage.English
                ? "Saved-state hint: The saved state differs from the files currently detected."
                : $"状态记录提示：{report.Detection.StateHint}");
        }

        if (report.Storage is not null)
        {
            text.AppendLine();
            text.AppendLine(Text(language, "ZZZSwitch 存储目录：", "ZZZSwitch storage:"));
            text.AppendLine($"  {Text(language, "根目录", "Root")}：{DirectoryStatus(report.Storage.RootExists, language)}");
            text.AppendLine($"      {report.Storage.RootPath}");
            text.AppendLine($"  {Text(language, "差异包根目录", "Package root")}：{DirectoryStatus(report.Storage.PackagesRootExists, language)}");
            text.AppendLine($"      {report.Storage.PackagesRootPath}");
            text.AppendLine($"  {Text(language, "当前版本目录", "Current-version directory")}：{DirectoryStatus(report.Storage.PackageVersionExists, language)}");
            text.AppendLine($"      {report.Storage.PackageVersionPath}");
            text.AppendLine($"  {Text(language, "缓存仓库", "Cache repository")}：{DirectoryStatus(report.Storage.CacheRootExists, language, missingIsNormal: true)}");
            text.AppendLine($"      {report.Storage.CacheRootPath}");
        }

        text.AppendLine();
        text.AppendLine(Text(language, "差异包：", "Packages:"));
        foreach (var package in report.Packages)
        {
            text.AppendLine($"  [{Availability(package.IsAvailable, language)}] {ProfileName(package.ProfileId, language)}：{PackageDetail(package, language)}");
            text.AppendLine($"      {package.Path}");
            if (report.Game.GameVersion is not null &&
                ProfileIds.All.Contains(package.ProfileId, StringComparer.Ordinal))
            {
                var snapshot = _snapshots.FindLatestValid(
                    ProfileIds.ToResourceProfile(package.ProfileId),
                    report.Game.GameVersion,
                    report.Game.GamePath);
                text.AppendLine(
                    $"      {Text(language, "version/revision 缓存快照", "version/revision snapshot")}：" +
                    (snapshot is null
                        ? Text(language, "无", "None")
                        : Text(language,
                            $"有效（{snapshot.Files.Count} 个文件，{snapshot.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}）",
                            $"Valid ({snapshot.Files.Count} files, {snapshot.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss})")));
            }
        }

        text.AppendLine();
        text.AppendLine(Text(language, "双服 Blocks 缓存：", "Regional Blocks caches:"));
        if (cacheStatuses.Count == 0)
        {
            text.AppendLine(Text(language, "  无法检查：游戏版本未知。", "  Cannot inspect: the game version is unknown."));
        }
        else
        {
            foreach (var cache in cacheStatuses)
            {
                text.AppendLine(
                    $"  {ProfileName(cache.Profile, language)}：{CacheStatusSummary(cache, language)}" +
                    $"{(cache.FileCount > 0 ? Text(language, $"，{cache.FileCount} 个文件，", $", {cache.FileCount} files, ") + DisplayFormatting.FormatBytes(cache.TotalBytes) : string.Empty)}");
                if (!string.IsNullOrWhiteSpace(cache.Path))
                {
                    text.AppendLine($"      {cache.Path}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine(Text(language, "关键文件匹配：", "Key-file matches:"));
        foreach (var match in report.Detection.Matches)
        {
            text.AppendLine(
                $"  {match.ProfileId}: {match.MatchingFiles}/{match.TotalFiles}, {Text(language, match.IsExact ? "完整匹配" : "不匹配", match.IsExact ? "exact match" : "not matched")}");
        }

        if (report.RunningProcesses.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"{Text(language, "相关进程", "Related processes")}：{string.Join(", ", report.RunningProcesses)}");
        }

        text.AppendLine();
        text.AppendLine(Text(language, "检查项：", "Checks:"));
        if (displayedIssues.Length == 0)
        {
            text.AppendLine(Text(language,
                "  无错误或警告。可用方向仍会在执行前再次完整预检。",
                "  No errors or warnings. A full preflight check will run again before switching."));
        }
        else
        {
            foreach (var issue in displayedIssues)
            {
                text.AppendLine(
                    $"  [{SeverityName(issue.Severity, language)}] {IssueMessage(issue, language)}{(issue.Path is null ? string.Empty : $" — {issue.Path}")}");
            }
        }

        return new(
            activeProfile,
            activeProfile is null
                ? DetectedProfileName(report.Detection.Profile, language)
                : ProfileName(activeProfile, language),
            report.Game.GameVersion ?? Text(language, "未知", "Unknown"),
            string.Join("    ", report.Packages.Select(x =>
                $"{ProfileName(x.ProfileId, language)}  {Availability(x.IsAvailable, language)} · {FileCount(x.FileCount, language)}")),
            cacheStatuses.Count == 0
                ? Text(language, "游戏版本未知，无法检查缓存", "Unknown game version; caches cannot be inspected")
                : string.Join("    ", cacheStatuses.Select(x =>
                    $"{ProfileName(x.Profile, language)}: {CacheStatusSummary(x, language)}")),
            canSwitch ? Text(language, "需要注意", "Attention required") : Text(language, "无法切换", "Cannot switch"),
            errors > 0
                ? Text(language, $"{errors} 个错误 · {warnings} 个警告", $"{errors} errors · {warnings} warnings")
                : warnings > 0
                    ? Text(language, $"{warnings} 个警告", $"{warnings} warnings")
                    : Text(language, "0 个问题", "No issues"),
            text.ToString(),
            !canSwitch || warnings > 0,
            report.Game.IsValid && report.Game.GameVersion is not null,
            activeProfile is not null && report.Game.GameVersion is not null,
            readOnlyBanner || errors > 0);
    }

    private static string SeverityName(IssueSeverity severity, AppLanguage language) => severity switch
    {
        IssueSeverity.Error => Text(language, "错误", "Error"),
        IssueSeverity.Warning => Text(language, "警告", "Warning"),
        _ => Text(language, "信息", "Information")
    };

    private static string DirectoryStatus(bool exists, AppLanguage language, bool missingIsNormal = false) =>
        exists
            ? Text(language, "正常", "Available")
            : missingIsNormal
                ? Text(language, "未建立（首次使用时正常）", "Not created (normal before first use)")
                : Text(language, "未检测到", "Not found");

    private static ValidationIssue[] BuildCacheIssues(
        IReadOnlyList<HotUpdateCacheStatus> statuses,
        AppLanguage language) =>
        statuses.Select(status =>
            !status.IsInitialized
                ? new ValidationIssue(
                    IssueSeverity.Information,
                    "cache.not-initialized",
                    Text(language,
                        $"{ProfileName(status.Profile, language)}热更新缓存尚未初始化。",
                        $"The {ProfileName(status.Profile, language)} hot-update cache has not been initialized."))
                : !status.IsAvailable
                    ? new ValidationIssue(
                        IssueSeverity.Error,
                        "cache.repository.invalid",
                        Text(language,
                            $"{ProfileName(status.Profile, language)}缓存索引或仓库不可用：{status.Detail}",
                            $"The {ProfileName(status.Profile, language)} cache index or repository is unavailable."),
                        status.Path)
                    : new ValidationIssue(
                        IssueSeverity.Information,
                        "cache.available",
                        status.Detail,
                        status.Path))
            .Where(x => x.Code != "cache.available")
            .ToArray();

    private static string CacheStatusSummary(HotUpdateCacheStatus status, AppLanguage language)
    {
        if (!status.IsInitialized)
        {
            return status.IsActive
                ? Text(language, "活动中 · 尚未初始化", "Active · Not initialized")
                : Text(language, "尚未初始化", "Not initialized");
        }

        if (!status.IsAvailable)
        {
            return status.IsActive
                ? Text(language, "活动中 · 索引需重建", "Active · Index must be rebuilt")
                : Text(language, "仓库不可用", "Repository unavailable");
        }

        return status.NeedsRefresh
            ? Text(language,
                $"活动中 · {DisplayFormatting.FormatBytes(status.TotalBytes)} · 有新资源",
                $"Active · {DisplayFormatting.FormatBytes(status.TotalBytes)} · New resources")
            : $"{Text(language, status.IsActive ? "活动中" : "可用", status.IsActive ? "Active" : "Available")} · {DisplayFormatting.FormatBytes(status.TotalBytes)}";
    }

    private static string ProfileName(string profileId, AppLanguage language) => profileId switch
    {
        ProfileIds.Global => Text(language, "国际服", "Global"),
        ProfileIds.CnOfficial => Text(language, "国服", "CN Official"),
        ProfileIds.Bilibili => Text(language, "B服", "Bilibili"),
        _ => profileId
    };

    private static string DetectedProfileName(DetectedProfile profile, AppLanguage language) => profile switch
    {
        DetectedProfile.Global => Text(language, "国际服", "Global"),
        DetectedProfile.CnOfficial => Text(language, "国服", "CN Official"),
        DetectedProfile.Bilibili => Text(language, "B服", "Bilibili"),
        DetectedProfile.Mixed => Text(language, "混合状态", "Mixed state"),
        _ => Text(language, "未知状态", "Unknown state")
    };

    private static string Availability(bool available, AppLanguage language) =>
        Text(language, available ? "可用" : "不可用", available ? "Available" : "Unavailable");

    private static string FileCount(int count, AppLanguage language) =>
        Text(language, $"{count} 个文件", $"{count} files");

    private static string PackageDetail(PackageStatus package, AppLanguage language) =>
        language == AppLanguage.Chinese
            ? package.Detail ?? string.Empty
            : package.IsAvailable
                ? $"Integrity verified ({package.FileCount} files)"
                : "Missing, incomplete, or failed integrity validation";

    private static string IssueMessage(ValidationIssue issue, AppLanguage language)
    {
        if (language == AppLanguage.Chinese)
        {
            return issue.Message;
        }

        return issue.Code switch
        {
            "cache.not-initialized" or "cache.repository.invalid" => issue.Message,
            "package.unavailable" => "A differential package is unavailable or incomplete.",
            "process.running" => "A related game or launcher process is running.",
            "storage.root.missing" => "The ZZZSwitch storage root was not found.",
            "storage.packages.missing" => "The differential-package directory was not found.",
            "storage.package-version.missing" => "The package directory for the current game version was not found.",
            "storage.cache.not-created" => "The cache repository has not been created yet.",
            "storage.cache.unavailable" => "The regional cache repository is unavailable.",
            "game.directory.missing" => "The game directory does not exist.",
            "game.marker.missing" => "A required game marker file is missing.",
            "game.version.invalid" => "The game version could not be read.",
            "state.invalid" => "The saved local state is invalid and was ignored.",
            "transaction.file.pending" => "An unfinished file transaction must be recovered before switching.",
            "manifest.version.ambiguous" => "More than one enabled game version was found in the configuration.",
            "manifest.set.invalid" => "The transition-manifest set is incomplete or duplicated.",
            "manifest.replace.count" => "The replacement count does not match the transition manifest.",
            "manifest.delete.count" => "The deletion count does not match the transition manifest.",
            var code when code.StartsWith("config.", StringComparison.Ordinal) => "A built-in configuration file is invalid or unreadable.",
            var code when code.StartsWith("manifest.", StringComparison.Ordinal) => "A transition manifest is invalid.",
            var code when code.StartsWith("path.", StringComparison.Ordinal) => "A configured path failed safety validation.",
            _ => "This check did not pass. See the related path or logs for details."
        };
    }

    private static string Text(AppLanguage language, string chinese, string english) =>
        language == AppLanguage.English ? english : chinese;
}
