using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace ZZZSwitch.Update;

public enum UpdateCheckStatus { UpToDate, Available, ManualUpgradeRequired, Unavailable }
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest, UpdateRelease? Release = null);
public sealed record UpdateDownloadProgress(long Received, long Total, double BytesPerSecond, bool Verifying = false);
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(string endpoint, string currentVersion, string channel, CancellationToken cancellationToken);
    Task<string> DownloadUpdateAsync(UpdateManifest manifest, string jobDirectory, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken);
    Task VerifyPackageAsync(string path, UpdatePackage package, CancellationToken cancellationToken);
}

public sealed class UpdateService : IUpdateService
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _readTimeout;
    private readonly IUpdateSource? _source;
    public UpdateService(HttpClient? http = null, TimeSpan? checkTimeout = null, TimeSpan? readTimeout = null, IUpdateSource? source = null)
    { _source = source; _http = http ?? SharedClient; _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(20); _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30); }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string endpoint, string currentVersion, string channel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new UpdateException(UpdateError.NotConfigured, "No update endpoint configured.");
        var uri = UpdateManifestParser.ValidateUrl(endpoint);
        var current = UpdateVersion.ParseForUpdateCheck(currentVersion, channel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_checkTimeout);
        try
        {
            var source = _source ?? (endpoint == GitHubReleaseUpdateSource.Endpoint
                ? (IUpdateSource)new GitHubReleaseUpdateSource(_http) : new ManifestUpdateSource(_http));
            var release = await source.GetLatestAsync(uri.AbsoluteUri, currentVersion, timeout.Token).ConfigureAwait(false);
            var m = release.Manifest;
            if ((m?.Channel ?? "stable") != channel) throw new UpdateException(UpdateError.InvalidManifest, "Update channel does not match settings.");
            var status = current.CompareTo(UpdateVersion.Parse(release.Version)) >= 0 ? UpdateCheckStatus.UpToDate
                : m is null ? UpdateCheckStatus.Unavailable
                : current.CompareTo(UpdateVersion.Parse(m.MinSupportedVersion)) < 0 ? UpdateCheckStatus.ManualUpgradeRequired : UpdateCheckStatus.Available;
            return new(status, m, release);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new UpdateException(UpdateError.Timeout, "Update check timed out.", ex); }
        catch (HttpRequestException ex) { throw new UpdateException(UpdateError.Network, "Update server could not be reached.", ex); }
        catch (IOException ex) { throw new UpdateException(UpdateError.Network, "Update response was interrupted.", ex); }
    }

    public async Task<string> DownloadUpdateAsync(UpdateManifest manifest, string jobDirectory, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        UpdateManifestParser.Validate(manifest);
        UpdatePaths.EnsureOrdinary(jobDirectory);
        Directory.CreateDirectory(jobDirectory);
        var partial = UpdatePaths.Under(jobDirectory, "package.part");
        var complete = UpdatePaths.Under(jobDirectory, "package.zip");
        if (File.Exists(partial) || File.Exists(complete)) throw new UpdateException(UpdateError.Storage, "Use a new download job directory.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            var uri = UpdateManifestParser.ValidateUrl(manifest.Package.Url);
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            headersTimeout.CancelAfter(_readTimeout);
            using var response = await UpdateHttp.GetAsync(_http, uri, manifest.Version, headersTimeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long size && size != manifest.Package.Size)
                throw new UpdateException(UpdateError.SizeMismatch, "Package Content-Length differs from manifest.");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; long total = 0; var clock = Stopwatch.StartNew(); var reported = TimeSpan.Zero;
                while (true)
                {
                    using var read = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    read.CancelAfter(_readTimeout);
                    var n = await input.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    total += n;
                    if (total > manifest.Package.Size) throw new UpdateException(UpdateError.SizeMismatch, "Package exceeds declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
                    if (clock.Elapsed - reported > TimeSpan.FromMilliseconds(100))
                    { progress?.Report(new(total, manifest.Package.Size, total / Math.Max(.001, clock.Elapsed.TotalSeconds))); reported = clock.Elapsed; }
                }
                if (total != manifest.Package.Size) throw new UpdateException(UpdateError.SizeMismatch, "Incomplete package.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                output.Flush(true);
                progress?.Report(new(total, total, 0, true));
            }
            await VerifyPackageAsync(partial, manifest.Package, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            UpdatePaths.EnsureOrdinary(complete);
            File.Move(partial, complete);
            return complete;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { throw new UpdateException(UpdateError.Timeout, "Update download timed out.", ex); }
        catch (HttpRequestException ex) { throw new UpdateException(UpdateError.Network, "Update download was interrupted.", ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new UpdateException(UpdateError.Storage, "Unable to download or save update package.", ex); }
        finally { if (File.Exists(partial)) { UpdatePaths.EnsureOrdinary(partial); File.Delete(partial); } }
    }

    public async Task VerifyPackageAsync(string path, UpdatePackage package, CancellationToken cancellationToken)
    {
        if (package.Size is <= 0 or > UpdateManifestParser.MaxPackageSize || !UpdateManifestParser.IsHash(package.Sha256))
            throw new UpdateException(UpdateError.InvalidManifest, "Invalid package size or SHA-256.");
        UpdatePaths.EnsureOrdinary(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length != package.Size) throw new UpdateException(UpdateError.SizeMismatch, "Update package size does not match.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new UpdateException(UpdateError.HashMismatch, "Update package SHA-256 does not match.");
    }

}
