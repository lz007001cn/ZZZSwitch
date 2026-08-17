using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Classification;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.Core.Models;

public sealed class OnlineDifferencePlan
{
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public required string GameVersion { get; init; }
    public required SophonRegion SourceRegion { get; init; }
    public required SophonRegion TargetRegion { get; init; }
    public required string TargetManifestId { get; init; }
    public required ManifestCategory TargetCategory { get; init; }
    public required IReadOnlyList<ManifestEntry> DownloadFiles { get; init; }
    public required IReadOnlyList<string> DeleteFiles { get; init; }
    public int ExcludedStreamingBlocksCount { get; init; }
    public long ExcludedStreamingBlocksBytes { get; init; }
    public int ExcludedObservationCount { get; init; }
    public int ExcludedDeletionReviewCount { get; init; }
    public long DownloadBytes => DownloadFiles.Aggregate(0L, (sum, item) => checked(sum + item.Size));
}

public sealed class OnlineDifferenceProgress
{
    public required string CurrentFile { get; init; }
    public long CompletedBytes { get; init; }
    public long TotalBytes { get; init; }
    public int CompletedFiles { get; init; }
    public int TotalFiles { get; init; }
    public int CurrentFileChunksCompleted { get; init; }
    public int CurrentFileChunksTotal { get; init; }
    public bool ReusingExistingFile { get; init; }
    public long CurrentChunkBytesDownloaded { get; init; }
    public long CurrentChunkBytesTotal { get; init; }
    public bool ReusingChunkCache { get; init; }
    public bool VerifyingExistingFile { get; init; }
    public int DownloadAttempt { get; init; } = 1;
    public int MaximumDownloadAttempts { get; init; } = 1;
}

public sealed class OnlineDifferenceMaterialization
{
    public required string PackageRoot { get; init; }
    public required string PackageDirectory { get; init; }
    public required TransitionManifest Manifest { get; init; }
    public int DownloadedFiles { get; init; }
    public int ReusedFiles { get; init; }
    public bool ReusedReadyPackage { get; init; }
}

public enum OnlineDifferencePackageState
{
    Ready,
    Incomplete,
    Invalid
}

public sealed class OnlineDifferencePackageInfo
{
    public required string GameVersion { get; init; }
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public required string ManifestId { get; init; }
    public required string WorkspacePath { get; init; }
    public required OnlineDifferencePackageState State { get; init; }
    public int FileCount { get; init; }
    public long ContentBytes { get; init; }
    public int CheckpointCount { get; init; }
    public long CheckpointBytes { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
    public string? Problem { get; init; }
    public long TotalBytes => checked(ContentBytes + CheckpointBytes);
}

public sealed class OnlineDifferenceInventory
{
    public required IReadOnlyList<OnlineDifferencePackageInfo> Packages { get; init; }
    public int ManifestCacheFileCount { get; init; }
    public long ManifestCacheBytes { get; init; }
    public long PackageBytes => Packages.Aggregate(
        0L, (sum, package) => checked(sum + package.TotalBytes));
}

public sealed record OnlineManifestSummary(
    SophonRegion Region,
    string ManifestId,
    int FileCount,
    long ContentBytes,
    DateTimeOffset CreatedAtUtc);

public sealed record OnlineManifestRefreshResult(
    string GameVersion,
    OnlineManifestSummary Global,
    OnlineManifestSummary Cn,
    int ModifiedFiles,
    int AddedFiles,
    int RemovedFiles);

public sealed record OnlineDifferencePreviewFile(
    string Path,
    long? Length,
    string Integrity,
    string State);

public sealed class OnlineDifferencePackagePreview
{
    public required OnlineDifferencePackageInfo Package { get; init; }
    public required IReadOnlyList<OnlineDifferencePreviewFile> Files { get; init; }
    public required IReadOnlyList<string> DeleteFiles { get; init; }
    public string? Notes { get; init; }
}

public sealed record OnlineManifestBrowseFile(
    string Path,
    long Size,
    string Md5,
    ManifestChangeType? ChangeType,
    ManifestFileClass? FileClass,
    bool IsClientDifference,
    bool IsStoryMedia,
    bool IsAudio,
    bool IsStreamingBlocks,
    bool IsStateMetadata);

public sealed class OnlineManifestDirection
{
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public required OnlineManifestSummary TargetManifest { get; init; }
    public required IReadOnlyList<OnlineManifestBrowseFile> Files { get; init; }
}

public sealed class OnlineManifestBrowserData
{
    public required string GameVersion { get; init; }
    public required OnlineManifestDirection GlobalToCn { get; init; }
    public required OnlineManifestDirection CnToGlobal { get; init; }
}
