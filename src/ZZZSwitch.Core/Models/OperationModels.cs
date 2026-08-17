namespace ZZZSwitch.Core.Models;

public enum IssueSeverity
{
    Information,
    Warning,
    Error
}

public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, string? Path = null);

public sealed class GameDirectoryResult
{
    public required string GamePath { get; init; }
    public bool IsValid { get; init; }
    public string? GameVersion { get; init; }
    public List<ValidationIssue> Issues { get; init; } = [];
}

public sealed class ProfileMatch
{
    public required string ProfileId { get; init; }
    public int MatchingFiles { get; init; }
    public int TotalFiles { get; init; }
    public bool IsExact { get; init; }
    public List<string> Mismatches { get; init; } = [];
}

public sealed class DetectionResult
{
    public DetectedProfile Profile { get; init; }
    public string? StateHint { get; init; }
    public List<ProfileMatch> Matches { get; init; } = [];
    public List<string> Mismatches { get; init; } = [];
}

public sealed class PackageStatus
{
    public required string ProfileId { get; init; }
    public required string DisplayName { get; init; }
    public required string Path { get; init; }
    public bool IsAvailable { get; init; }
    public int FileCount { get; init; }
    public string? Detail { get; init; }
}

public sealed class StorageLayoutStatus
{
    public required string RootPath { get; init; }
    public required string PackagesRootPath { get; init; }
    public required string PackageVersionPath { get; init; }
    public required string CacheRootPath { get; init; }
    public bool RootExists { get; init; }
    public bool PackagesRootExists { get; init; }
    public bool PackageVersionExists { get; init; }
    public bool CacheRootExists { get; init; }
    public List<string> MissingProfileDirectories { get; init; } = [];
    public bool NeedsDirectoryRepair =>
        !RootExists ||
        !PackagesRootExists ||
        !PackageVersionExists;
}

public sealed class StorageRepairResult
{
    public required StorageLayoutStatus Before { get; init; }
    public required StorageLayoutStatus After { get; init; }
    public List<string> CreatedDirectories { get; init; } = [];
}

public sealed class StateLoadResult
{
    public AppState? State { get; init; }
    public string? Warning { get; init; }
}

public sealed class ConfigurationLoadError
{
    public required string Path { get; init; }
    public required string Message { get; init; }
}

public sealed class ConfigurationLoadResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public IReadOnlyList<ConfigurationLoadError> Errors { get; init; } = [];
}

public sealed class PendingRecoveryResult
{
    public bool Found { get; init; }
    public bool Success { get; init; }
    public required string Message { get; init; }
}

public sealed class InspectionReport
{
    public required GameDirectoryResult Game { get; init; }
    public required DetectionResult Detection { get; init; }
    public StorageLayoutStatus? Storage { get; init; }
    public List<PackageStatus> Packages { get; init; } = [];
    public List<ValidationIssue> Issues { get; init; } = [];
    public List<string> RunningProcesses { get; init; } = [];
    public bool CanSwitch => Game.IsValid && Detection.Profile.ToProfileId() is not null &&
                             Issues.All(x => x.Severity != IssueSeverity.Error);
}

public sealed class SwitchPlan
{
    public required string OperationId { get; init; }
    public required string GamePath { get; init; }
    public required string PackageRoot { get; init; }
    public required string PackageDirectory { get; init; }
    public required TransitionManifest Manifest { get; init; }
    public required string BackupPath { get; init; }
    public ProfileSnapshotManifest? TargetSnapshot { get; init; }
    public HotUpdateTransitionPlan? HotUpdateTransition { get; init; }
    public string FileSourceDescription { get; init; } = "本地差异包";
    public List<ValidationIssue> Issues { get; init; } = [];
    public bool CanExecute => Issues.All(x => x.Severity != IssueSeverity.Error);
}

public enum HotUpdateTransitionMode
{
    InitializeTarget,
    Swap
}

public sealed class HotUpdateTransitionPlan
{
    public required HotUpdateTransitionMode Mode { get; init; }
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public required string GameVersion { get; init; }
    public required string GamePath { get; init; }
    public required HotUpdateCacheManifest SourceManifest { get; init; }
    public HotUpdateCacheManifest? TargetManifest { get; init; }
    public string? SeedDirectory { get; init; }
}

public sealed class HotUpdateCacheStatus
{
    public required string Profile { get; init; }
    public bool IsInitialized { get; init; }
    public bool IsActive { get; init; }
    public bool IsAvailable { get; init; }
    public bool NeedsRefresh { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? Path { get; init; }
    public required string Detail { get; init; }
}

public sealed class OperationProgress
{
    public required string Step { get; init; }
    public int PlannedReplace { get; init; }
    public int SuccessfulReplace { get; init; }
    public int FailedReplace { get; init; }
    public int PlannedDelete { get; init; }
    public int SuccessfulDelete { get; init; }
    public int FailedDelete { get; init; }
    public int PlannedCacheRestore { get; init; }
    public int SuccessfulCacheRestore { get; init; }
    public int FailedCacheRestore { get; init; }
    public bool IsRollingBack { get; init; }
}

public sealed class OperationResult
{
    public required string OperationId { get; init; }
    public bool Success { get; init; }
    public bool WasNoOp { get; init; }
    public bool RolledBack { get; init; }
    public int PlannedReplace { get; init; }
    public int SuccessfulReplace { get; init; }
    public int FailedReplace { get; init; }
    public int PlannedDelete { get; init; }
    public int SuccessfulDelete { get; init; }
    public int FailedDelete { get; init; }
    public int PlannedCacheRestore { get; init; }
    public int SuccessfulCacheRestore { get; init; }
    public int FailedCacheRestore { get; init; }
    public string? BackupPath { get; init; }
    public string? Error { get; init; }
}

public sealed class OperationLogEntry
{
    public DateTimeOffset Time { get; init; }
    public required string OperationId { get; init; }
    public required string GamePath { get; init; }
    public required string GameVersion { get; init; }
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public int PlannedReplace { get; init; }
    public int SuccessfulReplace { get; init; }
    public int PlannedDelete { get; init; }
    public int SuccessfulDelete { get; init; }
    public int PlannedCacheRestore { get; init; }
    public int SuccessfulCacheRestore { get; init; }
    public List<string> FailedFiles { get; init; } = [];
    public string? RollbackResult { get; init; }
    public string? Error { get; init; }
}
