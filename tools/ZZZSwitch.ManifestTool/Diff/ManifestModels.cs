using System.Text.Json.Serialization;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool.Diff;

public sealed class ManifestChunk
{
    [JsonConstructor]
    public ManifestChunk(
        string name,
        string decompressedMd5,
        long fileOffset,
        long compressedSize,
        long decompressedSize)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#' or ':'))
        {
            throw new InvalidDataException($"Manifest chunk has an unsafe name '{name}'.");
        }

        Name = name;
        DecompressedMd5 = ManifestEntry.IsMd5(decompressedMd5)
            ? decompressedMd5.ToUpperInvariant()
            : throw new InvalidDataException($"Manifest chunk '{name}' has an invalid decompressed MD5.");
        FileOffset = fileOffset >= 0
            ? fileOffset
            : throw new InvalidDataException($"Manifest chunk '{name}' has a negative file offset.");
        CompressedSize = compressedSize > 0
            ? compressedSize
            : throw new InvalidDataException($"Manifest chunk '{name}' has an invalid compressed size.");
        DecompressedSize = decompressedSize > 0
            ? decompressedSize
            : throw new InvalidDataException($"Manifest chunk '{name}' has an invalid decompressed size.");
        if (FileOffset > long.MaxValue - DecompressedSize)
        {
            throw new InvalidDataException($"Manifest chunk '{name}' exceeds Int64 range.");
        }
    }

    public string Name { get; }
    public string DecompressedMd5 { get; }
    public long FileOffset { get; }
    public long CompressedSize { get; }
    public long DecompressedSize { get; }
}

public sealed class ManifestEntry
{
    [JsonConstructor]
    public ManifestEntry(
        string path,
        long size,
        string md5,
        int chunkCount = 0,
        IReadOnlyList<ManifestChunk>? chunks = null)
    {
        Path = NormalizePath(path);
        Size = size >= 0
            ? size
            : throw new InvalidDataException($"Manifest entry '{Path}' has a negative size.");
        Md5 = IsMd5(md5)
            ? md5.ToUpperInvariant()
            : throw new InvalidDataException($"Manifest entry '{Path}' has an invalid MD5.");
        if (chunkCount < 0)
        {
            throw new InvalidDataException($"Manifest entry '{Path}' has a negative chunk count.");
        }

        Chunks = chunks?.OrderBy(chunk => chunk.FileOffset).ThenBy(chunk => chunk.Name, StringComparer.Ordinal).ToArray()
            ?? [];
        if (Chunks.Count > 0 && chunkCount != 0 && chunkCount != Chunks.Count)
        {
            throw new InvalidDataException(
                $"Manifest entry '{Path}' declares {chunkCount} chunks but contains {Chunks.Count}.");
        }

        foreach (var chunk in Chunks)
        {
            if (chunk.FileOffset + chunk.DecompressedSize > Size)
            {
                throw new InvalidDataException(
                    $"Manifest chunk '{chunk.Name}' does not fit inside '{Path}'.");
            }
        }

        ChunkCount = Chunks.Count > 0 ? Chunks.Count : chunkCount;
    }

    public string Path { get; }
    public long Size { get; }
    public string Md5 { get; }
    public int ChunkCount { get; }
    public IReadOnlyList<ManifestChunk> Chunks { get; }
    public bool HasCompleteChunkMetadata => ChunkCount > 0 && Chunks.Count == ChunkCount;

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("Manifest entry path is empty.");
        }

        var replaced = path.Trim().Replace('/', '\\');
        if (System.IO.Path.IsPathRooted(replaced) || replaced.StartsWith('\\'))
        {
            throw new InvalidDataException($"Manifest entry path is absolute: '{path}'.");
        }

        var parts = replaced.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part == ".."))
        {
            throw new InvalidDataException($"Manifest entry path is unsafe: '{path}'.");
        }

        if (parts.Any(part => part.Contains(':', StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Manifest entry path contains a drive or stream marker: '{path}'.");
        }

        var normalized = string.Join('\\', parts.Where(part => part != "."));
        return normalized.Length > 0
            ? normalized
            : throw new InvalidDataException($"Manifest entry path is unsafe: '{path}'.");
    }

    public static bool IsMd5(string? value) =>
        value is { Length: 32 } && value.All(Uri.IsHexDigit);
}

public sealed class ManifestSnapshot
{
    [JsonConstructor]
    public ManifestSnapshot(
        string game,
        SophonRegion region,
        string version,
        string categoryId,
        string manifestId,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<ManifestEntry> entries)
    {
        if (!string.Equals(game, SophonRegionConfig.Game, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported game '{game}'.");
        }

        Game = game;
        Region = region;
        Version = string.IsNullOrWhiteSpace(version)
            ? throw new InvalidDataException("Manifest version is empty.")
            : version;
        CategoryId = string.IsNullOrWhiteSpace(categoryId)
            ? throw new InvalidDataException("Manifest category is empty.")
            : categoryId;
        ManifestId = string.IsNullOrWhiteSpace(manifestId)
            ? throw new InvalidDataException("Manifest id is empty.")
            : manifestId;
        CreatedAtUtc = createdAtUtc;

        ArgumentNullException.ThrowIfNull(entries);
        var sorted = entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var duplicate = sorted
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Manifest contains duplicate Windows path '{duplicate.Key}'.");
        }

        Entries = sorted;
    }

    public string Game { get; }
    public SophonRegion Region { get; }
    public string Version { get; }
    public string CategoryId { get; }
    public string ManifestId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public IReadOnlyList<ManifestEntry> Entries { get; }
}

public sealed record FileDifference(
    string Path,
    long? SourceSize,
    long? TargetSize,
    string? SourceMd5,
    string? TargetMd5);

public sealed record ManifestDiffSummary(
    int Same,
    int Modified,
    int Added,
    int Removed);

public sealed record ManifestDiff(
    string Game,
    string Version,
    SophonRegion SourceRegion,
    SophonRegion TargetRegion,
    ManifestDiffSummary Summary,
    IReadOnlyList<FileDifference> Modified,
    IReadOnlyList<FileDifference> Added,
    IReadOnlyList<FileDifference> Removed);
