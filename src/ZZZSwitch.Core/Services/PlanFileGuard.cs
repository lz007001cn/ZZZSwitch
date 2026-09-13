using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

internal static class PlanFileGuard
{
    internal static IEnumerable<string> Affected(TransitionManifest manifest, ProfileSnapshotManifest? snapshot) =>
        manifest.ReplaceFiles.Select(x => x.Target).Concat(manifest.IniPatches.Select(x => x.Target))
            .Concat(manifest.DeleteFiles.Select(x => x.Target)).Concat(manifest.OptionalDeleteFiles.Select(x => x.Target))
            .Concat(snapshot?.Files.Select(x => x.RelativePath) ?? []);

    internal static OrdinaryPathGuard CheckPaths(string game, TransitionManifest manifest, ProfileSnapshotManifest? snapshot, string backup)
    {
        var guard = new OrdinaryPathGuard();
        guard.Ensure(game);
        guard.Ensure(backup);
        guard.Ensure(GameStorageLayout.GetStagingRoot(game));
        foreach (var relative in Affected(manifest, snapshot))
            guard.Ensure(PathSafety.ResolveOrThrow(game, relative));
        if (snapshot is not null)
        {
            guard.Ensure(snapshot.SnapshotPath);
            foreach (var file in snapshot.Files)
                guard.Ensure(PathSafety.ResolveOrThrow(Path.Combine(snapshot.SnapshotPath, "files"), file.RelativePath));
        }
        return guard;
    }

    private static readonly string[] IdentityFiles = ["version_info", "config.ini", "GameAssembly.dll", "ZenlessZoneZero.exe", "mhypbase.dll",
        @"ZenlessZoneZero_Data\il2cpp_data\Metadata\global-metadata.dat", @"ZenlessZoneZero_Data\resources.assets",
        @"ZenlessZoneZero_Data\Plugins\x86_64\PCGameSDK.dll",
        @"ZenlessZoneZero_Data\Plugins\x86_64\BLPlatform64\PCGamePlatform.exe",
        @"ZenlessZoneZero_Data\Plugins\x86_64\BLPlatform64\game_security_protection.exe"];

    internal static Dictionary<string, string> Capture(string game, IEnumerable<string> affected) =>
        affected.Concat(IdentityFiles).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => Stamp(PathSafety.ResolveOrThrow(game, path)), StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, string>? ForPlan(string game, TransitionManifest manifest, ProfileSnapshotManifest? snapshot,
        IReadOnlyList<ValidationIssue> issues) => issues.Any(x => x.Severity == IssueSeverity.Error) ? null :
        Capture(game, manifest.ReplaceFiles.Select(x => x.Target).Concat(manifest.IniPatches.Select(x => x.Target))
            .Concat(manifest.DeleteFiles.Select(x => x.Target)).Concat(manifest.OptionalDeleteFiles.Select(x => x.Target))
            .Concat(snapshot?.Files.Select(x => x.RelativePath) ?? []));

    internal static void Validate(string game, IReadOnlyDictionary<string, string>? stamps)
    {
        if (stamps is null) return;
        foreach (var (relative, value) in stamps)
            if (Stamp(PathSafety.ResolveOrThrow(game, relative)) != value)
                throw new InvalidDataException("切换计划已过期，文件发生变化，请重新检查：" + relative);
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.Length}|{file.LastWriteTimeUtc.Ticks}|{file.CreationTimeUtc.Ticks}|{(int)file.Attributes}" : "missing";
    }
}
