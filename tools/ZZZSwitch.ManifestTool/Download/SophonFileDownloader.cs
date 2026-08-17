using System.Security.Cryptography;
using ZstdSharp;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool.Download;

public sealed class SophonFileDownloader
{
    private const int MaximumChunkAttempts = 6;
    private const int MaximumParallelChunks = 4;
    private readonly ISophonTransport _transport;
    private readonly Action<string>? _verbose;

    public SophonFileDownloader(ISophonTransport transport, Action<string>? verbose = null)
    {
        _transport = transport;
        _verbose = verbose;
    }

    public async Task<DownloadedManifestFile> DownloadAsync(
        ManifestEntry entry,
        ManifestCategory category,
        string outputRoot,
        CancellationToken cancellationToken = default,
        IProgress<SophonFileDownloadProgress>? progress = null,
        string? chunkCacheRoot = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(category);
        var root = Path.GetFullPath(outputRoot);
        var destination = ResolveUnderRoot(root, entry.Path);
        EnsureNoReparsePoints(root, destination);
        if (File.Exists(destination) &&
            (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Download destination is a reparse point: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(destination) && new FileInfo(destination).Length == entry.Size)
        {
            var existing = await HashFileAsync(
                destination,
                cancellationToken,
                bytes => progress?.Report(new(
                    entry.Path,
                    bytes,
                    entry.Size,
                    0,
                    entry.Chunks.Count,
                    false,
                    VerifyingExistingFile: true))).ConfigureAwait(false);
            if (string.Equals(existing.Md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
            {
                _verbose?.Invoke($"reuse verified file: {entry.Path}");
                progress?.Report(new(
                    entry.Path, entry.Size, entry.Size, entry.Chunks.Count, entry.Chunks.Count, true));
                return new DownloadedManifestFile(
                    entry.Path, entry.Size, entry.Md5, existing.Sha256, true);
            }
        }

        ValidateChunkLayout(entry);
        var temporary = $"{destination}.sophon-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                stream.SetLength(entry.Size);
                long completedBytes = 0;
                var orderedChunks = entry.Chunks.OrderBy(chunk => chunk.FileOffset).ToArray();
                var progressGate = new object();
                for (var batchStart = 0; batchStart < orderedChunks.Length; batchStart += MaximumParallelChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = orderedChunks
                        .Skip(batchStart)
                        .Take(MaximumParallelChunks)
                        .ToArray();
                    var receivedByChunk = new long[batch.Length];
                    var estimatedByChunk = new long[batch.Length];
                    var attemptByChunk = new int[batch.Length];
                    var completedBeforeBatch = completedBytes;
                    var batchCompressedBytes = batch.Aggregate(
                        0L, (sum, chunk) => checked(sum + chunk.CompressedSize));
                    var tasks = batch.Select((chunk, batchIndex) =>
                    {
                        var absoluteIndex = batchStart + batchIndex + 1;
                        _verbose?.Invoke(
                            $"{entry.Path}: chunk {absoluteIndex}/{entry.Chunks.Count} ({chunk.Name})");
                        return DownloadChunkAsync(
                            category,
                            chunk,
                            chunkCacheRoot,
                            (received, attempt) =>
                            {
                                lock (progressGate)
                                {
                                    receivedByChunk[batchIndex] = received;
                                    attemptByChunk[batchIndex] = attempt;
                                    estimatedByChunk[batchIndex] = chunk.CompressedSize == 0
                                        ? 0L
                                        : (long)Math.Min(
                                            chunk.DecompressedSize,
                                            (double)received / chunk.CompressedSize * chunk.DecompressedSize);
                                    progress?.Report(new(
                                        entry.Path,
                                        checked(completedBeforeBatch + estimatedByChunk.Sum()),
                                        entry.Size,
                                        batchStart,
                                        entry.Chunks.Count,
                                        false,
                                        receivedByChunk.Sum(),
                                        batchCompressedBytes,
                                        DownloadAttempt: Math.Max(1, attemptByChunk.Max()),
                                        MaximumDownloadAttempts: MaximumChunkAttempts));
                                }
                            },
                            cancellationToken);
                    }).ToArray();
                    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                    for (var batchIndex = 0; batchIndex < batch.Length; batchIndex++)
                    {
                        var chunk = batch[batchIndex];
                        var chunkResult = results[batchIndex];
                        stream.Position = chunk.FileOffset;
                        await stream.WriteAsync(chunkResult.Plain, cancellationToken).ConfigureAwait(false);
                        completedBytes = checked(completedBytes + chunk.DecompressedSize);
                        progress?.Report(new(
                            entry.Path,
                            completedBytes,
                            entry.Size,
                            batchStart + batchIndex + 1,
                            entry.Chunks.Count,
                            false,
                            receivedByChunk.Sum(),
                            batchCompressedBytes,
                            chunkResult.ReusedCache,
                            DownloadAttempt: Math.Max(1, attemptByChunk.Max()),
                            MaximumDownloadAttempts: MaximumChunkAttempts));
                    }
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var hashes = await HashFileAsync(
                temporary,
                cancellationToken,
                _ => progress?.Report(new(
                    entry.Path,
                    entry.Size,
                    entry.Size,
                    entry.Chunks.Count,
                    entry.Chunks.Count,
                    false,
                    VerifyingExistingFile: true))).ConfigureAwait(false);
            if (!string.Equals(hashes.Md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Reconstructed file MD5 mismatch for '{entry.Path}': expected {entry.Md5}, actual {hashes.Md5}.");
            }

            File.Move(temporary, destination, overwrite: true);
            CleanupChunkCache(chunkCacheRoot, entry);
            return new DownloadedManifestFile(
                entry.Path, entry.Size, entry.Md5, hashes.Sha256, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void ValidateChunkLayout(ManifestEntry entry)
    {
        if (!entry.HasCompleteChunkMetadata)
        {
            throw new InvalidDataException(
                $"Manifest entry '{entry.Path}' does not contain complete chunk metadata. Refresh the manifest with --no-cache.");
        }

        long expectedOffset = 0;
        foreach (var chunk in entry.Chunks.OrderBy(chunk => chunk.FileOffset))
        {
            if (chunk.FileOffset != expectedOffset)
            {
                throw new InvalidDataException(
                    $"Manifest chunks for '{entry.Path}' have a gap or overlap at offset {expectedOffset}.");
            }

            expectedOffset = checked(chunk.FileOffset + chunk.DecompressedSize);
        }

        if (expectedOffset != entry.Size)
        {
            throw new InvalidDataException(
                $"Manifest chunks for '{entry.Path}' cover {expectedOffset} bytes, expected {entry.Size}.");
        }
    }

    public static string ResolveUnderRoot(string outputRoot, string relativePath)
    {
        var root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Download path escapes the output root: '{relativePath}'.");
        }

        return destination;
    }

    private async Task<(byte[] Plain, bool ReusedCache)> DownloadChunkAsync(
        ManifestCategory category,
        ManifestChunk chunk,
        string? chunkCacheRoot,
        Action<long, int>? reportNetworkProgress,
        CancellationToken cancellationToken)
    {
        var cached = await TryReadChunkCacheAsync(
            chunkCacheRoot, chunk, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return (cached, true);
        }

        Exception? lastError = null;
        var attemptsUsed = 0;
        for (var attempt = 1; attempt <= MaximumChunkAttempts; attempt++)
        {
            attemptsUsed = attempt;
            try
            {
                var uri = SophonClient.BuildChunkUri(category, chunk.Name);
                _verbose?.Invoke($"chunk URL: {HttpSophonTransport.Redact(uri)}");
                reportNetworkProgress?.Invoke(0, attempt);
                byte[] compressed;
                if (_transport is IProgressiveSophonTransport progressive)
                {
                    compressed = await progressive.GetBytesAsync(
                        uri,
                        new InlineProgress<long>(received => reportNetworkProgress?.Invoke(received, attempt)),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    compressed = await _transport.GetBytesAsync(uri, cancellationToken).ConfigureAwait(false);
                    reportNetworkProgress?.Invoke(compressed.LongLength, attempt);
                }
                if (compressed.LongLength != chunk.CompressedSize)
                {
                    throw new InvalidDataException(
                        $"Chunk '{chunk.Name}' compressed length mismatch: expected {chunk.CompressedSize}, actual {compressed.LongLength}.");
                }

                var plain = Decompress(compressed, chunk);
                var md5 = Convert.ToHexString(MD5.HashData(plain));
                if (!string.Equals(md5, chunk.DecompressedMd5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Chunk '{chunk.Name}' decompressed MD5 mismatch: expected {chunk.DecompressedMd5}, actual {md5}.");
                }

                await SaveChunkCacheAsync(
                    chunkCacheRoot, chunk, plain, cancellationToken).ConfigureAwait(false);
                return (plain, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                IsRetryableChunkFailure(ex) && attempt < MaximumChunkAttempts)
            {
                lastError = ex;
                _verbose?.Invoke(
                    $"chunk {chunk.Name} attempt {attempt}/{MaximumChunkAttempts} failed: {ex.Message}");
                var delayMilliseconds = Math.Min(8000, 500 * (1 << (attempt - 1)));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        var reason = lastError?.GetBaseException().Message ?? lastError?.Message ?? "未知网络错误";
        throw new IOException(
            $"分块 '{chunk.Name}' 连续 {attemptsUsed} 次下载或校验失败。" +
            $"已验证的断点已经保留，点击“重试”会继续。最后错误：{reason}",
            lastError);
    }

    private static bool IsRetryableChunkFailure(Exception exception)
    {
        if (exception is InvalidDataException or IOException or TaskCanceledException)
        {
            return true;
        }

        if (exception is not HttpRequestException http)
        {
            return false;
        }

        var status = http.StatusCode;
        return status is null || status == System.Net.HttpStatusCode.RequestTimeout ||
               status == System.Net.HttpStatusCode.TooManyRequests || (int)status >= 500;
    }

    private static byte[] Decompress(byte[] compressed, ManifestChunk chunk)
    {
        if (chunk.DecompressedSize > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Chunk '{chunk.Name}' is too large to decompress safely in memory.");
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var zstd = new DecompressionStream(input);
        using var output = new MemoryStream((int)chunk.DecompressedSize);
        var buffer = new byte[81920];
        while (true)
        {
            var read = zstd.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > chunk.DecompressedSize)
            {
                throw new InvalidDataException(
                    $"Chunk '{chunk.Name}' decompressed beyond its declared size.");
            }

            output.Write(buffer, 0, read);
        }

        if (output.Length != chunk.DecompressedSize)
        {
            throw new InvalidDataException(
                $"Chunk '{chunk.Name}' decompressed to {output.Length} bytes, expected {chunk.DecompressedSize}.");
        }

        return output.ToArray();
    }

    private static async Task<(string Md5, string Sha256)> HashFileAsync(
        string path,
        CancellationToken cancellationToken,
        Action<long>? progress = null)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            md5.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
            total = checked(total + read);
            progress?.Invoke(total);
        }

        return (
            Convert.ToHexString(md5.GetHashAndReset()),
            Convert.ToHexString(sha256.GetHashAndReset()));
    }

    private static async Task<byte[]?> TryReadChunkCacheAsync(
        string? chunkCacheRoot,
        ManifestChunk chunk,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chunkCacheRoot))
        {
            return null;
        }

        var path = ResolveChunkCachePath(chunkCacheRoot, chunk);
        if (!File.Exists(path) || new FileInfo(path).Length != chunk.DecompressedSize)
        {
            return null;
        }

        var plain = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var md5 = Convert.ToHexString(MD5.HashData(plain));
        if (string.Equals(md5, chunk.DecompressedMd5, StringComparison.OrdinalIgnoreCase))
        {
            return plain;
        }

        File.Delete(path);
        return null;
    }

    private static async Task SaveChunkCacheAsync(
        string? chunkCacheRoot,
        ManifestChunk chunk,
        byte[] plain,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chunkCacheRoot))
        {
            return;
        }

        var destination = ResolveChunkCachePath(chunkCacheRoot, chunk);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, plain, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ResolveChunkCachePath(string root, ManifestChunk chunk)
    {
        var cacheRoot = Path.GetFullPath(root);
        var prefix = chunk.Name.Length >= 2 ? chunk.Name[..2] : "__";
        var path = ResolveUnderRoot(cacheRoot, Path.Combine(prefix, chunk.Name + ".plain"));
        EnsureNoReparsePoints(cacheRoot, path);
        return path;
    }

    private static void CleanupChunkCache(string? chunkCacheRoot, ManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(chunkCacheRoot))
        {
            return;
        }

        foreach (var chunk in entry.Chunks)
        {
            var path = ResolveChunkCachePath(chunkCacheRoot, chunk);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void EnsureNoReparsePoints(string root, string destination)
    {
        var rootFull = Path.GetFullPath(root);
        if (Directory.Exists(rootFull) &&
            (File.GetAttributes(rootFull) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Download output root is a reparse point: {rootFull}");
        }

        var current = Path.GetDirectoryName(destination);
        while (current is not null && current.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Download path crosses a reparse point: {current}");
            }

            if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
