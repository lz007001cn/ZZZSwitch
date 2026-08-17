using System.Security.Cryptography;
using System.Text.Json;
using ZZZSwitch.Core.Models;
using ZZZSwitch.ManifestTool;
using ZZZSwitch.ManifestTool.Classification;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Download;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.Core.Services;

public interface IOnlineDifferenceService
{
    OnlineDifferenceInventory GetInventory();

    bool TryGetReadyMaterialization(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        out OnlineDifferenceMaterialization? materialization);

    Task<OnlineDifferencePlan> AnalyzeAsync(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        string? localGamePath = null,
        CancellationToken cancellationToken = default);

    Task<OnlineManifestRefreshResult> RefreshManifestsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<OnlineManifestBrowserData> GetManifestBrowserAsync(
        string gameVersion,
        CancellationToken cancellationToken = default);

    Task<OnlineDifferenceMaterialization> MaterializeAsync(
        OnlineDifferencePlan plan,
        IProgress<OnlineDifferenceProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class OnlineDifferenceService : IOnlineDifferenceService
{
    private const string StreamingBlocksPrefix = @"ZenlessZoneZero_Data\StreamingAssets\Blocks\";
    private readonly AppPaths _paths;
    private readonly Func<ISophonTransport> _transportFactory;
    private readonly OnlineDifferencePackageCatalog _catalog;

    public OnlineDifferenceService(
        AppPaths paths,
        Func<ISophonTransport>? transportFactory = null,
        OnlineDifferencePackageCatalog? catalog = null)
    {
        _paths = paths;
        _transportFactory = transportFactory ?? (() => new HttpSophonTransport(TimeSpan.FromSeconds(45)));
        _catalog = catalog ?? new OnlineDifferencePackageCatalog(paths);
    }

    public OnlineDifferenceInventory GetInventory() => _catalog.GetInventory();

    public bool TryGetReadyMaterialization(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        out OnlineDifferenceMaterialization? materialization) =>
        _catalog.TryGetReadyMaterialization(
            sourceProfile, targetProfile, gameVersion, out materialization);

    public async Task<OnlineDifferencePlan> AnalyzeAsync(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        string? localGamePath = null,
        CancellationToken cancellationToken = default)
    {
        var sourceRegion = ToSupportedRegion(sourceProfile);
        var targetRegion = ToSupportedRegion(targetProfile);
        if (sourceRegion == targetRegion)
        {
            throw new NotSupportedException("在线差异只用于国际服与国服官服之间的资源切换。");
        }

        var transport = _transportFactory();
        try
        {
            var service = new ManifestService(
                new SophonClient(transport),
                new SophonManifestReader(transport),
                new ManifestCache(_paths.ManifestCacheRoot, JsonSupport.Options));
            var sourceTask = service.FetchAsync(
                sourceRegion, gameVersion, null, useCache: true, cancellationToken);
            var targetTask = service.FetchAsync(
                targetRegion, gameVersion, null, useCache: true, cancellationToken);
            await Task.WhenAll(sourceTask, targetTask).ConfigureAwait(false);
            var source = await sourceTask.ConfigureAwait(false);
            var target = await targetTask.ConfigureAwait(false);
            var diffEngine = new ManifestDiffEngine();
            var diff = diffEngine.Compare(source.Snapshot, target.Snapshot);
            OnlineLocalSourceCapturePlan? localSourceCapture = null;
            if (!string.IsNullOrWhiteSpace(localGamePath))
            {
                var reverseDiff = diffEngine.Compare(target.Snapshot, source.Snapshot);
                var reversePlan = BuildPlan(
                    targetProfile,
                    sourceProfile,
                    reverseDiff,
                    source.Snapshot,
                    source.Category);
                localSourceCapture = new OnlineLocalSourceCapturePlan
                {
                    SourceProfile = targetProfile,
                    TargetProfile = sourceProfile,
                    TargetManifestId = source.Snapshot.ManifestId,
                    Files = reversePlan.DownloadFiles
                };
            }

            return BuildPlan(
                sourceProfile,
                targetProfile,
                diff,
                target.Snapshot,
                target.Category,
                localGamePath,
                localSourceCapture);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }

    public async Task<OnlineManifestRefreshResult> RefreshManifestsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new ArgumentException("游戏版本不能为空。", nameof(gameVersion));
        }

        var transport = _transportFactory();
        try
        {
            var cache = new ManifestCache(_paths.ManifestCacheRoot, JsonSupport.Options);
            var service = new ManifestService(
                new SophonClient(transport),
                new SophonManifestReader(transport),
                cache);
            var globalTask = service.FetchAsync(
                SophonRegion.OS, gameVersion, null, useCache: false, cancellationToken);
            var cnTask = service.FetchAsync(
                SophonRegion.CN, gameVersion, null, useCache: false, cancellationToken);
            await Task.WhenAll(globalTask, cnTask).ConfigureAwait(false);
            var global = await globalTask.ConfigureAwait(false);
            var cn = await cnTask.ConfigureAwait(false);

            await Task.WhenAll(
                cache.SaveAsync(global.Snapshot, cancellationToken),
                cache.SaveAsync(cn.Snapshot, cancellationToken)).ConfigureAwait(false);
            var diff = new ManifestDiffEngine().Compare(global.Snapshot, cn.Snapshot);
            return new OnlineManifestRefreshResult(
                global.Snapshot.Version,
                Summary(global.Snapshot),
                Summary(cn.Snapshot),
                diff.Summary.Modified,
                diff.Summary.Added,
                diff.Summary.Removed);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }

    public async Task<OnlineManifestBrowserData> GetManifestBrowserAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new ArgumentException("游戏版本不能为空。", nameof(gameVersion));
        }

        var globalTask = LoadCachedSnapshotAsync(SophonRegion.OS, gameVersion, cancellationToken);
        var cnTask = LoadCachedSnapshotAsync(SophonRegion.CN, gameVersion, cancellationToken);
        await Task.WhenAll(globalTask, cnTask).ConfigureAwait(false);
        var global = await globalTask.ConfigureAwait(false);
        var cn = await cnTask.ConfigureAwait(false);
        return new OnlineManifestBrowserData
        {
            GameVersion = gameVersion,
            GlobalToCn = BuildBrowserDirection(ProfileIds.Global, ProfileIds.CnOfficial, global, cn),
            CnToGlobal = BuildBrowserDirection(ProfileIds.CnOfficial, ProfileIds.Global, cn, global)
        };
    }

    public async Task<OnlineDifferenceMaterialization> MaterializeAsync(
        OnlineDifferencePlan plan,
        IProgress<OnlineDifferenceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var workspace = Path.Combine(
            _paths.OnlineDifferenceFilesRoot,
            SafeSegment(plan.GameVersion),
            SafeSegment(plan.TargetProfile),
            SafeSegment(plan.TargetManifestId));
        var content = Path.Combine(workspace, "content");
        var chunkCache = Path.Combine(workspace, "chunks");
        Directory.CreateDirectory(content);
        await SeedTargetFilesFromGameAsync(
            plan, content, progress, cancellationToken).ConfigureAwait(false);
        var sourceCapture = await CaptureLocalSourceAsync(
            plan, progress, cancellationToken).ConfigureAwait(false);
        ValidateDownloadDiskSpace(plan, content);

        var transport = _transportFactory();
        var downloaded = new List<DownloadedManifestFile>(plan.DownloadFiles.Count);
        long completedBytes = 0;
        try
        {
            var downloader = new SophonFileDownloader(transport);
            for (var fileIndex = 0; fileIndex < plan.DownloadFiles.Count; fileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = plan.DownloadFiles[fileIndex];
                var completedBeforeFile = completedBytes;
                progress?.Report(new OnlineDifferenceProgress
                {
                    CurrentFile = entry.Path,
                    CompletedBytes = completedBytes,
                    TotalBytes = plan.DownloadBytes,
                    CompletedFiles = fileIndex,
                    TotalFiles = plan.DownloadFiles.Count,
                    CurrentFileChunksCompleted = 0,
                    CurrentFileChunksTotal = entry.Chunks.Count
                });
                var fileProgress = new InlineProgress<SophonFileDownloadProgress>(item =>
                    progress?.Report(new OnlineDifferenceProgress
                    {
                        CurrentFile = item.Path,
                        CompletedBytes = checked(completedBeforeFile + item.FileBytesCompleted),
                        TotalBytes = plan.DownloadBytes,
                        CompletedFiles = fileIndex,
                        TotalFiles = plan.DownloadFiles.Count,
                        CurrentFileChunksCompleted = item.ChunksCompleted,
                        CurrentFileChunksTotal = item.ChunksTotal,
                        ReusingExistingFile = item.ReusedExistingFile,
                        CurrentChunkBytesDownloaded = item.CurrentChunkBytesDownloaded,
                        CurrentChunkBytesTotal = item.CurrentChunkBytesTotal,
                        ReusingChunkCache = item.ReusedChunkCache,
                        VerifyingExistingFile = item.VerifyingExistingFile,
                        DownloadAttempt = item.DownloadAttempt,
                        MaximumDownloadAttempts = item.MaximumDownloadAttempts
                    }));
                var result = await downloader.DownloadAsync(
                    entry,
                    plan.TargetCategory,
                    content,
                    cancellationToken,
                    fileProgress,
                    chunkCache).ConfigureAwait(false);
                downloaded.Add(result);
                completedBytes = checked(completedBytes + entry.Size);
                progress?.Report(new OnlineDifferenceProgress
                {
                    CurrentFile = entry.Path,
                    CompletedBytes = completedBytes,
                    TotalBytes = plan.DownloadBytes,
                    CompletedFiles = fileIndex + 1,
                    TotalFiles = plan.DownloadFiles.Count,
                    CurrentFileChunksCompleted = entry.Chunks.Count,
                    CurrentFileChunksTotal = entry.Chunks.Count,
                    ReusingExistingFile = result.ReusedExistingFile
                });
            }
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }

        var manifest = new TransitionManifest
        {
            SourceProfile = plan.SourceProfile,
            TargetProfile = plan.TargetProfile,
            GameVersion = plan.GameVersion,
            ExpectedReplaceCount = downloaded.Count,
            ExpectedDeleteCount = plan.DeleteFiles.Count,
            ReplaceFiles = downloaded.Select(file => new ReplaceFileEntry
            {
                Source = file.Path,
                Target = file.Path,
                Length = file.Length,
                Sha256 = file.Sha256
            }).ToList(),
            DeleteFiles = plan.DeleteFiles.Select(path => new DeleteFileEntry { Target = path }).ToList(),
            Notes = "Sophon 在线差异源：已完成完整性校验；未使用 .zzzswitch\\packages。"
        };
        await WriteManifestAsync(workspace, manifest, cancellationToken).ConfigureAwait(false);
        return new OnlineDifferenceMaterialization
        {
            PackageRoot = content,
            PackageDirectory = content,
            Manifest = manifest,
            DownloadedFiles = downloaded.Count(item => !item.ReusedExistingFile),
            ReusedFiles = downloaded.Count(item => item.ReusedExistingFile),
            PreservedSourceFiles = sourceCapture.PreservedFiles,
            SourcePackageReady = sourceCapture.Ready,
            ReusedReadyPackage = false
        };
    }

    private async Task SeedTargetFilesFromGameAsync(
        OnlineDifferencePlan plan,
        string contentRoot,
        IProgress<OnlineDifferenceProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.LocalGamePath) ||
            !Directory.Exists(plan.LocalGamePath))
        {
            return;
        }

        long completedBytes = 0;
        for (var index = 0; index < plan.DownloadFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = plan.DownloadFiles[index];
            var completedBeforeFile = completedBytes;
            await TryPrepareManifestFileAsync(
                plan.LocalGamePath,
                contentRoot,
                entry,
                bytes => progress?.Report(new OnlineDifferenceProgress
                {
                    CurrentFile = entry.Path,
                    CompletedBytes = checked(completedBeforeFile + bytes),
                    TotalBytes = plan.DownloadBytes,
                    CompletedFiles = index,
                    TotalFiles = plan.DownloadFiles.Count,
                    CurrentFileChunksCompleted = 0,
                    CurrentFileChunksTotal = entry.Chunks.Count,
                    VerifyingExistingFile = true,
                    CheckingLocalFiles = true
                }),
                cancellationToken).ConfigureAwait(false);
            completedBytes = checked(completedBytes + entry.Size);
        }
    }

    private async Task<LocalSourceCaptureResult> CaptureLocalSourceAsync(
        OnlineDifferencePlan plan,
        IProgress<OnlineDifferenceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var capture = plan.LocalSourceCapture;
        if (capture is null || string.IsNullOrWhiteSpace(plan.LocalGamePath) ||
            !Directory.Exists(plan.LocalGamePath))
        {
            return default;
        }

        var workspace = Path.Combine(
            _paths.OnlineDifferenceFilesRoot,
            SafeSegment(plan.GameVersion),
            SafeSegment(capture.TargetProfile),
            SafeSegment(capture.TargetManifestId));
        var content = Path.Combine(workspace, "content");
        Directory.CreateDirectory(content);

        var preserved = new List<DownloadedManifestFile>(capture.Files.Count);
        long completedBytes = 0;
        for (var index = 0; index < capture.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = capture.Files[index];
            var completedBeforeFile = completedBytes;
            var file = await TryPrepareManifestFileAsync(
                plan.LocalGamePath,
                content,
                entry,
                bytes => progress?.Report(new OnlineDifferenceProgress
                {
                    CurrentFile = entry.Path,
                    CompletedBytes = checked(completedBeforeFile + bytes),
                    TotalBytes = capture.ContentBytes,
                    CompletedFiles = index,
                    TotalFiles = capture.Files.Count,
                    CurrentFileChunksCompleted = 0,
                    CurrentFileChunksTotal = 0,
                    VerifyingExistingFile = true,
                    CheckingLocalFiles = true,
                    PreservingSourceFiles = true
                }),
                cancellationToken).ConfigureAwait(false);
            if (file is not null)
            {
                preserved.Add(file);
            }

            completedBytes = checked(completedBytes + entry.Size);
            progress?.Report(new OnlineDifferenceProgress
            {
                CurrentFile = entry.Path,
                CompletedBytes = completedBytes,
                TotalBytes = capture.ContentBytes,
                CompletedFiles = index + 1,
                TotalFiles = capture.Files.Count,
                CurrentFileChunksCompleted = 0,
                CurrentFileChunksTotal = 0,
                ReusingExistingFile = file is not null,
                CheckingLocalFiles = true,
                PreservingSourceFiles = true
            });
        }

        var ready = preserved.Count == capture.Files.Count;
        if (ready)
        {
            var manifest = new TransitionManifest
            {
                SourceProfile = capture.SourceProfile,
                TargetProfile = capture.TargetProfile,
                GameVersion = plan.GameVersion,
                ExpectedReplaceCount = preserved.Count,
                ExpectedDeleteCount = 0,
                ReplaceFiles = preserved.Select(file => new ReplaceFileEntry
                {
                    Source = file.Path,
                    Target = file.Path,
                    Length = file.Length,
                    Sha256 = file.Sha256
                }).ToList(),
                Notes = "由当前客户端按 Sophon Manifest 校验并保存，可用于反向切换。"
            };
            await WriteManifestAsync(workspace, manifest, cancellationToken).ConfigureAwait(false);
        }

        return new LocalSourceCaptureResult(preserved.Count, ready);
    }

    private static async Task<DownloadedManifestFile?> TryPrepareManifestFileAsync(
        string candidateRoot,
        string outputRoot,
        ManifestEntry entry,
        Action<long>? reportProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            var destination = SophonFileDownloader.ResolveUnderRoot(outputRoot, entry.Path);
            if (CrossesReparsePoint(outputRoot, destination))
            {
                return null;
            }

            if (File.Exists(destination) &&
                (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var existing = await TryHashMatchingFileAsync(
                destination, entry, reportProgress, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            var source = PathSafety.ResolveOrThrow(candidateRoot, entry.Path);
            if (!File.Exists(source) || new FileInfo(source).Length != entry.Size ||
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = $"{destination}.local-{Guid.NewGuid():N}.tmp";
            try
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                {
                    await using var input = new FileStream(
                        source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await using var output = new FileStream(
                        temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    var buffer = new byte[81920];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        md5.AppendData(buffer, 0, read);
                        sha256.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        total = checked(total + read);
                        reportProgress?.Invoke(total);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var actualMd5 = Convert.ToHexString(md5.GetHashAndReset());
                if (total != entry.Size ||
                    !string.Equals(actualMd5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var actualSha256 = Convert.ToHexString(sha256.GetHashAndReset());
                File.Move(temporary, destination, overwrite: true);
                return new DownloadedManifestFile(
                    entry.Path, entry.Size, entry.Md5, actualSha256, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<DownloadedManifestFile?> TryHashMatchingFileAsync(
        string path,
        ManifestEntry entry,
        Action<long>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != entry.Size ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

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
            reportProgress?.Invoke(total);
        }

        var actualMd5 = Convert.ToHexString(md5.GetHashAndReset());
        return string.Equals(actualMd5, entry.Md5, StringComparison.OrdinalIgnoreCase)
            ? new DownloadedManifestFile(
                entry.Path,
                entry.Size,
                entry.Md5,
                Convert.ToHexString(sha256.GetHashAndReset()),
                true)
            : null;
    }

    private static bool CrossesReparsePoint(string root, string destination)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetDirectoryName(destination);
        while (current is not null &&
               current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private readonly record struct LocalSourceCaptureResult(int PreservedFiles, bool Ready);

    public static OnlineDifferencePlan BuildPlan(
        string sourceProfile,
        string targetProfile,
        ManifestDiff diff,
        ManifestSnapshot targetSnapshot,
        ManifestCategory targetCategory,
        string? localGamePath = null,
        OnlineLocalSourceCapturePlan? localSourceCapture = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        ArgumentNullException.ThrowIfNull(targetCategory);
        var report = new ManifestClassifier().Classify(diff);
        var targetIndex = targetSnapshot.Entries.ToDictionary(
            item => item.Path,
            StringComparer.OrdinalIgnoreCase);
        var downloadClassifications = report.Files
            .Where(item => item.ChangeType is ManifestChangeType.Modified or ManifestChangeType.Added)
            .Where(IsAutomaticDifference)
            .ToArray();
        var downloads = downloadClassifications
            .Select(item => targetIndex.TryGetValue(item.Path, out var entry)
                ? entry
                : throw new InvalidDataException($"目标 Sophon 清单缺少差异文件：{item.Path}"))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // Classifier policy deliberately marks every Removed item for review. Online mode
        // therefore never converts a remote source-only path into an automatic deletion.
        var deletes = Array.Empty<string>();
        var blocks = report.Files.Where(item =>
            (item.ChangeType is ManifestChangeType.Modified or ManifestChangeType.Added) &&
            item.Path.StartsWith(StreamingBlocksPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var observation = report.Files.Where(item =>
            item.FileClass == ManifestFileClass.NeedsObservation).ToArray();

        return new OnlineDifferencePlan
        {
            SourceProfile = sourceProfile,
            TargetProfile = targetProfile,
            GameVersion = targetSnapshot.Version,
            SourceRegion = diff.SourceRegion,
            TargetRegion = diff.TargetRegion,
            TargetManifestId = targetSnapshot.ManifestId,
            TargetCategory = targetCategory,
            DownloadFiles = downloads,
            DeleteFiles = deletes,
            LocalGamePath = string.IsNullOrWhiteSpace(localGamePath)
                ? null
                : Path.GetFullPath(localGamePath),
            LocalSourceCapture = localSourceCapture,
            ExcludedStreamingBlocksCount = blocks.Length,
            ExcludedStreamingBlocksBytes = blocks.Aggregate(
                0L, (sum, item) => checked(sum + (item.TargetSize ?? 0L))),
            ExcludedObservationCount = observation.Length,
            ExcludedDeletionReviewCount = report.Files.Count(item =>
                item.ChangeType == ManifestChangeType.Removed &&
                !item.Path.StartsWith(StreamingBlocksPrefix, StringComparison.OrdinalIgnoreCase))
        };
    }

    private static bool IsAutomaticDifference(ClassifiedManifestFile item) =>
        item.FileClass is ManifestFileClass.BaseClient or ManifestFileClass.StateMetadata ||
        item.FileClass == ManifestFileClass.BaseResource &&
        !item.Path.StartsWith(StreamingBlocksPrefix, StringComparison.OrdinalIgnoreCase);

    private static OnlineManifestSummary Summary(ManifestSnapshot snapshot) => new(
        snapshot.Region,
        snapshot.ManifestId,
        snapshot.Entries.Count,
        snapshot.Entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Size)),
        snapshot.CreatedAtUtc);

    private async Task<ManifestSnapshot> LoadCachedSnapshotAsync(
        SophonRegion region,
        string gameVersion,
        CancellationToken cancellationToken)
    {
        var versionRoot = Path.Combine(
            _paths.ManifestCacheRoot,
            SophonRegionConfig.Game,
            region.ToString(),
            SafeSegment(gameVersion));
        if (!Directory.Exists(versionRoot))
        {
            throw new InvalidDataException(
                $"尚未缓存 {gameVersion} {region} Manifest，请先点击“更新 Manifest”。");
        }

        var candidates = new DirectoryInfo(versionRoot)
            .EnumerateDirectories()
            .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(directory => new
            {
                CategoryId = directory.Name,
                SnapshotPath = Path.Combine(directory.FullName, "snapshot.json")
            })
            .Where(item => File.Exists(item.SnapshotPath))
            .OrderByDescending(item => File.GetLastWriteTimeUtc(item.SnapshotPath))
            .ToArray();
        var cache = new ManifestCache(_paths.ManifestCacheRoot, JsonSupport.Options);
        foreach (var candidate in candidates)
        {
            var snapshot = await cache.TryLoadAsync(
                region, gameVersion, candidate.CategoryId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        throw new InvalidDataException(
            $"没有可用的 {gameVersion} {region} Manifest 缓存，请先点击“更新 Manifest”。");
    }

    private static OnlineManifestDirection BuildBrowserDirection(
        string sourceProfile,
        string targetProfile,
        ManifestSnapshot source,
        ManifestSnapshot target)
    {
        var diff = new ManifestDiffEngine().Compare(source, target);
        var classifications = new ManifestClassifier().Classify(diff).Files
            .ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var files = target.Entries.Select(entry =>
        {
            classifications.TryGetValue(entry.Path, out var classified);
            var automatic = classified is not null &&
                            classified.ChangeType is ManifestChangeType.Modified or ManifestChangeType.Added &&
                            IsAutomaticDifference(classified);
            return new OnlineManifestBrowseFile(
                entry.Path,
                entry.Size,
                entry.Md5,
                classified?.ChangeType,
                classified?.FileClass,
                automatic,
                entry.Path.StartsWith(
                    @"ZenlessZoneZero_Data\StreamingAssets\Video\",
                    StringComparison.OrdinalIgnoreCase),
                entry.Path.StartsWith(
                    @"ZenlessZoneZero_Data\StreamingAssets\Audio\",
                    StringComparison.OrdinalIgnoreCase),
                entry.Path.StartsWith(StreamingBlocksPrefix, StringComparison.OrdinalIgnoreCase),
                classified?.FileClass == ManifestFileClass.StateMetadata);
        }).ToArray();
        return new OnlineManifestDirection
        {
            SourceProfile = sourceProfile,
            TargetProfile = targetProfile,
            TargetManifest = Summary(target),
            Files = files
        };
    }

    private static SophonRegion ToSupportedRegion(string profile) => profile switch
    {
        ProfileIds.Global => SophonRegion.OS,
        ProfileIds.CnOfficial => SophonRegion.CN,
        ProfileIds.Bilibili => throw new NotSupportedException(
            "尚未提供 B 服独立 Sophon 清单，已阻止在线切换；不会回退到已有差异包。"),
        _ => throw new NotSupportedException($"不支持的服务器配置：{profile}")
    };

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new InvalidDataException($"在线差异缓存标识不安全：{value}");
        }

        return value;
    }

    private static void ValidateDownloadDiskSpace(OnlineDifferencePlan plan, string contentRoot)
    {
        long bytesForMissingFiles = 0;
        long largestExistingCandidate = 0;
        long largestDownloadFile = 0;
        foreach (var entry in plan.DownloadFiles)
        {
            largestDownloadFile = Math.Max(largestDownloadFile, entry.Size);
            var destination = SophonFileDownloader.ResolveUnderRoot(contentRoot, entry.Path);
            if (!File.Exists(destination) || new FileInfo(destination).Length != entry.Size)
            {
                bytesForMissingFiles = checked(bytesForMissingFiles + entry.Size);
            }
            else
            {
                // A same-length cache file still needs a temporary replacement if its MD5 is bad.
                largestExistingCandidate = Math.Max(largestExistingCandidate, entry.Size);
            }
        }

        const long safetyMargin = 128L * 1024 * 1024;
        // The verified chunk checkpoint cache can temporarily hold one complete file in
        // addition to the reconstructed output. It is removed after that file commits.
        var required = checked(
            bytesForMissingFiles + largestExistingCandidate + largestDownloadFile + safetyMargin);
        var driveRoot = Path.GetPathRoot(contentRoot);
        if (!string.IsNullOrWhiteSpace(driveRoot) && new DriveInfo(driveRoot).AvailableFreeSpace < required)
        {
            throw new IOException(
                $"在线差异缓存盘空间不足。至少需要约 {required:N0} 字节（含临时校验余量）。");
        }
    }

    private static async Task WriteManifestAsync(
        string workspace,
        TransitionManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace);
        var destination = Path.Combine(workspace, "transition-manifest.json");
        var temporary = Path.Combine(workspace, $"transition-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream, manifest, JsonSupport.Options, cancellationToken).ConfigureAwait(false);
            }

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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
