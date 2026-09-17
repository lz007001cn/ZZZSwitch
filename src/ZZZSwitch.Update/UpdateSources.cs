using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZZZSwitch.Update;

public sealed record UpdateRelease(string Version, string Name, string Body, DateTimeOffset PublishedAt, string HtmlUrl,
    UpdateManifest? Manifest, string? UnavailableReason = null);

public interface IUpdateSource
{
    Task<UpdateRelease> GetLatestAsync(string endpoint, string currentVersion, CancellationToken token);
}

public sealed class ManifestUpdateSource(HttpClient http) : IUpdateSource
{
    public async Task<UpdateRelease> GetLatestAsync(string endpoint, string currentVersion, CancellationToken token)
    {
        var manifest = UpdateManifestParser.Parse(await UpdateHttp.ReadTextAsync(http, UpdateManifestParser.ValidateUrl(endpoint), currentVersion, token).ConfigureAwait(false));
        return new(manifest.Version, manifest.Version, manifest.Notes("en-US"), manifest.PublishedAt, "", manifest);
    }
}

public sealed class GitHubReleaseUpdateSource(HttpClient http) : IUpdateSource
{
    public const string Endpoint = "https://api.github.com/repos/lz007001cn/ZZZSwitch/releases/latest";
    public static string PackageName(string version) => $"ZZZSwitch-win-x64-v{version}.zip";

    public async Task<UpdateRelease> GetLatestAsync(string endpoint, string currentVersion, CancellationToken token)
    {
        var json = await UpdateHttp.ReadTextAsync(http, UpdateManifestParser.ValidateUrl(endpoint), currentVersion, token).ConfigureAwait(false);
        GitHubRelease release;
        try { release = JsonSerializer.Deserialize<GitHubRelease>(json, UpdateManifestParser.Json) ?? throw new JsonException("Empty release."); }
        catch (JsonException ex) { throw new UpdateException(UpdateError.InvalidJson, "Invalid GitHub Release JSON.", ex); }
        var version = release.TagName?.StartsWith('v') == true ? release.TagName[1..] : release.TagName;
        if (UpdateVersion.Parse(version).PreRelease is not null || release.Draft || release.Prerelease || release.PublishedAt == default ||
            release.HtmlUrl is null || !release.HtmlUrl.StartsWith("https://github.com/lz007001cn/ZZZSwitch/releases/tag/", StringComparison.Ordinal))
            throw new UpdateException(UpdateError.InvalidManifest, "Invalid stable GitHub release.");
        var result = new UpdateRelease(version!, release.Name ?? "", release.Body ?? "", release.PublishedAt, release.HtmlUrl, null);
        try
        {
            var name = PackageName(version!);
            var package = SelectAsset(release.Assets, name);
            // GitHub binds digest to this exact asset. Metadata checks never fetch sidecar files.
            var digest = package.Digest;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateManifestParser.IsHash(digest[7..]))
                throw new UpdateException(UpdateError.InvalidPackage, "GitHub has not supplied a valid SHA-256 digest for this program ZIP.");
            var manifest = new UpdateManifest
            {
                SchemaVersion = 1, Version = version!, Channel = "stable", Mandatory = false, MinSupportedVersion = "0.0.0",
                PublishedAt = release.PublishedAt, ReleaseNotes = new() { ["zh-CN"] = result.Body, ["en-US"] = result.Body },
                Package = new() { Url = AssetUri(package, release.TagName!).AbsoluteUri, Size = package.Size, Sha256 = digest[7..] }
            };
            UpdateManifestParser.Validate(manifest);
            return result with { Manifest = manifest };
        }
        catch (UpdateException ex) when (ex.Error is UpdateError.InvalidPackage or UpdateError.InvalidManifest)
        { return result with { UnavailableReason = ex.Message }; }
    }

    private static GitHubAsset SelectAsset(GitHubAsset[]? assets, string name)
    {
        var matches = assets?.Where(a => a is not null && a.Name == name).ToArray() ?? [];
        if (matches.Length != 1 || matches[0].Size <= 0)
            throw new UpdateException(UpdateError.InvalidPackage, "Missing, duplicate or empty release asset: " + name);
        return matches[0];
    }
    private static Uri AssetUri(GitHubAsset asset, string tag)
    {
        var uri = UpdateManifestParser.ValidateUrl(asset.Url);
        var expected = $"/lz007001cn/ZZZSwitch/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset.Name!)}";
        if (!UpdateHttp.IsGitHubAsset(uri) || uri.AbsolutePath != expected || uri.Query.Length != 0)
            throw new UpdateException(UpdateError.InvalidPackage, "Release asset URL does not match its tag and name.");
        return uri;
    }
    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("assets")] public GitHubAsset[]? Assets { get; init; }
    }
    private sealed record GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("digest")] public string? Digest { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; init; }
    }
}
