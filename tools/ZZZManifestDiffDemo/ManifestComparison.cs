using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZManifestDiffDemo;

public enum DifferenceState
{
    Modified,
    Added,
    Removed,
    Same
}

public sealed record DifferenceRow(
    DifferenceState State,
    string Path,
    long? SourceSize,
    long? TargetSize,
    string? SourceMd5,
    string? TargetMd5)
{
    public string StateLabel => State switch
    {
        DifferenceState.Modified => "修改",
        DifferenceState.Added => "新增",
        DifferenceState.Removed => "删除",
        _ => "相同"
    };

    public string SourceSizeText => ManifestComparisonService.FormatBytes(SourceSize);
    public string TargetSizeText => ManifestComparisonService.FormatBytes(TargetSize);

    public string DeltaText => State switch
    {
        DifferenceState.Added => "+" + ManifestComparisonService.FormatBytes(TargetSize),
        DifferenceState.Removed => "-" + ManifestComparisonService.FormatBytes(SourceSize),
        _ when SourceSize.HasValue && TargetSize.HasValue =>
            ManifestComparisonService.FormatSignedBytes(TargetSize.Value - SourceSize.Value),
        _ => "—"
    };
}

public sealed record ManifestComparisonResult(
    ManifestSnapshot Source,
    ManifestSnapshot Target,
    ManifestDiff Diff,
    IReadOnlyList<DifferenceRow> Rows)
{
    public long SourceBytes => Source.Entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Size));
    public long TargetBytes => Target.Entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Size));
}

public sealed class ManifestComparisonService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ManifestSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ManifestSnapshot>(stream, Json, cancellationToken)
                   ?? throw new InvalidDataException("Manifest JSON 为空或格式不正确。");
    }

    public ManifestComparisonResult Compare(ManifestSnapshot source, ManifestSnapshot target)
    {
        var diff = new ManifestDiffEngine().Compare(source, target);
        var rows = new List<DifferenceRow>(
            diff.Summary.Modified + diff.Summary.Added + diff.Summary.Removed + diff.Summary.Same);
        rows.AddRange(diff.Modified.Select(item => Row(DifferenceState.Modified, item)));
        rows.AddRange(diff.Added.Select(item => Row(DifferenceState.Added, item)));
        rows.AddRange(diff.Removed.Select(item => Row(DifferenceState.Removed, item)));

        var targetIndex = target.Entries.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        rows.AddRange(source.Entries
            .Where(sourceEntry =>
                targetIndex.TryGetValue(sourceEntry.Path, out var targetEntry) &&
                sourceEntry.Size == targetEntry.Size &&
                string.Equals(sourceEntry.Md5, targetEntry.Md5, StringComparison.OrdinalIgnoreCase))
            .Select(sourceEntry =>
            {
                var targetEntry = targetIndex[sourceEntry.Path];
                return new DifferenceRow(
                    DifferenceState.Same,
                    sourceEntry.Path,
                    sourceEntry.Size,
                    targetEntry.Size,
                    sourceEntry.Md5,
                    targetEntry.Md5);
            }));

        var ordered = rows
            .OrderBy(row => row.State)
            .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .ToArray();
        return new ManifestComparisonResult(source, target, diff, ordered);
    }

    public static (ManifestSnapshot Source, ManifestSnapshot Target) CreateDemoSnapshots()
    {
        var source = Snapshot(
            SophonRegion.OS,
            "global-demo-310",
            [
                Entry("ZenlessZoneZero.exe", 182_419_456, "11111111111111111111111111111111"),
                Entry("UnityPlayer.dll", 31_498_240, "22222222222222222222222222222222"),
                Entry("ZenlessZoneZero_Data\\app.info", 1_284, "33333333333333333333333333333333"),
                Entry("ZenlessZoneZero_Data\\StreamingAssets\\Audio\\GlobalVoice.pck", 856_424_448, "44444444444444444444444444444444"),
                Entry("ZenlessZoneZero_Data\\StreamingAssets\\Blocks\\00\\block_a", 1_610_612_736, "55555555555555555555555555555555"),
                Entry("ZenlessZoneZero_Data\\Persistent\\data_revision", 28, "66666666666666666666666666666666"),
                Entry("pkg_version", 96, "77777777777777777777777777777777")
            ]);
        var target = Snapshot(
            SophonRegion.CN,
            "cn-demo-310",
            [
                Entry("ZenlessZoneZero.exe", 182_419_456, "11111111111111111111111111111111"),
                Entry("UnityPlayer.dll", 31_498_240, "22222222222222222222222222222222"),
                Entry("ZenlessZoneZero_Data\\app.info", 1_412, "88888888888888888888888888888888"),
                Entry("ZenlessZoneZero_Data\\StreamingAssets\\Audio\\CNVoice.pck", 902_299_648, "99999999999999999999999999999999"),
                Entry("ZenlessZoneZero_Data\\StreamingAssets\\Blocks\\00\\block_a", 1_611_661_312, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                Entry("ZenlessZoneZero_Data\\Persistent\\data_revision", 28, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"),
                Entry("ZenlessZoneZero_Data\\Plugins\\PCGameSDK.dll", 6_291_456, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"),
                Entry("pkg_version", 96, "77777777777777777777777777777777")
            ]);
        return (source, target);
    }

    public async Task ExportAsync(
        ManifestComparisonResult result,
        string path,
        CancellationToken cancellationToken = default)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csv":
                await File.WriteAllTextAsync(
                    path, BuildCsv(result), new UTF8Encoding(true), cancellationToken);
                break;
            case ".txt":
                await File.WriteAllTextAsync(
                    path, BuildText(result), new UTF8Encoding(false), cancellationToken);
                break;
            default:
                await using (var stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, new
                    {
                        source = Describe(result.Source),
                        target = Describe(result.Target),
                        summary = result.Diff.Summary,
                        files = result.Rows.Select(row => new
                        {
                            state = row.State.ToString(),
                            row.Path,
                            row.SourceSize,
                            row.TargetSize,
                            row.SourceMd5,
                            row.TargetMd5
                        })
                    }, Json, cancellationToken);
                }
                break;
        }
    }

    public static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "—";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes.Value);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public static string FormatSignedBytes(long bytes) => bytes switch
    {
        > 0 => "+" + FormatBytes(bytes),
        < 0 => "-" + FormatBytes(-bytes),
        _ => "0 B"
    };

    private static DifferenceRow Row(DifferenceState state, FileDifference item) => new(
        state,
        item.Path,
        item.SourceSize,
        item.TargetSize,
        item.SourceMd5,
        item.TargetMd5);

    private static ManifestSnapshot Snapshot(
        SophonRegion region,
        string manifestId,
        IReadOnlyList<ManifestEntry> entries) => new(
        SophonRegionConfig.Game,
        region,
        "3.1.0",
        "game",
        manifestId,
        DateTimeOffset.UtcNow,
        entries);

    private static ManifestEntry Entry(string path, long size, string md5) =>
        new(path, size, md5);

    private static object Describe(ManifestSnapshot snapshot) => new
    {
        snapshot.Game,
        region = snapshot.Region.ToString(),
        snapshot.Version,
        snapshot.CategoryId,
        snapshot.ManifestId,
        snapshot.CreatedAtUtc,
        fileCount = snapshot.Entries.Count,
        totalBytes = snapshot.Entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Size))
    };

    private static string BuildCsv(ManifestComparisonResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("State,Path,SourceBytes,TargetBytes,SourceMD5,TargetMD5");
        foreach (var row in result.Rows)
        {
            builder.Append(Csv(row.State.ToString())).Append(',')
                .Append(Csv(row.Path)).Append(',')
                .Append(row.SourceSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(row.TargetSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(Csv(row.SourceMd5 ?? string.Empty)).Append(',')
                .AppendLine(Csv(row.TargetMd5 ?? string.Empty));
        }

        return builder.ToString();
    }

    private static string BuildText(ManifestComparisonResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ZZZ Manifest Difference Verification");
        builder.AppendLine($"Source: {result.Source.Region} {result.Source.Version} ({result.Source.ManifestId})");
        builder.AppendLine($"Target: {result.Target.Region} {result.Target.Version} ({result.Target.ManifestId})");
        builder.AppendLine($"Same: {result.Diff.Summary.Same:N0}");
        builder.AppendLine($"Modified: {result.Diff.Summary.Modified:N0}");
        builder.AppendLine($"Added: {result.Diff.Summary.Added:N0}");
        builder.AppendLine($"Removed: {result.Diff.Summary.Removed:N0}");
        foreach (var row in result.Rows)
        {
            builder.AppendLine();
            builder.AppendLine($"[{row.State}] {row.Path}");
            builder.AppendLine($"  source: {row.SourceSize?.ToString() ?? "-"} bytes, {row.SourceMd5 ?? "-"}");
            builder.AppendLine($"  target: {row.TargetSize?.ToString() ?? "-"} bytes, {row.TargetMd5 ?? "-"}");
        }

        return builder.ToString();
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
