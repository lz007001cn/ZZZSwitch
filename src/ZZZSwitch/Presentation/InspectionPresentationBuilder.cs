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
        bool readOnlyBanner)
    {
        var activeProfile = report.Detection.Profile.ToProfileId();
        var cacheIssues = BuildCacheIssues(cacheStatuses);
        var displayedIssues = report.Issues.Concat(cacheIssues).ToArray();
        var errors = displayedIssues.Count(x => x.Severity == IssueSeverity.Error);
        var warnings = displayedIssues.Count(x => x.Severity == IssueSeverity.Warning);
        var canSwitch = report.CanSwitch && errors == 0;

        var text = new StringBuilder();
        if (readOnlyBanner)
        {
            text.AppendLine("[仅检查模式] 本次检查未创建备份、未复制/删除文件、未修改状态或游戏目录。");
            text.AppendLine();
        }

        text.AppendLine($"游戏目录：{report.Game.GamePath}");
        text.AppendLine($"目录有效：{(report.Game.IsValid ? "是" : "否")}");
        text.AppendLine($"游戏版本：{report.Game.GameVersion ?? "未知"}");
        text.AppendLine($"当前识别服：{report.Detection.Profile.ToDisplayName()}");
        if (!string.IsNullOrWhiteSpace(report.Detection.StateHint))
        {
            text.AppendLine($"状态记录提示：{report.Detection.StateHint}");
        }

        if (report.Storage is not null)
        {
            text.AppendLine();
            text.AppendLine("ZZZSwitch 存储目录：");
            text.AppendLine($"  根目录：{DirectoryStatus(report.Storage.RootExists)}");
            text.AppendLine($"      {report.Storage.RootPath}");
            text.AppendLine($"  差异包根目录：{DirectoryStatus(report.Storage.PackagesRootExists)}");
            text.AppendLine($"      {report.Storage.PackagesRootPath}");
            text.AppendLine($"  当前版本目录：{DirectoryStatus(report.Storage.PackageVersionExists)}");
            text.AppendLine($"      {report.Storage.PackageVersionPath}");
            text.AppendLine($"  缓存仓库：{DirectoryStatus(report.Storage.CacheRootExists, missingIsNormal: true)}");
            text.AppendLine($"      {report.Storage.CacheRootPath}");
        }

        text.AppendLine();
        text.AppendLine("差异包：");
        foreach (var package in report.Packages)
        {
            text.AppendLine($"  [{(package.IsAvailable ? "可用" : "不可用")}] {package.DisplayName}：{package.Detail}");
            text.AppendLine($"      {package.Path}");
            if (report.Game.GameVersion is not null &&
                ProfileIds.All.Contains(package.ProfileId, StringComparer.Ordinal))
            {
                var snapshot = _snapshots.FindLatestValid(
                    ProfileIds.ToResourceProfile(package.ProfileId),
                    report.Game.GameVersion,
                    report.Game.GamePath);
                text.AppendLine(
                    $"      version/revision 缓存快照：{(snapshot is null ? "无" : $"有效（{snapshot.Files.Count} 个文件，{snapshot.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}）")}");
            }
        }

        text.AppendLine();
        text.AppendLine("双服 Blocks 缓存：");
        if (cacheStatuses.Count == 0)
        {
            text.AppendLine("  无法检查：游戏版本未知。");
        }
        else
        {
            foreach (var cache in cacheStatuses)
            {
                text.AppendLine(
                    $"  {DisplayFormatting.ShortProfileName(cache.Profile)}：{cache.Detail}" +
                    $"{(cache.FileCount > 0 ? $"，{cache.FileCount} 个文件，{DisplayFormatting.FormatBytes(cache.TotalBytes)}" : string.Empty)}");
                if (!string.IsNullOrWhiteSpace(cache.Path))
                {
                    text.AppendLine($"      {cache.Path}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("关键文件匹配：");
        foreach (var match in report.Detection.Matches)
        {
            text.AppendLine(
                $"  {match.ProfileId}: {match.MatchingFiles}/{match.TotalFiles}，{(match.IsExact ? "完整匹配" : "不匹配")}");
        }

        if (report.RunningProcesses.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"相关进程：{string.Join("、", report.RunningProcesses)}");
        }

        text.AppendLine();
        text.AppendLine("检查项：");
        if (displayedIssues.Length == 0)
        {
            text.AppendLine("  无错误或警告。可用方向仍会在执行前再次完整预检。");
        }
        else
        {
            foreach (var issue in displayedIssues)
            {
                text.AppendLine(
                    $"  [{SeverityName(issue.Severity)}] {issue.Message}{(issue.Path is null ? string.Empty : $" — {issue.Path}")}");
            }
        }

        return new(
            activeProfile,
            activeProfile is null
                ? report.Detection.Profile.ToDisplayName()
                : DisplayFormatting.ShortProfileName(activeProfile),
            report.Game.GameVersion ?? "未知",
            string.Join("    ", report.Packages.Select(x =>
                $"{DisplayFormatting.ShortProfileName(x.ProfileId)}  {(x.IsAvailable ? "可用" : "不可用")} · {x.FileCount} 个文件")),
            cacheStatuses.Count == 0
                ? "游戏版本未知，无法检查缓存"
                : string.Join("    ", cacheStatuses.Select(x =>
                    $"{DisplayFormatting.ShortProfileName(x.Profile)}：{CacheStatusSummary(x)}")),
            canSwitch ? "需要注意" : "无法切换",
            errors > 0
                ? $"{errors} 个错误 · {warnings} 个警告"
                : warnings > 0 ? $"{warnings} 个警告" : "0 个问题",
            text.ToString(),
            !canSwitch || warnings > 0,
            report.Game.IsValid && report.Game.GameVersion is not null,
            activeProfile is not null && report.Game.GameVersion is not null,
            readOnlyBanner || errors > 0);
    }

    private static string SeverityName(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "错误",
        IssueSeverity.Warning => "警告",
        _ => "信息"
    };

    private static string DirectoryStatus(bool exists, bool missingIsNormal = false) =>
        exists
            ? "正常"
            : missingIsNormal
                ? "未建立（首次使用时正常）"
                : "未检测到";

    private static ValidationIssue[] BuildCacheIssues(
        IReadOnlyList<HotUpdateCacheStatus> statuses) =>
        statuses.Select(status =>
            !status.IsInitialized
                ? new ValidationIssue(
                    IssueSeverity.Information,
                    "cache.not-initialized",
                    $"{DisplayFormatting.ShortProfileName(status.Profile)}热更新缓存尚未初始化。")
                : !status.IsAvailable
                    ? new ValidationIssue(
                        IssueSeverity.Error,
                        "cache.repository.invalid",
                        $"{DisplayFormatting.ShortProfileName(status.Profile)}缓存索引或仓库不可用：{status.Detail}",
                        status.Path)
                    : new ValidationIssue(
                        IssueSeverity.Information,
                        "cache.available",
                        status.Detail,
                        status.Path))
            .Where(x => x.Code != "cache.available")
            .ToArray();

    private static string CacheStatusSummary(HotUpdateCacheStatus status)
    {
        if (!status.IsInitialized)
        {
            return status.IsActive ? "活动中 · 尚未初始化" : "尚未初始化";
        }

        if (!status.IsAvailable)
        {
            return status.IsActive ? "活动中 · 索引需重建" : "仓库不可用";
        }

        return status.NeedsRefresh
            ? $"活动中 · {DisplayFormatting.FormatBytes(status.TotalBytes)} · 有新资源"
            : $"{(status.IsActive ? "活动中" : "可用")} · {DisplayFormatting.FormatBytes(status.TotalBytes)}";
    }
}
