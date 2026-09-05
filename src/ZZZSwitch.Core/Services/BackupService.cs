using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class BackupService
{
    private readonly IFileOperations _files;
    private readonly AppPaths _paths;
    private readonly VerifiedFileTransfer _transfers;

    public BackupService(IFileOperations files, AppPaths paths)
    {
        _files = files;
        _paths = paths;
        _transfers = new VerifiedFileTransfer(files);
    }

    public BackupRecord CreateBackup(SwitchPlan plan, IEnumerable<string>? additionalAffectedFiles = null)
    {
        var affected = plan.Manifest.ReplaceFiles.Select(x => x.Target)
            .Concat(plan.Manifest.IniPatches.Select(x => x.Target))
            .Concat(plan.Manifest.DeleteFiles.Select(x => x.Target))
            .Concat(plan.Manifest.OptionalDeleteFiles.Select(x => x.Target))
            .Concat(additionalAffectedFiles ?? []);
        return CreateBackupForAffectedFiles(plan, affected);
    }

    public BackupRecord CreateBackupForAffectedFiles(SwitchPlan plan, IEnumerable<string> affectedFiles)
    {
        _paths.EnsureWritableDirectories();
        EnsureUnderBackupsRoot(plan.BackupPath);
        _files.CreateDirectory(plan.BackupPath);
        var affected = affectedFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var record = new BackupRecord
        {
            OperationId = plan.OperationId,
            OperationTime = DateTimeOffset.Now,
            SourceProfile = plan.Manifest.SourceProfile,
            TargetProfile = plan.Manifest.TargetProfile,
            GameVersion = plan.Manifest.GameVersion,
            GamePath = plan.GamePath,
            FilesPlannedForDeletion = plan.Manifest.DeleteFiles.Select(x => x.Target).ToList(),
            ReplaceCount = plan.Manifest.PlannedReplaceCount,
            DeleteCount = plan.Manifest.PlannedDeleteCount
        };
        var filesRoot = Path.Combine(plan.BackupPath, "files");

        foreach (var relative in affected)
        {
            var source = PathSafety.ResolveOrThrow(plan.GamePath, relative);
            if (!_files.FileExists(source))
            {
                record.OriginallyMissingFiles.Add(relative);
                continue;
            }

            var target = PathSafety.ResolveOrThrow(filesRoot, relative);
            var parent = Path.GetDirectoryName(target);
            if (parent is not null)
            {
                _files.CreateDirectory(parent);
            }
            _transfers.CopyAndVerify(source, target, overwrite: false);
            record.BackedUpFiles.Add(relative);
        }

        SaveRecord(plan.BackupPath, record);
        return record;
    }

    public bool Rollback(string backupPath, BackupRecord record, out string detail)
    {
        EnsureUnderBackupsRoot(backupPath);
        var failures = new List<string>();
        var filesRoot = Path.Combine(backupPath, "files");

        foreach (var relative in record.BackedUpFiles)
        {
            try
            {
                var source = ResolveBackupSource(record, filesRoot, relative);
                var destination = PathSafety.ResolveOrThrow(record.GamePath, relative);
                var parent = Path.GetDirectoryName(destination);
                if (parent is not null)
                {
                    _files.CreateDirectory(parent);
                }

                _transfers.CopyAndVerify(source, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                failures.Add($"{relative}: {ex.Message}");
            }
        }

        foreach (var relative in record.OriginallyMissingFiles)
        {
            try
            {
                var destination = PathSafety.ResolveOrThrow(record.GamePath, relative);
                if (_files.FileExists(destination))
                {
                    _files.DeleteFile(destination);
                }

                if (_files.FileExists(destination))
                {
                    throw new IOException("新增文件未能删除。");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{relative}: {ex.Message}");
            }
        }

        detail = failures.Count == 0 ? "回滚完整成功。" : string.Join(Environment.NewLine, failures);
        return failures.Count == 0;
    }

    public IReadOnlyList<(string Path, BackupRecord Record)> ListBackups()
    {
        if (!Directory.Exists(_paths.BackupsRoot))
        {
            return [];
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_paths.BackupsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var result = new List<(string, BackupRecord)>();
        foreach (var directory in directories)
        {
            var recordPath = Path.Combine(directory, "backup.json");
            if (!File.Exists(recordPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(recordPath);
                var record = JsonSerializer.Deserialize<BackupRecord>(stream, JsonSupport.Options);
                // 历史窗口只展示可安全恢复的记录；坏记录保留在磁盘上供人工检查。
                if (record is not null && IsUsableRecord(record))
                {
                    result.Add((directory, record));
                }
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                // A damaged record is ignored here and remains on disk for manual inspection.
            }
        }

        return result.OrderByDescending(x => x.Item2.OperationTime).ToArray();
    }

    public BackupRecord LoadRecord(string backupPath)
    {
        EnsureUnderBackupsRoot(backupPath);
        var recordPath = Path.Combine(backupPath, "backup.json");
        if (!File.Exists(recordPath))
        {
            throw new FileNotFoundException("备份记录不存在。", recordPath);
        }

        using var stream = File.OpenRead(recordPath);
        var record = JsonSerializer.Deserialize<BackupRecord>(stream, JsonSupport.Options)
                     ?? throw new InvalidDataException("备份记录内容为空。");
        if (!IsUsableRecord(record))
        {
            throw new InvalidDataException("备份记录缺少恢复所需的必要字段。");
        }

        return record;
    }

    public void DeleteBackup(string backupPath)
    {
        EnsureUnderBackupsRoot(backupPath);
        if (Directory.Exists(backupPath))
        {
            _files.DeleteDirectory(backupPath, true);
        }
    }

    public int PruneRedundantBackups(string retainedBackupPath, string gamePath)
    {
        EnsureUnderBackupsRoot(retainedBackupPath);
        var retained = Path.GetFullPath(retainedBackupPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedGamePath = Path.GetFullPath(gamePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = ListBackups()
            .Where(candidate => SameGame(candidate.Record.GamePath, normalizedGamePath))
            .ToArray();
        var retainedCandidate = candidates.FirstOrDefault(candidate =>
            SameBackupPath(candidate.Path, retained));
        if (retainedCandidate.Record is null || !IsRestorable(candidate: retainedCandidate.Record))
        {
            throw new InvalidOperationException("当前备份不是可恢复的成功备份，拒绝执行轮换。");
        }

        var keep = SelectLatestSourceBackups(candidates, retained);

        return PruneCandidates(candidates, keep);
    }

    public int PruneAllBackups(string? retainedBackupPath = null)
    {
        var protectedBackupPath = TryNormalizeBackupPath(retainedBackupPath);
        var candidates = ListBackups();
        var keep = SelectLatestSourceBackups(candidates, protectedBackupPath);

        return PruneCandidates(candidates, keep);
    }

    private static HashSet<string> SelectLatestSourceBackups(
        IEnumerable<(string Path, BackupRecord Record)> candidates,
        string? protectedBackupPath)
    {
        return candidates
            .Where(candidate => IsRestorable(candidate.Record))
            .GroupBy(candidate => NormalizeGamePath(candidate.Record.GamePath), StringComparer.OrdinalIgnoreCase)
            .SelectMany(gameGroup => gameGroup
                .GroupBy(candidate => candidate.Record.SourceProfile, StringComparer.Ordinal)
                .Select(sourceGroup => sourceGroup
                    .OrderByDescending(candidate =>
                        protectedBackupPath is not null && SameBackupPath(candidate.Path, protectedBackupPath))
                    .ThenByDescending(candidate => candidate.Record.OperationTime)
                    .ThenByDescending(candidate => candidate.Record.OperationId, StringComparer.Ordinal)
                    .ThenByDescending(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                    .First()))
            .Select(candidate => NormalizeBackupPath(candidate.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private int PruneCandidates(
        IEnumerable<(string Path, BackupRecord Record)> candidates,
        IReadOnlySet<string> keep)
    {
        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (keep.Contains(NormalizeBackupPath(candidate.Path)) || !CanPrune(candidate.Record))
            {
                continue;
            }

            try
            {
                DeleteBackup(candidate.Path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Rotation is best effort. A locked old backup must never turn a
                // successfully committed server switch into a failed transaction.
            }
        }

        return removed;
    }

    public bool TryDeleteBackup(string backupPath)
    {
        try
        {
            DeleteBackup(backupPath);
            return !Directory.Exists(backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryDeleteCompletedRollback(string backupPath) => TryDeleteBackup(backupPath);

    public void SaveRecord(string backupPath, BackupRecord record)
    {
        EnsureUnderBackupsRoot(backupPath);
        var path = Path.Combine(backupPath, "backup.json");
        AtomicJsonFile.Write(path, record);
    }

    private static bool IsUsableRecord(BackupRecord record)
    {
        // backup.json 属于恢复输入，不能因为它位于应用目录内就信任其中的游戏路径和相对路径。
        if (string.IsNullOrWhiteSpace(record.OperationId) ||
            !ProfileIds.All.Contains(record.SourceProfile, StringComparer.Ordinal) ||
            !ProfileIds.All.Contains(record.TargetProfile, StringComparer.Ordinal) ||
            string.Equals(record.SourceProfile, record.TargetProfile, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(record.GameVersion) ||
            string.IsNullOrWhiteSpace(record.GamePath) ||
            record.BackedUpFiles is null ||
            record.OriginallyMissingFiles is null ||
            record.FilesPlannedForDeletion is null)
        {
            return false;
        }

        try
        {
            var gamePath = Path.GetFullPath(record.GamePath);
            var paths = record.BackedUpFiles
                .Concat(record.OriginallyMissingFiles)
                .Concat(record.FilesPlannedForDeletion);
            if (paths.Any(relative =>
                    string.IsNullOrWhiteSpace(relative) ||
                    !PathSafety.TryResolveUnderRoot(gamePath, relative, out _, out _)))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool CanPrune(BackupRecord record) =>
        string.Equals(record.OperationResult, "success", StringComparison.Ordinal) ||
        record.RestoredAt is not null ||
        (string.Equals(record.OperationResult, "failed", StringComparison.Ordinal) &&
         string.Equals(record.RollbackResult, "success", StringComparison.Ordinal)) ||
        (string.Equals(record.OperationResult, "interrupted", StringComparison.Ordinal) &&
         string.Equals(record.RollbackResult, "startup_recovery_success", StringComparison.Ordinal));

    private static bool IsRestorable(BackupRecord candidate) =>
        string.Equals(candidate.OperationResult, "success", StringComparison.Ordinal) &&
        candidate.RestoredAt is null;

    private static string NormalizeBackupPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeGamePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? TryNormalizeBackupPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return NormalizeBackupPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SameBackupPath(string candidate, string normalizedExpected) =>
        string.Equals(NormalizeBackupPath(candidate), normalizedExpected, StringComparison.OrdinalIgnoreCase);

    private static bool SameGame(string candidate, string normalizedExpected)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalizedExpected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void EnsureUnderBackupsRoot(string backupPath)
    {
        var root = Path.GetFullPath(_paths.BackupsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(backupPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("备份路径不在应用专用备份目录内。拒绝操作。");
        }
    }

    private string ResolveBackupSource(BackupRecord record, string filesRoot, string relative)
    {
        var physical = PathSafety.ResolveOrThrow(filesRoot, relative);
        if (_files.FileExists(physical))
        {
            return physical;
        }

        if (record.LegacyContentObjects is not null &&
            record.LegacyContentLengths is not null &&
            record.LegacyContentObjects.TryGetValue(relative, out var objectId) &&
            record.LegacyContentLengths.TryGetValue(relative, out var expectedLength) &&
            IsLegacyObjectId(objectId))
        {
            var normalized = objectId.ToUpperInvariant();
            var legacy = Path.Combine(
                GetLegacyContentRoot(record.GamePath),
                "sha256",
                normalized[..2],
                normalized + ".blob");
            if (_files.FileExists(legacy) && _files.GetLength(legacy) == expectedLength)
            {
                return legacy;
            }
        }

        throw new FileNotFoundException("备份文件不存在。", physical);
    }

    private static string GetLegacyContentRoot(string gamePath) =>
        Path.Combine(
            GameStorageLayout.GetAppDataRoot(gamePath),
            GameStorageLayout.BackupContentDirectoryName);

    private static bool IsLegacyObjectId(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
