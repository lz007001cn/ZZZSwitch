using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch.Core.Tests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ReviewRegressionTests() => Release137RegressionTests().Concat(FollowupRegressionTests()).Concat(new (string, Func<Task>)[] {
        ("自动恢复拒绝升级后的游戏且保留日志", RecoveryRejectsUpgrade),
        ("缺失版本与缺损备份在手动恢复写入前拒绝", RestoreRejectsMissingInputs),
        ("手动恢复事务成功提交及原记录关联", () => RestoreTransactionFault("success")),
        ("手动恢复替换失败撤销全部修改", () => RestoreTransactionFault("move")),
        ("手动恢复删除失败撤销全部修改", () => RestoreTransactionFault("delete")),
        ("手动恢复撤销再次失败后保留副本并可重试", () => RestoreTransactionFault("undo")),
        ("手动恢复状态提交失败撤销修改", () => RestoreTransactionFault("state")),
        ("手动恢复收尾失败保留记录且可重复重试", () => RestoreTransactionFault("cleanup")),
        ("手动恢复进程中断后从持久化副本撤销", RestoreSurvivesProcessExit),
        ("过期切换计划拒绝写入新版客户端", StalePlanRejected),
        ("动态坏清单隔离且健康包继续可用", InvalidPackageIsIsolated),
        ("重置仅清除识别且不能绕过未完成事务", ResetPreservesRecoveryEvidence),
        ("正式生成配置通过运行时契约检查", () => { Equal(0, ValidateGeneratedConfig(FindRepositoryConfig())); return Task.CompletedTask; })
    });

    private static Task RecoveryRejectsUpgrade()
    {
        using var f = new TempFixture();
        File.WriteAllText(Path.Combine(f.Game, "a.bin"), "old");
        var plan = f.CreatePlan([Entry("a.bin")], []);
        var backups = new BackupService(new PhysicalFileOperations(), f.Paths);
        backups.CreateBackup(plan);
        File.WriteAllText(Path.Combine(f.Game, "a.bin"), "upgraded");
        f.CreateGameMarkers("3.2.0");
        var journals = new FileTransactionJournalStore(f.Paths);
        journals.Save(CreateFileJournal(plan, FileTransactionStage.FilesApplied));
        var result = CreateRecoveryService(f, backups, journals).RecoverPending();
        True(!result.Success && journals.Exists, "跨版本恢复必须拒绝并保留日志。");
        Equal("upgraded", File.ReadAllText(Path.Combine(f.Game, "a.bin")));
        Equal(0, backups.PruneAllBackups());
        return Task.CompletedTask;
    }

    private static Task RestoreRejectsMissingInputs()
    {
        using var f = new TempFixture();
        var (backups, record, path) = PrepareManualRestore(f, new PhysicalFileOperations());
        File.Delete(Path.Combine(f.Game, "version_info"));
        var service = MakeRestore(f, backups);
        True(!service.Restore(path, record, f.Game).Success, "缺版本不能恢复。");
        AssertBeforeRestore(f);
        f.CreateGameMarkers("3.0.0");
        File.Delete(Path.Combine(path, "files", "b.bin"));
        True(!service.Restore(path, record, f.Game).Success, "缺备份正文不能恢复。");
        AssertBeforeRestore(f);
        True(!File.Exists(f.Paths.FileTransactionJournalFile), "预检拒绝不应创建游戏事务。");
        return Task.CompletedTask;
    }

    private static Task RestoreTransactionFault(string fault)
    {
        using var f = new TempFixture();
        var files = new RestoreFaultFiles();
        var (backups, record, path) = PrepareManualRestore(f, files);
        var failed = false;
        files.Before = (verb, target) =>
        {
            if (fault == "undo" && failed && verb == "write" && target == Path.Combine(f.Game, "a.bin"))
                throw new IOException("Injected undo failure");
            if (failed) return;
            if (((fault == "move" || fault == "undo") && verb == "move" && target == Path.Combine(f.Game, "b.bin")) ||
                (fault == "delete" && verb == "delete" && target == Path.Combine(f.Game, "added.bin")) ||
                (fault == "cleanup" && verb == "directory"))
            { failed = true; throw new IOException("Injected " + fault); }
        };
        files.After = (verb, target) =>
        {
            if (fault == "state" && verb == "move" && target == Path.Combine(f.Game, "a.bin"))
            {
                File.Delete(f.Paths.StateFile);
                Directory.CreateDirectory(f.Paths.StateFile);
            }
        };
        var result = MakeRestore(f, backups).Restore(path, record, f.Game);
        if (fault is "success" or "cleanup")
        {
            True(result.Success, result.Error ?? "手动恢复没有成功。");
            Equal("old-a", File.ReadAllText(Path.Combine(f.Game, "a.bin")));
            Equal("old-b", File.ReadAllText(Path.Combine(f.Game, "b.bin")));
            True(!File.Exists(Path.Combine(f.Game, "added.bin")), "新增文件应删除。");
            True(new StateStore(f.Paths).Load()?.LastOperationId?.StartsWith("restore_", StringComparison.Ordinal) == true, "提交状态必须关联本次恢复。");
            True(backups.LoadRecord(path).RestoredAt is not null, "原记录应在提交后标记已恢复。");
        }
        else if (fault == "undo")
        {
            True(!result.Success && !result.RolledBack && !result.GameFilesUnchanged, "撤销失败不能报告已恢复或未修改。");
            True(File.Exists(f.Paths.FileTransactionJournalFile), "撤销失败必须留下日志。");
            Equal(0, backups.PruneAllBackups());
        }
        else
        {
            True(!result.Success && result.RolledBack, result.Error ?? "失败应撤销本次恢复。");
            AssertBeforeRestore(f);
            True(backups.LoadRecord(path).RestoredAt is null, "失败不能把历史记录标记成功。");
        }
        files.Before = null; files.After = null;
        var journals = new FileTransactionJournalStore(f.Paths);
        if (fault is "cleanup" or "undo")
        {
            True(journals.Exists, "清理失败需保留日志。");
            var recovery = CreateRecoveryService(f, backups, journals).RecoverPending();
            True(recovery.Success, recovery.Message);
            if (fault == "cleanup") Equal("old-a", File.ReadAllText(Path.Combine(f.Game, "a.bin")));
            else AssertBeforeRestore(f);
        }
        True(!journals.Exists, "完成或撤销成功应清理日志。");
        return Task.CompletedTask;
    }

    private static async Task RestoreSurvivesProcessExit()
    {
        foreach (var phase in new[] { "move", "delete" })
        {
            using var f = new TempFixture();
            var (backups, _, _) = PrepareManualRestore(f, new PhysicalFileOperations());
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { typeof(Program).Assembly.Location, "--restore-crash", f.Root, phase }) start.ArgumentList.Add(value);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            Equal(86, process.ExitCode);
            await stdout; await stderr;
            var journals = new FileTransactionJournalStore(f.Paths);
            True(journals.Exists, "子进程中断必须留下持久化日志。");
            True(!new OperationCoordinator(f.Paths).TryBegin(out _), "其他写操作不能绕过待恢复事务。");
            Equal(0, backups.PruneAllBackups());
            var recovered = CreateRecoveryService(f, backups, journals).RecoverPending();
            True(recovered.Success, recovered.Message);
            AssertBeforeRestore(f);
            True(!journals.Exists, "中断恢复完成应清理日志。");
            True(!CreateRecoveryService(f, backups, journals).RecoverPending().Found, "重复恢复应无操作。");
        }
    }

    private static int RunCrashRestore(string root, string phase)
    {
        var prefix = Path.Combine(Path.GetTempPath(), "ZZZSwitch.Tests") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 90;
        var paths = new AppPaths(Path.Combine(root, "AppData"), Path.Combine(root, "config"));
        var files = new RestoreFaultFiles();
        var backups = new BackupService(files, paths);
        var state = new StateStore(paths);
        var prior = state.Load()!;
        var path = prior.LastBackupPath!;
        files.After = (verb, target) =>
        {
            if (verb == phase && target.StartsWith(Path.Combine(root, "Game") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Environment.Exit(86); // Deliberately skips finally blocks, no real game/process is used.
        };
        var processes = new FakeProcessMonitor();
        var restore = new RestoreService(backups, processes, files, state,
            new LegacyRestoreSafetyPolicy(state, new HotUpdateCacheService(paths, processes)));
        restore.Restore(path, backups.LoadRecord(path), prior.GamePath!);
        return 91;
    }

    private static (BackupService, BackupRecord, string) PrepareManualRestore(TempFixture f, IFileOperations files)
    {
        f.CreateGameMarkers("3.0.0");
        File.WriteAllText(Path.Combine(f.Game, "a.bin"), "old-a");
        File.WriteAllText(Path.Combine(f.Game, "b.bin"), "old-b");
        var plan = f.CreatePlan([Entry("a.bin"), Entry("b.bin"), Entry("added.bin")], []);
        var backups = new BackupService(files, f.Paths);
        var record = backups.CreateBackup(plan);
        record.OperationResult = "success";
        backups.SaveRecord(plan.BackupPath, record);
        File.WriteAllText(Path.Combine(f.Game, "a.bin"), "new-a");
        File.WriteAllText(Path.Combine(f.Game, "b.bin"), "new-b");
        File.WriteAllText(Path.Combine(f.Game, "added.bin"), "new-added");
        new StateStore(f.Paths).Save(new AppState { GamePath = f.Game, GameVersion = "3.0.0", CurrentProfile = ProfileIds.CnOfficial,
            LastOperationId = record.OperationId, LastBackupPath = plan.BackupPath });
        return (backups, record, plan.BackupPath);
    }

    private static RestoreService MakeRestore(TempFixture f, BackupService backups)
    {
        var state = new StateStore(f.Paths); var processes = new FakeProcessMonitor();
        return new(backups, processes, new PhysicalFileOperations(), state,
            new LegacyRestoreSafetyPolicy(state, new HotUpdateCacheService(f.Paths, processes)));
    }

    private static void AssertBeforeRestore(TempFixture f)
    {
        Equal("new-a", File.ReadAllText(Path.Combine(f.Game, "a.bin")));
        Equal("new-b", File.ReadAllText(Path.Combine(f.Game, "b.bin")));
        Equal("new-added", File.ReadAllText(Path.Combine(f.Game, "added.bin")));
    }

    private static async Task StalePlanRejected()
    {
        using var f = new TempFixture();
        f.CreateGameMarkers("3.0.0");
        File.WriteAllText(Path.Combine(f.Game, "a.bin"), "current");
        File.WriteAllText(Path.Combine(f.Package, "a.bin"), "target");
        var manifest = new TransitionManifest { SourceProfile = ProfileIds.Global, TargetProfile = ProfileIds.CnOfficial, GameVersion = "3.0.0",
            ReplaceFiles = [new() { Source = "a.bin", Target = "a.bin", Length = 6, Sha256 = Convert.ToHexString(SHA256.HashData("target"u8)) }] };
        var planner = new SwitchPlanner(new ConfigurationRepository(f.Paths), new GameDirectoryService(), new FakeProcessMonitor(),
            new PhysicalFileOperations(), f.Paths, new ProfileSnapshotService(f.Paths, new PhysicalFileOperations()));
        var plan = planner.CreateOnlinePlan(f.Game, new OnlineDifferenceMaterialization { PackageRoot = f.Package, PackageDirectory = f.Package, Manifest = manifest });
        True(plan.CanExecute, string.Join(";", plan.Issues.Select(x => x.Message)));
        File.WriteAllText(Path.Combine(f.Game, "version_info"), "3.2.0");
        var result = await f.CreateEngine(new PhysicalFileOperations()).ExecuteAsync(plan);
        True(!result.Success && result.GameFilesUnchanged, "过期计划必须在修改前失败。");
        Equal("current", File.ReadAllText(Path.Combine(f.Game, "a.bin")));
    }

    private static Task InvalidPackageIsIsolated()
    {
        using var f = new TempFixture();
        var bad = Path.Combine(f.Paths.OnlineDifferenceFilesRoot, "3.0.0", ProfileIds.CnOfficial, "bad");
        var good = Path.Combine(f.Paths.OnlineDifferenceFilesRoot, "3.0.0", ProfileIds.CnOfficial, "good");
        Directory.CreateDirectory(bad); Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(bad, "transition-manifest.json"), "{\"sourceProfile\":\"global\",\"targetProfile\":\"cn_official\",\"gameVersion\":\"3.0.0\",\"replaceFiles\":null}");
        var manifest = new TransitionManifest { SourceProfile = ProfileIds.Global, TargetProfile = ProfileIds.CnOfficial, GameVersion = "3.0.0" };
        File.WriteAllText(Path.Combine(good, "transition-manifest.json"), JsonSerializer.Serialize(manifest, JsonSupport.Options));
        var catalog = new OnlineDifferencePackageCatalog(f.Paths);
        var inventory = catalog.GetInventory();
        Equal(2, inventory.Packages.Count);
        Equal(1, inventory.Packages.Count(x => x.State == OnlineDifferencePackageState.Invalid));
        True(catalog.TryGetReadyMaterialization(ProfileIds.Global, ProfileIds.CnOfficial, "3.0.0", out _), "坏包不能阻止健康包。");
        catalog.DeletePackage(bad);
        Equal(1, catalog.GetInventory().Packages.Count);
        return Task.CompletedTask;
    }

    private static Task ResetPreservesRecoveryEvidence()
    {
        using var f = new TempFixture();
        var (backups, record, path) = PrepareManualRestore(f, new PhysicalFileOperations());
        var state = new StateStore(f.Paths); var current = state.Load()!;
        current.ClientDetection = new() { Fingerprint = "test", Result = new() { Profile = DetectedProfile.CnOfficial } }; state.Save(current);
        var inspection = new InspectionService(new ConfigurationRepository(f.Paths), new GameDirectoryService(), new ProfileDetector(f.Paths), state,
            new FakeProcessMonitor(), inspectLocalPackages: false);
        var maintenance = new ClientMaintenanceService(f.Paths, inspection);
        maintenance.ResetDetection();
        var after = state.Load()!;
        True(after.ClientDetection is null && after.LastBackupPath == path && after.LastOperationId == record.OperationId && after.GamePath == f.Game,
            "重置不能清除备份与安装关联。");
        File.WriteAllText(f.Paths.FileTransactionJournalFile, "damaged transaction");
        var before = File.ReadAllText(f.Paths.StateFile);
        try { maintenance.ResetDetection(); throw new Exception("应拒绝重置"); }
        catch (InvalidOperationException) { }
        Equal(before, File.ReadAllText(f.Paths.StateFile));
        AssertBeforeRestore(f);
        return Task.CompletedTask;
    }

    private static string FindRepositoryConfig()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ZZZSwitch.sln"))) return Path.Combine(dir.FullName, "config");
        throw new InvalidOperationException("未找到仓库配置。");
    }

    private static int ValidateGeneratedConfig(string config)
    {
        var repository = new ConfigurationRepository(new AppPaths(Path.Combine(Path.GetTempPath(), "unused-config-validation"), config));
        var profiles = repository.LoadProfilesWithStatus(); var transitions = repository.LoadTransitionsWithStatus();
        if (profiles.Errors.Count > 0 || transitions.Errors.Count > 0 || profiles.Items.Count != 3 || transitions.Items.Count != 6) return 1;
        var cn = profiles.Items.Single(x => x.Id == ProfileIds.CnOfficial);
        var b = profiles.Items.Single(x => x.Id == ProfileIds.Bilibili);
        if (string.IsNullOrWhiteSpace(cn.GameVersion) || b.GameVersion != cn.GameVersion || !b.SupportsOverlayVersion(cn.GameVersion)) return 2;
        if (transitions.Items.Any(x => x.GameVersion != cn.GameVersion)) return 3;
        return 0;
    }

    private sealed class RestoreFaultFiles : IFileOperations
    {
        private readonly PhysicalFileOperations _inner = new();
        public Action<string, string>? Before, After;
        public bool FileExists(string path) => _inner.FileExists(path);
        public long GetLength(string path) => _inner.GetLength(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void CopyFile(string source, string target, bool overwrite) => _inner.CopyFile(source, target, overwrite);
        public void MoveFile(string source, string target, bool overwrite) { Before?.Invoke("move", target); _inner.MoveFile(source, target, overwrite); After?.Invoke("move", target); }
        public void DeleteFile(string path) { Before?.Invoke("delete", path); _inner.DeleteFile(path); After?.Invoke("delete", path); }
        public void DeleteDirectory(string path, bool recursive) { Before?.Invoke("directory", path); _inner.DeleteDirectory(path, recursive); After?.Invoke("directory", path); }
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream OpenWrite(string path, bool overwrite) { Before?.Invoke("write", path); return _inner.OpenWrite(path, overwrite); }
        public Stream OpenExclusive(string path) => _inner.OpenExclusive(path);
    }
}
