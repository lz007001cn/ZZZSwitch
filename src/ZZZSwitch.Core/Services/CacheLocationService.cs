using System.Security.Cryptography;
using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public interface ICacheRootResolver
{
    string GetCacheRoot(string gamePath);
}

public sealed class DefaultCacheRootResolver : ICacheRootResolver
{
    public static DefaultCacheRootResolver Instance { get; } = new();

    private DefaultCacheRootResolver()
    {
    }

    public string GetCacheRoot(string gamePath) => GameStorageLayout.GetCacheRoot(gamePath);
}

public sealed class CacheLocationService : ICacheRootResolver
{
    private readonly AppPaths _paths;

    public CacheLocationService(AppPaths paths) => _paths = paths;

    public string GetCacheRoot(string gamePath)
    {
        var normalizedGamePath = NormalizeGamePath(gamePath);
        var identity = GameStorageLayout.GetGameIdentity(normalizedGamePath);
        var settings = LoadSettings();
        if (settings.Locations.TryGetValue(identity, out var entry) &&
            SamePath(entry.GamePath, normalizedGamePath))
        {
            try
            {
                return GameStorageLayout.NormalizeCacheRoot(entry.CacheRootPath);
            }
            catch (ArgumentException)
            {
                // A damaged preference must not redirect cache operations to an unsafe path.
            }
        }

        return GameStorageLayout.GetCacheRoot(normalizedGamePath);
    }

    public bool IsUsingCustomLocation(string gamePath) =>
        !SamePath(GetCacheRoot(gamePath), GameStorageLayout.GetCacheRoot(gamePath));

    public CacheUsageSummary GetUsage(string gamePath, string currentGameVersion)
    {
        var cacheRoot = GetCacheRoot(gamePath);
        var gameCacheRoot = GetGameCacheRoot(cacheRoot, gamePath);
        var versionDirectories = Directory.Exists(gameCacheRoot)
            ? Directory.GetDirectories(gameCacheRoot)
            : [];
        var snapshotVersionDirectories = GetSnapshotVersionDirectories(gamePath).ToArray();
        var manifestVersionDirectories = GetManifestVersionDirectories(gamePath).ToArray();
        var all = MeasureDirectories(versionDirectories
            .Concat(snapshotVersionDirectories)
            .Concat(manifestVersionDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var obsoleteDirectories = versionDirectories
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                currentGameVersion,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var obsoleteSnapshotDirectories = snapshotVersionDirectories
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                currentGameVersion,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var obsoleteManifestDirectories = manifestVersionDirectories
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                currentGameVersion,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var obsoleteVersions = obsoleteDirectories
            .Select(path => Path.GetFileName(path)!)
            .Concat(obsoleteManifestDirectories.Select(path => Path.GetFileName(path)!))
            .Concat(obsoleteSnapshotDirectories.Select(path => Path.GetFileName(path)!))
            .Where(version => !string.Equals(version, currentGameVersion, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var obsolete = MeasureDirectories(obsoleteDirectories
            .Concat(obsoleteSnapshotDirectories)
            .Concat(obsoleteManifestDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        return new(
            cacheRoot,
            all.FileCount,
            all.TotalBytes,
            obsoleteVersions.Length,
            obsolete.FileCount,
            obsolete.TotalBytes,
            IsUsingCustomLocation(gamePath));
    }

    public CacheCleanupResult DeleteObsoleteVersions(string gamePath, string currentGameVersion)
    {
        var cacheRoot = GetCacheRoot(gamePath);
        var gameCacheRoot = GetGameCacheRoot(cacheRoot, gamePath);
        var obsoleteDirectories = Directory.Exists(gameCacheRoot)
            ? Directory.GetDirectories(gameCacheRoot)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                currentGameVersion,
                StringComparison.OrdinalIgnoreCase))
            .ToArray()
            : [];
        var obsoleteSnapshotDirectories = GetSnapshotVersionDirectories(gamePath)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                currentGameVersion,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var obsoleteManifestDirectories = GetManifestVersionDirectories(gamePath)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                currentGameVersion,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var obsoleteVersions = obsoleteDirectories
            .Select(path => Path.GetFileName(path)!)
            .Concat(obsoleteManifestDirectories.Select(path => Path.GetFileName(path)!))
            .Concat(obsoleteSnapshotDirectories.Select(path => Path.GetFileName(path)!))
            .Where(version => !string.Equals(version, currentGameVersion, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var measured = MeasureDirectories(obsoleteDirectories
            .Concat(obsoleteSnapshotDirectories)
            .Concat(obsoleteManifestDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var directory in obsoleteDirectories)
        {
            EnsureChildPath(gameCacheRoot, directory);
            DeleteDirectoryRobust(directory);
        }

        foreach (var version in obsoleteVersions)
        {
            DeleteManifestVersion(gamePath, version);
        }
        foreach (var directory in obsoleteSnapshotDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteSnapshotVersionDirectory(directory);
        }

        if (Directory.Exists(gameCacheRoot))
        {
            DeleteIfEmpty(gameCacheRoot);
        }
        return new(obsoleteVersions.Length, measured.FileCount, measured.TotalBytes);
    }

    public CacheMigrationResult ChangeLocation(string gamePath, string requestedCacheRoot)
    {
        var normalizedGamePath = NormalizeGamePath(gamePath);
        var sourceCacheRoot = GetCacheRoot(normalizedGamePath);
        var targetCacheRoot = GameStorageLayout.NormalizeCacheRoot(requestedCacheRoot);
        ValidateTarget(normalizedGamePath, targetCacheRoot);
        if (SamePath(sourceCacheRoot, targetCacheRoot))
        {
            return new(sourceCacheRoot, targetCacheRoot, 0, 0, false, true);
        }

        Directory.CreateDirectory(targetCacheRoot);
        VerifyWritable(targetCacheRoot);
        var sourceGameRoot = GetGameCacheRoot(sourceCacheRoot, normalizedGamePath);
        var targetGameRoot = GetGameCacheRoot(targetCacheRoot, normalizedGamePath);
        if (IsSameOrChild(sourceGameRoot, targetGameRoot) || IsSameOrChild(targetGameRoot, sourceGameRoot))
        {
            throw new InvalidOperationException("目标缓存目录不能与现有游戏缓存目录相互包含。");
        }

        if (Directory.Exists(targetGameRoot) && Directory.EnumerateFileSystemEntries(targetGameRoot).Any())
        {
            throw new InvalidOperationException($"目标位置已存在同一游戏的缓存内容：{targetGameRoot}");
        }

        if (Directory.Exists(targetGameRoot))
        {
            Directory.Delete(targetGameRoot);
        }

        var sourceExists = Directory.Exists(sourceGameRoot) &&
                           Directory.EnumerateFileSystemEntries(sourceGameRoot).Any();
        var staging = targetGameRoot + ".migrating-" + Guid.NewGuid().ToString("N");
        var measured = sourceExists ? MeasureDirectories([sourceGameRoot]) : new DirectoryMeasure(0, 0);
        var destinationCommitted = false;
        var settingCommitted = false;
        try
        {
            if (sourceExists)
            {
                CopyDirectoryVerified(sourceGameRoot, staging);
                Directory.Move(staging, targetGameRoot);
                destinationCommitted = true;
            }
            else
            {
                Directory.CreateDirectory(targetGameRoot);
                destinationCommitted = true;
            }

            SaveLocation(normalizedGamePath, targetCacheRoot);
            settingCommitted = true;

            var sourceRemoved = true;
            if (sourceExists && Directory.Exists(sourceGameRoot))
            {
                try
                {
                    Directory.Delete(sourceGameRoot, true);
                    DeleteIfEmpty(sourceCacheRoot);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    sourceRemoved = false;
                }
            }

            return new(
                sourceCacheRoot,
                targetCacheRoot,
                measured.FileCount,
                measured.TotalBytes,
                sourceExists,
                sourceRemoved);
        }
        catch
        {
            if (!settingCommitted)
            {
                TryDeleteDirectory(staging);
                if (destinationCommitted)
                {
                    TryDeleteDirectory(targetGameRoot);
                }
            }

            throw;
        }
    }

    public CacheMigrationResult RestoreDefaultLocation(string gamePath) =>
        ChangeLocation(gamePath, GameStorageLayout.GetCacheRoot(gamePath));

    private void SaveLocation(string gamePath, string cacheRoot)
    {
        var settings = LoadSettings();
        var identity = GameStorageLayout.GetGameIdentity(gamePath);
        var defaultRoot = GameStorageLayout.GetCacheRoot(gamePath);
        if (SamePath(cacheRoot, defaultRoot))
        {
            settings.Locations.Remove(identity);
        }
        else
        {
            settings.Locations[identity] = new CacheLocationEntry
            {
                GamePath = gamePath,
                CacheRootPath = cacheRoot
            };
        }

        _paths.EnsureWritableDirectories();
        AtomicJsonFile.Write(_paths.CacheLocationsFile, settings);
    }

    private CacheLocationSettings LoadSettings()
    {
        if (!File.Exists(_paths.CacheLocationsFile))
        {
            return new();
        }

        try
        {
            using var stream = File.OpenRead(_paths.CacheLocationsFile);
            return JsonSerializer.Deserialize<CacheLocationSettings>(stream, JsonSupport.Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private static void ValidateTarget(string gamePath, string cacheRoot)
    {
        var normalizedGame = NormalizeGamePath(gamePath) + Path.DirectorySeparatorChar;
        var normalizedCache = cacheRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (normalizedCache.StartsWith(normalizedGame, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("缓存目录不能放在游戏目录内部。");
        }

        var storageRoot = Path.GetFullPath(GameStorageLayout.GetRoot(gamePath))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!SamePath(cacheRoot, GameStorageLayout.GetCacheRoot(gamePath)) &&
            (normalizedCache.StartsWith(storageRoot, StringComparison.OrdinalIgnoreCase) ||
             storageRoot.StartsWith(normalizedCache, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("缓存目录不能与 .zzzswitch 存储目录重叠。");
        }
    }

    private static void VerifyWritable(string path)
    {
        var probe = Path.Combine(path, ".zzzswitch-write-test-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe, 1, FileOptions.DeleteOnClose))
        {
        }
    }

    private static void CopyDirectoryVerified(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, false);
            var sourceInfo = new FileInfo(source);
            var targetInfo = new FileInfo(target);
            if (sourceInfo.Length != targetInfo.Length ||
                !string.Equals(ComputeSha256(source), ComputeSha256(target), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"缓存迁移校验失败：{relative}");
            }
        }
    }

    private void DeleteManifestVersion(string gamePath, string version)
    {
        var identityRoot = Path.Combine(
            _paths.HotUpdateManifestsRoot,
            GameStorageLayout.GetGameIdentity(gamePath));
        var versionPath = Path.Combine(identityRoot, version);
        if (Directory.Exists(versionPath))
        {
            EnsureChildPath(identityRoot, versionPath);
            DeleteDirectoryRobust(versionPath);
            DeleteIfEmpty(identityRoot);
        }

        foreach (var profile in ProfileIds.HotUpdateProfiles)
        {
            var legacyProfileRoot = Path.Combine(_paths.HotUpdateManifestsRoot, profile);
            var legacyVersionPath = Path.Combine(legacyProfileRoot, version);
            if (LegacyHotUpdateVersionBelongsToGame(legacyVersionPath, gamePath))
            {
                EnsureChildPath(legacyProfileRoot, legacyVersionPath);
                DeleteDirectoryRobust(legacyVersionPath);
                DeleteIfEmpty(legacyProfileRoot);
            }
        }
    }

    private IEnumerable<string> GetManifestVersionDirectories(string gamePath)
    {
        var identityRoot = Path.Combine(
            _paths.HotUpdateManifestsRoot,
            GameStorageLayout.GetGameIdentity(gamePath));
        var directories = Directory.Exists(identityRoot)
            ? Directory.GetDirectories(identityRoot).ToList()
            : [];
        foreach (var profile in ProfileIds.HotUpdateProfiles)
        {
            var legacyProfileRoot = Path.Combine(_paths.HotUpdateManifestsRoot, profile);
            if (!Directory.Exists(legacyProfileRoot))
            {
                continue;
            }

            directories.AddRange(Directory.GetDirectories(legacyProfileRoot)
                .Where(path => LegacyHotUpdateVersionBelongsToGame(path, gamePath)));
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IEnumerable<string> GetSnapshotVersionDirectories(string gamePath)
    {
        var identityRoot = Path.Combine(
            _paths.ProfileSnapshotsRoot,
            GameStorageLayout.GetGameIdentity(gamePath));
        if (Directory.Exists(identityRoot))
        {
            foreach (var versionDirectory in Directory.GetDirectories(identityRoot))
            {
                yield return versionDirectory;
            }
        }

        foreach (var profile in ProfileIds.HotUpdateProfiles)
        {
            var legacyProfileRoot = Path.Combine(_paths.ProfileSnapshotsRoot, profile);
            if (!Directory.Exists(legacyProfileRoot))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.GetDirectories(legacyProfileRoot))
            {
                if (LegacySnapshotVersionBelongsToGame(versionDirectory, gamePath))
                {
                    yield return versionDirectory;
                }
            }
        }
    }

    private static bool LegacyHotUpdateVersionBelongsToGame(string versionPath, string gamePath)
    {
        if (!Directory.Exists(versionPath))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles(versionPath, "cache.json", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var manifest = JsonSerializer.Deserialize<HotUpdateCacheManifest>(
                    stream,
                    JsonSupport.Options);
                if (manifest is not null && SamePath(manifest.GamePath, gamePath))
                {
                    return true;
                }
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Damaged legacy manifests are preserved because ownership is unknown.
            }
        }

        return false;
    }

    private static bool LegacySnapshotVersionBelongsToGame(string versionPath, string gamePath)
    {
        foreach (var path in Directory.EnumerateFiles(versionPath, "snapshot.json", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var manifest = JsonSerializer.Deserialize<ProfileSnapshotManifest>(
                    stream,
                    JsonSupport.Options);
                if (manifest is not null && SamePath(manifest.GamePath, gamePath))
                {
                    return true;
                }
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Damaged legacy snapshots are preserved because ownership is unknown.
            }
        }

        return false;
    }

    private void DeleteSnapshotVersionDirectory(string directory)
    {
        var root = Path.GetFullPath(_paths.ProfileSnapshotsRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("快照清理路径超出应用数据目录。");
        }

        DeleteDirectoryRobust(directory);
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null)
        {
            DeleteIfEmpty(parent);
        }
    }

    private static void DeleteDirectoryRobust(string path)
    {
        var root = new DirectoryInfo(path);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"拒绝清理重解析点缓存目录：{path}");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var file in root.EnumerateFiles("*", options))
        {
            if (file.IsReadOnly || (file.Attributes & FileAttributes.System) != 0)
            {
                file.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System);
            }
        }

        foreach (var directory in root.EnumerateDirectories("*", options)
                     .OrderByDescending(item => item.FullName.Length))
        {
            if ((directory.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
            {
                directory.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System);
            }
        }

        root.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System);
        root.Delete(true);
    }

    private static DirectoryMeasure MeasureDirectories(IEnumerable<string> directories)
    {
        var count = 0;
        var bytes = 0L;
        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
        }

        return new(count, bytes);
    }

    private static string GetGameCacheRoot(string cacheRoot, string gamePath) =>
        Path.Combine(cacheRoot, GameStorageLayout.GetGameIdentity(gamePath));

    private static string NormalizeGamePath(string gamePath) =>
        Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSameOrChild(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureChildPath(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("缓存清理路径超出当前游戏缓存目录。");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void DeleteIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

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
            // A failed migration never switches the configured root. Leftovers are inert.
        }
    }

    private sealed class CacheLocationSettings
    {
        public Dictionary<string, CacheLocationEntry> Locations { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CacheLocationEntry
    {
        public required string GamePath { get; init; }
        public required string CacheRootPath { get; init; }
    }

    private readonly record struct DirectoryMeasure(int FileCount, long TotalBytes);
}

public sealed record CacheUsageSummary(
    string CacheRootPath,
    int FileCount,
    long TotalBytes,
    int ObsoleteVersionCount,
    int ObsoleteFileCount,
    long ObsoleteBytes,
    bool IsCustomLocation);

public sealed record CacheCleanupResult(int RemovedVersionCount, int RemovedFileCount, long FreedBytes);

public sealed record CacheMigrationResult(
    string SourceCacheRoot,
    string TargetCacheRoot,
    int MigratedFileCount,
    long MigratedBytes,
    bool ContentMoved,
    bool SourceRemoved);
