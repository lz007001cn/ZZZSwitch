using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Core.Services;
using ZZZSwitch.ManifestTool;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.Core.Tests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> FollowupRegressionTests() => [
        ("损坏状态不使已提交文件与 Blocks 反向混合", () => AmbiguousRecoveryStops(true)),
        ("普通文件与 Blocks 提交依据冲突时保留现场", () => AmbiguousRecoveryStops(false)),
        ("内置与 Manifest 识别均拒绝损坏 B 服且健康客户端可用", OverlayDetectionParity),
        ("预检和执行拒绝通过目录联接替换修改或删除", JunctionSwitchPaths),
        ("自动回滚拒绝游戏目录与备份来源联接且可重试", JunctionRollbackPaths),
        ("反向包重新捕获解除磁盘与内存失效状态", ReverseCaptureClearsInvalidation),
        ("固定与动态配置均隔离非法路径且保留健康清单", InvalidPathIsolationParity)
    ];

    private static Task AmbiguousRecoveryStops(bool corruptState)
    {
        using var f = new TempFixture(); f.CreateGameMarkers("3.0.0");
        var blocks = Path.Combine(f.Game, HotUpdateCacheService.BlocksRelativePath);
        Directory.CreateDirectory(blocks); File.WriteAllText(Path.Combine(blocks, "cn.blk"), "CN");
        var hot = new HotUpdateCacheService(f.Paths, new FakeProcessMonitor());
        hot.InitializeActive(ProfileIds.CnOfficial, "3.0.0", f.Game);
        hot.Commit(hot.BeginTransition(hot.CreateTransitionPlan(ProfileIds.CnOfficial, ProfileIds.Global, "3.0.0", f.Game, [])!));
        File.WriteAllText(Path.Combine(blocks, "global.blk"), "OS");
        hot.InitializeActive(ProfileIds.Global, "3.0.0", f.Game);
        File.WriteAllText(Path.Combine(f.Game, "a.bin"), "source");
        var plan = f.CreatePlan([Entry("a.bin")], []);
        var backups = new BackupService(new PhysicalFileOperations(), f.Paths);
        backups.CreateBackup(plan);
        var transaction = hot.BeginTransition(hot.CreateTransitionPlan(ProfileIds.Global, ProfileIds.CnOfficial, "3.0.0", f.Game, [])!);
        transaction.Committed = true;
        File.WriteAllText(f.Paths.HotUpdateJournalFile, JsonSerializer.Serialize(transaction, JsonSupport.Options));
        File.WriteAllText(Path.Combine(f.Game, "a.bin"), "target");
        var journals = new FileTransactionJournalStore(f.Paths); journals.Save(CreateFileJournal(plan, FileTransactionStage.MetadataRestored));
        var state = new StateStore(f.Paths);
        state.Save(new AppState { GamePath=f.Game, GameVersion="3.0.0", CurrentProfile=ProfileIds.Global });
        if (corruptState) File.WriteAllText(f.Paths.StateFile, "{broken");
        var beforeFile = File.ReadAllText(f.Paths.FileTransactionJournalFile);
        var beforeBlocks = File.ReadAllText(f.Paths.HotUpdateJournalFile);
        var service = new PendingTransactionRecoveryService(f.Paths, state, backups, hot, journals, new FakeProcessMonitor());
        for (var attempt=0; attempt<2; attempt++)
        {
            var result=service.RecoverPending();
            True(result.Found && !result.Success, "未知/冲突提交必须停止。");
            Equal("target", File.ReadAllText(Path.Combine(f.Game,"a.bin")));
            True(File.Exists(Path.Combine(blocks,"cn.blk")), "不能独立撤销 Blocks。");
            Equal(beforeFile, File.ReadAllText(f.Paths.FileTransactionJournalFile));
            Equal(beforeBlocks, File.ReadAllText(f.Paths.HotUpdateJournalFile));
            Equal(0, backups.PruneAllBackups());
        }
        state.Save(new AppState { GamePath=f.Game, GameVersion="3.0.0", CurrentProfile=ProfileIds.CnOfficial,
            LastOperationId=plan.OperationId, LastBackupPath=plan.BackupPath });
        True(service.RecoverPending().Success, "提交依据修复后一致的操作应能收尾。");
        Equal("target", File.ReadAllText(Path.Combine(f.Game,"a.bin")));
        return Task.CompletedTask;
    }

    private static async Task OverlayDetectionParity()
    {
        foreach (var version in new[] { "3.0.0", "3.2.0" })
        {
            using var f = new TempFixture(); f.CreateGameMarkers(version);
            var cn=new FileSignature { Path="GameAssembly.dll",Length=3,Sha256=Sha256Text("cn!") };
            var profiles=new[] {
                new ProfileDefinition { Id=ProfileIds.Global,DisplayName="OS",PackageDirectoryName=ProfileIds.Global,GameVersion="3.0.0",KeyFiles=[new(){Path="GameAssembly.dll",Length=3,Sha256=Sha256Text("os!")}] },
                new ProfileDefinition { Id=ProfileIds.CnOfficial,DisplayName="CN",PackageDirectoryName=ProfileIds.CnOfficial,GameVersion="3.0.0",KeyFiles=[cn] },
                new ProfileDefinition { Id=ProfileIds.Bilibili,DisplayName="B",PackageDirectoryName=ProfileIds.Bilibili,GameVersion="3.0.0",ReuseOverlayAcrossGameVersions=true,KeyFiles=[cn,new(){Path="overlay.dll",Length=3,Sha256=Sha256Text("sdk")}] }
            };
            var cache=new ManifestCache(f.Paths.ManifestCacheRoot,JsonSupport.Options);
            foreach(var region in new[]{SophonRegion.OS,SophonRegion.CN})
                await cache.SaveAsync(new ManifestSnapshot(SophonRegionConfig.Game,region,version,"game",region.ToString(),DateTimeOffset.UtcNow,
                    [new ManifestEntry("GameAssembly.dll",3,Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(region==SophonRegion.OS?"os!":"cn!"))))]));
            var detector=new ProfileDetector(f.Paths);
            File.WriteAllText(Path.Combine(f.Game,"GameAssembly.dll"),"cn!");
            Equal(DetectedProfile.CnOfficial,detector.Detect(f.Game,profiles,gameVersion:version).Profile);
            File.WriteAllText(Path.Combine(f.Game,"overlay.dll"),"bad");
            var damaged=detector.Detect(f.Game,profiles,gameVersion:version);
            Equal(DetectedProfile.Unknown,damaged.Profile); True(damaged.Issues.Count>0,"需报告 B 服组件异常。");
            File.WriteAllText(Path.Combine(f.Game,"overlay.dll"),"sdk");
            Equal(DetectedProfile.Bilibili,detector.Detect(f.Game,profiles,gameVersion:version).Profile);
        }
    }

    private static async Task JunctionSwitchPaths()
    {
        using var f=new TempFixture(); f.CreateGameMarkers("3.0.0");
        var outside=Path.Combine(f.Root,"outside"); Directory.CreateDirectory(outside);
        var link=Path.Combine(f.Game,"linked"); CreateFixtureJunction(f,link,outside);
        try
        {
            var relative=@"linked\payload.bin"; var external=Path.Combine(outside,"payload.bin");
            File.WriteAllText(external,"[General]\nx=old\n"); var original=File.ReadAllText(external);
            Directory.CreateDirectory(f.Package); File.WriteAllText(Path.Combine(f.Package,"payload.bin"),"new");
            var entry=new ReplaceFileEntry { Source="payload.bin",Target=relative,Length=3,Sha256=Sha256Text("new") };
            var manifest=new TransitionManifest {SourceProfile=ProfileIds.Global,TargetProfile=ProfileIds.CnOfficial,GameVersion="3.0.0",ReplaceFiles=[entry]};
            var preflight=CreatePlanner(f).CreateOnlinePlan(f.Game,new(){PackageRoot=f.Package,PackageDirectory=f.Package,Manifest=manifest});
            True(!preflight.CanExecute,"联接不能通过预检。");
            foreach(var action in new[]{"replace","ini","delete"})
            {
                var m=new TransitionManifest {SourceProfile=ProfileIds.Global,TargetProfile=ProfileIds.CnOfficial,GameVersion="3.0.0",
                    ReplaceFiles=action=="replace"?[entry]:[],IniPatches=action=="ini"?[new(){Target=relative,Section="General",Values=new(){["x"]="new"}}]:[],
                    DeleteFiles=action=="delete"?[new(){Target=relative}]:[]};
                var r=await f.CreateEngine().ExecuteAsync(f.CreatePlan([],[],manifest:m));
                True(!r.Success && r.GameFilesUnchanged,"执行入口本身也必须拒绝联接。"); Equal(original,File.ReadAllText(external));
            }
        }
        finally { Directory.Delete(link); }
    }

    private static Task JunctionRollbackPaths()
    {
        foreach(var linkKind in new[]{"game", "source", "record"})
        {
            using var f=new TempFixture(); f.CreateGameMarkers("3.0.0");
            var gameDir=Path.Combine(f.Game,"nested"); Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir,"a.bin"),"source");
            var plan=f.CreatePlan([new(){Source="a.bin",Target=@"nested\a.bin"}],[]);
            var backups=new BackupService(new PhysicalFileOperations(),f.Paths); backups.CreateBackup(plan);
            File.WriteAllText(Path.Combine(gameDir,"a.bin"),"target");
            var journals=new FileTransactionJournalStore(f.Paths); journals.Save(CreateFileJournal(plan,FileTransactionStage.FilesApplied));
            var link=linkKind=="record"?plan.BackupPath:linkKind=="source"?Path.Combine(plan.BackupPath,"files","nested"):gameDir;
            var original=link+"-retained"; Directory.Move(link,original);
            var outside=Path.Combine(f.Root,"outside");Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"a.bin"),"external");
            if (linkKind=="record") File.Copy(Path.Combine(original,"backup.json"),Path.Combine(outside,"backup.json"));
            var outsideRecord=linkKind=="record"?File.ReadAllText(Path.Combine(outside,"backup.json")):null;
            CreateFixtureJunction(f,link,outside);
            var recovery=CreateRecoveryService(f,backups,journals);
            try { True(!recovery.RecoverPending().Success && journals.Exists,"越界回滚应保留日志。"); Equal("external",File.ReadAllText(Path.Combine(outside,"a.bin")));
                if(outsideRecord is not null) Equal(outsideRecord,File.ReadAllText(Path.Combine(outside,"backup.json"))); }
            finally { Directory.Delete(link); Directory.Move(original,link); }
            True(recovery.RecoverPending().Success,"移除联接后应可重试。");
            Equal("source",File.ReadAllText(Path.Combine(gameDir,"a.bin")));
        }
        return Task.CompletedTask;
    }

    private static void CreateFixtureJunction(TempFixture f,string link,string target)
    {
        var prefix=Path.GetFullPath(f.Root)+Path.DirectorySeparatorChar;
        True(new[]{link,target}.All(p=>Path.GetFullPath(p).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)),"测试联接必须限制在隔离目录内。");
        var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        info.ArgumentList.Add("-NoProfile");info.ArgumentList.Add("-Command");
        info.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'","''")}' -Target '{target.Replace("'","''")}' | Out-Null");
        using var process=Process.Start(info)!; True(process.WaitForExit(15000),"创建隔离联接超时。");
        True(process.ExitCode==0,process.StandardError.ReadToEnd());
    }

    private static async Task ReverseCaptureClearsInvalidation()
    {
        using var f=new TempFixture(); File.WriteAllText(Path.Combine(f.Game,"a.bin"),"good");
        var workspace=Path.Combine(f.Paths.OnlineDifferenceFilesRoot,"3.0.0",ProfileIds.Global,"reverse");Directory.CreateDirectory(Path.Combine(workspace,"content"));
        var catalog=new OnlineDifferencePackageCatalog(f.Paths);catalog.MarkInvalidSource(Path.Combine(workspace,"content","a.bin"));
        var service=new OnlineDifferenceService(f.Paths,()=>new RejectingSophonTransport());
        var result=await service.MaterializeAsync(new(){SourceProfile=ProfileIds.Global,TargetProfile=ProfileIds.CnOfficial,GameVersion="3.0.0",SourceRegion=SophonRegion.OS,TargetRegion=SophonRegion.CN,
            TargetManifestId="forward",TargetCategory=new("game","game","forward","","","",""),DownloadFiles=[],DeleteFiles=[],LocalGamePath=f.Game,
            LocalSourceCapture=new(){SourceProfile=ProfileIds.CnOfficial,TargetProfile=ProfileIds.Global,TargetManifestId="reverse",Files=[new ManifestEntry("a.bin",4,Convert.ToHexString(MD5.HashData("good"u8)))]}});
        True(result.SourcePackageReady,"反向包应保存成功。");
        True(catalog.TryGetReadyMaterialization(ProfileIds.CnOfficial,ProfileIds.Global,"3.0.0",out _),"本进程内应解除失效。");
        True(!File.Exists(Path.Combine(workspace,".invalid-package.json")),"磁盘失效标记也应删除。");
        catalog.VerifyPackage(catalog.GetInventory().Packages.Single(x=>x.TargetProfile==ProfileIds.Global));
    }

    private static Task InvalidPathIsolationParity()
    {
        using var f=new TempFixture();
        var good=new TransitionManifest {SourceProfile=ProfileIds.Global,TargetProfile=ProfileIds.CnOfficial,GameVersion="3.0.0"};
        var bad=new TransitionManifest {SourceProfile=ProfileIds.Global,TargetProfile=ProfileIds.CnOfficial,GameVersion="3.0.0",ReplaceFiles=[new(){Source="a.bin",Target="bad\0path",Length=3,Sha256=Sha256Text("bad")}]};
        foreach(var (name,manifest) in new[]{("good",good),("bad",bad)})
        {
            var json=JsonSerializer.Serialize(manifest,JsonSupport.Options); File.WriteAllText(Path.Combine(f.Config,"transitions",name+".json"),json);
            var workspace=Path.Combine(f.Paths.OnlineDifferenceFilesRoot,"3.0.0",ProfileIds.CnOfficial,name);Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace,"transition-manifest.json"),json);
        }
        var fixedLoad=new ConfigurationRepository(f.Paths).LoadTransitionsWithStatus(); Equal(1,fixedLoad.Items.Count);Equal(1,fixedLoad.Errors.Count);
        var packages=new OnlineDifferencePackageCatalog(f.Paths).GetInventory().Packages;
        Equal(1,packages.Count(x=>x.State==OnlineDifferencePackageState.Ready));Equal(1,packages.Count(x=>x.State==OnlineDifferencePackageState.Invalid));
        return Task.CompletedTask;
    }
}
