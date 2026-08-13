using System.Text.Json.Serialization;

namespace ZZZSwitch.Core.Models;

public static class ProfileIds
{
    public const string Global = "global";
    public const string CnOfficial = "cn_official";
    public const string Bilibili = "bilibili";

    public static readonly string[] All = [Global, CnOfficial, Bilibili];

    // B 服使用国服的游戏资源与 Blocks，仅额外叠加哔哩哔哩 SDK/登录窗。
    // 缓存和 version/revision 快照必须归一到国服，避免复制出一套伪独立缓存。
    public static readonly string[] HotUpdateProfiles = [Global, CnOfficial];

    public static string ToResourceProfile(string profile) => profile switch
    {
        Bilibili => CnOfficial,
        _ => profile
    };
}

public sealed class ProfileDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string PackageDirectoryName { get; init; }
    public bool Enabled { get; init; } = true;
    public string? DisabledReason { get; init; }
    public List<FileSignature> KeyFiles { get; init; } = [];
}

public sealed class FileSignature
{
    public required string Path { get; init; }
    public long Length { get; init; }
    public string? Sha256 { get; init; }
}

public sealed class TransitionManifest
{
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public required string GameVersion { get; init; }
    public bool Enabled { get; init; } = true;
    public string? DisabledReason { get; init; }
    public int ExpectedReplaceCount { get; init; }
    public int ExpectedDeleteCount { get; init; }
    public List<ReplaceFileEntry> ReplaceFiles { get; init; } = [];
    public List<IniFilePatch> IniPatches { get; init; } = [];
    public List<DeleteFileEntry> DeleteFiles { get; init; } = [];
    public List<DeleteFileEntry> OptionalDeleteFiles { get; init; } = [];
    public string? Notes { get; init; }

    [JsonIgnore]
    public int PlannedReplaceCount => ReplaceFiles.Count + IniPatches.Count;
}

public sealed class ReplaceFileEntry
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public string? SourcePackageDirectoryName { get; init; }
    public long? Length { get; init; }
    public string? Sha256 { get; init; }
}

public sealed class IniFilePatch
{
    public required string Target { get; init; }
    public required string Section { get; init; }
    public Dictionary<string, string> Values { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeleteFileEntry
{
    public required string Target { get; init; }
}

public sealed class AppState
{
    public string? GamePath { get; set; }
    public string? GameVersion { get; set; }
    public string? CurrentProfile { get; set; }
    public DateTimeOffset? LastSuccessfulSwitch { get; set; }
    public string? LastOperationId { get; set; }
    public int LastReplaceCount { get; set; }
    public int LastDeleteCount { get; set; }
    public string? LastBackupPath { get; set; }
}

public sealed class BackupRecord
{
    public required string OperationId { get; init; }
    public DateTimeOffset OperationTime { get; init; }
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public required string GameVersion { get; init; }
    public required string GamePath { get; init; }
    public List<string> BackedUpFiles { get; init; } = [];
    public Dictionary<string, string> BackedUpFileSha256 { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> OriginallyMissingFiles { get; init; } = [];
    public List<string> FilesPlannedForDeletion { get; init; } = [];
    public int ReplaceCount { get; set; }
    public int DeleteCount { get; set; }
    public string? SourceSnapshotPath { get; set; }
    public string? TargetSnapshotPath { get; set; }
    public int CacheRestoreCount { get; set; }
    public string OperationResult { get; set; } = "pending";
    public string RollbackResult { get; set; } = "not_required";
    public DateTimeOffset? RestoredAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileTransactionStage
{
    Prepared,
    BlocksTransitioned,
    FilesApplied,
    MetadataRestored
}

public sealed class FileTransactionJournal
{
    public required string OperationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string BackupPath { get; init; }
    public required string GamePath { get; init; }
    public required string GameVersion { get; init; }
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public FileTransactionStage Stage { get; set; }
}

public sealed class ProfileSnapshotManifest
{
    public required string SnapshotId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string Profile { get; init; }
    public required string GameVersion { get; init; }
    public required string GamePath { get; init; }
    public required string SnapshotPath { get; init; }
    public List<SnapshotFileRecord> Files { get; init; } = [];
}

public sealed class SnapshotFileRecord
{
    public required string RelativePath { get; init; }
    public long Length { get; init; }
    public required string Sha256 { get; init; }
}

public sealed class HotUpdateCacheManifest
{
    public required string CacheId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string Profile { get; init; }
    public required string GameVersion { get; init; }
    public required string GamePath { get; init; }
    public required string StoredBlocksPath { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public required string InventorySha256 { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectedProfile
{
    Global,
    CnOfficial,
    Bilibili,
    Mixed,
    Unknown
}

public static class DetectedProfileExtensions
{
    public static string? ToProfileId(this DetectedProfile profile) => profile switch
    {
        DetectedProfile.Global => ProfileIds.Global,
        DetectedProfile.CnOfficial => ProfileIds.CnOfficial,
        DetectedProfile.Bilibili => ProfileIds.Bilibili,
        _ => null
    };

    public static string ToDisplayName(this DetectedProfile profile) => profile switch
    {
        DetectedProfile.Global => "绝区零国际服",
        DetectedProfile.CnOfficial => "绝区零国服",
        DetectedProfile.Bilibili => "绝区零B服",
        DetectedProfile.Mixed => "混合状态",
        _ => "未知状态"
    };
}
