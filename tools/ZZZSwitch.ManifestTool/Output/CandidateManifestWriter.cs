using System.Text.Json;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool.Output;

public sealed class CandidateManifestWriter
{
    private readonly JsonSerializerOptions _json;

    public CandidateManifestWriter(JsonSerializerOptions json) => _json = json;

    public async Task<string> WriteAsync(
        ManifestDiff diff,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var sourceProfile = ToProfile(diff.SourceRegion);
        var targetProfile = ToProfile(diff.TargetRegion);
        var candidate = new
        {
            generatedCandidate = true,
            requiresManualReview = true,
            enabled = false,
            disabledReason = "GENERATED CANDIDATE - REQUIRES MANUAL REVIEW",
            sourceProfile,
            targetProfile,
            gameVersion = diff.Version,
            expectedReplaceCount = diff.Modified.Count + diff.Added.Count,
            expectedDeleteCount = diff.Removed.Count,
            replaceFiles = diff.Modified.Concat(diff.Added).Select(file => new
            {
                source = file.Path,
                target = file.Path,
                length = file.TargetSize,
                sophonMd5 = file.TargetMd5,
                sha256 = (string?)null
            }),
            deleteFiles = diff.Removed.Select(file => new { target = file.Path }),
            notes = "GENERATED CANDIDATE - REQUIRES MANUAL REVIEW. " +
                    "Sophon MD5 differences are discovery hints only. Download and hash real package files " +
                    "with SHA-256 before creating a production transition manifest."
        };

        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var fileName = $"candidate-{Friendly(diff.SourceRegion)}-to-{Friendly(diff.TargetRegion)}.json";
        var path = Path.Combine(directory, fileName);
        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, candidate, _json, cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    private static string ToProfile(SophonRegion region) => region switch
    {
        SophonRegion.OS => "global",
        SophonRegion.CN => "cn_official",
        SophonRegion.Bilibili => "bilibili",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, null)
    };

    private static string Friendly(SophonRegion region) => region switch
    {
        SophonRegion.OS => "global",
        SophonRegion.CN => "cn",
        SophonRegion.Bilibili => "bilibili",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, null)
    };
}
