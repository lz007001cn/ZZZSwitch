using System.IO.Compression;
using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public enum BundledBilibiliPackageStatus
{
    UnsupportedVersion,
    AlreadyInstalled,
    Installed,
    Repaired
}

public sealed record BundledBilibiliPackageResult(
    BundledBilibiliPackageStatus Status,
    string PackageDirectory,
    int FileCount,
    long TotalBytes);

public sealed class BundledBilibiliPackageService
{
    private const int MaximumEntries = 1_000;
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const string MarkerFileName = ".bundled-package.json";
    private readonly ConfigurationRepository _configuration;
    private readonly Func<Stream> _openArchive;
    private readonly string _supportedGameVersion;
    private readonly string _bundleId;
    private readonly FileIntegrityService _integrity = new(new PhysicalFileOperations());

    public BundledBilibiliPackageService(
        ConfigurationRepository configuration,
        Func<Stream> openArchive,
        string supportedGameVersion,
        string bundleId)
    {
        _configuration = configuration;
        _openArchive = openArchive;
        _supportedGameVersion = supportedGameVersion;
        _bundleId = bundleId;
    }

    public BundledBilibiliPackageResult EnsureInstalled(
        string gamePath,
        string currentGameVersion)
    {
        if (!string.Equals(currentGameVersion, _supportedGameVersion, StringComparison.Ordinal))
        {
            return new(
                BundledBilibiliPackageStatus.UnsupportedVersion,
                GameStorageLayout.GetPackageDirectory(gamePath, currentGameVersion, ProfileIds.Bilibili),
                0,
                0);
        }

        var expected = LoadExpectedFiles(currentGameVersion);
        var normalizedGamePath = Path.GetFullPath(gamePath);
        var packageRoot = GameStorageLayout.GetPackageRoot(normalizedGamePath, currentGameVersion);
        var target = GameStorageLayout.GetPackageDirectory(
            normalizedGamePath,
            currentGameVersion,
            ProfileIds.Bilibili);
        EnsureNotReparsePoint(GameStorageLayout.GetPackagesRoot(normalizedGamePath));
        EnsureNotReparsePoint(packageRoot);
        EnsureNotReparsePoint(target);

        if (Directory.Exists(target) && IsInstalled(target, expected))
        {
            return Result(BundledBilibiliPackageStatus.AlreadyInstalled, target, expected);
        }

        Directory.CreateDirectory(packageRoot);
        RecoverInterruptedInstall(packageRoot, target);
        if (Directory.Exists(target) && IsInstalled(target, expected))
        {
            return Result(BundledBilibiliPackageStatus.AlreadyInstalled, target, expected);
        }

        var replacingExisting = Directory.Exists(target);
        var staging = Path.Combine(packageRoot, $".installing-bilibili-{Guid.NewGuid():N}");
        var previous = Path.Combine(packageRoot, $".previous-bilibili-{Guid.NewGuid():N}");
        var targetMoved = false;
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            using var stream = _openArchive();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var files = InspectArchive(archive, currentGameVersion, expected);
            EnsureAvailableSpace(packageRoot, files.Sum(item => item.Entry.Length));
            foreach (var item in files)
            {
                var output = PathSafety.ResolveOrThrow(staging, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var source = item.Entry.Open();
                using var destination = new FileStream(
                    output,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                source.CopyTo(destination);
                if (destination.Length != item.Entry.Length)
                {
                    throw new IOException($"内置 B 服组件解压长度不匹配：{item.RelativePath}");
                }
            }

            ValidateAllFiles(staging, expected);
            WriteMarker(staging, expected.Count);
            if (Directory.Exists(target))
            {
                EnsureNotReparsePoint(target);
                Directory.Move(target, previous);
                targetMoved = true;
            }

            Directory.Move(staging, target);
            committed = true;
            if (targetMoved)
            {
                try
                {
                    DeleteDirectoryRobust(previous);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 新目录已经原子提交；旧副本留待下次启动恢复清理，不能把成功安装误报为失败。
                    _ = ex;
                }
            }

            return Result(
                replacingExisting
                    ? BundledBilibiliPackageStatus.Repaired
                    : BundledBilibiliPackageStatus.Installed,
                target,
                expected);
        }
        catch
        {
            if (!committed && targetMoved && !Directory.Exists(target) && Directory.Exists(previous))
            {
                Directory.Move(previous, target);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                DeleteDirectoryRobust(staging);
            }
        }
    }

    private Dictionary<string, ExpectedFile> LoadExpectedFiles(string gameVersion)
    {
        var profiles = _configuration.LoadProfilesWithStatus();
        var transitions = _configuration.LoadTransitionsWithStatus();
        if (profiles.Errors.Count > 0 || transitions.Errors.Count > 0)
        {
            throw new InvalidDataException("软件内置切换配置不完整，无法准备 B 服组件。");
        }

        var bilibiliDirectory = profiles.Items
            .SingleOrDefault(profile => string.Equals(profile.Id, ProfileIds.Bilibili, StringComparison.Ordinal))?
            .PackageDirectoryName;
        if (string.IsNullOrWhiteSpace(bilibiliDirectory))
        {
            throw new InvalidDataException("软件缺少 B 服目录配置。");
        }

        var expected = new Dictionary<string, ExpectedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in transitions.Items.Where(item =>
                     item.Enabled && string.Equals(item.GameVersion, gameVersion, StringComparison.Ordinal)))
        {
            var targetDirectory = profiles.Items.Single(profile =>
                string.Equals(profile.Id, transition.TargetProfile, StringComparison.Ordinal)).PackageDirectoryName;
            foreach (var entry in transition.ReplaceFiles.Where(entry =>
                         string.Equals(
                             PackageFileResolver.EffectiveDirectoryName(targetDirectory, entry),
                             bilibiliDirectory,
                             StringComparison.Ordinal)))
            {
                if (!entry.Length.HasValue ||
                    entry.Length.Value < 0 ||
                    !FileIntegrityService.IsValidSha256(entry.Sha256))
                {
                    throw new InvalidDataException($"B 服组件清单缺少完整性数据：{entry.Source}");
                }

                var relative = NormalizeRelativePath(entry.Source);
                var item = new ExpectedFile(relative, entry.Length.Value, entry.Sha256!);
                if (expected.TryGetValue(relative, out var previous) && previous != item)
                {
                    throw new InvalidDataException($"B 服组件清单存在冲突：{entry.Source}");
                }

                expected[relative] = item;
            }
        }

        if (expected.Count == 0)
        {
            throw new InvalidDataException($"软件没有游戏版本 {gameVersion} 的 B 服组件清单。");
        }

        return expected;
    }

    private bool IsInstalled(string target, IReadOnlyDictionary<string, ExpectedFile> expected)
    {
        var markerPath = Path.Combine(target, MarkerFileName);
        try
        {
            ValidateAllFiles(target, expected);
            var markerMatches = false;
            if (File.Exists(markerPath))
            {
                using var markerStream = File.OpenRead(markerPath);
                var marker = JsonSerializer.Deserialize<BundleMarker>(markerStream, JsonSupport.Options);
                markerMatches = marker is not null &&
                    string.Equals(marker.BundleId, _bundleId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(marker.GameVersion, _supportedGameVersion, StringComparison.Ordinal) &&
                    marker.FileCount == expected.Count;
            }

            if (!markerMatches)
            {
                WriteMarker(target, expected.Count);
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static List<ArchiveFile> InspectArchive(
        ZipArchive archive,
        string gameVersion,
        IReadOnlyDictionary<string, ExpectedFile> expected)
    {
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("内置 B 服组件文件数量超过安全上限。");
        }

        var prefix = $".zzzswitch/packages/{gameVersion}/bilibili/";
        var files = new List<ArchiveFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(name, "README-BILIBILI-PACKAGES.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"内置 B 服组件目录结构错误：{entry.FullName}");
            }

            var relative = NormalizeRelativePath(name[prefix.Length..]);
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
                !paths.Add(relative) ||
                !expected.TryGetValue(relative, out var expectedFile) ||
                expectedFile.Length != entry.Length)
            {
                throw new InvalidDataException($"内置 B 服组件包含异常文件：{entry.FullName}");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("内置 B 服组件解压大小超过安全上限。");
            }

            files.Add(new(entry, relative));
        }

        if (paths.Count != expected.Count || expected.Keys.Any(path => !paths.Contains(path)))
        {
            throw new InvalidDataException("内置 B 服组件缺少切换所需文件。");
        }

        return files;
    }

    private void ValidateAllFiles(
        string root,
        IReadOnlyDictionary<string, ExpectedFile> expected)
    {
        foreach (var item in expected.Values)
        {
            var path = PathSafety.ResolveOrThrow(root, item.RelativePath);
            var result = _integrity.Validate(path, item.Length, item.Sha256);
            if (!result.IsValid)
            {
                throw new InvalidDataException($"B 服组件校验失败：{item.RelativePath}；{result.Message}");
            }
        }
    }

    private void WriteMarker(string target, int fileCount)
    {
        var path = Path.Combine(target, MarkerFileName);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(
                   temporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new BundleMarker
            {
                BundleId = _bundleId,
                GameVersion = _supportedGameVersion,
                FileCount = fileCount
            }, JsonSupport.Options);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':') ||
            normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"B 服组件路径不安全：{path}");
        }

        return normalized;
    }

    private static void EnsureAvailableSpace(string targetRoot, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(targetRoot));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("无法确定 B 服组件目标磁盘。");
        }

        var available = new DriveInfo(root).AvailableFreeSpace;
        var required = checked(requiredBytes + 64L * 1024 * 1024);
        if (available < required)
        {
            throw new IOException(
                $"B 服组件需要至少 {ByteSizeFormatter.Format(required)} 可用空间，当前仅有 {ByteSizeFormatter.Format(available)}。");
        }
    }

    private static void RecoverInterruptedInstall(string packageRoot, string target)
    {
        var previous = Directory.GetDirectories(
            packageRoot,
            ".previous-bilibili-*",
            SearchOption.TopDirectoryOnly);
        var installing = Directory.GetDirectories(
            packageRoot,
            ".installing-bilibili-*",
            SearchOption.TopDirectoryOnly);
        if (!Directory.Exists(target) && previous.Length == 1)
        {
            EnsureNotReparsePoint(previous[0]);
            Directory.Move(previous[0], target);
            previous = [];
        }
        else if (!Directory.Exists(target) && previous.Length > 1)
        {
            throw new InvalidOperationException($"检测到多个中断的 B 服组件备份：{packageRoot}");
        }

        foreach (var path in previous.Concat(installing))
        {
            DeleteDirectoryRobust(path);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"拒绝写入重解析点 B 服组件目录：{path}");
        }
    }

    private static void DeleteDirectoryRobust(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        EnsureNotReparsePoint(path);
        var root = new DirectoryInfo(path);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var file in root.EnumerateFiles("*", options))
        {
            file.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System);
        }

        root.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System);
        root.Delete(true);
    }

    private static BundledBilibiliPackageResult Result(
        BundledBilibiliPackageStatus status,
        string target,
        IReadOnlyDictionary<string, ExpectedFile> expected) =>
        new(status, target, expected.Count, expected.Values.Sum(item => item.Length));

    private sealed record ExpectedFile(string RelativePath, long Length, string Sha256);
    private sealed record ArchiveFile(ZipArchiveEntry Entry, string RelativePath);

    private sealed class BundleMarker
    {
        public string? BundleId { get; init; }
        public string? GameVersion { get; init; }
        public int FileCount { get; init; }
    }
}
