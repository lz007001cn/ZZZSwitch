using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed record ApplicationDataPlacementResult(
    string SourceRoot,
    string TargetRoot,
    int MigratedFileCount,
    long MigratedBytes,
    bool ContentMoved,
    bool SourceRemoved,
    bool LayoutRenamed = false);

public sealed class ApplicationDataPlacementService
{
    private const long SafetyMargin = 64L * 1024 * 1024;
    private static readonly StorageDirectoryMapping[] StorageDirectories =
    [
        new("Backups", GameStorageLayout.BackupRecordsDirectoryName),
        new("Objects", GameStorageLayout.BackupContentDirectoryName),
        new("OnlineDifferenceFiles", GameStorageLayout.DownloadsDirectoryName),
        new("ProfileSnapshots", GameStorageLayout.SnapshotsDirectoryName),
        new("ManifestCache", GameStorageLayout.SophonManifestsDirectoryName),
        new("HotUpdateCaches", GameStorageLayout.BlocksManifestsDirectoryName),
        new("Logs", GameStorageLayout.LogsDirectoryName),
        new("Temp", GameStorageLayout.TempDirectoryName)
    ];

    private readonly AppPaths _paths;
    private readonly IFileOperations _files;
    private readonly VerifiedFileTransfer _transfers;
    private readonly FileIntegrityService _integrity;

    public ApplicationDataPlacementService(
        AppPaths paths,
        IFileOperations? files = null)
    {
        _paths = paths;
        _files = files ?? new PhysicalFileOperations();
        _transfers = new VerifiedFileTransfer(_files);
        _integrity = new FileIntegrityService(_files);
    }

    public ApplicationDataPlacementResult ActivateGameStorage(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Path.IsPathFullyQualified(gamePath))
        {
            throw new ArgumentException("游戏目录必须是完整的本地路径。", nameof(gamePath));
        }

        var normalizedGamePath = Path.GetFullPath(gamePath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var targetRoot = Path.GetFullPath(GameStorageLayout.GetAppDataRoot(normalizedGamePath));
        EnsureSafeTarget(normalizedGamePath, targetRoot);

        var sourceRoot = Path.GetFullPath(_paths.StorageRoot);
        var schemaMigrationRequired = LegacyStorageExists(normalizedGamePath, targetRoot);
        if ((schemaMigrationRequired || !SamePath(sourceRoot, targetRoot)) &&
            (File.Exists(_paths.FileTransactionJournalFile) ||
             File.Exists(_paths.HotUpdateJournalFile)))
        {
            throw new InvalidOperationException("检测到未完成切换事务，必须先完成自动恢复再迁移数据。");
        }

        var schema = MigrateLegacyStorageSchema(normalizedGamePath, targetRoot);
        if (SamePath(sourceRoot, targetRoot))
        {
            _paths.SetGameStorage(normalizedGamePath, persist: true);
            _paths.EnsureWritableDirectories();
            RepairLastBackupPath(normalizedGamePath, targetRoot);
            RepairPortableMetadata(_paths.StorageRoot, _paths.BackupsRoot, normalizedGamePath);
            return new(
                schema.SourceRoot ?? sourceRoot,
                targetRoot,
                schema.FileCount,
                schema.Bytes,
                schema.ContentMoved,
                schema.SourceRemoved,
                schema.ContentMoved);
        }

        // A configured .zzzswitch root belongs to a different game installation.
        // Keep that installation's data in place and activate an independent root.
        if (!_paths.UsesLegacyDataRoot)
        {
            _paths.SetGameStorage(normalizedGamePath, persist: true);
            _paths.EnsureWritableDirectories();
            RepairLastBackupPath(normalizedGamePath, targetRoot);
            RepairPortableMetadata(_paths.StorageRoot, _paths.BackupsRoot, normalizedGamePath);
            return new(
                schema.SourceRoot ?? sourceRoot,
                targetRoot,
                schema.FileCount,
                schema.Bytes,
                schema.ContentMoved,
                schema.SourceRemoved,
                schema.ContentMoved);
        }

        var sources = GetLegacySources();
        var measured = MeasureSources(sources);
        if (measured.FileCount == 0)
        {
            ResetBackupLocationToDefault();
            _paths.SetGameStorage(normalizedGamePath, persist: true);
            _paths.EnsureWritableDirectories();
            RepairLastBackupPath(normalizedGamePath, targetRoot);
            RepairPortableMetadata(_paths.StorageRoot, _paths.BackupsRoot, normalizedGamePath);
            return new(
                schema.SourceRoot ?? sourceRoot,
                targetRoot,
                schema.FileCount,
                schema.Bytes,
                schema.ContentMoved,
                schema.SourceRemoved,
                schema.ContentMoved);
        }

        EnsureAvailableSpace(targetRoot, measured.Bytes);
        var stagingRoot = targetRoot + ".migrating-" + Guid.NewGuid().ToString("N");
        var targetCommitted = false;
        try
        {
            if (Directory.Exists(targetRoot))
            {
                EnsureNotReparsePoint(targetRoot);
                if (Directory.EnumerateFileSystemEntries(targetRoot).Any())
                {
                    VerifyCommittedTarget(sources, targetRoot, measured.FileCount);
                    targetCommitted = true;
                }
                else
                {
                    Directory.Delete(targetRoot);
                }
            }

            if (!targetCommitted)
            {
                Directory.CreateDirectory(stagingRoot);
                foreach (var source in sources.Where(item => Directory.Exists(item.SourcePath)))
                {
                    CopyDirectoryVerified(source.SourcePath, Path.Combine(stagingRoot, source.TargetName));
                }

                Directory.Move(stagingRoot, targetRoot);
                targetCommitted = true;
            }

            ResetBackupLocationToDefault();
            _paths.SetGameStorage(normalizedGamePath, persist: true);
            UpdateLastBackupPath(sources, targetRoot);
            _paths.EnsureWritableDirectories();
            RepairLastBackupPath(normalizedGamePath, targetRoot);
            RepairPortableMetadata(_paths.StorageRoot, _paths.BackupsRoot, normalizedGamePath);

            var sourceRemoved = true;
            foreach (var source in sources.Where(item => Directory.Exists(item.SourcePath)))
            {
                try
                {
                    DeleteDirectorySafe(source.SourcePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    sourceRemoved = false;
                }
            }

            return new(
                sourceRoot,
                targetRoot,
                measured.FileCount + schema.FileCount,
                checked(measured.Bytes + schema.Bytes),
                measured.FileCount > 0 || schema.ContentMoved,
                sourceRemoved && schema.SourceRemoved,
                schema.ContentMoved);
        }
        catch
        {
            if (!targetCommitted && Directory.Exists(stagingRoot))
            {
                DeleteDirectorySafe(stagingRoot);
            }

            throw;
        }
    }

    private static bool LegacyStorageExists(string gamePath, string targetRoot)
    {
        if (Directory.Exists(GameStorageLayout.GetLegacyDataRoot(gamePath)) ||
            Directory.Exists(GameStorageLayout.GetLegacyCacheRoot(gamePath)))
        {
            return true;
        }

        return Directory.Exists(targetRoot) && StorageDirectories.Any(mapping =>
            Directory.Exists(Path.Combine(targetRoot, mapping.LegacyName)) &&
            !SamePath(
                Path.Combine(targetRoot, mapping.LegacyName),
                Path.Combine(targetRoot, mapping.CurrentName)));
    }

    private StorageSchemaMigration MigrateLegacyStorageSchema(
        string gamePath,
        string targetRoot)
    {
        var sourceRoot = (string?)null;
        var fileCount = 0;
        var bytes = 0L;
        var contentMoved = false;
        var legacyDataRoot = Path.GetFullPath(GameStorageLayout.GetLegacyDataRoot(gamePath));
        var legacyCacheRoot = Path.GetFullPath(GameStorageLayout.GetLegacyCacheRoot(gamePath));
        var cacheRoot = Path.GetFullPath(GameStorageLayout.GetCacheRoot(gamePath));
        var outerDataMoved = false;

        if (Directory.Exists(legacyDataRoot))
        {
            var moved = MoveLegacyDirectory(legacyDataRoot, targetRoot, countContent: true);
            sourceRoot ??= legacyDataRoot;
            fileCount += moved.FileCount;
            bytes = checked(bytes + moved.Bytes);
            contentMoved |= moved.Moved;
            outerDataMoved = moved.Moved;
        }

        if (Directory.Exists(targetRoot))
        {
            foreach (var mapping in StorageDirectories)
            {
                var oldPath = Path.Combine(targetRoot, mapping.LegacyName);
                var newPath = Path.Combine(targetRoot, mapping.CurrentName);
                if (SamePath(oldPath, newPath) || !Directory.Exists(oldPath))
                {
                    continue;
                }

                var moved = MoveLegacyDirectory(oldPath, newPath, countContent: !outerDataMoved);
                sourceRoot ??= oldPath;
                fileCount += moved.FileCount;
                bytes = checked(bytes + moved.Bytes);
                contentMoved |= moved.Moved;
            }
        }

        if (Directory.Exists(legacyCacheRoot))
        {
            var moved = MoveLegacyDirectory(legacyCacheRoot, cacheRoot, countContent: true);
            sourceRoot ??= legacyCacheRoot;
            fileCount += moved.FileCount;
            bytes = checked(bytes + moved.Bytes);
            contentMoved |= moved.Moved;
        }

        RepairLegacyDefaultBackupSetting(gamePath, targetRoot);
        var sourceRemoved = !Directory.Exists(legacyDataRoot) &&
                            !Directory.Exists(legacyCacheRoot) &&
                            (!Directory.Exists(targetRoot) || StorageDirectories.All(mapping =>
                                SamePath(
                                    Path.Combine(targetRoot, mapping.LegacyName),
                                    Path.Combine(targetRoot, mapping.CurrentName)) ||
                                !Directory.Exists(Path.Combine(targetRoot, mapping.LegacyName))));
        return new(sourceRoot, fileCount, bytes, contentMoved, sourceRemoved);
    }

    private static DirectoryMoveResult MoveLegacyDirectory(
        string source,
        string target,
        bool countContent)
    {
        if (!Directory.Exists(source))
        {
            return new(0, 0, false);
        }

        EnsureTreeHasNoReparsePoints(source);
        if (Directory.Exists(target))
        {
            EnsureTreeHasNoReparsePoints(target);
            var sourceHasContent = Directory.EnumerateFileSystemEntries(source).Any();
            var targetHasContent = Directory.EnumerateFileSystemEntries(target).Any();
            if (sourceHasContent && targetHasContent)
            {
                throw new InvalidOperationException(
                    $"旧目录与新目录同时包含数据，拒绝自动合并：{source}；{target}");
            }

            if (!sourceHasContent)
            {
                Directory.Delete(source);
                return new(0, 0, true);
            }

            Directory.Delete(target);
        }

        var measured = countContent
            ? MeasureSources([new MigrationSource(source, string.Empty)])
            : (FileCount: 0, Bytes: 0L);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        return new(measured.FileCount, measured.Bytes, true);
    }

    private void RepairLegacyDefaultBackupSetting(string gamePath, string targetRoot)
    {
        if (!File.Exists(_paths.BackupLocationFile))
        {
            return;
        }

        try
        {
            BackupLocationSettings? settings;
            using (var stream = File.OpenRead(_paths.BackupLocationFile))
            {
                settings = JsonSerializer.Deserialize<BackupLocationSettings>(stream, JsonSupport.Options);
            }

            if (string.IsNullOrWhiteSpace(settings?.BackupRootPath))
            {
                return;
            }

            var oldDefaults = new[]
            {
                Path.Combine(GameStorageLayout.GetLegacyDataRoot(gamePath), "Backups"),
                Path.Combine(targetRoot, "Backups")
            };
            if (oldDefaults.Any(path => SamePathSafely(settings.BackupRootPath, path)))
            {
                AtomicJsonFile.Write(_paths.BackupLocationFile, new BackupLocationSettings());
            }
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            // Invalid settings are preserved and handled by AppPaths' safe fallback.
        }
    }

    private void RepairLastBackupPath(string gamePath, string targetRoot)
    {
        if (!File.Exists(_paths.StateFile))
        {
            return;
        }

        try
        {
            AppState? state;
            using (var stream = File.OpenRead(_paths.StateFile))
            {
                state = JsonSerializer.Deserialize<AppState>(stream, JsonSupport.Options);
            }

            if (state is null || string.IsNullOrWhiteSpace(state.LastBackupPath))
            {
                return;
            }

            var oldRoots = new[]
            {
                Path.Combine(GameStorageLayout.GetLegacyDataRoot(gamePath), "Backups"),
                Path.Combine(targetRoot, "Backups"),
                Path.Combine(_paths.DataRoot, "Backups")
            };
            foreach (var oldRoot in oldRoots)
            {
                if (!TryGetRelativeChild(oldRoot, state.LastBackupPath, out var relative))
                {
                    continue;
                }

                state.LastBackupPath = Path.Combine(
                    targetRoot,
                    GameStorageLayout.BackupRecordsDirectoryName,
                    relative);
                AtomicJsonFile.Write(_paths.StateFile, state);
                return;
            }
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            // Invalid state is preserved for the existing state loader to report.
        }
    }

    private List<MigrationSource> GetLegacySources()
    {
        var sources = StorageDirectories
            .Where(mapping => !string.Equals(mapping.LegacyName, "Backups", StringComparison.OrdinalIgnoreCase))
            .SelectMany(mapping => new[]
            {
                new MigrationSource(
                    Path.Combine(_paths.DataRoot, mapping.LegacyName),
                    mapping.CurrentName),
                new MigrationSource(
                    Path.Combine(_paths.DataRoot, mapping.CurrentName),
                    mapping.CurrentName)
            })
            .ToList();
        sources.InsertRange(0,
        [
            new MigrationSource(
                _paths.BackupsRoot,
                GameStorageLayout.BackupRecordsDirectoryName),
            new MigrationSource(
                Path.Combine(_paths.DataRoot, "Backups"),
                GameStorageLayout.BackupRecordsDirectoryName),
            new MigrationSource(
                Path.Combine(_paths.DataRoot, GameStorageLayout.BackupRecordsDirectoryName),
                GameStorageLayout.BackupRecordsDirectoryName)
        ]);
        return sources
            .GroupBy(item => Path.GetFullPath(item.SourcePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private void CopyDirectoryVerified(string sourceRoot, string targetRoot)
    {
        EnsureNotReparsePoint(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };
        foreach (var directory in new DirectoryInfo(sourceRoot).EnumerateDirectories("*", options))
        {
            EnsureNotReparsePoint(directory.FullName);
            Directory.CreateDirectory(Path.Combine(
                targetRoot,
                Path.GetRelativePath(sourceRoot, directory.FullName)));
        }

        foreach (var source in new DirectoryInfo(sourceRoot).EnumerateFiles("*", options))
        {
            EnsureNotReparsePoint(source.FullName);
            var target = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, source.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            _transfers.CopyAndVerify(source.FullName, target, overwrite: false, source.Length);
            File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
        }
    }

    private void VerifyCommittedTarget(
        IReadOnlyList<MigrationSource> sources,
        string targetRoot,
        int expectedFileCount)
    {
        var targetFileCount = Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories).Count();
        if (targetFileCount != expectedFileCount)
        {
            throw new InvalidOperationException(
                $"目标数据目录包含不明内容，拒绝继续迁移：{targetRoot}");
        }

        foreach (var source in sources.Where(item => Directory.Exists(item.SourcePath)))
        {
            foreach (var sourceFile in Directory.EnumerateFiles(
                         source.SourcePath,
                         "*",
                         SearchOption.AllDirectories))
            {
                EnsureNotReparsePoint(sourceFile);
                var target = Path.Combine(
                    targetRoot,
                    source.TargetName,
                    Path.GetRelativePath(source.SourcePath, sourceFile));
                using var sourceStream = _files.OpenRead(sourceFile);
                var sourceHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(sourceStream));
                var integrity = _integrity.Validate(target, new FileInfo(sourceFile).Length, sourceHash);
                if (!integrity.IsValid)
                {
                    throw new InvalidOperationException(
                        $"目标数据目录与旧数据不一致，拒绝继续迁移：{target}");
                }
            }
        }
    }

    private void UpdateLastBackupPath(
        IReadOnlyList<MigrationSource> sources,
        string targetRoot)
    {
        if (!File.Exists(_paths.StateFile))
        {
            return;
        }

        AppState? state;
        using (var stream = File.OpenRead(_paths.StateFile))
        {
            state = JsonSerializer.Deserialize<AppState>(stream, JsonSupport.Options);
        }
        if (state is null || string.IsNullOrWhiteSpace(state.LastBackupPath))
        {
            return;
        }

        foreach (var backupSource in sources
                     .Where(item => string.Equals(
                         item.TargetName,
                         GameStorageLayout.BackupRecordsDirectoryName,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(item => item.SourcePath))
        {
            if (!TryGetRelativeChild(backupSource, state.LastBackupPath, out var relative))
            {
                continue;
            }

            state.LastBackupPath = Path.Combine(
                targetRoot,
                GameStorageLayout.BackupRecordsDirectoryName,
                relative);
            AtomicJsonFile.Write(_paths.StateFile, state);
            return;
        }
    }

    private void ResetBackupLocationToDefault()
    {
        if (File.Exists(_paths.BackupLocationFile) ||
            !SamePath(_paths.BackupsRoot, Path.Combine(_paths.DataRoot, "Backups")))
        {
            Directory.CreateDirectory(_paths.DataRoot);
            AtomicJsonFile.Write(_paths.BackupLocationFile, new BackupLocationSettings());
        }
    }

    private void RepairPortableMetadata(
        string storageRoot,
        string backupsRoot,
        string activeGamePath)
    {
        RepairHotUpdateManifestPaths(activeGamePath);
        var snapshotsRoot = Path.Combine(
            Path.GetFullPath(storageRoot),
            GameStorageLayout.SnapshotsDirectoryName);
        var snapshotPaths = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (Directory.Exists(snapshotsRoot))
        {
            foreach (var manifestPath in EnumerateMetadataFiles(snapshotsRoot, "snapshot.json"))
            {
                try
                {
                    ProfileSnapshotManifest? manifest;
                    using (var stream = File.OpenRead(manifestPath))
                    {
                        manifest = JsonSerializer.Deserialize<ProfileSnapshotManifest>(stream, JsonSupport.Options);
                    }
                    var actualPath = Path.GetDirectoryName(manifestPath);
                    if (manifest is null ||
                        string.IsNullOrWhiteSpace(actualPath) ||
                        !string.Equals(
                            Path.GetFileName(actualPath),
                            manifest.SnapshotId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (snapshotPaths.TryGetValue(manifest.SnapshotId, out var existing))
                    {
                        if (existing is not null && !SamePath(existing, actualPath))
                        {
                            snapshotPaths[manifest.SnapshotId] = null;
                        }
                    }
                    else
                    {
                        snapshotPaths[manifest.SnapshotId] = actualPath;
                    }

                    if (!SamePathSafely(manifest.SnapshotPath, actualPath))
                    {
                        manifest.SnapshotPath = Path.GetFullPath(actualPath);
                        AtomicJsonFile.Write(manifestPath, manifest);
                    }
                }
                catch (Exception ex) when (
                    ex is JsonException or IOException or UnauthorizedAccessException or
                        InvalidDataException or ArgumentException or NotSupportedException)
                {
                    // Invalid metadata is preserved for manual inspection.
                }
            }
        }

        if (!Directory.Exists(backupsRoot))
        {
            return;
        }

        foreach (var recordPath in EnumerateMetadataFiles(backupsRoot, "backup.json"))
        {
            try
            {
                BackupRecord? record;
                using (var stream = File.OpenRead(recordPath))
                {
                    record = JsonSerializer.Deserialize<BackupRecord>(stream, JsonSupport.Options);
                }
                if (record is null)
                {
                    continue;
                }

                var changed = RebindSnapshotReference(record.SourceSnapshotPath, snapshotPaths, value =>
                    record.SourceSnapshotPath = value);
                changed |= RebindSnapshotReference(record.TargetSnapshotPath, snapshotPaths, value =>
                    record.TargetSnapshotPath = value);
                if (SamePathSafely(record.GamePath, activeGamePath))
                {
                    changed |= RebindLegacySnapshotReference(
                        record.SourceSnapshotPath,
                        storageRoot,
                        activeGamePath,
                        value => record.SourceSnapshotPath = value);
                    changed |= RebindLegacySnapshotReference(
                        record.TargetSnapshotPath,
                        storageRoot,
                        activeGamePath,
                        value => record.TargetSnapshotPath = value);
                }
                if (changed)
                {
                    AtomicJsonFile.Write(recordPath, record);
                }

            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or NotSupportedException)
            {
                // Invalid backup records remain untouched for manual inspection.
            }
        }
    }

    private static bool RebindSnapshotReference(
        string? storedPath,
        IReadOnlyDictionary<string, string?> snapshotPaths,
        Action<string> assign)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return false;
        }

        string snapshotId;
        try
        {
            snapshotId = Path.GetFileName(Path.TrimEndingDirectorySeparator(storedPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshotId) ||
            !snapshotPaths.TryGetValue(snapshotId, out var actualPath) ||
            string.IsNullOrWhiteSpace(actualPath) ||
            SamePathSafely(storedPath, actualPath))
        {
            return false;
        }

        assign(actualPath);
        return true;
    }

    private static bool RebindLegacySnapshotReference(
        string? storedPath,
        string storageRoot,
        string activeGamePath,
        Action<string> assign)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return false;
        }

        var oldRoots = new[]
        {
            Path.Combine(GameStorageLayout.GetLegacyDataRoot(activeGamePath), "ProfileSnapshots"),
            Path.Combine(storageRoot, "ProfileSnapshots")
        };
        foreach (var oldRoot in oldRoots)
        {
            if (!TryGetRelativeChild(oldRoot, storedPath, out var relative))
            {
                continue;
            }

            assign(Path.Combine(storageRoot, GameStorageLayout.SnapshotsDirectoryName, relative));
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateMetadataFiles(string root, string fileName)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        return new DirectoryInfo(root).EnumerateFiles(fileName, options).Select(file => file.FullName);
    }

    private static bool SamePathSafely(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return SamePath(left, right);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static (int FileCount, long Bytes) MeasureSources(
        IEnumerable<MigrationSource> sources)
    {
        var count = 0;
        var bytes = 0L;
        foreach (var source in sources.Where(item => Directory.Exists(item.SourcePath)))
        {
            EnsureNotReparsePoint(source.SourcePath);
            foreach (var file in Directory.EnumerateFiles(source.SourcePath, "*", SearchOption.AllDirectories))
            {
                EnsureNotReparsePoint(file);
                bytes = checked(bytes + new FileInfo(file).Length);
                count++;
            }
        }

        return (count, bytes);
    }

    private static void EnsureAvailableSpace(string targetRoot, long bytes)
    {
        var driveRoot = Path.GetPathRoot(targetRoot)
                        ?? throw new InvalidOperationException("无法确定数据目录所在磁盘。");
        var required = checked(bytes + SafetyMargin);
        var available = new DriveInfo(driveRoot).AvailableFreeSpace;
        if (available < required)
        {
            throw new IOException(
                $"目标磁盘空间不足。需要约 {ByteSizeFormatter.Format(required)}，当前可用 {ByteSizeFormatter.Format(available)}。");
        }
    }

    private static void EnsureSafeTarget(string gamePath, string targetRoot)
    {
        var storageRoot = Path.GetFullPath(GameStorageLayout.GetRoot(gamePath));
        var expectedTarget = Path.GetFullPath(GameStorageLayout.GetAppDataRoot(gamePath));
        if (!SamePath(targetRoot, expectedTarget) ||
            !TryGetRelativeChild(storageRoot, targetRoot, out _))
        {
            throw new InvalidOperationException("数据目录不在当前游戏的 .zzzswitch 范围内。");
        }

        if (Directory.Exists(storageRoot))
        {
            EnsureNotReparsePoint(storageRoot);
        }
    }

    private static bool TryGetRelativeChild(string root, string candidate, out string relative)
    {
        relative = string.Empty;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        relative = normalizedCandidate[normalizedRoot.Length..];
        return !string.IsNullOrWhiteSpace(relative);
    }

    private static void DeleteDirectorySafe(string path)
    {
        EnsureNotReparsePoint(path);
        Directory.Delete(path, recursive: true);
    }

    private static void EnsureTreeHasNoReparsePoints(string root)
    {
        EnsureNotReparsePoint(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            EnsureNotReparsePoint(entry.FullName);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"拒绝迁移或删除重解析点：{path}");
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void RepairHotUpdateManifestPaths(string activeGamePath)
    {
        if (!Directory.Exists(_paths.HotUpdateManifestsRoot))
        {
            return;
        }

        var cacheRoot = new CacheLocationService(_paths).GetCacheRoot(activeGamePath);
        foreach (var manifestPath in EnumerateMetadataFiles(_paths.HotUpdateManifestsRoot, "cache.json"))
        {
            try
            {
                HotUpdateCacheManifest? manifest;
                using (var stream = File.OpenRead(manifestPath))
                {
                    manifest = JsonSerializer.Deserialize<HotUpdateCacheManifest>(stream, JsonSupport.Options);
                }

                if (manifest is null ||
                    !SamePathSafely(manifest.GamePath, activeGamePath) ||
                    string.IsNullOrWhiteSpace(manifest.GameVersion) ||
                    string.IsNullOrWhiteSpace(manifest.Profile))
                {
                    continue;
                }

                var expected = GameStorageLayout.GetStoredBlocksPath(
                    activeGamePath,
                    manifest.GameVersion,
                    ProfileIds.ToResourceProfile(manifest.Profile),
                    cacheRoot);
                if (!SamePathSafely(manifest.StoredBlocksPath, expected))
                {
                    manifest.StoredBlocksPath = expected;
                    AtomicJsonFile.Write(manifestPath, manifest);
                }
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or NotSupportedException)
            {
                // Invalid or foreign metadata is preserved for manual inspection.
            }
        }
    }

    private sealed record MigrationSource(string SourcePath, string TargetName);
    private sealed record StorageDirectoryMapping(string LegacyName, string CurrentName);
    private readonly record struct DirectoryMoveResult(int FileCount, long Bytes, bool Moved);
    private readonly record struct StorageSchemaMigration(
        string? SourceRoot,
        int FileCount,
        long Bytes,
        bool ContentMoved,
        bool SourceRemoved);
}
