using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

internal static class PackageFileResolver
{
    public static string ResolveOrThrow(
        string packageRoot,
        string defaultPackageDirectory,
        ReplaceFileEntry entry)
    {
        var packageDirectory = string.IsNullOrWhiteSpace(entry.SourcePackageDirectoryName)
            ? defaultPackageDirectory
            : PathSafety.ResolveOrThrow(packageRoot, entry.SourcePackageDirectoryName);
        return PathSafety.ResolveOrThrow(packageDirectory, entry.Source);
    }

    public static string EffectiveDirectoryName(
        string defaultDirectoryName,
        ReplaceFileEntry entry) =>
        string.IsNullOrWhiteSpace(entry.SourcePackageDirectoryName)
            ? defaultDirectoryName
            : entry.SourcePackageDirectoryName;
}
