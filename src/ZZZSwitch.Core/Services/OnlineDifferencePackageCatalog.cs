using System.Text.Json;
using System.Security.Cryptography;
using ZZZSwitch.Core.Models;
using ZZZSwitch.ManifestTool.Download;

namespace ZZZSwitch.Core.Services;

public sealed class OnlineDifferencePackageCatalog
{
    private const string TransitionManifestName = "transition-manifest.json";
    private readonly AppPaths _paths;

    public OnlineDifferencePackageCatalog(AppPaths paths) => _paths = paths;

    public OnlineDifferenceInventory GetInventory()
    {
        var packages = new List<OnlineDifferencePackageInfo>();
        if (Directory.Exists(_paths.OnlineDifferenceFilesRoot))
        {
            foreach (var versionDirectory in SafeDirectories(_paths.OnlineDifferenceFilesRoot))
            {
                foreach (var targetDirectory in SafeDirectories(versionDirectory.FullName))
                {
                    foreach (var workspace in SafeDirectories(targetDirectory.FullName))
                    {
                        packages.Add(InspectWorkspace(
                            versionDirectory.Name,
                            targetDirectory.Name,
                            workspace));
                    }
                }
            }
        }

        var manifestFiles = Directory.Exists(_paths.ManifestCacheRoot)
            ? Directory.EnumerateFiles(_paths.ManifestCacheRoot, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToArray()
            : [];
        return new OnlineDifferenceInventory
        {
            Packages = packages
                .OrderByDescending(item => VersionKey(item.GameVersion))
                .ThenBy(item => item.TargetProfile, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.LastUpdated)
                .ToArray(),
            ManifestCacheFileCount = manifestFiles.Length,
            ManifestCacheBytes = manifestFiles.Aggregate(
                0L, (sum, file) => checked(sum + file.Length))
        };
    }

    public bool TryGetReadyMaterialization(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        out OnlineDifferenceMaterialization? materialization)
    {
        materialization = null;
        var candidates = GetInventory().Packages
            .Where(item => item.State == OnlineDifferencePackageState.Ready)
            .Where(item => string.Equals(item.GameVersion, gameVersion, StringComparison.Ordinal))
            .Where(item => string.Equals(item.SourceProfile, sourceProfile, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.TargetProfile, targetProfile, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.LastUpdated)
            .ToArray();
        foreach (var candidate in candidates)
        {
            if (!TryReadTransition(candidate.WorkspacePath, out var manifest, out _))
            {
                continue;
            }

            try
            {
                VerifyPackage(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                continue;
            }

            var content = Path.Combine(candidate.WorkspacePath, "content");
            materialization = new OnlineDifferenceMaterialization
            {
                PackageRoot = content,
                PackageDirectory = content,
                Manifest = manifest!,
                DownloadedFiles = 0,
                ReusedFiles = manifest!.ReplaceFiles.Count,
                ReusedReadyPackage = true
            };
            return true;
        }

        return false;
    }

    public void DeletePackage(string workspacePath)
    {
        var root = Path.GetFullPath(_paths.OnlineDifferenceFilesRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var workspace = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar);
        if (!workspace.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workspace + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(workspace))
        {
            throw new InvalidDataException("版本资源目录不在 ZZZSwitch 在线差异缓存范围内。");
        }

        if ((File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("拒绝删除重解析点版本资源目录。");
        }

        Directory.Delete(workspace, recursive: true);
        RemoveEmptyParents(Path.GetDirectoryName(workspace), root.TrimEnd(Path.DirectorySeparatorChar));
    }

    public OnlineDifferencePackagePreview GetPreview(OnlineDifferencePackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var workspace = ResolveWorkspace(package.WorkspacePath, requireExists: true);
        var content = Path.Combine(workspace, "content");
        if (!TryReadTransition(workspace, out var manifest, out var problem))
        {
            var partialFiles = SafeFiles(content)
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(file => new OnlineDifferencePreviewFile(
                    Path.GetRelativePath(content, file.FullName).Replace('/', '\\'),
                    file.Length,
                    "清单未完成",
                    "未完成"))
                .ToArray();
            return new OnlineDifferencePackagePreview
            {
                Package = package,
                Files = partialFiles,
                DeleteFiles = [],
                Notes = problem
            };
        }

        var files = manifest!.ReplaceFiles.Select(entry =>
        {
            var path = SophonFileDownloader.ResolveUnderRoot(content, entry.Source);
            var state = !File.Exists(path)
                ? "缺失"
                : entry.Length.HasValue && new FileInfo(path).Length != entry.Length.Value
                    ? "长度不符"
                    : "已就绪";
            return new OnlineDifferencePreviewFile(
                entry.Target,
                entry.Length,
                string.IsNullOrWhiteSpace(entry.Sha256) ? "完整性数据缺失" : "已记录",
                state);
        }).ToArray();
        return new OnlineDifferencePackagePreview
        {
            Package = package,
            Files = files,
            DeleteFiles = manifest.DeleteFiles.Select(entry => entry.Target).ToArray(),
            Notes = manifest.Notes
        };
    }

    public void VerifyPackage(OnlineDifferencePackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var workspace = ResolveWorkspace(package.WorkspacePath, requireExists: true);
        if (!TryReadTransition(workspace, out var manifest, out var problem))
        {
            throw new InvalidDataException(problem ?? "差异包清单不可读。");
        }

        var content = Path.Combine(workspace, "content");
        foreach (var entry in manifest!.ReplaceFiles)
        {
            if (!entry.Length.HasValue || string.IsNullOrWhiteSpace(entry.Sha256))
            {
                throw new InvalidDataException($"差异包清单缺少长度或完整性数据：{entry.Source}");
            }

            var path = SophonFileDownloader.ResolveUnderRoot(content, entry.Source);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Length.Value)
            {
                throw new InvalidDataException($"差异包文件缺失或长度不匹配：{entry.Source}");
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"差异包文件完整性不匹配：{entry.Source}");
            }
        }
    }

    public int DeleteSupersededPackages(
        string sourceProfile,
        string targetProfile,
        string gameVersion,
        string keepWorkspacePath)
    {
        var keep = ResolveWorkspace(keepWorkspacePath, requireExists: true);
        var superseded = GetInventory().Packages
            .Where(item => string.Equals(item.SourceProfile, sourceProfile, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.TargetProfile, targetProfile, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.GameVersion, gameVersion, StringComparison.Ordinal))
            .Where(item => !string.Equals(
                Path.GetFullPath(item.WorkspacePath).TrimEnd(Path.DirectorySeparatorChar),
                keep,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var package in superseded)
        {
            DeletePackage(package.WorkspacePath);
        }

        return superseded.Length;
    }

    private OnlineDifferencePackageInfo InspectWorkspace(
        string version,
        string targetProfile,
        DirectoryInfo workspace)
    {
        var content = Path.Combine(workspace.FullName, "content");
        var chunks = Path.Combine(workspace.FullName, "chunks");
        var contentFiles = SafeFiles(content);
        var chunkFiles = SafeFiles(chunks);
        var contentBytes = contentFiles.Aggregate(0L, (sum, file) => checked(sum + file.Length));
        var chunkBytes = chunkFiles.Aggregate(0L, (sum, file) => checked(sum + file.Length));
        var fallbackSource = targetProfile switch
        {
            ProfileIds.CnOfficial => ProfileIds.Global,
            ProfileIds.Global => ProfileIds.CnOfficial,
            _ => string.Empty
        };

        if (!TryReadTransition(workspace.FullName, out var manifest, out var problem))
        {
            var hasManifest = File.Exists(Path.Combine(workspace.FullName, TransitionManifestName));
            return new OnlineDifferencePackageInfo
            {
                GameVersion = version,
                SourceProfile = fallbackSource,
                TargetProfile = targetProfile,
                ManifestId = workspace.Name,
                WorkspacePath = workspace.FullName,
                State = hasManifest ? OnlineDifferencePackageState.Invalid : OnlineDifferencePackageState.Incomplete,
                FileCount = contentFiles.Length,
                ContentBytes = contentBytes,
                CheckpointCount = chunkFiles.Length,
                CheckpointBytes = chunkBytes,
                LastUpdated = workspace.LastWriteTimeUtc,
                Problem = problem
            };
        }

        var state = ValidateReady(
            version, targetProfile, content, manifest!, out var readyProblem)
            ? OnlineDifferencePackageState.Ready
            : OnlineDifferencePackageState.Invalid;
        return new OnlineDifferencePackageInfo
        {
            GameVersion = version,
            SourceProfile = manifest!.SourceProfile,
            TargetProfile = manifest.TargetProfile,
            ManifestId = workspace.Name,
            WorkspacePath = workspace.FullName,
            State = state,
            FileCount = contentFiles.Length,
            ContentBytes = contentBytes,
            CheckpointCount = chunkFiles.Length,
            CheckpointBytes = chunkBytes,
            LastUpdated = File.GetLastWriteTimeUtc(Path.Combine(workspace.FullName, TransitionManifestName)),
            Problem = readyProblem
        };
    }

    private static bool ValidateReady(
        string version,
        string targetProfile,
        string content,
        TransitionManifest manifest,
        out string? problem)
    {
        problem = null;
        if (!manifest.Enabled ||
            !string.Equals(manifest.GameVersion, version, StringComparison.Ordinal) ||
            !string.Equals(manifest.TargetProfile, targetProfile, StringComparison.OrdinalIgnoreCase) ||
            manifest.ExpectedReplaceCount != manifest.ReplaceFiles.Count)
        {
            problem = "动态切换清单与目录身份或文件数量不匹配。";
            return false;
        }

        foreach (var entry in manifest.ReplaceFiles)
        {
            if (!entry.Length.HasValue)
            {
                problem = $"清单缺少文件长度：{entry.Source}";
                return false;
            }

            string path;
            try
            {
                path = SophonFileDownloader.ResolveUnderRoot(content, entry.Source);
            }
            catch (InvalidDataException ex)
            {
                problem = ex.Message;
                return false;
            }

            if (!File.Exists(path) || new FileInfo(path).Length != entry.Length.Value)
            {
                problem = $"文件缺失或长度不匹配：{entry.Source}";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadTransition(
        string workspace,
        out TransitionManifest? manifest,
        out string? problem)
    {
        manifest = null;
        problem = null;
        var path = Path.Combine(workspace, TransitionManifestName);
        if (!File.Exists(path))
        {
            problem = "尚未生成完整差异包清单。";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            manifest = JsonSerializer.Deserialize<TransitionManifest>(stream, JsonSupport.Options);
            if (manifest is null)
            {
                throw new InvalidDataException("动态切换清单为空。");
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static DirectoryInfo[] SafeDirectories(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateDirectories()
                .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                .ToArray()
            : [];

    private static FileInfo[] SafeFiles(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .ToArray()
            : [];

    private static string VersionKey(string version) => string.Join(
        '.',
        version.Split('.').Select(part => int.TryParse(part, out var value) ? value.ToString("D8") : part));

    private static void RemoveEmptyParents(string? path, string stopRoot)
    {
        while (!string.IsNullOrWhiteSpace(path) &&
               !string.Equals(path, stopRoot, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(path) &&
               !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
            path = Path.GetDirectoryName(path);
        }
    }

    private string ResolveWorkspace(string workspacePath, bool requireExists)
    {
        var root = Path.GetFullPath(_paths.OnlineDifferenceFilesRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var workspace = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar);
        if (!workspace.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workspace + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase) ||
            requireExists && !Directory.Exists(workspace))
        {
            throw new InvalidDataException("客户端差异包目录不在 ZZZSwitch 管理范围内。");
        }

        if (Directory.Exists(workspace) &&
            (File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("拒绝访问重解析点客户端差异包目录。");
        }

        return workspace;
    }
}
