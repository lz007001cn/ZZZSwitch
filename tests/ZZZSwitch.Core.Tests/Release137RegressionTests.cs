using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch.Core.Tests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Release137RegressionTests() => [
        ("迁移备份后精确关联同步并可立即恢复", BackupMigrationKeepsLatest),
        ("迁移关联保存失败保留两份副本并明确警告", BackupMigrationStateFailure),
        ("损坏或失效的最后备份查找不抛异常", InvalidLatestBackupIsUnavailable),
        ("备份迁移保留其他安装关联与已恢复记录身份", BackupMigrationPreservesOtherIdentity)
    ];

    private static Task BackupMigrationKeepsLatest()
    {
        using var f = new TempFixture();
        var (backups, record, oldPath) = PrepareManualRestore(f, new PhysicalFileOperations());
        var store = new StateStore(f.Paths);
        var operation = store.Load()!.LastOperationId;
        var target = Path.Combine(f.Root, "migrated-backups");
        var moved = new BackupLocationService(f.Paths).ChangeLocation(target, f.Game);
        var newPath = Path.Combine(target, Path.GetFileName(oldPath));
        True(moved.Warning is null && moved.SourceRemoved, "迁移必须成功。");
        Equal(newPath, store.Load()!.LastBackupPath);
        Equal(operation, store.Load()!.LastOperationId);
        var restore = MakeRestore(f, backups);
        Equal(record.OperationId, restore.FindLatestRecord(f.Game)?.OperationId);
        var result = restore.RestoreLatest(f.Game);
        True(result.Success, result.Error ?? "应可立即恢复。");
        Equal("old-a", File.ReadAllText(Path.Combine(f.Game, "a.bin")));
        return Task.CompletedTask;
    }

    private static Task BackupMigrationStateFailure()
    {
        using var f = new TempFixture();
        var (_, _, oldPath) = PrepareManualRestore(f, new PhysicalFileOperations());
        var originalState = File.ReadAllText(f.Paths.StateFile);
        var target = Path.Combine(f.Root, "migrated-backups");
        using (var locked = new FileStream(f.Paths.StateFile + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var moved = new BackupLocationService(f.Paths).ChangeLocation(target, f.Game);
            True(!moved.SourceRemoved && moved.Warning is not null, "状态写入失败不能删除旧副本或隐瞒警告。");
            True(File.Exists(Path.Combine(oldPath, "backup.json")) &&
                File.Exists(Path.Combine(target, Path.GetFileName(oldPath), "backup.json")), "新旧备份都必须保留。");
            Equal(originalState, File.ReadAllText(f.Paths.StateFile));
            Equal(target, f.Paths.BackupsRoot);
        }
        return Task.CompletedTask;
    }

    private static Task InvalidLatestBackupIsUnavailable()
    {
        using var f = new TempFixture();
        var (backups, _, path) = PrepareManualRestore(f, new PhysicalFileOperations());
        var restore = MakeRestore(f, backups);
        File.WriteAllText(Path.Combine(path, "backup.json"), "{broken");
        True(restore.FindLatestRecord(f.Game) is null, "损坏JSON应返回不可恢复。");
        True(!restore.RestoreLatest(f.Game).Success, "损坏JSON不能进入恢复。");
        var store = new StateStore(f.Paths); var state = store.Load()!;
        state.LastBackupPath = Path.Combine(f.Root, "old-root", "operation"); store.Save(state);
        True(restore.FindLatestRecord(f.Game) is null, "旧目录关联不能使界面崩溃。");
        AssertBeforeRestore(f);
        return Task.CompletedTask;
    }

    private static Task BackupMigrationPreservesOtherIdentity()
    {
        foreach (var foreign in new[] { false, true })
        {
            using var f = new TempFixture();
            var (backups, record, oldPath) = PrepareManualRestore(f, new PhysicalFileOperations());
            var store = new StateStore(f.Paths);
            if (!foreign) True(MakeRestore(f, backups).RestoreLatest(f.Game).Success, "先完成手动恢复。");
            var state = store.Load()!;
            if (foreign) { state.LastBackupPath = Path.Combine(f.Root, "other-install", "operation"); store.Save(state); }
            var oldAssociation = state.LastBackupPath; var operation = state.LastOperationId;
            var target = Path.Combine(f.Root, "migrated-backups");
            new BackupLocationService(f.Paths).ChangeLocation(target, f.Game);
            Equal(operation, store.Load()!.LastOperationId);
            Equal(foreign ? oldAssociation : Path.Combine(target, Path.GetFileName(oldPath)), store.Load()!.LastBackupPath);
        }
        return Task.CompletedTask;
    }
}
