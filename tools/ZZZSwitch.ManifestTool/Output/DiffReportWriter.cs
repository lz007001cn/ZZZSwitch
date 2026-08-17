using System.Text;
using System.Text.Json;
using ZZZSwitch.ManifestTool.Diff;

namespace ZZZSwitch.ManifestTool.Output;

public sealed record DiffReportPaths(string Text, string Json);

public sealed class DiffReportWriter
{
    private readonly JsonSerializerOptions _json;

    public DiffReportWriter(JsonSerializerOptions json) => _json = json;

    public async Task<DiffReportPaths> WriteAsync(
        ManifestDiff diff,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var stem = $"manifest-diff-{diff.SourceRegion}-to-{diff.TargetRegion}-{diff.Version}";
        var textPath = Path.Combine(directory, $"{stem}.txt");
        var jsonPath = Path.Combine(directory, $"{stem}.json");

        await File.WriteAllTextAsync(
            textPath, BuildText(diff), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await using (var stream = new FileStream(
            jsonPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, diff, _json, cancellationToken).ConfigureAwait(false);
        }

        return new DiffReportPaths(textPath, jsonPath);
    }

    public static string BuildText(ManifestDiff diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Summary");
        builder.AppendLine();
        builder.AppendLine($"Game: {diff.Game}");
        builder.AppendLine($"Version: {diff.Version}");
        builder.AppendLine($"Source: {diff.SourceRegion}");
        builder.AppendLine($"Target: {diff.TargetRegion}");
        builder.AppendLine();
        builder.AppendLine($"Same: {diff.Summary.Same}");
        builder.AppendLine($"Modified: {diff.Summary.Modified}");
        builder.AppendLine($"Added: {diff.Summary.Added}");
        builder.AppendLine($"Removed: {diff.Summary.Removed}");
        AppendSection(builder, "Modified files", diff.Modified);
        AppendSection(builder, "Added files", diff.Added);
        AppendSection(builder, "Removed files", diff.Removed);
        return builder.ToString();
    }

    private static void AppendSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<FileDifference> files)
    {
        builder.AppendLine();
        builder.AppendLine($"{title}:");
        foreach (var file in files)
        {
            builder.AppendLine(file.Path);
            builder.AppendLine(
                $"  source: size={Format(file.SourceSize)}, md5={file.SourceMd5 ?? "-"}");
            builder.AppendLine(
                $"  target: size={Format(file.TargetSize)}, md5={file.TargetMd5 ?? "-"}");
        }
    }

    private static string Format(long? value) => value?.ToString() ?? "-";
}
