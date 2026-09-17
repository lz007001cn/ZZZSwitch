using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZZZSwitch.Update;

public enum UpdateError { NotConfigured, RateLimited, Http, Network, Timeout, InvalidJson, UnsupportedSchema, InvalidManifest, SizeMismatch, HashMismatch, Storage, UnsafePath, InvalidPackage, Installation, Recovery }
public sealed class UpdateException(UpdateError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public UpdateError Error { get; } = error;
}

public sealed record UpdatePackage
{
    public required string Url { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record UpdateManifest
{
    public required int SchemaVersion { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; }
    public required bool Mandatory { get; init; }
    public required string MinSupportedVersion { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required Dictionary<string, string> ReleaseNotes { get; init; }
    public required UpdatePackage Package { get; init; }

    public string Notes(string language) => ReleaseNotes.GetValueOrDefault(language)
        ?? ReleaseNotes.GetValueOrDefault("en-US") ?? ReleaseNotes.GetValueOrDefault("zh-CN") ?? "";
}

public static class UpdateManifestParser
{
    public const long MaxPackageSize = 1024L * 1024 * 1024;
    public const int MaxManifestSize = 256 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = false, MaxDepth = 32 };

    public static UpdateManifest Parse(string json)
    {
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxManifestSize)
                throw new UpdateException(UpdateError.InvalidManifest, "Manifest is too large.");
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            RejectDuplicates(document.RootElement);
            var date = document.RootElement.GetProperty("publishedAt").GetString();
            if (date is null || !Regex.IsMatch(date, @"(?:Z|[+-]\d{2}:\d{2})$"))
                throw new UpdateException(UpdateError.InvalidManifest, "publishedAt requires a timezone.");
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Json)
                ?? throw new JsonException("Empty manifest.");
            Validate(manifest);
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new UpdateException(UpdateError.InvalidJson, "Update manifest JSON is invalid or incomplete.", ex); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    public static void Validate(UpdateManifest m)
    {
        if (m.SchemaVersion != 1) throw new UpdateException(UpdateError.UnsupportedSchema, "Unsupported update schema.");
        var version = UpdateVersion.Parse(m.Version);
        var minimum = UpdateVersion.Parse(m.MinSupportedVersion);
        if (m.Channel is not ("stable" or "beta") || (m.Channel == "stable" && version.PreRelease is not null) || minimum.CompareTo(version) > 0 || m.PublishedAt == default ||
            m.ReleaseNotes is null || m.ReleaseNotes.Count == 0 || m.ReleaseNotes.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Length > 65536) || m.Package is null)
            throw new UpdateException(UpdateError.InvalidManifest, "Invalid update metadata.");
        ValidateUrl(m.Package.Url);
        if (m.Package.Size is <= 0 or > MaxPackageSize || !IsHash(m.Package.Sha256))
            throw new UpdateException(UpdateError.InvalidManifest, "Invalid package size or SHA-256.");
    }

    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    public static Uri ValidateUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new UpdateException(UpdateError.InvalidManifest, "Use HTTPS, or HTTP loopback for local testing.");
        return uri;
    }
}
