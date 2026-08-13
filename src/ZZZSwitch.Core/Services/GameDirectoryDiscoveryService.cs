using Microsoft.Win32;

namespace ZZZSwitch.Core.Services;

public sealed record GameDirectoryCandidate(string Path, string Source);

public interface IGameInstallLocator
{
    IReadOnlyList<GameDirectoryCandidate> Locate();
}

public sealed class GameDirectoryDiscoveryService
{
    private readonly GameDirectoryService _validator;
    private readonly IGameInstallLocator _locator;

    public GameDirectoryDiscoveryService(GameDirectoryService validator)
        : this(validator, new WindowsGameInstallLocator())
    {
    }

    public GameDirectoryDiscoveryService(
        GameDirectoryService validator,
        IGameInstallLocator locator)
    {
        _validator = validator;
        _locator = locator;
    }

    public IReadOnlyList<GameDirectoryCandidate> Discover(
        IEnumerable<string?> preferredPaths)
    {
        var candidates = new List<GameDirectoryCandidate>();
        candidates.AddRange(preferredPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new GameDirectoryCandidate(path!, "上次使用")));
        candidates.AddRange(_locator.Locate());

        var results = new List<GameDirectoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeCandidate(candidate.Path);
            if (normalized is null || !seen.Add(normalized))
            {
                continue;
            }

            var validation = _validator.Validate(normalized);
            if (validation.IsValid)
            {
                results.Add(candidate with { Path = validation.GamePath });
            }
        }

        return results;
    }

    private static string? NormalizeCandidate(string path)
    {
        try
        {
            var normalized = path.Trim().Trim('"');
            if (normalized.EndsWith("ZenlessZoneZero.exe", StringComparison.OrdinalIgnoreCase))
            {
                normalized = Path.GetDirectoryName(normalized) ?? normalized;
            }

            return Path.GetFullPath(normalized)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

public sealed class WindowsGameInstallLocator : IGameInstallLocator
{
    private const string GameDirectoryName = "ZenlessZoneZero Game";

    private static readonly string[] LauncherNameFragments =
    [
        "HoYoPlay",
        "miHoYo",
        "米哈游",
        "Zenless",
        "绝区零"
    ];

    private static readonly string[] CommonRelativePaths =
    [
        @"HoYoPlay\games\ZenlessZoneZero Game",
        @"Games\ZenlessZoneZero Game",
        @"Game\ZenlessZoneZero Game",
        @"Program Files\HoYoPlay\games\ZenlessZoneZero Game",
        @"Program Files (x86)\HoYoPlay\games\ZenlessZoneZero Game",
        @"miHoYo Launcher\games\ZenlessZoneZero Game",
        @"米哈游启动器\games\ZenlessZoneZero Game"
    ];

    public IReadOnlyList<GameDirectoryCandidate> Locate()
    {
        var results = new List<GameDirectoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var launcherRoot in ReadLauncherRoots())
        {
            AddLauncherCandidates(results, seen, launcherRoot);
        }

        foreach (var driveRoot in ReadFixedDriveRoots())
        {
            AddCommonDriveCandidates(results, seen, driveRoot);
        }

        return results;
    }

    private static void AddLauncherCandidates(
        ICollection<GameDirectoryCandidate> results,
        ISet<string> seen,
        string launcherRoot)
    {
        Add(results, seen, launcherRoot, "启动器记录");
        Add(results, seen, Path.Combine(launcherRoot, "games", GameDirectoryName), "启动器记录");
        Add(results, seen, Path.Combine(launcherRoot, GameDirectoryName), "启动器记录");

        var parent = Directory.GetParent(launcherRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Add(results, seen, Path.Combine(parent, "games", GameDirectoryName), "启动器记录");
        }
    }

    private static void AddCommonDriveCandidates(
        ICollection<GameDirectoryCandidate> results,
        ISet<string> seen,
        string driveRoot)
    {
        foreach (var relativePath in CommonRelativePaths)
        {
            Add(results, seen, Path.Combine(driveRoot, relativePath), "常见安装位置");
        }

        try
        {
            foreach (var topLevelDirectory in Directory.EnumerateDirectories(driveRoot))
            {
                Add(
                    results,
                    seen,
                    Path.Combine(topLevelDirectory, GameDirectoryName),
                    "固定磁盘");
                Add(
                    results,
                    seen,
                    Path.Combine(topLevelDirectory, "games", GameDirectoryName),
                    "固定磁盘");
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // A drive can contain protected top-level directories. Other sources still apply.
        }
    }

    private static void Add(
        ICollection<GameDirectoryCandidate> results,
        ISet<string> seen,
        string path,
        string source)
    {
        try
        {
            var normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (seen.Add(normalized))
            {
                results.Add(new(normalized, source));
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore malformed third-party registry values.
        }
    }

    private static IReadOnlyList<string> ReadFixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadLauncherRoots()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadUninstallRoot(RegistryHive.LocalMachine, RegistryView.Registry64, results);
        ReadUninstallRoot(RegistryHive.LocalMachine, RegistryView.Registry32, results);
        ReadUninstallRoot(RegistryHive.CurrentUser, RegistryView.Default, results);
        return results.ToList();
    }

    private static void ReadUninstallRoot(
        RegistryHive hive,
        RegistryView view,
        ISet<string> results)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                return;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName);
                var displayName = entry?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName) ||
                    !LauncherNameFragments.Any(fragment =>
                        displayName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                AddRegistryDirectory(results, entry?.GetValue("InstallLocation") as string);
                AddRegistryDirectory(results, entry?.GetValue("DisplayIcon") as string);
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Registry access can be restricted by policy; fixed-drive candidates remain available.
        }
    }

    private static void AddRegistryDirectory(ISet<string> results, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var cleaned = value.Trim().Trim('"');
            var comma = cleaned.LastIndexOf(',');
            if (comma > 0 && int.TryParse(cleaned[(comma + 1)..], out _))
            {
                cleaned = cleaned[..comma];
            }

            var directory = Path.HasExtension(cleaned)
                ? Path.GetDirectoryName(cleaned)
                : cleaned;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                results.Add(Path.GetFullPath(directory));
            }
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Ignore malformed third-party registry values.
        }
    }
}
