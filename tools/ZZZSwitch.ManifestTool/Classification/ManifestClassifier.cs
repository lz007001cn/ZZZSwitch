using System.Text.RegularExpressions;
using ZZZSwitch.ManifestTool.Diff;

namespace ZZZSwitch.ManifestTool.Classification;

public sealed partial class ManifestClassifier
{
    private const string PersistentBlocks = @"ZenlessZoneZero_Data\Persistent\Blocks\";
    private const string StreamingBlocks = @"ZenlessZoneZero_Data\StreamingAssets\Blocks\";
    private const string PersistentRoot = @"ZenlessZoneZero_Data\Persistent\";
    private const string StreamingRoot = @"ZenlessZoneZero_Data\StreamingAssets\";
    private const string PluginsRoot = @"ZenlessZoneZero_Data\Plugins\";
    private const string Il2CppRoot = @"ZenlessZoneZero_Data\il2cpp_data\";

    public ManifestClassificationReport Classify(ManifestDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var files = diff.Modified.Select(file => Classify(file, ManifestChangeType.Modified))
            .Concat(diff.Added.Select(file => Classify(file, ManifestChangeType.Added)))
            .Concat(diff.Removed.Select(file => Classify(file, ManifestChangeType.Removed)))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        return new ManifestClassificationReport(
            diff.Game,
            diff.Version,
            diff.SourceRegion,
            diff.TargetRegion,
            new ManifestClassificationSummary(
                files.Length,
                Count(files, ManifestFileClass.BaseClient),
                Count(files, ManifestFileClass.BaseResource),
                Count(files, ManifestFileClass.RuntimeHotUpdate),
                Count(files, ManifestFileClass.StateMetadata),
                Count(files, ManifestFileClass.NeedsObservation)),
            files);
    }

    private static ClassifiedManifestFile Classify(
        FileDifference difference,
        ManifestChangeType changeType)
    {
        var path = difference.Path;
        Rule rule;
        if (StartsWith(path, PersistentBlocks))
        {
            rule = new(
                ManifestFileClass.RuntimeHotUpdate,
                ClassificationConfidence.High,
                "HotUpdateCache",
                "persistent-blocks",
                "Persistent\\Blocks is the runtime cache exchanged by ZZZSwitch after the game downloads resources.");
        }
        else if (IsStateMetadata(path))
        {
            rule = new(
                ManifestFileClass.StateMetadata,
                ClassificationConfidence.High,
                "ProfileSnapshot",
                "version-revision-state",
                "Top-level version/revision state must remain aligned with the selected resource profile.");
        }
        else if (StartsWith(path, StreamingBlocks))
        {
            rule = new(
                ManifestFileClass.BaseResource,
                ClassificationConfidence.High,
                "BaseResourceSeedCandidate",
                "streaming-blocks",
                "StreamingAssets\\Blocks is shipped by the Sophon build and is distinct from Persistent\\Blocks runtime cache.");
        }
        else if (IsBaseClient(path))
        {
            rule = new(
                ManifestFileClass.BaseClient,
                ClassificationConfidence.High,
                "DifferencePackageCandidate",
                "native-or-player-runtime",
                "Executable, native plug-in, IL2CPP, or Unity player data is required before or during process startup.");
        }
        else if (StartsWith(path, StreamingRoot))
        {
            rule = new(
                ManifestFileClass.BaseResource,
                ClassificationConfidence.Medium,
                "BaseResourceCandidate",
                "streaming-assets",
                "The file is part of the build's StreamingAssets payload, but its runtime refresh behavior is not proven by path alone.");
        }
        else
        {
            rule = new(
                ManifestFileClass.NeedsObservation,
                ClassificationConfidence.Low,
                "ManualObservation",
                "unclassified",
                "Path-only evidence is insufficient; attribute writes to launcher or game processes before changing switch behavior.");
        }

        var action = changeType == ManifestChangeType.Removed
            ? "ReviewTargetDeletion"
            : rule.RecommendedAction;
        return new ClassifiedManifestFile(
            path,
            changeType,
            rule.FileClass,
            rule.Confidence,
            action,
            rule.RuleId,
            rule.Reason,
            difference.SourceSize,
            difference.TargetSize,
            difference.SourceMd5,
            difference.TargetMd5);
    }

    private static bool IsStateMetadata(string path)
    {
        if (!StartsWith(path, PersistentRoot) && !StartsWith(path, StreamingRoot))
        {
            return false;
        }

        var relativeRoot = StartsWith(path, PersistentRoot) ? PersistentRoot : StreamingRoot;
        var remainder = path[relativeRoot.Length..];
        if (remainder.Contains('\\'))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return name.Contains("version", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("revision", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "base_version_hash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBaseClient(string path)
    {
        if (StartsWith(path, PluginsRoot) || StartsWith(path, Il2CppRoot))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sys", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return name.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("resources.assets", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "app.info", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "file_category_launcher", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "pkg_version", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "version_info", StringComparison.OrdinalIgnoreCase) ||
               LevelFile().IsMatch(name);
    }

    private static bool StartsWith(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static int Count(
        IEnumerable<ClassifiedManifestFile> files,
        ManifestFileClass fileClass) => files.Count(file => file.FileClass == fileClass);

    [GeneratedRegex(@"^level\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelFile();

    private sealed record Rule(
        ManifestFileClass FileClass,
        ClassificationConfidence Confidence,
        string RecommendedAction,
        string RuleId,
        string Reason);
}
