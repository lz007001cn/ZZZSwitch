using System.Text.Json;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool;

public sealed class ManifestCache
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json;

    public ManifestCache(string root, JsonSerializerOptions json)
    {
        _root = Path.GetFullPath(root);
        _json = json;
    }

    public string GetPath(SophonRegion region, string version, string categoryId)
    {
        var safeVersion = SafeSegment(version, nameof(version));
        var safeCategory = SafeSegment(categoryId, nameof(categoryId));
        return Path.Combine(
            _root,
            SophonRegionConfig.Game,
            region.ToString(),
            safeVersion,
            safeCategory,
            "snapshot.json");
    }

    public async Task<ManifestSnapshot?> TryLoadAsync(
        SophonRegion region,
        string version,
        string categoryId,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(region, version, categoryId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<ManifestSnapshot>(
                stream, _json, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Region != region ||
                !string.Equals(snapshot.Version, version, StringComparison.Ordinal) ||
                !string.Equals(snapshot.CategoryId, categoryId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Cached manifest identity does not match its cache key.");
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cached manifest is unreadable: {path}: {ex.Message}", ex);
        }
    }

    public async Task SaveAsync(ManifestSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var path = GetPath(snapshot.Region, snapshot.Version, snapshot.CategoryId);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"snapshot-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _json, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string SafeSegment(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException($"Unsafe cache key segment '{value}'.", parameter);
        }

        return value;
    }
}
