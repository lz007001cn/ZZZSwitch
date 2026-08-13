using System.IO.Compression;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed record PackageImportResult(
    string GameVersion,
    string PackageRoot,
    int FileCount,
    long TotalBytes,
    bool ReplacedExisting,
    string? RetainedPreviousPath);

public sealed class PackageImportService
{
    private const int MaximumEntries = 100_000;
    private const long MaximumExpandedBytes = 20L * 1024 * 1024 * 1024;
    private readonly ConfigurationRepository _configuration;
    private readonly FileIntegrityService _integrity = new(new PhysicalFileOperations());

    public PackageImportService(ConfigurationRepository configuration) =>
        _configuration = configuration;

    public PackageImportResult Import(string archivePath, string gamePath, string currentGameVersion)
    {
        if (!File.Exists(archivePath) ||
            !string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("请选择有效的 ZIP 差异包。", archivePath);
        }

        var normalizedGamePath = Path.GetFullPath(gamePath);
        var packageRoot = GameStorageLayout.GetPackageRoot(normalizedGamePath, currentGameVersion);
        var packagesRoot = GameStorageLayout.GetPackagesRoot(normalizedGamePath);
        EnsureNotReparsePoint(packagesRoot);
        EnsureNotReparsePoint(packageRoot);

        using var archive = ZipFile.OpenRead(archivePath);
        var files = InspectArchive(archive, currentGameVersion);
        RecoverInterruptedImport(packagesRoot, packageRoot, currentGameVersion);
        var expandedBytes = files.Sum(item => item.Entry.Length);
        var staging = Path.Combine(packagesRoot, $".importing-{currentGameVersion}-{Guid.NewGuid():N}");
        var previous = Path.Combine(packagesRoot, $".previous-{currentGameVersion}-{Guid.NewGuid():N}");
        var targetMoved = false;
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var item in files)
            {
                var target = PathSafety.ResolveOrThrow(staging, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = item.Entry.Open();
                using var destination = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                source.CopyTo(destination);
                if (destination.Length != item.Entry.Length)
                {
                    throw new IOException($"差异包解压长度不匹配：{item.RelativePath}");
                }
            }

            ValidateExtractedPackage(staging, currentGameVersion);
            Directory.CreateDirectory(packagesRoot);
            if (Directory.Exists(packageRoot))
            {
                EnsureNotReparsePoint(packageRoot);
                Directory.Move(packageRoot, previous);
                targetMoved = true;
            }

            Directory.Move(staging, packageRoot);
            committed = true;

            string? retainedPrevious = null;
            if (targetMoved)
            {
                try
                {
                    DeleteDirectoryRobust(previous);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    retainedPrevious = previous;
                }
            }

            return new(
                currentGameVersion,
                packageRoot,
                files.Count,
                expandedBytes,
                targetMoved,
                retainedPrevious);
        }
        catch
        {
            if (!committed && targetMoved && !Directory.Exists(packageRoot) && Directory.Exists(previous))
            {
                Directory.Move(previous, packageRoot);
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

    private static List<ArchiveFile> InspectArchive(ZipArchive archive, string currentGameVersion)
    {
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException($"差异包文件数量超过安全上限 {MaximumEntries:N0}。 ");
        }

        var prefix = $".zzzswitch/packages/{currentGameVersion}/";
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

            if (string.Equals(name, "README-Packages.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"差异包目录结构不正确，或资源版本不是 {currentGameVersion}：{entry.FullName}");
            }

            var relative = name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative) ||
                relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "" or "." or "..") ||
                Path.IsPathRooted(relative) ||
                relative.Contains(':') ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            {
                throw new InvalidDataException($"差异包包含不安全路径或符号链接：{entry.FullName}");
            }

            if (!paths.Add(relative))
            {
                throw new InvalidDataException($"差异包包含重复路径：{entry.FullName}");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("差异包解压后的总大小超过 20 GiB 安全上限。");
            }

            files.Add(new(entry, relative));
        }

        if (files.Count == 0 || !paths.Contains("version.ini"))
        {
            throw new InvalidDataException("差异包为空或缺少 version.ini。");
        }

        foreach (var profile in ProfileIds.All)
        {
            var profilePrefix = profile + Path.DirectorySeparatorChar;
            if (!paths.Any(path => path.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"统一差异包缺少 {profile} 目录内容。");
            }
        }

        return files;
    }

    private void ValidateExtractedPackage(string packageRoot, string gameVersion)
    {
        var profiles = _configuration.LoadProfilesWithStatus();
        var transitions = _configuration.LoadTransitionsWithStatus();
        if (profiles.Errors.Count > 0 || transitions.Errors.Count > 0)
        {
            throw new InvalidDataException("软件内置差异包配置不完整，无法安全校验导入内容。");
        }

        var profileDirectories = profiles.Items.ToDictionary(
            profile => profile.Id,
            profile => profile.PackageDirectoryName,
            StringComparer.Ordinal);
        foreach (var profile in ProfileIds.All)
        {
            if (!profileDirectories.TryGetValue(profile, out var directoryName) ||
                !Directory.Exists(PathSafety.ResolveOrThrow(packageRoot, directoryName)))
            {
                throw new InvalidDataException($"导入内容缺少 {profile} 的有效目录。");
            }
        }

        var relevantTransitions = transitions.Items
            .Where(item => item.Enabled && string.Equals(item.GameVersion, gameVersion, StringComparison.Ordinal))
            .ToArray();
        var expectedDirections = (
            from source in ProfileIds.All
            from target in ProfileIds.All
            where !string.Equals(source, target, StringComparison.Ordinal)
            select $"{source}>{target}").ToHashSet(StringComparer.Ordinal);
        var actualDirections = relevantTransitions
            .Select(item => $"{item.SourceProfile}>{item.TargetProfile}")
            .ToArray();
        if (actualDirections.Length != expectedDirections.Count ||
            actualDirections.Distinct(StringComparer.Ordinal).Count() != expectedDirections.Count ||
            actualDirections.Any(direction => !expectedDirections.Contains(direction)))
        {
            throw new InvalidDataException($"软件没有游戏版本 {gameVersion} 的完整六向切换配置。");
        }

        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in relevantTransitions)
        {
            var defaultDirectory = PathSafety.ResolveOrThrow(
                packageRoot,
                profileDirectories[transition.TargetProfile]);
            foreach (var entry in transition.ReplaceFiles)
            {
                var source = PackageFileResolver.ResolveOrThrow(packageRoot, defaultDirectory, entry);
                if (!validated.Add(source))
                {
                    continue;
                }

                var result = _integrity.Validate(source, entry.Length, entry.Sha256);
                if (!result.IsValid)
                {
                    throw new InvalidDataException($"差异文件校验失败：{entry.Source}；{result.Message}");
                }
            }
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"拒绝覆盖重解析点差异包目录：{path}");
        }
    }

    private static void RecoverInterruptedImport(
        string packagesRoot,
        string packageRoot,
        string gameVersion)
    {
        if (!Directory.Exists(packagesRoot))
        {
            return;
        }

        var previous = Directory.GetDirectories(
            packagesRoot,
            $".previous-{gameVersion}-*",
            SearchOption.TopDirectoryOnly);
        var importing = Directory.GetDirectories(
            packagesRoot,
            $".importing-{gameVersion}-*",
            SearchOption.TopDirectoryOnly);

        if (!Directory.Exists(packageRoot) && previous.Length == 1)
        {
            EnsureNotReparsePoint(previous[0]);
            Directory.Move(previous[0], packageRoot);
            previous = [];
        }
        else if (!Directory.Exists(packageRoot) && previous.Length > 1)
        {
            throw new InvalidOperationException(
                $"检测到多个中断导入备份，无法自动判断应恢复哪一个：{packagesRoot}");
        }

        foreach (var path in previous.Concat(importing))
        {
            DeleteDirectoryRobust(path);
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

    private sealed record ArchiveFile(ZipArchiveEntry Entry, string RelativePath);
}
