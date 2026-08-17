using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool;

public sealed record ManifestFetchResult(
    ManifestSnapshot Snapshot,
    SophonBuild Build,
    ManifestCategory Category,
    bool CacheHit);

public sealed class ManifestService
{
    private readonly SophonClient _client;
    private readonly SophonManifestReader _reader;
    private readonly ManifestCache _cache;
    private readonly Action<string>? _verbose;

    public ManifestService(
        SophonClient client,
        SophonManifestReader reader,
        ManifestCache cache,
        Action<string>? verbose = null)
    {
        _client = client;
        _reader = reader;
        _cache = cache;
        _verbose = verbose;
    }

    public async Task<ManifestFetchResult> FetchAsync(
        SophonRegion region,
        string version,
        string? categoryId,
        bool useCache,
        CancellationToken cancellationToken = default)
    {
        var config = SophonRegionConfig.For(region);
        var catalog = await _client.GetGameBranchesAsync(region, cancellationToken).ConfigureAwait(false);
        var gameBranch = catalog.Branches.FirstOrDefault(
            item => string.Equals(item.GameId, config.GameId, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"getGameBranches did not return expected game id '{config.GameId}'.");
        _verbose?.Invoke($"package_id: {gameBranch.Main.PackageId}");
        var build = await _client.GetBuildAsync(region, gameBranch.Main, version, cancellationToken)
            .ConfigureAwait(false);
        var category = SophonClient.SelectManifest(build, categoryId);
        _verbose?.Invoke($"version: {build.Version}");
        _verbose?.Invoke($"category_id: {category.CategoryId}");
        _verbose?.Invoke($"manifest id: {category.ManifestId}");

        if (useCache)
        {
            var cached = await _cache.TryLoadAsync(
                region, build.Version, category.CategoryId, cancellationToken).ConfigureAwait(false);
            if (cached is not null && string.Equals(
                    cached.ManifestId, category.ManifestId, StringComparison.Ordinal))
            {
                if (cached.Entries.All(entry => entry.HasCompleteChunkMetadata))
                {
                    _verbose?.Invoke($"cache hit: {_cache.GetPath(region, build.Version, category.CategoryId)}");
                    return new ManifestFetchResult(cached, build, category, true);
                }

                _verbose?.Invoke("cache snapshot predates chunk metadata support; downloading a fresh manifest");
            }
        }

        var snapshot = await _reader.DownloadAsync(
            region, build.Version, category, cancellationToken).ConfigureAwait(false);
        _verbose?.Invoke($"file count: {snapshot.Entries.Count:N0}");
        if (useCache)
        {
            await _cache.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        return new ManifestFetchResult(snapshot, build, category, false);
    }
}
