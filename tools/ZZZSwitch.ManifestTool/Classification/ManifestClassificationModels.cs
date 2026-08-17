using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool.Classification;

public enum ManifestChangeType
{
    Modified,
    Added,
    Removed
}

public enum ManifestFileClass
{
    BaseClient,
    BaseResource,
    RuntimeHotUpdate,
    StateMetadata,
    NeedsObservation
}

public enum ClassificationConfidence
{
    High,
    Medium,
    Low
}

public sealed record ClassifiedManifestFile(
    string Path,
    ManifestChangeType ChangeType,
    ManifestFileClass FileClass,
    ClassificationConfidence Confidence,
    string RecommendedAction,
    string RuleId,
    string Reason,
    long? SourceSize,
    long? TargetSize,
    string? SourceMd5,
    string? TargetMd5);

public sealed record ManifestClassificationSummary(
    int Total,
    int BaseClient,
    int BaseResource,
    int RuntimeHotUpdate,
    int StateMetadata,
    int NeedsObservation);

public sealed record ManifestClassificationReport(
    string Game,
    string Version,
    SophonRegion SourceRegion,
    SophonRegion TargetRegion,
    ManifestClassificationSummary Summary,
    IReadOnlyList<ClassifiedManifestFile> Files);
