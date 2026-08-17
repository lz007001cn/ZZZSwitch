using System.Text;
using System.Security.Cryptography;
using ZstdSharp;
using ZZZSwitch.ManifestTool.Classification;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Download;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool.Tests;

internal static class Program
{
    private const string HashA = "00112233445566778899AABBCCDDEEFF";
    private const string HashB = "FFEEDDCCBBAA99887766554433221100";

    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("完全相同", DiffSame),
        ("目标新增文件", DiffAdded),
        ("目标删除文件", DiffRemoved),
        ("MD5 变化", DiffModifiedHash),
        ("长度变化", DiffModifiedSize),
        ("差异路径稳定排序", DiffStableOrder),
        ("拒绝重复路径", DuplicatePathRejected),
        ("Windows 路径大小写不敏感", DiffCaseInsensitive),
        ("路径分隔符归一化", PathNormalized),
        ("拒绝不安全路径", UnsafePathsRejected),
        ("版本格式集中处理", VersionCandidates),
        ("getGameBranches 通过 fake HTTP 解析", BranchesViaFakeHttp),
        ("getBuild 通过 fake HTTP 解析并选择 game", BuildViaFakeHttp),
        ("API 错误返回明确异常", ApiErrorIsExplicit),
        ("manifest URL 安全拼接", ManifestUrlConstruction),
        ("chunk URL 安全拼接", ChunkUrlConstruction),
        ("Bilibili 参数未知时明确拒绝", BilibiliNotSupported),
        ("Zstd 与 protobuf 通过 fake HTTP 解析完整 chunk 元数据", ManifestViaFakeHttp),
        ("基础文件与热更新路径自动分类", ManifestClassification),
        ("chunk 布局缺口被拒绝", ChunkGapRejected),
        ("取消下载会清理未完成临时文件", CancelledDownloadCleansTemporaryFile),
        ("真实文件由 fake chunk 重建并输出 SHA-256", FileDownloadViaFakeChunks),
        ("分块下载报告连续网络进度", ChunkDownloadReportsStreamingProgress),
        ("首次下载最多四路并发且保持文件顺序", ChunkDownloadUsesBoundedParallelism),
        ("瞬时网络中断由单层分块重试自动恢复", ChunkAutomaticallyRetriesTransientNetworkFailure),
        ("取消后复用已验证分块继续下载", ChunkCheckpointResumesAfterCancellation)
    ];

    public static async Task<int> Main()
    {
        var passed = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"结果：{passed}/{Tests.Count} 通过");
        return passed == Tests.Count ? 0 : 1;
    }

    private static Task DiffSame()
    {
        var diff = Compare([Entry("a.bin")], [Entry("a.bin")]);
        Equal(1, diff.Summary.Same);
        Equal(0, diff.Summary.Modified);
        Equal(0, diff.Summary.Added);
        Equal(0, diff.Summary.Removed);
        return Task.CompletedTask;
    }

    private static Task DiffAdded()
    {
        var diff = Compare([], [Entry("added.bin")]);
        Equal(1, diff.Summary.Added);
        Equal("added.bin", diff.Added[0].Path);
        Equal(null, diff.Added[0].SourceSize);
        Equal(1L, diff.Added[0].TargetSize);
        return Task.CompletedTask;
    }

    private static Task DiffRemoved()
    {
        var diff = Compare([Entry("removed.bin")], []);
        Equal(1, diff.Summary.Removed);
        Equal("removed.bin", diff.Removed[0].Path);
        return Task.CompletedTask;
    }

    private static Task DiffModifiedHash()
    {
        var diff = Compare([Entry("a.bin", 1, HashA)], [Entry("a.bin", 1, HashB)]);
        Equal(1, diff.Summary.Modified);
        Equal(HashA, diff.Modified[0].SourceMd5);
        Equal(HashB, diff.Modified[0].TargetMd5);
        return Task.CompletedTask;
    }

    private static Task DiffModifiedSize()
    {
        var diff = Compare([Entry("a.bin", 1)], [Entry("a.bin", 2)]);
        Equal(1, diff.Summary.Modified);
        Equal(1L, diff.Modified[0].SourceSize);
        Equal(2L, diff.Modified[0].TargetSize);
        return Task.CompletedTask;
    }

    private static Task DiffStableOrder()
    {
        var diff = Compare([], [Entry("z.bin"), Entry("A.bin"), Entry("b.bin")]);
        SequenceEqual(["A.bin", "b.bin", "z.bin"], diff.Added.Select(item => item.Path));
        return Task.CompletedTask;
    }

    private static Task DuplicatePathRejected()
    {
        ExpectThrows<InvalidDataException>(() => Snapshot(
            SophonRegion.OS,
            [Entry("Data/A.bin"), Entry("data\\a.bin")]));
        return Task.CompletedTask;
    }

    private static Task DiffCaseInsensitive()
    {
        var diff = Compare([Entry("Data/A.bin")], [Entry("data\\a.bin")]);
        Equal(1, diff.Summary.Same);
        return Task.CompletedTask;
    }

    private static Task PathNormalized()
    {
        Equal("Data\\file.bin", Entry("Data//file.bin").Path);
        return Task.CompletedTask;
    }

    private static Task UnsafePathsRejected()
    {
        foreach (var path in new[] { ".", "../a.bin", "C:\\a.bin", "\\\\server\\a.bin", "a/../../b" })
        {
            ExpectThrows<InvalidDataException>(() => Entry(path));
        }

        return Task.CompletedTask;
    }

    private static Task VersionCandidates()
    {
        SequenceEqual(["3.1.0", "3.1.0.0"], SophonClient.GetVersionCandidates("3.1.0"));
        SequenceEqual(["3.1.0.0", "3.1.0"], SophonClient.GetVersionCandidates("3.1.0.0"));
        ExpectThrows<ArgumentException>(() => SophonClient.GetVersionCandidates("3.1"));
        return Task.CompletedTask;
    }

    private static async Task BranchesViaFakeHttp()
    {
        var transport = new FakeTransport
        {
            Strings = new Queue<string>(
            ["""
            {"retcode":0,"data":{"game_branches":[{"game":{"id":"U5hbdsT9W7"},"main":{"package_id":"pkg","password":"secret","tag":"3.1.0"}}]}}
            """])
        };
        var logs = new List<string>();
        var result = await new SophonClient(transport, logs.Add).GetGameBranchesAsync(SophonRegion.OS);
        Equal("pkg", result.Branches[0].Main.PackageId);
        Equal("3.1.0", result.Branches[0].Main.Version);
        True(result.Branches[0].Main.HasPassword);
        True(logs.All(log => !log.Contains("secret", StringComparison.Ordinal)));
    }

    private static async Task BuildViaFakeHttp()
    {
        var transport = new FakeTransport
        {
            Strings = new Queue<string>(
            ["""
            {"retcode":0,"data":{"tag":"3.1.0","manifests":[{"category_id":10037,"matching_field":"game","manifest":{"id":"manifest_abc"},"manifest_download":{"url_prefix":"https://example.test/manifests","url_suffix":""},"chunk_download":{"url_prefix":"https://example.test/chunks","url_suffix":"key=value"}}]}}
            """])
        };
        var logs = new List<string>();
        var package = new BranchPackage("pkg", "secret", "3.1.0");
        var build = await new SophonClient(transport, logs.Add).GetBuildAsync(
            SophonRegion.OS, package, "3.1.0");
        Equal("3.1.0", build.Version);
        Equal("10037", SophonClient.SelectManifest(build).CategoryId);
        True(transport.Requests.Single().Query.Contains("password=secret", StringComparison.Ordinal));
        True(logs.All(log => !log.Contains("secret", StringComparison.Ordinal)));
    }

    private static Task ApiErrorIsExplicit()
    {
        var error = ExpectThrows<SophonApiException>(() =>
            SophonClient.ParseBuild("{\"retcode\":-202,\"message\":\"not found\",\"data\":{}}", SophonRegion.CN, "3.1.0"));
        Equal(-202, error.RetCode);
        True(error.Message.Contains("not found", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static Task ManifestUrlConstruction()
    {
        var category = Category(suffix: "token=secret value");
        var uri = SophonClient.BuildManifestUri(category);
        Equal("https://example.test/manifests/manifest_abc?token=secret%20value", uri.AbsoluteUri);
        Equal("https://example.test/manifests/manifest_abc?<redacted>", HttpSophonTransport.Redact(uri));
        return Task.CompletedTask;
    }

    private static Task ChunkUrlConstruction()
    {
        var category = Category(chunkSuffix: "token=secret value");
        var uri = SophonClient.BuildChunkUri(category, "chunk_abc");
        Equal("https://example.test/chunks/chunk_abc?token=secret%20value", uri.AbsoluteUri);
        ExpectThrows<InvalidDataException>(() => SophonClient.BuildChunkUri(category, "../escape"));
        return Task.CompletedTask;
    }

    private static Task BilibiliNotSupported()
    {
        var error = ExpectThrows<NotSupportedException>(() =>
            SophonRegionConfig.For(SophonRegion.Bilibili));
        Equal("Bilibili manifest source has not been identified yet.", error.Message);
        return Task.CompletedTask;
    }

    private static async Task ManifestViaFakeHttp()
    {
        var chunk = Message(
            StringField(1, "chunk"),
            StringField(2, HashA),
            VarintField(4, 100),
            VarintField(5, 123));
        var asset = Message(
            StringField(1, "Data/file.bin"),
            MessageField(2, chunk),
            VarintField(4, 123),
            StringField(5, HashA));
        var protobuf = Message(MessageField(1, asset));
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(protobuf).ToArray();
        var transport = new FakeTransport { Bytes = compressed };
        var snapshot = await new SophonManifestReader(transport).DownloadAsync(
            SophonRegion.OS, "3.1.0", Category());
        Equal(1, snapshot.Entries.Count);
        Equal("Data\\file.bin", snapshot.Entries[0].Path);
        Equal(123L, snapshot.Entries[0].Size);
        Equal(HashA, snapshot.Entries[0].Md5);
        Equal(1, snapshot.Entries[0].ChunkCount);
        Equal("chunk", snapshot.Entries[0].Chunks[0].Name);
        Equal(HashA, snapshot.Entries[0].Chunks[0].DecompressedMd5);
        Equal(100L, snapshot.Entries[0].Chunks[0].CompressedSize);
        Equal(123L, snapshot.Entries[0].Chunks[0].DecompressedSize);
        True(snapshot.Entries[0].HasCompleteChunkMetadata);
    }

    private static Task ManifestClassification()
    {
        var target = new[]
        {
            Entry("UnityPlayer.dll"),
            Entry("ZenlessZoneZero_Data/StreamingAssets/Blocks/1.blk"),
            Entry("ZenlessZoneZero_Data/Persistent/Blocks/2.blk"),
            Entry("ZenlessZoneZero_Data/StreamingAssets/data_version"),
            Entry("Unknown/content.bin")
        };
        var report = new ManifestClassifier().Classify(Compare([], target));
        Equal(1, report.Summary.BaseClient);
        Equal(1, report.Summary.BaseResource);
        Equal(1, report.Summary.RuntimeHotUpdate);
        Equal(1, report.Summary.StateMetadata);
        Equal(1, report.Summary.NeedsObservation);
        Equal(
            ManifestFileClass.BaseResource,
            report.Files.Single(file => file.Path.EndsWith("1.blk", StringComparison.Ordinal)).FileClass);
        Equal(
            ManifestFileClass.RuntimeHotUpdate,
            report.Files.Single(file => file.Path.EndsWith("2.blk", StringComparison.Ordinal)).FileClass);
        return Task.CompletedTask;
    }

    private static Task ChunkGapRejected()
    {
        var chunk = new ManifestChunk("chunk", HashA, 1, 10, 1);
        var entry = new ManifestEntry("file.bin", 2, HashA, 1, [chunk]);
        ExpectThrows<InvalidDataException>(() => SophonFileDownloader.ValidateChunkLayout(entry));
        return Task.CompletedTask;
    }

    private static async Task FileDownloadViaFakeChunks()
    {
        var first = Encoding.UTF8.GetBytes("hello ");
        var second = Encoding.UTF8.GetBytes("world");
        using var compressor = new Compressor(3);
        var compressedFirst = compressor.Wrap(first).ToArray();
        var compressedSecond = compressor.Wrap(second).ToArray();
        var complete = first.Concat(second).ToArray();
        var chunks = new[]
        {
            new ManifestChunk("chunk-a", Md5(first), 0, compressedFirst.Length, first.Length),
            new ManifestChunk("chunk-b", Md5(second), first.Length, compressedSecond.Length, second.Length)
        };
        var entry = new ManifestEntry("Data/test.bin", complete.Length, Md5(complete), chunks.Length, chunks);
        var transport = new FakeTransport
        {
            ByteResponses = new Queue<byte[]>([compressedFirst, compressedSecond])
        };
        var root = Path.Combine(Path.GetTempPath(), $"zzzswitch-manifest-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var downloader = new SophonFileDownloader(transport);
            var progressValues = new List<SophonFileDownloadProgress>();
            var downloaded = await downloader.DownloadAsync(
                entry,
                Category(),
                root,
                default,
                new CaptureProgress<SophonFileDownloadProgress>(progressValues));
            Equal(Convert.ToHexString(SHA256.HashData(complete)), downloaded.Sha256);
            True(!downloaded.ReusedExistingFile);
            True(progressValues.Count > 2);
            Equal(complete.LongLength, progressValues[^1].FileBytesCompleted);
            Equal(2, progressValues[^1].ChunksCompleted);
            SequenceEqual(complete, await File.ReadAllBytesAsync(Path.Combine(root, "Data", "test.bin")));

            progressValues.Clear();
            var reused = await downloader.DownloadAsync(
                entry,
                Category(),
                root,
                default,
                new CaptureProgress<SophonFileDownloadProgress>(progressValues));
            True(reused.ReusedExistingFile);
            True(progressValues.Any(item => item.VerifyingExistingFile));
            True(progressValues[^1].ReusedExistingFile);
            Equal(2, transport.Requests.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ChunkDownloadReportsStreamingProgress()
    {
        var plain = Encoding.UTF8.GetBytes("streaming progress test payload");
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(plain).ToArray();
        var chunk = new ManifestChunk("chunk-progress", Md5(plain), 0, compressed.Length, plain.Length);
        var entry = new ManifestEntry("Data/progress.bin", plain.Length, Md5(plain), 1, [chunk]);
        var transport = new FakeTransport { ByteResponses = new Queue<byte[]>([compressed]) };
        var root = Path.Combine(Path.GetTempPath(), $"zzzswitch-progress-{Guid.NewGuid():N}");
        try
        {
            var values = new List<SophonFileDownloadProgress>();
            await new SophonFileDownloader(transport).DownloadAsync(
                entry,
                Category(),
                root,
                default,
                new CaptureProgress<SophonFileDownloadProgress>(values));

            True(values.Any(item =>
                item.CurrentChunkBytesDownloaded > 0 &&
                item.CurrentChunkBytesDownloaded < item.CurrentChunkBytesTotal));
            Equal(compressed.LongLength, values.Max(item => item.CurrentChunkBytesDownloaded));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ChunkCheckpointResumesAfterCancellation()
    {
        var first = Encoding.UTF8.GetBytes("checkpoint-a");
        var second = Encoding.UTF8.GetBytes("checkpoint-b");
        using var compressor = new Compressor(3);
        var compressedFirst = compressor.Wrap(first).ToArray();
        var compressedSecond = compressor.Wrap(second).ToArray();
        var complete = first.Concat(second).ToArray();
        var chunks = new[]
        {
            new ManifestChunk("resume-a", Md5(first), 0, compressedFirst.Length, first.Length),
            new ManifestChunk("resume-b", Md5(second), first.Length, compressedSecond.Length, second.Length)
        };
        var entry = new ManifestEntry("Data/resume.bin", complete.Length, Md5(complete), 2, chunks);
        var root = Path.Combine(Path.GetTempPath(), $"zzzswitch-resume-{Guid.NewGuid():N}");
        var cache = Path.Combine(root, "chunks");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var interrupted = new CancelOnSecondRequestTransport(cancellation, compressedFirst);
            try
            {
                await new SophonFileDownloader(interrupted).DownloadAsync(
                    entry, Category(), Path.Combine(root, "content"), cancellation.Token, null, cache);
                throw new InvalidOperationException("Expected cancellation.");
            }
            catch (OperationCanceledException)
            {
            }

            Equal(1, Directory.EnumerateFiles(cache, "*.plain", SearchOption.AllDirectories).Count());
            var retry = new FakeTransport { ByteResponses = new Queue<byte[]>([compressedSecond]) };
            var values = new List<SophonFileDownloadProgress>();
            await new SophonFileDownloader(retry).DownloadAsync(
                entry,
                Category(),
                Path.Combine(root, "content"),
                default,
                new CaptureProgress<SophonFileDownloadProgress>(values),
                cache);

            Equal(1, retry.Requests.Count);
            True(values.Any(item => item.ReusedChunkCache));
            SequenceEqual(
                complete,
                await File.ReadAllBytesAsync(Path.Combine(root, "content", "Data", "resume.bin")));
            Equal(0, Directory.EnumerateFiles(cache, "*.plain", SearchOption.AllDirectories).Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ChunkDownloadUsesBoundedParallelism()
    {
        var plainChunks = Enumerable.Range(0, 8)
            .Select(index => Encoding.UTF8.GetBytes($"parallel-{index:D2}"))
            .ToArray();
        using var compressor = new Compressor(3);
        var compressedChunks = plainChunks
            .Select(bytes => compressor.Wrap(bytes).ToArray())
            .ToArray();
        long offset = 0;
        var chunks = plainChunks.Select((plain, index) =>
        {
            var chunk = new ManifestChunk(
                $"parallel-{index:D2}",
                Md5(plain),
                offset,
                compressedChunks[index].Length,
                plain.Length);
            offset += plain.Length;
            return chunk;
        }).ToArray();
        var complete = plainChunks.SelectMany(bytes => bytes).ToArray();
        var entry = new ManifestEntry("Data/parallel.bin", complete.Length, Md5(complete), chunks.Length, chunks);
        var transport = new ConcurrentTrackingTransport(compressedChunks);
        var root = Path.Combine(Path.GetTempPath(), $"zzzswitch-parallel-{Guid.NewGuid():N}");
        try
        {
            await new SophonFileDownloader(transport).DownloadAsync(entry, Category(), root);
            True(transport.MaximumConcurrency > 1);
            True(transport.MaximumConcurrency <= 4);
            SequenceEqual(complete, await File.ReadAllBytesAsync(Path.Combine(root, "Data", "parallel.bin")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ChunkAutomaticallyRetriesTransientNetworkFailure()
    {
        var plain = Encoding.UTF8.GetBytes("retry after transient EOF");
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(plain).ToArray();
        var chunk = new ManifestChunk("retry-chunk", Md5(plain), 0, compressed.Length, plain.Length);
        var entry = new ManifestEntry("Data/retry.bin", plain.Length, Md5(plain), 1, [chunk]);
        var transport = new FlakyProgressiveTransport(3, compressed);
        var root = Path.Combine(Path.GetTempPath(), $"zzzswitch-retry-{Guid.NewGuid():N}");
        try
        {
            var values = new List<SophonFileDownloadProgress>();
            await new SophonFileDownloader(transport).DownloadAsync(
                entry,
                Category(),
                root,
                default,
                new CaptureProgress<SophonFileDownloadProgress>(values));

            Equal(4, transport.RequestCount);
            True(values.Any(item => item.DownloadAttempt == 4));
            SequenceEqual(plain, await File.ReadAllBytesAsync(Path.Combine(root, "Data", "retry.bin")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CancelledDownloadCleansTemporaryFile()
    {
        var entry = new ManifestEntry(
            "Data/cancel.bin",
            1,
            HashA,
            1,
            [new ManifestChunk("chunk-cancel", HashA, 0, 1, 1)]);
        var root = Path.Combine(Path.GetTempPath(), $"zzzswitch-manifest-cancel-{Guid.NewGuid():N}");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await new SophonFileDownloader(new FakeTransport()).DownloadAsync(
                    entry, Category(), root, cancellation.Token);
                throw new InvalidOperationException("Expected cancellation.");
            }
            catch (OperationCanceledException)
            {
            }

            True(!File.Exists(Path.Combine(root, "Data", "cancel.bin")));
            True(!Directory.Exists(root) ||
                 !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ManifestDiff Compare(
        IReadOnlyList<ManifestEntry> source,
        IReadOnlyList<ManifestEntry> target) => new ManifestDiffEngine().Compare(
            Snapshot(SophonRegion.OS, source),
            Snapshot(SophonRegion.CN, target));

    private static ManifestSnapshot Snapshot(
        SophonRegion region,
        IReadOnlyList<ManifestEntry> entries) => new(
            SophonRegionConfig.Game,
            region,
            "3.1.0",
            "game",
            $"manifest-{region}",
            DateTimeOffset.UnixEpoch,
            entries);

    private static ManifestEntry Entry(string path, long size = 1, string md5 = HashA) =>
        new(path, size, md5);

    private static ManifestCategory Category(string suffix = "", string chunkSuffix = "") => new(
        "10037",
        "game",
        "manifest_abc",
        "https://example.test/manifests",
        suffix,
        "https://example.test/chunks",
        chunkSuffix);

    private static string Md5(byte[] value) => Convert.ToHexString(MD5.HashData(value));

    private sealed class CaptureProgress<T>(List<T> values) : IProgress<T>
    {
        public void Report(T value) => values.Add(value);
    }

    private static byte[] Message(params byte[][] fields) => fields.SelectMany(field => field).ToArray();

    private static byte[] StringField(int field, string value) =>
        LengthDelimitedField(field, Encoding.UTF8.GetBytes(value));

    private static byte[] MessageField(int field, byte[] value) => LengthDelimitedField(field, value);

    private static byte[] LengthDelimitedField(int field, byte[] value) =>
        Message(Varint((ulong)((field << 3) | 2)), Varint((ulong)value.Length), value);

    private static byte[] VarintField(int field, ulong value) =>
        Message(Varint((ulong)(field << 3)), Varint(value));

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
            {
                next |= 0x80;
            }

            bytes.Add(next);
        }
        while (value != 0);
        return bytes.ToArray();
    }

    private static TException ExpectThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private sealed class FakeTransport : ISophonTransport, IProgressiveSophonTransport
    {
        public Queue<string> Strings { get; init; } = new();
        public byte[] Bytes { get; init; } = [];
        public Queue<byte[]> ByteResponses { get; init; } = new();
        public List<Uri> Requests { get; } = [];

        public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Requests.Add(uri);
            return Task.FromResult(Strings.Dequeue());
        }

        public Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Requests.Add(uri);
            return Task.FromResult(ByteResponses.Count > 0 ? ByteResponses.Dequeue() : Bytes);
        }

        public Task<byte[]> GetBytesAsync(
            Uri uri,
            IProgress<long> bytesReceived,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(uri);
            var response = ByteResponses.Count > 0 ? ByteResponses.Dequeue() : Bytes;
            if (response.LongLength > 1)
            {
                bytesReceived.Report(response.LongLength / 2);
            }

            bytesReceived.Report(response.LongLength);
            return Task.FromResult(response);
        }
    }

    private sealed class CancelOnSecondRequestTransport(
        CancellationTokenSource cancellation,
        byte[] firstResponse) : ISophonTransport, IProgressiveSophonTransport
    {
        private int _requests;

        public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default) =>
            GetBytesAsync(uri, new CaptureProgress<long>([]), cancellationToken);

        public Task<byte[]> GetBytesAsync(
            Uri uri,
            IProgress<long> bytesReceived,
            CancellationToken cancellationToken = default)
        {
            _requests++;
            if (_requests == 1)
            {
                bytesReceived.Report(firstResponse.LongLength);
                return Task.FromResult(firstResponse);
            }

            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FlakyProgressiveTransport(
        int failuresBeforeSuccess,
        byte[] response) : ISophonTransport, IProgressiveSophonTransport
    {
        public int RequestCount { get; private set; }

        public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default) =>
            GetBytesAsync(uri, new CaptureProgress<long>([]), cancellationToken);

        public Task<byte[]> GetBytesAsync(
            Uri uri,
            IProgress<long> bytesReceived,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            if (RequestCount <= failuresBeforeSuccess)
            {
                throw new HttpRequestException("unexpected EOF");
            }

            bytesReceived.Report(response.LongLength);
            return Task.FromResult(response);
        }
    }

    private sealed class ConcurrentTrackingTransport(byte[][] responses)
        : ISophonTransport, IProgressiveSophonTransport
    {
        private readonly IReadOnlyDictionary<string, byte[]> _responses = responses
            .Select((response, index) => new KeyValuePair<string, byte[]>($"parallel-{index:D2}", response))
            .ToDictionary();
        private int _active;
        private int _maximumConcurrency;

        public int MaximumConcurrency => _maximumConcurrency;

        public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default) =>
            GetBytesAsync(uri, new CaptureProgress<long>([]), cancellationToken);

        public async Task<byte[]> GetBytesAsync(
            Uri uri,
            IProgress<long> bytesReceived,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var current = _maximumConcurrency;
                if (active <= current ||
                    Interlocked.CompareExchange(ref _maximumConcurrency, active, current) == current)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(40, cancellationToken);
                var name = Uri.UnescapeDataString(uri.Segments[^1]).TrimEnd('/');
                var response = _responses[name];

                bytesReceived.Report(response.LongLength);
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
