using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed partial class HotUpdateCacheService
{
    public const string BlocksRelativePath = @"ZenlessZoneZero_Data\Persistent\Blocks";

    private readonly AppPaths _paths;
    private readonly IProcessMonitor _processMonitor;
    private readonly ICacheRootResolver _cacheRoots;
    private readonly bool _forceVerifiedCopyTransfers;

    public HotUpdateCacheService(
        AppPaths paths,
        IProcessMonitor processMonitor,
        ICacheRootResolver? cacheRoots = null,
        bool forceVerifiedCopyTransfers = false)
    {
        _paths = paths;
        _processMonitor = processMonitor;
        _cacheRoots = cacheRoots ?? DefaultCacheRootResolver.Instance;
        _forceVerifiedCopyTransfers = forceVerifiedCopyTransfers;
    }

    public HotUpdateCacheManifest InitializeActive(
        string profile,
        string gameVersion,
        string gamePath)
    {
        ValidateProfileAndVersion(profile, gameVersion);
        EnsureNoRelatedProcesses();

        var normalizedGamePath = Path.GetFullPath(gamePath);
        var activeBlocks = PathSafety.ResolveOrThrow(normalizedGamePath, BlocksRelativePath);
        EnsureBlocksReady(activeBlocks);

        var storedBlocks = GetStoredBlocksPath(normalizedGamePath, gameVersion, profile);
        if (Directory.Exists(storedBlocks) &&
            Directory.EnumerateFileSystemEntries(storedBlocks).Any())
        {
            throw new InvalidOperationException(
                "当前服同时存在活动 Blocks 和已存储 Blocks。为避免覆盖有效缓存，请先处理未完成的切换事务。");
        }

        var first = CaptureInventory(activeBlocks);
        Thread.Sleep(250);
        var second = CaptureInventory(activeBlocks);
        if (!InventoriesEqual(first, second))
        {
            throw new InvalidOperationException("Blocks 目录仍在变化，资源下载可能尚未结束。请稍后重新检查。");
        }

        var manifest = new HotUpdateCacheManifest
        {
            CacheId = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.Now,
            Profile = profile,
            GameVersion = gameVersion,
            GamePath = normalizedGamePath,
            StoredBlocksPath = storedBlocks,
            FileCount = second.FileCount,
            TotalBytes = second.TotalBytes,
            InventorySha256 = second.InventorySha256
        };
        SaveManifest(manifest);
        return manifest;
    }

    public HotUpdateCacheStatus GetStatus(
        string profile,
        string gameVersion,
        string gamePath,
        string? activeProfile)
    {
        try
        {
            var manifest = LoadManifest(profile, gameVersion, gamePath);
            if (manifest is null)
            {
                return MissingStatus(profile);
            }

            if (!ManifestIdentityMatches(manifest, profile, gameVersion, gamePath))
            {
                return InvalidStatus(profile, manifest, "缓存属于其他游戏目录或版本。");
            }

            var isActive = string.Equals(profile, activeProfile, StringComparison.Ordinal);
            var actualPath = isActive
                ? PathSafety.ResolveOrThrow(gamePath, BlocksRelativePath)
                : manifest.StoredBlocksPath;
            if (!Directory.Exists(actualPath))
            {
                return InvalidStatus(profile, manifest, isActive
                    ? "活动 Blocks 目录不存在。"
                    : "已存储 Blocks 目录不存在。");
            }

            if (FindTemporaryFiles(actualPath).Count > 0)
            {
                return InvalidStatus(profile, manifest, "存在未完成下载的 .tmp 文件。");
            }

            var inventory = CaptureInventory(actualPath);
            var exact = InventoryMatches(manifest, inventory);
            if (isActive && !exact)
            {
                return new()
                {
                    Profile = profile,
                    IsInitialized = true,
                    IsActive = true,
                    IsAvailable = true,
                    NeedsRefresh = true,
                    FileCount = inventory.FileCount,
                    TotalBytes = inventory.TotalBytes,
                    CreatedAt = manifest.CreatedAt,
                    Path = actualPath,
                    Detail = "活动缓存有新资源，切走时将自动更新清单。"
                };
            }

            return new()
            {
                Profile = profile,
                IsInitialized = true,
                IsActive = isActive,
                IsAvailable = exact,
                NeedsRefresh = false,
                FileCount = inventory.FileCount,
                TotalBytes = inventory.TotalBytes,
                CreatedAt = manifest.CreatedAt,
                Path = actualPath,
                Detail = exact ? (isActive ? "可用（当前活动）" : "可用") : "文件清单与缓存记录不一致。"
            };
        }
        catch (Exception ex) when (IsExpectedMetadataException(ex))
        {
            return new()
            {
                Profile = profile,
                IsInitialized = true,
                IsAvailable = false,
                Detail = ex.Message
            };
        }
    }

    public HotUpdateTransitionPlan? CreateTransitionPlan(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        string gamePath,
        List<ValidationIssue> issues)
    {
        ValidateProfileAndVersion(sourceProfile, gameVersion);
        ValidateProfileAndVersion(targetProfile, gameVersion);

        if (File.Exists(_paths.HotUpdateJournalFile))
        {
            issues.Add(new(
                IssueSeverity.Error,
                "hot-cache.transaction.pending",
                "检测到未完成的热更新缓存事务。请先重新打开程序完成恢复。"));
            return null;
        }

        HotUpdateCacheManifest? source;
        try
        {
            // 清单按游戏目录身份隔离；旧版全局清单只会在身份完全匹配时迁移。
            source = LoadManifest(sourceProfile, gameVersion, gamePath);
        }
        catch (Exception ex) when (IsExpectedMetadataException(ex))
        {
            issues.Add(new(
                IssueSeverity.Error,
                "hot-cache.source.manifest.invalid",
                $"当前服务器缓存记录无法读取或已损坏：{ex.Message}",
                GetManifestPath(sourceProfile, gameVersion, gamePath)));
            return null;
        }

        if (source is null || !ManifestIdentityMatches(source, sourceProfile, gameVersion, gamePath))
        {
            issues.Add(new(
                IssueSeverity.Error,
                "hot-cache.source.missing",
                $"尚未初始化{DisplayName(sourceProfile)}缓存。请先在当前服下载完成后点击“初始化当前服缓存”。"));
            return null;
        }

        var activeBlocks = PathSafety.ResolveOrThrow(gamePath, BlocksRelativePath);
        if (!Directory.Exists(activeBlocks))
        {
            issues.Add(new(IssueSeverity.Error, "hot-cache.active.missing", "当前活动 Blocks 目录不存在。", activeBlocks));
            return null;
        }

        IReadOnlyList<string> temporaryFiles;
        try
        {
            temporaryFiles = FindTemporaryFiles(activeBlocks);
        }
        catch (Exception ex) when (IsExpectedMetadataException(ex))
        {
            issues.Add(new(
                IssueSeverity.Error,
                "hot-cache.active.unreadable",
                $"无法检查当前活动 Blocks 目录：{ex.Message}",
                activeBlocks));
            return null;
        }
        if (temporaryFiles.Count > 0)
        {
            issues.Add(new(
                IssueSeverity.Error,
                "hot-cache.download.pending",
                $"Blocks 中仍有 {temporaryFiles.Count} 个 .tmp 文件，资源下载尚未完成。",
                temporaryFiles[0]));
            return null;
        }

        HotUpdateCacheManifest? target;
        try
        {
            target = LoadManifest(targetProfile, gameVersion, gamePath);
        }
        catch (Exception ex) when (IsExpectedMetadataException(ex))
        {
            issues.Add(new(
                IssueSeverity.Error,
                "hot-cache.target.manifest.invalid",
                $"目标服务器缓存记录无法读取或已损坏：{ex.Message}",
                GetManifestPath(targetProfile, gameVersion, gamePath)));
            return null;
        }

        var targetCacheWasLost = false;
        if (target is not null)
        {
            if (!ManifestIdentityMatches(target, targetProfile, gameVersion, gamePath))
            {
                issues.Add(new(IssueSeverity.Error, "hot-cache.target.identity", "目标服缓存属于其他游戏目录或版本。"));
                return null;
            }

            if (!Directory.Exists(target.StoredBlocksPath))
            {
                issues.Add(new(
                    IssueSeverity.Warning,
                    "hot-cache.target.lost",
                    "目标服缓存记录仍存在，但实际 Blocks 仓库已经丢失；本次将按未初始化状态重建。",
                    target.StoredBlocksPath));
                target = null;
                targetCacheWasLost = true;
            }
            else
            {
                Inventory inventory;
                try
                {
                    inventory = CaptureInventory(target.StoredBlocksPath);
                }
                catch (Exception ex) when (IsExpectedMetadataException(ex))
                {
                    issues.Add(new(
                        IssueSeverity.Error,
                        "hot-cache.target.unreadable",
                        $"无法检查目标服务器 Blocks 缓存：{ex.Message}",
                        target.StoredBlocksPath));
                    return null;
                }

                if (!InventoryMatches(target, inventory))
                {
                    issues.Add(new(IssueSeverity.Error, "hot-cache.target.invalid", "目标服 Blocks 缓存清单不匹配，拒绝切换。", target.StoredBlocksPath));
                    return null;
                }
            }
        }

        if (target is null && !targetCacheWasLost)
        {
            issues.Add(new(
                IssueSeverity.Warning,
                "hot-cache.target.initialization",
                $"{DisplayName(targetProfile)}缓存尚未初始化；本次将进入一次性初始化模式，启动目标服后仍需完成资源下载。"));
        }

        return new()
        {
            Mode = target is null ? HotUpdateTransitionMode.InitializeTarget : HotUpdateTransitionMode.Swap,
            SourceProfile = sourceProfile,
            TargetProfile = targetProfile,
            GameVersion = gameVersion,
            GamePath = Path.GetFullPath(gamePath),
            SourceManifest = source,
            TargetManifest = target,
            SeedDirectory = target is null
                ? FindSeedDirectory(gamePath, gameVersion, targetProfile)
                : null
        };
    }

    public HotUpdateTransaction BeginTransition(HotUpdateTransitionPlan plan)
    {
        EnsureNoRelatedProcesses();
        var activeBlocks = PathSafety.ResolveOrThrow(plan.GamePath, BlocksRelativePath);
        EnsureBlocksReady(activeBlocks);

        var sourceInventory = CaptureInventory(activeBlocks);
        var refreshedSource = new HotUpdateCacheManifest
        {
            CacheId = plan.SourceManifest.CacheId,
            CreatedAt = DateTimeOffset.Now,
            Profile = plan.SourceProfile,
            GameVersion = plan.GameVersion,
            GamePath = Path.GetFullPath(plan.GamePath),
            StoredBlocksPath = plan.SourceManifest.StoredBlocksPath,
            FileCount = sourceInventory.FileCount,
            TotalBytes = sourceInventory.TotalBytes,
            InventorySha256 = sourceInventory.InventorySha256
        };
        SaveManifest(refreshedSource);

        var sourceStored = refreshedSource.StoredBlocksPath;
        if (Directory.Exists(sourceStored))
        {
            if (Directory.EnumerateFileSystemEntries(sourceStored).Any())
            {
                throw new InvalidOperationException("来源服缓存仓库非空，拒绝覆盖。");
            }

            Directory.Delete(sourceStored);
        }

        var targetStored = plan.TargetManifest?.StoredBlocksPath;
        if (plan.Mode == HotUpdateTransitionMode.Swap &&
            (targetStored is null || !Directory.Exists(targetStored)))
        {
            throw new InvalidOperationException("目标服缓存仓库不存在。");
        }

        var transaction = new HotUpdateTransaction
        {
            TransactionId = Guid.NewGuid().ToString("N"),
            Mode = plan.Mode,
            SourceProfile = plan.SourceProfile,
            TargetProfile = plan.TargetProfile,
            GameVersion = plan.GameVersion,
            GamePath = Path.GetFullPath(plan.GamePath),
            ActiveBlocksPath = activeBlocks,
            SourceStoredBlocksPath = sourceStored,
            TargetStoredBlocksPath = targetStored,
            SeedDirectory = plan.SeedDirectory
        };
        SaveJournal(transaction);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceStored)!);
            MoveDirectoryTransactional(
                activeBlocks,
                sourceStored,
                transaction.TransactionId,
                sourceInventory,
                () =>
                {
                    transaction.SourceMoved = true;
                    SaveJournal(transaction);
                });

            if (plan.Mode == HotUpdateTransitionMode.Swap)
            {
                var targetInventory = CaptureInventory(targetStored!);
                MoveDirectoryTransactional(
                    targetStored!,
                    activeBlocks,
                    transaction.TransactionId,
                    targetInventory,
                    () =>
                    {
                        transaction.TargetMoved = true;
                        SaveJournal(transaction);
                    });
            }
            else
            {
                Directory.CreateDirectory(activeBlocks);
                CopySeedFiles(plan.SeedDirectory, activeBlocks);
            }

            SaveJournal(transaction);
            return transaction;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public void Commit(HotUpdateTransaction transaction)
    {
        transaction.Committed = true;
        try
        {
            SaveJournal(transaction);
        }
        catch
        {
            // State is already committed by the engine. A stale journal is reconciled
            // against the committed profile on the next launch.
        }

        TryDeleteJournal();
    }

    public string? RecoverPending(string? committedProfile)
    {
        if (!File.Exists(_paths.HotUpdateJournalFile))
        {
            return null;
        }

        EnsureNoRelatedProcesses();
        HotUpdateTransaction transaction;
        using (var stream = File.OpenRead(_paths.HotUpdateJournalFile))
        {
            transaction = JsonSerializer.Deserialize<HotUpdateTransaction>(stream, JsonSupport.Options)
                          ?? throw new InvalidDataException("热更新缓存事务日志损坏。");
        }

        ValidateTransactionPaths(transaction);

        if (transaction.Committed ||
            string.Equals(committedProfile, transaction.TargetProfile, StringComparison.Ordinal))
        {
            TryDeleteJournal();
            return "已清理上次已完成切换遗留的事务记录。";
        }

        if (!Rollback(transaction))
        {
            throw new IOException("未完成的 Blocks 缓存事务自动恢复失败。");
        }

        return $"已自动撤销未完成的 {DisplayName(transaction.SourceProfile)} → {DisplayName(transaction.TargetProfile)} Blocks 交换。";
    }

    public bool Rollback(HotUpdateTransaction transaction)
    {
        try
        {
            ValidateTransactionPaths(transaction);
            var active = transaction.ActiveBlocksPath;
            var sourceStored = transaction.SourceStoredBlocksPath;
            var targetStored = transaction.TargetStoredBlocksPath;
            TryDeleteDirectory(GetTransferStagingPath(sourceStored, transaction.TransactionId));
            TryDeleteDirectory(GetTransferStagingPath(active, transaction.TransactionId));

            var activeIsSource = Directory.Exists(active) &&
                                 ActiveMatchesProfile(transaction, transaction.SourceProfile);
            var sourceExists = Directory.Exists(sourceStored);
            var targetExists = targetStored is not null && Directory.Exists(targetStored);

            // The journal is written before the first move. If the app stopped in that
            // window, the original active Blocks are already the correct rollback state.
            // A cross-volume copy can also leave a verified duplicate in sourceStored
            // before the original is removed; discard only that duplicate.
            if (activeIsSource && !transaction.SourceMoved)
            {
                if (sourceExists)
                {
                    var sourceManifest = LoadManifest(
                        transaction.SourceProfile,
                        transaction.GameVersion,
                        transaction.GamePath);
                    if (sourceManifest is null ||
                        !InventoryMatches(sourceManifest, CaptureInventory(sourceStored)))
                    {
                        return false;
                    }

                    Directory.Delete(sourceStored, true);
                }

                TryDeleteJournal();
                return true;
            }

            // Recovery itself may have completed the source restore and then stopped
            // before deleting the journal. Treat that state as success and never remove
            // the restored active cache.
            if (activeIsSource && !sourceExists)
            {
                if (transaction.Mode == HotUpdateTransitionMode.Swap && !targetExists)
                {
                    return false;
                }

                TryDeleteJournal();
                return true;
            }

            // A source move (including the crash window before SourceMoved was saved)
            // is recoverable only while its verified stored copy still exists.
            if (!sourceExists)
            {
                return false;
            }

            if (Directory.Exists(active))
            {
                if (transaction.Mode == HotUpdateTransitionMode.Swap && targetStored is not null)
                {
                    if (targetExists)
                    {
                        // Cross-volume target copy was committed but the original target
                        // cache still exists. Keep the original and remove the duplicate.
                        Directory.Delete(active, true);
                    }
                    else
                    {
                        MoveDirectoryTransactional(
                            active,
                            targetStored,
                            transaction.TransactionId,
                            CaptureInventory(active));
                    }
                }
                else
                {
                    Directory.Delete(active, true);
                }
            }

            MoveDirectoryTransactional(
                sourceStored,
                active,
                transaction.TransactionId,
                CaptureInventory(sourceStored));
            TryDeleteJournal();
            return Directory.Exists(active) && ActiveMatchesProfile(transaction, transaction.SourceProfile);
        }
        catch
        {
            return false;
        }
    }

    private HotUpdateCacheManifest? LoadManifest(
        string profile,
        string gameVersion,
        string gamePath)
    {
        ValidateProfileAndVersion(profile, gameVersion);
        var path = GetManifestPath(profile, gameVersion, gamePath);
        if (File.Exists(path))
        {
            return ResolveStoredPath(LoadManifestFile(path), profile, gameVersion, gamePath);
        }

        var legacyPath = GetLegacyManifestPath(profile, gameVersion);
        if (!File.Exists(legacyPath))
        {
            return null;
        }

        var legacy = LoadManifestFile(legacyPath);
        if (legacy is null || !ManifestIdentityMatches(legacy, profile, gameVersion, gamePath))
        {
            return null;
        }

        legacy = ResolveStoredPath(legacy, profile, gameVersion, gamePath)!;
        SaveManifest(legacy);
        TryDeleteLegacyManifest(legacyPath);
        return legacy;
    }

    private HotUpdateCacheManifest? ResolveStoredPath(
        HotUpdateCacheManifest? manifest,
        string profile,
        string gameVersion,
        string gamePath)
    {
        if (manifest is null)
        {
            return null;
        }

        var expected = GetStoredBlocksPath(gamePath, gameVersion, profile);
        if (string.Equals(
                Path.GetFullPath(manifest.StoredBlocksPath),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
        {
            return manifest;
        }

        return new()
        {
            CacheId = manifest.CacheId,
            CreatedAt = manifest.CreatedAt,
            Profile = manifest.Profile,
            GameVersion = manifest.GameVersion,
            GamePath = manifest.GamePath,
            StoredBlocksPath = expected,
            FileCount = manifest.FileCount,
            TotalBytes = manifest.TotalBytes,
            InventorySha256 = manifest.InventorySha256
        };
    }

    private void SaveManifest(HotUpdateCacheManifest manifest)
    {
        _paths.EnsureWritableDirectories();
        ValidateProfileAndVersion(manifest.Profile, manifest.GameVersion);
        var path = GetManifestPath(manifest.Profile, manifest.GameVersion, manifest.GamePath);
        AtomicJsonFile.Write(path, manifest);
        TryDeleteMatchingLegacyManifest(manifest);
    }

    private static HotUpdateCacheManifest? LoadManifestFile(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<HotUpdateCacheManifest>(stream, JsonSupport.Options);
        if (manifest is not null &&
            (string.IsNullOrWhiteSpace(manifest.CacheId) ||
             !ProfileIds.HotUpdateProfiles.Contains(manifest.Profile, StringComparer.Ordinal) ||
             string.IsNullOrWhiteSpace(manifest.GameVersion) ||
             !GameVersionRegex().IsMatch(manifest.GameVersion) ||
             string.IsNullOrWhiteSpace(manifest.GamePath) ||
             !Path.IsPathFullyQualified(manifest.GamePath) ||
             string.IsNullOrWhiteSpace(manifest.StoredBlocksPath) ||
             !Path.IsPathFullyQualified(manifest.StoredBlocksPath) ||
             manifest.FileCount < 0 ||
             manifest.TotalBytes < 0 ||
             !FileIntegrityService.IsValidSha256(manifest.InventorySha256)))
        {
            throw new InvalidDataException($"Blocks 缓存记录缺少必要字段或包含无效值：{path}");
        }

        return manifest;
    }

    private string GetManifestPath(string profile, string gameVersion, string gamePath) =>
        Path.Combine(
            _paths.HotUpdateManifestsRoot,
            GameStorageLayout.GetGameIdentity(gamePath),
            gameVersion,
            profile,
            "cache.json");

    private string GetLegacyManifestPath(string profile, string gameVersion) =>
        Path.Combine(_paths.HotUpdateManifestsRoot, profile, gameVersion, "cache.json");

    private void TryDeleteMatchingLegacyManifest(HotUpdateCacheManifest manifest)
    {
        var legacyPath = GetLegacyManifestPath(manifest.Profile, manifest.GameVersion);
        if (!File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            var legacy = LoadManifestFile(legacyPath);
            if (legacy is not null && ManifestIdentityMatches(
                    legacy,
                    manifest.Profile,
                    manifest.GameVersion,
                    manifest.GamePath))
            {
                TryDeleteLegacyManifest(legacyPath);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Never let legacy cleanup invalidate a successfully written scoped manifest.
        }
    }

    private static void TryDeleteLegacyManifest(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
            DeleteIfEmpty(Path.GetDirectoryName(path));
            DeleteIfEmpty(Path.GetDirectoryName(Path.GetDirectoryName(path)!));
        }
        catch
        {
            // The identity-scoped copy is already durable. A stale legacy copy is harmless.
        }
    }

    private static void DeleteIfEmpty(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            Directory.Exists(path) &&
            !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private string GetStoredBlocksPath(string gamePath, string gameVersion, string profile) =>
        GameStorageLayout.GetStoredBlocksPath(
            gamePath,
            gameVersion,
            profile,
            _cacheRoots.GetCacheRoot(gamePath));

    private static string? FindSeedDirectory(
        string gamePath,
        string gameVersion,
        string targetProfile)
    {
        if (!ProfileIds.HotUpdateProfiles.Contains(targetProfile, StringComparer.Ordinal))
        {
            return null;
        }

        var path = GameStorageLayout.GetSeedDirectory(gamePath, gameVersion, targetProfile);
        return Directory.Exists(path) ? path : null;
    }

    private static void CopySeedFiles(string? seedDirectory, string activeBlocks)
    {
        if (seedDirectory is null || !Directory.Exists(seedDirectory))
        {
            return;
        }

        foreach (var source in Directory.EnumerateFiles(seedDirectory, "*.blk", SearchOption.TopDirectoryOnly))
        {
            File.Copy(source, Path.Combine(activeBlocks, Path.GetFileName(source)), false);
        }
    }

    private void MoveDirectoryTransactional(
        string source,
        string target,
        string transactionId,
        Inventory expectedInventory,
        Action? destinationCommitted = null)
    {
        if (Directory.Exists(target))
        {
            throw new IOException($"目录移动目标已存在：{target}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!_forceVerifiedCopyTransfers && SameVolume(source, target))
        {
            Directory.Move(source, target);
            destinationCommitted?.Invoke();
            return;
        }

        var staging = GetTransferStagingPath(target, transactionId);
        TryDeleteDirectory(staging);
        CopyDirectory(source, staging);
        var copiedInventory = CaptureInventory(staging);
        if (!InventoriesEqual(expectedInventory, copiedInventory))
        {
            TryDeleteDirectory(staging);
            throw new IOException("跨磁盘缓存复制后的文件清单校验失败。");
        }

        Directory.Move(staging, target);
        destinationCommitted?.Invoke();
        Directory.Delete(source, true);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
        }
    }

    private static bool SameVolume(string first, string second) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(first)),
            Path.GetPathRoot(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static string GetTransferStagingPath(string target, string transactionId) =>
        target + ".moving-" + transactionId;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // A staging directory never becomes authoritative until it is renamed.
        }
    }

    private bool ActiveMatchesProfile(HotUpdateTransaction transaction, string profile)
    {
        var manifest = LoadManifest(profile, transaction.GameVersion, transaction.GamePath);
        return manifest is not null &&
               ManifestIdentityMatches(manifest, profile, transaction.GameVersion, transaction.GamePath) &&
               InventoryMatches(manifest, CaptureInventory(transaction.ActiveBlocksPath));
    }

    private void EnsureNoRelatedProcesses()
    {
        var processes = _processMonitor.FindRelatedProcesses();
        if (processes.Count > 0)
        {
            throw new InvalidOperationException(
                $"请先完全退出游戏和 HoYoPlay：{string.Join("、", processes)}");
        }
    }

    private static void EnsureBlocksReady(string blocksPath)
    {
        if (!Directory.Exists(blocksPath))
        {
            throw new DirectoryNotFoundException($"Blocks 目录不存在：{blocksPath}");
        }

        var temporaryFiles = FindTemporaryFiles(blocksPath);
        if (temporaryFiles.Count > 0)
        {
            throw new InvalidOperationException(
                $"Blocks 中仍有 {temporaryFiles.Count} 个 .tmp 文件，资源下载尚未完成。");
        }

        if (!Directory.EnumerateFiles(blocksPath, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException("Blocks 目录为空，不能初始化热更新缓存。");
        }
    }

    private static IReadOnlyList<string> FindTemporaryFiles(string blocksPath) =>
        Directory.Exists(blocksPath)
            ? Directory.EnumerateFiles(blocksPath, "*.tmp", SearchOption.AllDirectories).Take(20).ToArray()
            : [];

    private static Inventory CaptureInventory(string path)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        var total = 0L;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                     .OrderBy(x => Path.GetRelativePath(path, x), StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(path, file).Replace('/', '\\');
            var line = Encoding.UTF8.GetBytes($"{relative}\0{info.Length}\n");
            incremental.AppendData(line);
            count++;
            total += info.Length;
        }

        return new(count, total, Convert.ToHexString(incremental.GetHashAndReset()));
    }

    private static bool InventoryMatches(HotUpdateCacheManifest manifest, Inventory inventory) =>
        manifest.FileCount == inventory.FileCount &&
        manifest.TotalBytes == inventory.TotalBytes &&
        string.Equals(manifest.InventorySha256, inventory.InventorySha256, StringComparison.OrdinalIgnoreCase);

    private static bool InventoriesEqual(Inventory first, Inventory second) =>
        first.FileCount == second.FileCount &&
        first.TotalBytes == second.TotalBytes &&
        string.Equals(first.InventorySha256, second.InventorySha256, StringComparison.OrdinalIgnoreCase);

    private static bool ManifestIdentityMatches(
        HotUpdateCacheManifest manifest,
        string profile,
        string gameVersion,
        string gamePath)
    {
        if (!string.Equals(manifest.Profile, profile, StringComparison.Ordinal) ||
            !string.Equals(manifest.GameVersion, gameVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.GamePath) ||
            !Path.IsPathFullyQualified(manifest.GamePath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(manifest.GamePath),
                Path.GetFullPath(gamePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static HotUpdateCacheStatus MissingStatus(string profile) => new()
    {
        Profile = profile,
        IsInitialized = false,
        IsAvailable = false,
        Detail = "未初始化"
    };

    private static HotUpdateCacheStatus InvalidStatus(
        string profile,
        HotUpdateCacheManifest manifest,
        string detail) => new()
        {
            Profile = profile,
            IsInitialized = true,
            IsAvailable = false,
            FileCount = manifest.FileCount,
            TotalBytes = manifest.TotalBytes,
            CreatedAt = manifest.CreatedAt,
            Path = manifest.StoredBlocksPath,
            Detail = detail
        };

    private static string DisplayName(string profile) =>
        profile == ProfileIds.Global ? "国际服" : "国服";

    private static bool IsExpectedMetadataException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException;

    private void SaveJournal(HotUpdateTransaction transaction)
    {
        _paths.EnsureWritableDirectories();
        AtomicJsonFile.Write(_paths.HotUpdateJournalFile, transaction);
    }

    private void TryDeleteJournal()
    {
        try
        {
            File.Delete(_paths.HotUpdateJournalFile);
            File.Delete(_paths.HotUpdateJournalFile + ".tmp");
        }
        catch
        {
            // A stale journal is safer than hiding a potentially incomplete transaction.
        }
    }

    private void ValidateTransactionPaths(HotUpdateTransaction transaction)
    {
        // 启动恢复会读取上次进程留下的日志。先从可信字段重新推导所有路径，
        // 再与日志比较，避免损坏或被修改的日志把移动/删除操作引向游戏目录之外。
        if (string.IsNullOrWhiteSpace(transaction.TransactionId) ||
            !Enum.IsDefined(transaction.Mode) ||
            string.Equals(transaction.SourceProfile, transaction.TargetProfile, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(transaction.GamePath) ||
            string.IsNullOrWhiteSpace(transaction.ActiveBlocksPath) ||
            string.IsNullOrWhiteSpace(transaction.SourceStoredBlocksPath))
        {
            throw new InvalidDataException("热更新事务缺少必要字段或包含无效值。");
        }

        ValidateProfileAndVersion(transaction.SourceProfile, transaction.GameVersion);
        ValidateProfileAndVersion(transaction.TargetProfile, transaction.GameVersion);
        var expectedActive = PathSafety.ResolveOrThrow(transaction.GamePath, BlocksRelativePath);
        var expectedSource = GetStoredBlocksPath(
            transaction.GamePath,
            transaction.GameVersion,
            transaction.SourceProfile);
        var expectedTarget = GetStoredBlocksPath(
            transaction.GamePath,
            transaction.GameVersion,
            transaction.TargetProfile);
        if (!string.Equals(Path.GetFullPath(expectedActive), Path.GetFullPath(transaction.ActiveBlocksPath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(expectedSource), Path.GetFullPath(transaction.SourceStoredBlocksPath), StringComparison.OrdinalIgnoreCase) ||
            (transaction.TargetStoredBlocksPath is not null &&
             !string.Equals(Path.GetFullPath(expectedTarget), Path.GetFullPath(transaction.TargetStoredBlocksPath), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("热更新事务路径校验失败，拒绝执行恢复。");
        }
    }

    private static void ValidateProfileAndVersion(string profile, string gameVersion)
    {
        if (!ProfileIds.HotUpdateProfiles.Contains(profile, StringComparer.Ordinal) ||
            !GameVersionRegex().IsMatch(gameVersion))
        {
            throw new InvalidDataException("非法 profile 或游戏版本。");
        }
    }

    private sealed record Inventory(int FileCount, long TotalBytes, string InventorySha256);

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex GameVersionRegex();
}

public sealed class HotUpdateTransaction
{
    public required string TransactionId { get; init; }
    public HotUpdateTransitionMode Mode { get; init; }
    public required string SourceProfile { get; init; }
    public required string TargetProfile { get; init; }
    public required string GameVersion { get; init; }
    public required string GamePath { get; init; }
    public required string ActiveBlocksPath { get; init; }
    public required string SourceStoredBlocksPath { get; init; }
    public string? TargetStoredBlocksPath { get; init; }
    public string? SeedDirectory { get; init; }
    public bool SourceMoved { get; set; }
    public bool TargetMoved { get; set; }
    public bool Committed { get; set; }
}
