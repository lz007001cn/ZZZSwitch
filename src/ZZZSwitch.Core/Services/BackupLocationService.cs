using System.Security.Cryptography;

namespace ZZZSwitch.Core.Services;

public sealed class BackupLocationService
{
    private readonly AppPaths _paths;

    public BackupLocationService(AppPaths paths) => _paths = paths;

    public BackupLocationUsage GetUsage()
    {
        var measure = MeasureDirectory(_paths.BackupsRoot);
        var backupCount = Directory.Exists(_paths.BackupsRoot)
            ? Directory.GetDirectories(_paths.BackupsRoot).Length
            : 0;
        return new(
            _paths.BackupsRoot,
            backupCount,
            measure.FileCount,
            measure.TotalBytes,
            !SamePath(_paths.BackupsRoot, _paths.DefaultBackupsRoot));
    }

    public BackupLocationMigrationResult ChangeLocation(string requestedRoot, string? gamePath = null)
    {
        var sourceRoot = NormalizeBackupRoot(_paths.BackupsRoot);
        var targetRoot = NormalizeBackupRoot(requestedRoot);
        ValidateTarget(targetRoot, gamePath);
        if (SamePath(sourceRoot, targetRoot))
        {
            return new(sourceRoot, targetRoot, 0, 0, false, true);
        }

        if (IsSameOrChild(sourceRoot, targetRoot) || IsSameOrChild(targetRoot, sourceRoot))
        {
            throw new InvalidOperationException("目标备份目录不能与当前备份目录相互包含。");
        }

        if (Directory.Exists(targetRoot) && Directory.EnumerateFileSystemEntries(targetRoot).Any())
        {
            throw new InvalidOperationException("目标备份目录必须为空。请新建或选择一个空目录。");
        }

        var targetParent = Directory.GetParent(targetRoot)?.FullName
                           ?? throw new InvalidOperationException("无法确定目标备份目录的父目录。");
        Directory.CreateDirectory(targetParent);
        VerifyWritable(targetParent);
        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot);
        }

        var sourceExists = Directory.Exists(sourceRoot) &&
                           Directory.EnumerateFileSystemEntries(sourceRoot).Any();
        var measure = sourceExists ? MeasureDirectory(sourceRoot) : new DirectoryMeasure(0, 0);
        var staging = targetRoot + ".migrating-" + Guid.NewGuid().ToString("N");
        var destinationCommitted = false;
        var settingCommitted = false;
        try
        {
            if (sourceExists)
            {
                CopyDirectoryVerified(sourceRoot, staging);
                Directory.Move(staging, targetRoot);
            }
            else
            {
                Directory.CreateDirectory(targetRoot);
            }

            destinationCommitted = true;
            SaveLocation(targetRoot);
            _paths.SetBackupsRoot(targetRoot);
            settingCommitted = true;

            var sourceRemoved = true;
            if (sourceExists && Directory.Exists(sourceRoot))
            {
                try
                {
                    Directory.Delete(sourceRoot, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    sourceRemoved = false;
                }
            }

            return new(
                sourceRoot,
                targetRoot,
                measure.FileCount,
                measure.TotalBytes,
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
                    TryDeleteDirectory(targetRoot);
                }
            }

            throw;
        }
    }

    public BackupLocationMigrationResult RestoreDefaultLocation(string? gamePath = null) =>
        ChangeLocation(_paths.DefaultBackupsRoot, gamePath);

    internal static string NormalizeBackupRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("备份目录必须是完整的本地路径。", nameof(path));
        }

        var normalized = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(normalized)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("不能将磁盘根目录直接用作备份目录。", nameof(path));
        }

        return normalized;
    }

    private void SaveLocation(string backupRoot)
    {
        var settings = new BackupLocationSettings
        {
            BackupRootPath = SamePath(backupRoot, _paths.DefaultBackupsRoot) ? null : backupRoot
        };
        Directory.CreateDirectory(_paths.DataRoot);
        AtomicJsonFile.Write(_paths.BackupLocationFile, settings);
    }

    private void ValidateTarget(string targetRoot, string? gamePath)
    {
        if (!SamePath(targetRoot, _paths.DefaultBackupsRoot) &&
            (IsSameOrChild(_paths.DataRoot, targetRoot) || IsSameOrChild(targetRoot, _paths.DataRoot)))
        {
            throw new InvalidOperationException("自定义备份目录不能与应用数据目录重叠。");
        }

        if (string.IsNullOrWhiteSpace(gamePath) || !Path.IsPathFullyQualified(gamePath))
        {
            return;
        }

        var normalizedGame = Path.GetFullPath(gamePath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var storageRoot = GameStorageLayout.GetRoot(normalizedGame);
        if (IsSameOrChild(normalizedGame, targetRoot) || IsSameOrChild(targetRoot, normalizedGame))
        {
            throw new InvalidOperationException("备份目录不能与游戏目录重叠。");
        }

        if (IsSameOrChild(storageRoot, targetRoot) || IsSameOrChild(targetRoot, storageRoot))
        {
            throw new InvalidOperationException("备份目录不能与 .zzzswitch 存储目录重叠。");
        }
    }

    private static void CopyDirectoryVerified(string sourceRoot, string targetRoot)
    {
        EnsureNoReparsePoint(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            EnsureNoReparsePoint(directory);
            Directory.CreateDirectory(Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (var source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            EnsureNoReparsePoint(source);
            var target = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, false);
            if (new FileInfo(source).Length != new FileInfo(target).Length ||
                !string.Equals(ComputeSha256(source), ComputeSha256(target), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"备份迁移校验失败：{Path.GetRelativePath(sourceRoot, source)}");
            }
        }
    }

    private static DirectoryMeasure MeasureDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return new(0, 0);
        }

        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        return new(files.Length, files.Sum(path => new FileInfo(path).Length));
    }

    private static void VerifyWritable(string path)
    {
        var probe = Path.Combine(path, ".zzzswitch-backup-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, [0x5A]);
            using var stream = new FileStream(probe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Flush(true);
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"备份目录中包含不支持迁移的链接：{path}");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(NormalizeBackupRoot(left), NormalizeBackupRoot(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChild(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
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
            // Best effort cleanup after a failed, uncommitted migration.
        }
    }

    private sealed record DirectoryMeasure(int FileCount, long TotalBytes);
}

public sealed class BackupLocationSettings
{
    public string? BackupRootPath { get; set; }
}

public sealed record BackupLocationUsage(
    string BackupRootPath,
    int BackupCount,
    int FileCount,
    long TotalBytes,
    bool IsCustomLocation);

public sealed record BackupLocationMigrationResult(
    string SourceBackupRoot,
    string TargetBackupRoot,
    int MigratedFileCount,
    long MigratedBytes,
    bool ContentMoved,
    bool SourceRemoved);
