using System.Globalization;
using System.Text.Json;

namespace ZZZSwitch.ManifestTool.Sophon;

public sealed class SophonClient
{
    private readonly ISophonTransport _transport;
    private readonly Action<string>? _verbose;

    public SophonClient(ISophonTransport transport, Action<string>? verbose = null)
    {
        _transport = transport;
        _verbose = verbose;
    }

    public async Task<GameBranchCatalog> GetGameBranchesAsync(
        SophonRegion region,
        CancellationToken cancellationToken = default)
    {
        var config = SophonRegionConfig.For(region);
        var uri = BuildUri(config.BranchesEndpoint, new Dictionary<string, string>
        {
            ["launcher_id"] = config.LauncherId,
            ["game_ids[]"] = config.GameId
        });
        _verbose?.Invoke($"getGameBranches: {uri}");
        var json = await _transport.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        return ParseGameBranches(json, region);
    }

    public async Task<SophonBuild> GetBuildAsync(
        SophonRegion region,
        BranchPackage package,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var config = SophonRegionConfig.For(region);
        SophonApiException? lastApiError = null;

        foreach (var candidate in GetVersionCandidates(version))
        {
            var uri = BuildUri(config.BuildEndpoint, new Dictionary<string, string>
            {
                ["branch"] = "main",
                ["package_id"] = package.PackageId,
                ["password"] = package.Password,
                ["plat_app"] = config.PlatformApp,
                ["tag"] = candidate
            });
            _verbose?.Invoke(
                $"getBuild: {config.BuildEndpoint}?branch=main&package_id={Uri.EscapeDataString(package.PackageId)}" +
                $"&password=<redacted>&plat_app={Uri.EscapeDataString(config.PlatformApp)}&tag={Uri.EscapeDataString(candidate)}");

            var json = await _transport.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
            try
            {
                return ParseBuild(json, region, version);
            }
            catch (SophonApiException ex)
            {
                lastApiError = ex;
            }
        }

        throw lastApiError is not null
            ? lastApiError
            : new InvalidDataException("Sophon getBuild returned no usable response.");
    }

    public static GameBranchCatalog ParseGameBranches(string json, SophonRegion region)
    {
        using var document = ParseJson(json, "getGameBranches");
        var root = document.RootElement;
        EnsureSuccess(root, "getGameBranches");
        var data = RequiredObject(root, "data", "getGameBranches");
        var branchesElement = RequiredArray(data, "game_branches", "getGameBranches.data");
        var branches = new List<GameBranch>();

        foreach (var item in branchesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("getGameBranches.data.game_branches contains a non-object item.");
            }

            var game = RequiredObject(item, "game", "game_branches[]");
            var gameId = RequiredText(game, "id", "game_branches[].game");
            var main = ParsePackage(RequiredObject(item, "main", "game_branches[]"), "game_branches[].main");
            BranchPackage? preDownload = null;
            if (item.TryGetProperty("pre_download", out var pre) && pre.ValueKind == JsonValueKind.Object)
            {
                preDownload = ParsePackage(pre, "game_branches[].pre_download");
            }

            branches.Add(new GameBranch(gameId, main, preDownload));
        }

        if (branches.Count == 0)
        {
            throw new InvalidDataException("getGameBranches returned no game branches.");
        }

        return new GameBranchCatalog(region, branches);
    }

    public static SophonBuild ParseBuild(string json, SophonRegion region, string requestedVersion)
    {
        using var document = ParseJson(json, "getBuild");
        var root = document.RootElement;
        EnsureSuccess(root, "getBuild");
        var data = RequiredObject(root, "data", "getBuild");
        var serverVersion = RequiredText(data, "tag", "getBuild.data");
        GetVersionCandidates(serverVersion);
        var manifestsElement = RequiredArray(data, "manifests", "getBuild.data");
        var manifests = new List<ManifestCategory>();

        foreach (var item in manifestsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("getBuild.data.manifests contains a non-object item.");
            }

            var context = "getBuild.data.manifests[]";
            var manifest = RequiredObject(item, "manifest", context);
            var manifestDownload = RequiredObject(item, "manifest_download", context);
            var chunkDownload = RequiredObject(item, "chunk_download", context);
            manifests.Add(new ManifestCategory(
                RequiredScalarText(item, "category_id", context),
                RequiredScalarText(item, "matching_field", context),
                RequiredText(manifest, "id", $"{context}.manifest"),
                RequiredText(manifestDownload, "url_prefix", $"{context}.manifest_download"),
                OptionalText(manifestDownload, "url_suffix"),
                RequiredText(chunkDownload, "url_prefix", $"{context}.chunk_download"),
                OptionalText(chunkDownload, "url_suffix")));
        }

        if (manifests.Count == 0)
        {
            throw new InvalidDataException("getBuild returned no manifests.");
        }

        return new SophonBuild(region, requestedVersion, serverVersion, manifests);
    }

    public static ManifestCategory SelectManifest(
        SophonBuild build,
        string? categoryId = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            return build.Manifests.FirstOrDefault(
                item => string.Equals(item.CategoryId, categoryId, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"Category '{categoryId}' was not returned. Available categories: {Describe(build.Manifests)}");
        }

        var gameCandidates = build.Manifests
            .Where(item => string.Equals(item.MatchingField, "game", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return gameCandidates.Length switch
        {
            1 => gameCandidates[0],
            0 => throw new InvalidDataException(
                $"No manifest has matching_field 'game'. Available categories: {Describe(build.Manifests)}"),
            _ => throw new InvalidDataException(
                $"Multiple manifests have matching_field 'game'. Specify --category. Candidates: {Describe(gameCandidates)}")
        };
    }

    public static Uri BuildManifestUri(ManifestCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return BuildDownloadUri(
            category.ManifestUrlPrefix,
            category.ManifestId,
            category.ManifestUrlSuffix,
            "Manifest");
    }

    public static Uri BuildChunkUri(ManifestCategory category, string chunkName)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (string.IsNullOrWhiteSpace(chunkName) ||
            chunkName.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#' or ':'))
        {
            throw new InvalidDataException($"Chunk name is unsafe: '{chunkName}'.");
        }

        return BuildDownloadUri(
            category.ChunkUrlPrefix,
            chunkName,
            category.ChunkUrlSuffix,
            "Chunk");
    }

    private static Uri BuildDownloadUri(
        string urlPrefix,
        string itemName,
        string urlSuffix,
        string kind)
    {
        if (!Uri.TryCreate(urlPrefix, UriKind.Absolute, out var prefix))
        {
            throw new InvalidDataException($"{kind} URL prefix is not an absolute URL.");
        }

        if (!string.IsNullOrEmpty(prefix.Query) || !string.IsNullOrEmpty(prefix.Fragment))
        {
            throw new InvalidDataException($"{kind} URL prefix unexpectedly contains a query or fragment.");
        }

        var baseText = prefix.AbsoluteUri.TrimEnd('/');
        var uriText = $"{baseText}/{Uri.EscapeDataString(itemName)}";
        if (!string.IsNullOrWhiteSpace(urlSuffix))
        {
            uriText += urlSuffix.StartsWith("?", StringComparison.Ordinal)
                ? urlSuffix
                : $"?{urlSuffix}";
        }

        return Uri.TryCreate(uriText, UriKind.Absolute, out var result)
            ? result
            : throw new InvalidDataException($"Sophon {kind.ToLowerInvariant()} URL could not be constructed.");
    }

    public static IReadOnlyList<string> GetVersionCandidates(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Version is required.", nameof(version));
        }

        var normalized = version.Trim();
        var parts = normalized.Split('.');
        if (parts.Length is < 3 or > 4 || parts.Any(
                part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new ArgumentException(
                $"Version '{version}' must contain three or four numeric components.", nameof(version));
        }

        var candidates = new List<string> { normalized };
        if (parts.Length == 3)
        {
            candidates.Add($"{normalized}.0");
        }
        else if (parts[^1] == "0")
        {
            candidates.Add(string.Join('.', parts[..^1]));
        }

        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static BranchPackage ParsePackage(JsonElement package, string context) => new(
        RequiredText(package, "package_id", context),
        RequiredText(package, "password", context),
        RequiredText(package, "tag", context));

    private static JsonDocument ParseJson(string json, string operation)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Sophon {operation} returned invalid JSON: {ex.Message}", ex);
        }
    }

    private static void EnsureSuccess(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("retcode", out var code))
        {
            throw new InvalidDataException($"Sophon {operation} response is missing retcode.");
        }

        int retCode;
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numeric))
        {
            retCode = numeric;
        }
        else if (code.ValueKind == JsonValueKind.String && int.TryParse(code.GetString(), out numeric))
        {
            retCode = numeric;
        }
        else
        {
            throw new InvalidDataException($"Sophon {operation} retcode has an invalid type.");
        }

        if (retCode != 0)
        {
            throw new SophonApiException(operation, retCode, OptionalText(root, "message"));
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context}.{name} is missing or is not an object.");
        }

        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{context}.{name} is missing or is not an array.");
        }

        return value;
    }

    private static string RequiredText(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{context}.{name} is missing or empty.");
        }

        return value.GetString()!;
    }

    private static string RequiredScalarText(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"{context}.{name} is missing.");
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidDataException($"{context}.{name} is empty or has an invalid type.")
            : text;
    }

    private static string OptionalText(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> values)
    {
        var query = string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    private static string Describe(IEnumerable<ManifestCategory> manifests) => string.Join(
        ", ",
        manifests.Select(item => $"{item.CategoryId} ({item.MatchingField}, {item.ManifestId})"));
}
