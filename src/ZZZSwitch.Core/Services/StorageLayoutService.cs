using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class StorageLayoutService
{
    private readonly ICacheRootResolver _cacheRoots;

    public StorageLayoutService(ICacheRootResolver? cacheRoots = null) =>
        _cacheRoots = cacheRoots ?? DefaultCacheRootResolver.Instance;

    public StorageLayoutStatus Inspect(
        string gamePath,
        string gameVersion,
        IReadOnlyList<ProfileDefinition> profiles)
    {
        var root = GameStorageLayout.GetRoot(gamePath);
        var packagesRoot = GameStorageLayout.GetPackagesRoot(gamePath);
        var packageVersion = GameStorageLayout.GetPackageRoot(gamePath, gameVersion);
        var cacheRoot = _cacheRoots.GetCacheRoot(gamePath);
        var missingProfiles = profiles
            .Where(x => x.Enabled)
            .Select(x => GameStorageLayout.GetPackageDirectory(
                gamePath,
                gameVersion,
                x.PackageDirectoryName))
            .Where(x => !Directory.Exists(x))
            .ToList();

        return new()
        {
            RootPath = root,
            PackagesRootPath = packagesRoot,
            PackageVersionPath = packageVersion,
            CacheRootPath = cacheRoot,
            RootExists = Directory.Exists(root),
            PackagesRootExists = Directory.Exists(packagesRoot),
            PackageVersionExists = Directory.Exists(packageVersion),
            CacheRootExists = Directory.Exists(cacheRoot),
            MissingProfileDirectories = missingProfiles
        };
    }

    public StorageRepairResult Repair(
        string gamePath,
        string gameVersion,
        IReadOnlyList<ProfileDefinition> profiles)
    {
        var before = Inspect(gamePath, gameVersion, profiles);
        var directories = new[]
            {
                before.RootPath,
                before.PackagesRootPath,
                before.PackageVersionPath,
                before.CacheRootPath
            }
            .Concat(profiles
                .Where(x => x.Enabled)
                .Select(x => GameStorageLayout.GetPackageDirectory(
                    gamePath,
                    gameVersion,
                    x.PackageDirectoryName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var created = directories
            .Where(x => !Directory.Exists(x))
            .ToList();

        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
        }

        return new()
        {
            Before = before,
            After = Inspect(gamePath, gameVersion, profiles),
            CreatedDirectories = created
        };
    }
}
