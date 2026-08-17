using System.Text;
using System.Text.Json;
using ZZZSwitch.ManifestTool.Classification;

namespace ZZZSwitch.ManifestTool.Output;

public sealed record ClassificationReportPaths(string Text, string Json, string Csv);

public sealed class ClassificationReportWriter
{
    private readonly JsonSerializerOptions _json;

    public ClassificationReportWriter(JsonSerializerOptions json) => _json = json;

    public async Task<ClassificationReportPaths> WriteAsync(
        ManifestClassificationReport report,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var stem = $"manifest-classification-{report.SourceRegion}-to-{report.TargetRegion}-{report.Version}";
        var textPath = Path.Combine(directory, $"{stem}.txt");
        var jsonPath = Path.Combine(directory, $"{stem}.json");
        var csvPath = Path.Combine(directory, $"{stem}.csv");

        await File.WriteAllTextAsync(
            textPath, BuildText(report), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await using (var stream = new FileStream(
            jsonPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, report, _json, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(
            csvPath, BuildCsv(report), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        return new ClassificationReportPaths(textPath, jsonPath, csvPath);
    }

    public static string BuildText(ManifestClassificationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Classification summary");
        builder.AppendLine();
        builder.AppendLine($"Game: {report.Game}");
        builder.AppendLine($"Version: {report.Version}");
        builder.AppendLine($"Source: {report.SourceRegion}");
        builder.AppendLine($"Target: {report.TargetRegion}");
        builder.AppendLine($"Total differences: {report.Summary.Total}");
        builder.AppendLine($"Base client: {report.Summary.BaseClient}");
        builder.AppendLine($"Base resource: {report.Summary.BaseResource}");
        builder.AppendLine($"Runtime hot update: {report.Summary.RuntimeHotUpdate}");
        builder.AppendLine($"State metadata: {report.Summary.StateMetadata}");
        builder.AppendLine($"Needs observation: {report.Summary.NeedsObservation}");
        builder.AppendLine();
        builder.AppendLine("Files:");
        foreach (var file in report.Files)
        {
            builder.AppendLine(file.Path);
            builder.AppendLine(
                $"  change={file.ChangeType}, class={file.FileClass}, confidence={file.Confidence}, action={file.RecommendedAction}");
            builder.AppendLine($"  rule={file.RuleId}: {file.Reason}");
        }

        return builder.ToString();
    }

    private static string BuildCsv(ManifestClassificationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "Path,ChangeType,FileClass,Confidence,RecommendedAction,RuleId,Reason,SourceSize,TargetSize,SourceMd5,TargetMd5");
        foreach (var file in report.Files)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(file.Path),
                Csv(file.ChangeType.ToString()),
                Csv(file.FileClass.ToString()),
                Csv(file.Confidence.ToString()),
                Csv(file.RecommendedAction),
                Csv(file.RuleId),
                Csv(file.Reason),
                Csv(file.SourceSize?.ToString() ?? string.Empty),
                Csv(file.TargetSize?.ToString() ?? string.Empty),
                Csv(file.SourceMd5 ?? string.Empty),
                Csv(file.TargetMd5 ?? string.Empty)
            }));
        }

        return builder.ToString();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
