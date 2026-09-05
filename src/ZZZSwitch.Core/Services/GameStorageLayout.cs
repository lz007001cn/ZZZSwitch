using System.Security.Cryptography;
using System.Text;

namespace ZZZSwitch.Core.Services;

public static class GameStorageLayout
{
    public const string RootDirectoryName = ".zzzswitch";
    public const string BlocksCacheDirectoryName = "blocks-cache";
    public const string LegacyCacheDirectoryName = "cache";
    public const string PackagesDirectoryName = "packages";
    public const string SeedsDirectoryName = "seeds";
    public const string StagingDirectoryName = "staging";
    public const string AppDataDirectoryName = "app-data";
    public const string LegacyDataDirectoryName = "data";
    public const string BackupRecordsDirectoryName = "backup-records";
    public const string BackupContentDirectoryName = "backup-content";
    public const string DownloadsDirectoryName = "downloads";
    public const string SnapshotsDirectoryName = "snapshots";
    public const string SophonManifestsDirectoryName = "sophon-manifests";
    public const string BlocksManifestsDirectoryName = "blocks-manifests";
    public const string LogsDirectoryName = "operation-logs";
    public const string TempDirectoryName = "temporary";

    public static string GetRoot(string gamePath) =>
        Path.Combine(GetGameParent(gamePath), RootDirectoryName);

    public static string GetCacheRoot(string gamePath) =>
        Path.Combine(GetRoot(gamePath), BlocksCacheDirectoryName);

    public static string GetLegacyCacheRoot(string gamePath) =>
        Path.Combine(GetRoot(gamePath), LegacyCacheDirectoryName);

    public static string GetPackagesRoot(string gamePath) =>
        Path.Combine(GetRoot(gamePath), PackagesDirectoryName);

    public static string GetStagingRoot(string gamePath) =>
        Path.Combine(GetRoot(gamePath), StagingDirectoryName);

    public static string GetAppDataRoot(string gamePath) =>
        Path.Combine(GetRoot(gamePath), AppDataDirectoryName);

    public static string GetLegacyDataRoot(string gamePath) =>
        Path.Combine(GetRoot(gamePath), LegacyDataDirectoryName);

    public static string GetOperationStagingDirectory(string gamePath, string operationId) =>
        Path.Combine(GetStagingRoot(gamePath), ValidateSegment(operationId, nameof(operationId)));

    public static string GetPackageRoot(string gamePath, string gameVersion) =>
        Path.Combine(GetPackagesRoot(gamePath), ValidateSegment(gameVersion, nameof(gameVersion)));

    public static string GetPackageDirectory(
        string gamePath,
        string gameVersion,
        string packageDirectoryName) =>
        Path.Combine(
            GetPackageRoot(gamePath, gameVersion),
            ValidateSegment(packageDirectoryName, nameof(packageDirectoryName)));

    public static string GetSeedDirectory(string gamePath, string gameVersion, string profile) =>
        Path.Combine(
            GetPackageRoot(gamePath, gameVersion),
            SeedsDirectoryName,
            ValidateSegment(profile, nameof(profile)));

    public static string GetStoredBlocksPath(
        string gamePath,
        string gameVersion,
        string profile,
        string? cacheRoot = null)
    {
        var fullGamePath = NormalizeGamePath(gamePath);
        return Path.Combine(
            cacheRoot is null ? GetCacheRoot(fullGamePath) : NormalizeCacheRoot(cacheRoot),
            GetGameIdentity(fullGamePath),
            ValidateSegment(gameVersion, nameof(gameVersion)),
            ValidateSegment(profile, nameof(profile)),
            "Blocks");
    }

    public static string GetGameIdentity(string gamePath)
    {
        var fullGamePath = NormalizeGamePath(gamePath);
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fullGamePath.ToUpperInvariant())))[..12];
        return $"{Path.GetFileName(fullGamePath)}-{identity}";
    }

    private static string GetGameParent(string gamePath)
    {
        var fullGamePath = NormalizeGamePath(gamePath);
        return Directory.GetParent(fullGamePath)?.FullName
               ?? throw new InvalidOperationException("无法确定游戏目录的父目录。");
    }

    private static string NormalizeGamePath(string gamePath) =>
        Path.GetFullPath(gamePath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    public static string NormalizeCacheRoot(string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || !Path.IsPathFullyQualified(cacheRoot))
        {
            throw new ArgumentException("缓存目录必须是完整的本地路径。", nameof(cacheRoot));
        }

        var normalized = Path.GetFullPath(cacheRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(normalized)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("不能将磁盘根目录直接用作缓存目录。", nameof(cacheRoot));
        }

        return normalized;
    }

    private static string ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value is "." or "..")
        {
            throw new ArgumentException("目录段无效。", parameterName);
        }

        return value;
    }
}
