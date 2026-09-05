using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed partial class ProfileSnapshotService
{
    private const int RetainedValidSnapshotsPerScope = 2;
    private static readonly string[] CacheDirectories =
    [
        @"ZenlessZoneZero_Data\Persistent",
        @"ZenlessZoneZero_Data\StreamingAssets"
    ];

    private readonly AppPaths _paths;
    private readonly IFileOperations _files;

    public ProfileSnapshotService(AppPaths paths, IFileOperations files)
    {
        _paths = paths;
        _files = files;
    }

    public ProfileSnapshotManifest Capture(string profile, string gameVersion, string gamePath)
    {
        ValidateProfileAndVersion(profile, gameVersion);
        var relativeFiles = DiscoverCacheMetadataFiles(gamePath);
        if (relativeFiles.Count == 0)
        {
            throw new InvalidOperationException("未发现可快照的 version/revision 一级文件，拒绝在没有缓存保护的情况下切换。");
        }

        _paths.EnsureWritableDirectories();
        var snapshotId = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var versionRoot = GetVersionRoot(profile, gameVersion, gamePath);
        var snapshotPath = Path.Combine(versionRoot, snapshotId);
        EnsureUnderSnapshotsRoot(snapshotPath);
        _files.CreateDirectory(snapshotPath);
        var filesRoot = Path.Combine(snapshotPath, "files");
        _files.CreateDirectory(filesRoot);

        var records = new List<SnapshotFileRecord>();
        foreach (var relative in relativeFiles)
        {
            var source = PathSafety.ResolveOrThrow(gamePath, relative);
            var destination = PathSafety.ResolveOrThrow(filesRoot, relative);
            var parent = Path.GetDirectoryName(destination);
            if (parent is not null)
            {
                _files.CreateDirectory(parent);
            }

            _files.CopyFile(source, destination, false);
            if (_files.GetLength(source) != _files.GetLength(destination))
            {
                throw new IOException($"缓存快照校验失败：{relative}");
            }

            records.Add(new SnapshotFileRecord
            {
                RelativePath = relative,
                Length = _files.GetLength(destination)
            });
        }

        var manifest = new ProfileSnapshotManifest
        {
            SnapshotId = snapshotId,
            CreatedAt = DateTimeOffset.Now,
            Profile = profile,
            GameVersion = gameVersion,
            GamePath = Path.GetFullPath(gamePath),
            SnapshotPath = snapshotPath,
            Files = records
        };
        AtomicJsonFile.Write(Path.Combine(snapshotPath, "snapshot.json"), manifest);
        PruneRetainedSnapshots(profile, gameVersion, gamePath, RetainedValidSnapshotsPerScope);
        return manifest;
    }

    public ProfileSnapshotManifest? FindLatestValid(string profile, string gameVersion, string gamePath)
    {
        ValidateProfileAndVersion(profile, gameVersion);
        var candidates = new List<ProfileSnapshotManifest>();
        foreach (var directory in EnumerateSnapshotDirectories(profile, gameVersion, gamePath))
        {
            var manifestPath = Path.Combine(directory, "snapshot.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                // 新快照损坏时继续回退到更早的有效快照，而不是让切换预检崩溃。
                ProfileSnapshotManifest? manifest;
                using (var stream = File.OpenRead(manifestPath))
                {
                    manifest = JsonSerializer.Deserialize<ProfileSnapshotManifest>(stream, JsonSupport.Options);
                }
                if (manifest is not null && IsValid(manifest, directory, profile, gameVersion, gamePath))
                {
                    RebindManifestPath(manifestPath, manifest, directory);
                    candidates.Add(manifest);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Ignore damaged snapshots and try an older valid snapshot.
            }
        }

        return candidates.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
    }

    public int PruneRetainedSnapshots(
        string profile,
        string gameVersion,
        string gamePath,
        int retainedValidSnapshots = RetainedValidSnapshotsPerScope)
    {
        ValidateProfileAndVersion(profile, gameVersion);
        if (retainedValidSnapshots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedValidSnapshots));
        }

        var valid = new List<(string Directory, ProfileSnapshotManifest Manifest)>();
        foreach (var directory in EnumerateSnapshotDirectories(profile, gameVersion, gamePath))
        {
            var manifestPath = Path.Combine(directory, "snapshot.json");
            try
            {
                ProfileSnapshotManifest? manifest;
                using (var stream = File.OpenRead(manifestPath))
                {
                    manifest = JsonSerializer.Deserialize<ProfileSnapshotManifest>(stream, JsonSupport.Options);
                }
                if (manifest is not null && IsValid(manifest, directory, profile, gameVersion, gamePath))
                {
                    RebindManifestPath(manifestPath, manifest, directory);
                    valid.Add((directory, manifest));
                }
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Corrupt or incomplete snapshots are preserved for manual inspection.
            }
        }

        var removed = 0;
        foreach (var candidate in valid
                     .OrderByDescending(item => item.Manifest.CreatedAt)
                     .ThenByDescending(item => item.Manifest.SnapshotId, StringComparer.Ordinal)
                     .Skip(retainedValidSnapshots))
        {
            try
            {
                EnsureUnderSnapshotsRoot(candidate.Directory);
                _files.DeleteDirectory(candidate.Directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention is best effort and must not turn a safe switch into a failure.
            }
        }

        return removed;
    }

    public int Restore(ProfileSnapshotManifest snapshot, string gamePath)
    {
        if (!IsValid(snapshot, snapshot.SnapshotPath, snapshot.Profile, snapshot.GameVersion, gamePath))
        {
            throw new InvalidDataException("目标服缓存快照缺失、越界或哈希校验失败。");
        }

        var restored = 0;
        var filesRoot = Path.Combine(snapshot.SnapshotPath, "files");
        foreach (var record in snapshot.Files)
        {
            var source = PathSafety.ResolveOrThrow(filesRoot, record.RelativePath);
            var target = PathSafety.ResolveOrThrow(gamePath, record.RelativePath);
            var parent = Path.GetDirectoryName(target);
            if (parent is not null)
            {
                _files.CreateDirectory(parent);
            }

            _files.CopyFile(source, target, true);
            if (_files.GetLength(target) != record.Length)
            {
                throw new IOException($"目标服缓存恢复校验失败：{record.RelativePath}");
            }

            restored++;
        }

        return restored;
    }

    public IReadOnlyList<string> DiscoverCacheMetadataFiles(string gamePath)
    {
        var result = new List<string>();
        foreach (var relativeDirectory in CacheDirectories)
        {
            var directory = PathSafety.ResolveOrThrow(gamePath, relativeDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!name.Contains("version", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("revision", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(gamePath, file);
                if (IsAllowedCacheMetadataPath(relative))
                {
                    result.Add(relative);
                }
            }
        }

        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool IsValid(
        ProfileSnapshotManifest manifest,
        string actualSnapshotPath,
        string expectedProfile,
        string expectedVersion,
        string expectedGamePath)
    {
        try
        {
            EnsureUnderSnapshotsRoot(actualSnapshotPath);
            if (string.IsNullOrWhiteSpace(manifest.SnapshotPath) ||
                string.IsNullOrWhiteSpace(manifest.Profile) ||
                string.IsNullOrWhiteSpace(manifest.GameVersion) ||
                string.IsNullOrWhiteSpace(manifest.GamePath) ||
                manifest.Files is null ||
                manifest.Files.Cast<SnapshotFileRecord?>().Any(x =>
                    x is null || string.IsNullOrWhiteSpace(x.RelativePath) || x.Length < 0) ||
                !string.Equals(manifest.SnapshotId, Path.GetFileName(actualSnapshotPath), StringComparison.Ordinal) ||
                !string.Equals(manifest.Profile, expectedProfile, StringComparison.Ordinal) ||
                !string.Equals(manifest.GameVersion, expectedVersion, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(manifest.GamePath), Path.GetFullPath(expectedGamePath), StringComparison.OrdinalIgnoreCase) ||
                manifest.Files.Count == 0)
            {
                return false;
            }

            var filesRoot = Path.Combine(actualSnapshotPath, "files");
            foreach (var record in manifest.Files)
            {
                if (!IsAllowedCacheMetadataPath(record.RelativePath))
                {
                    return false;
                }

                var path = PathSafety.ResolveOrThrow(filesRoot, record.RelativePath);
                if (!_files.FileExists(path) || _files.GetLength(path) != record.Length)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsAllowedCacheMetadataPath(string relativePath)
    {
        var normalized = relativePath.Replace('/', '\\');
        foreach (var directory in CacheDirectories)
        {
            var prefix = directory + "\\";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = normalized[prefix.Length..];
            return !fileName.Contains('\\') &&
                   (fileName.Contains("version", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains("revision", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private string GetVersionRoot(string profile, string gameVersion, string gamePath) =>
        Path.Combine(
            _paths.ProfileSnapshotsRoot,
            GameStorageLayout.GetGameIdentity(gamePath),
            gameVersion,
            profile);

    private string GetLegacyVersionRoot(string profile, string gameVersion) =>
        Path.Combine(_paths.ProfileSnapshotsRoot, profile, gameVersion);

    private IEnumerable<string> EnumerateSnapshotDirectories(
        string profile,
        string gameVersion,
        string gamePath)
    {
        var roots = new[]
        {
            GetVersionRoot(profile, gameVersion, gamePath),
            GetLegacyVersionRoot(profile, gameVersion)
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                yield return directory;
            }
        }
    }

    private static void RebindManifestPath(
        string manifestPath,
        ProfileSnapshotManifest manifest,
        string actualSnapshotPath)
    {
        var actual = Path.GetFullPath(actualSnapshotPath);
        var alreadyBound = false;
        try
        {
            alreadyBound = string.Equals(
                Path.GetFullPath(manifest.SnapshotPath),
                actual,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed historical path is replaced only after the actual
            // snapshot directory and every payload hash have been validated.
        }

        if (alreadyBound)
        {
            return;
        }

        manifest.SnapshotPath = actual;
        AtomicJsonFile.Write(manifestPath, manifest);
    }

    private void EnsureUnderSnapshotsRoot(string path)
    {
        var root = Path.GetFullPath(_paths.ProfileSnapshotsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("缓存快照路径不在应用专用目录内。拒绝操作。");
        }
    }

    private static void ValidateProfileAndVersion(string profile, string gameVersion)
    {
        if (!ProfileIds.All.Contains(profile, StringComparer.Ordinal) || !GameVersionRegex().IsMatch(gameVersion))
        {
            throw new InvalidDataException("非法 profile 或游戏版本，拒绝创建缓存快照路径。");
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex GameVersionRegex();
}
