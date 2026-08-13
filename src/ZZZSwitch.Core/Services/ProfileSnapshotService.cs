using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed partial class ProfileSnapshotService
{
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
        var versionRoot = GetVersionRoot(profile, gameVersion);
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
            var sourceHash = ComputeSha256(source);
            var destinationHash = ComputeSha256(destination);
            if (_files.GetLength(source) != _files.GetLength(destination) ||
                !string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"缓存快照校验失败：{relative}");
            }

            records.Add(new SnapshotFileRecord
            {
                RelativePath = relative,
                Length = _files.GetLength(destination),
                Sha256 = destinationHash
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
        return manifest;
    }

    public ProfileSnapshotManifest? FindLatestValid(string profile, string gameVersion, string gamePath)
    {
        ValidateProfileAndVersion(profile, gameVersion);
        var versionRoot = GetVersionRoot(profile, gameVersion);
        if (!Directory.Exists(versionRoot))
        {
            return null;
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(versionRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var candidates = new List<ProfileSnapshotManifest>();
        foreach (var directory in directories)
        {
            var manifestPath = Path.Combine(directory, "snapshot.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                // 新快照损坏时继续回退到更早的有效快照，而不是让切换预检崩溃。
                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize<ProfileSnapshotManifest>(stream, JsonSupport.Options);
                if (manifest is not null && IsValid(manifest, directory, profile, gameVersion, gamePath))
                {
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
            if (_files.GetLength(target) != record.Length ||
                !string.Equals(ComputeSha256(target), record.Sha256, StringComparison.OrdinalIgnoreCase))
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
                    x is null || string.IsNullOrWhiteSpace(x.RelativePath) ||
                    string.IsNullOrWhiteSpace(x.Sha256) || x.Length < 0) ||
                !string.Equals(Path.GetFullPath(manifest.SnapshotPath), Path.GetFullPath(actualSnapshotPath), StringComparison.OrdinalIgnoreCase) ||
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
                if (!_files.FileExists(path) || _files.GetLength(path) != record.Length ||
                    !string.Equals(ComputeSha256(path), record.Sha256, StringComparison.OrdinalIgnoreCase))
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

    private string GetVersionRoot(string profile, string gameVersion) =>
        Path.Combine(_paths.ProfileSnapshotsRoot, profile, gameVersion);

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

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex GameVersionRegex();
}
