namespace ZZZSwitch.ManifestTool.Sophon;

public interface ISophonTransport
{
    Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default);
    Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default);
}

public interface IProgressiveSophonTransport
{
    Task<byte[]> GetBytesAsync(
        Uri uri,
        IProgress<long> bytesReceived,
        CancellationToken cancellationToken = default);
}

public sealed class HttpSophonTransport : ISophonTransport, IProgressiveSophonTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpSophonTransport(TimeSpan? timeout = null)
    {
        _client = new HttpClient
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ZZZSwitch.ManifestTool/0.1-test");
    }

    public async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            () => _client.GetStringAsync(uri, cancellationToken),
            uri,
            "metadata",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            () => DownloadBytesAsync(uri, null, cancellationToken),
            uri,
            "manifest",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> GetBytesAsync(
        Uri uri,
        IProgress<long> bytesReceived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytesReceived);
        // File chunks use the downloader's single visible retry loop. Keeping another
        // retry loop here made one failed chunk look frozen for many minutes.
        return await DownloadBytesAsync(uri, bytesReceived, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    public static string Redact(Uri uri) =>
        string.IsNullOrEmpty(uri.Query)
            ? uri.GetLeftPart(UriPartial.Path)
            : $"{uri.GetLeftPart(UriPartial.Path)}?<redacted>";

    private async Task<byte[]> DownloadBytesAsync(
        Uri uri,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var declaredLength = response.Content.Headers.ContentLength;
        const long maximumResponseBytes = 256L * 1024 * 1024;
        if (declaredLength > maximumResponseBytes)
        {
            throw new InvalidDataException($"Sophon response is too large: {declaredLength:N0} bytes.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = declaredLength is > 0 and <= int.MaxValue
            ? new MemoryStream((int)declaredLength.Value)
            : new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumResponseBytes)
            {
                throw new InvalidDataException(
                    $"Sophon response exceeded the {maximumResponseBytes:N0}-byte safety limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(total);
        }

        return output.ToArray();
    }

    private static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        Uri uri,
        string requestKind,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
                if (attempt == 3 || !ShouldRetry(ex, cancellationToken))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new HttpRequestException(
            $"HTTP {requestKind} request failed for {Redact(uri)} after retries: " +
            $"{lastError?.GetBaseException().Message ?? lastError?.Message}",
            lastError);
    }

    private static bool ShouldRetry(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TaskCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        var status = (exception as HttpRequestException)?.StatusCode;
        return status is null || status == System.Net.HttpStatusCode.RequestTimeout ||
               status == System.Net.HttpStatusCode.TooManyRequests || (int)status >= 500;
    }
}
