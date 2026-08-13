using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class ConfigurationRepository
{
    private readonly AppPaths _paths;

    public ConfigurationRepository(AppPaths paths) => _paths = paths;

    public IReadOnlyList<ProfileDefinition> LoadProfiles() => LoadProfilesWithStatus().Items;

    public ConfigurationLoadResult<ProfileDefinition> LoadProfilesWithStatus() =>
        LoadDirectory<ProfileDefinition>(Path.Combine(_paths.ConfigRoot, "profiles"));

    public IReadOnlyList<TransitionManifest> LoadTransitions() => LoadTransitionsWithStatus().Items;

    public ConfigurationLoadResult<TransitionManifest> LoadTransitionsWithStatus() =>
        LoadDirectory<TransitionManifest>(Path.Combine(_paths.ConfigRoot, "transitions"));

    public TransitionManifest? FindTransition(string source, string target)
    {
        var matches = LoadTransitions().Where(x =>
            string.Equals(x.SourceProfile, source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.TargetProfile, target, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static ConfigurationLoadResult<T> LoadDirectory<T>(string directory)
    {
        // 配置随软件本体分发，覆盖升级或不完整解压可能只损坏其中一个文件。
        // 因此逐文件隔离错误，保留其余有效配置，并把坏文件路径交给详细检查展示。
        if (!Directory.Exists(directory))
        {
            return new()
            {
                Errors =
                [
                    new ConfigurationLoadError
                    {
                        Path = directory,
                        Message = "配置目录不存在。"
                    }
                ]
            };
        }

        var items = new List<T>();
        var errors = new List<ConfigurationLoadError>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new()
            {
                Errors =
                [
                    new ConfigurationLoadError
                    {
                        Path = directory,
                        Message = $"无法枚举配置目录：{ex.Message}"
                    }
                ]
            };
        }

        foreach (var path in files)
        {
            try
            {
                items.Add(Read<T>(path));
            }
            catch (Exception ex) when (
                ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add(new()
                {
                    Path = path,
                    Message = $"配置无法读取：{ex.Message}"
                });
            }
        }

        return new() { Items = items, Errors = errors };
    }

    private static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        var value = JsonSerializer.Deserialize<T>(stream, JsonSupport.Options)
                    ?? throw new InvalidDataException($"无法解析配置：{path}");
        ValidateConfiguration(value, path);
        return value;
    }

    private static void ValidateConfiguration<T>(T value, string path)
    {
        // required 只能保证 JSON 中出现属性；显式 null 和危险目录段仍需在这里拦截。
        switch (value)
        {
            case ProfileDefinition profile:
                if (!ProfileIds.All.Contains(profile.Id, StringComparer.Ordinal) ||
                    string.IsNullOrWhiteSpace(profile.DisplayName) ||
                    !IsSafeDirectoryName(profile.PackageDirectoryName) ||
                    profile.KeyFiles is null ||
                    profile.KeyFiles.Cast<FileSignature?>().Any(x =>
                        x is null || string.IsNullOrWhiteSpace(x.Path) || x.Length < 0))
                {
                    throw new InvalidDataException($"服务器配置缺少必要字段或包含无效值：{path}");
                }

                break;
            case TransitionManifest transition:
                if (!ProfileIds.All.Contains(transition.SourceProfile, StringComparer.Ordinal) ||
                    !ProfileIds.All.Contains(transition.TargetProfile, StringComparer.Ordinal) ||
                    string.Equals(transition.SourceProfile, transition.TargetProfile, StringComparison.Ordinal) ||
                    !IsSafeDirectoryName(transition.GameVersion) ||
                    transition.ExpectedReplaceCount < 0 ||
                    transition.ExpectedDeleteCount < 0 ||
                    transition.ReplaceFiles is null ||
                    transition.IniPatches is null ||
                    transition.DeleteFiles is null ||
                    transition.OptionalDeleteFiles is null ||
                    transition.ReplaceFiles.Cast<ReplaceFileEntry?>().Any(x =>
                        x is null ||
                        string.IsNullOrWhiteSpace(x.Source) ||
                        string.IsNullOrWhiteSpace(x.Target) ||
                        x.Length < 0 ||
                        (x.SourcePackageDirectoryName is not null && !IsSafeDirectoryName(x.SourcePackageDirectoryName))) ||
                    transition.IniPatches.Cast<IniFilePatch?>().Any(x =>
                        x is null ||
                        string.IsNullOrWhiteSpace(x.Target) ||
                        string.IsNullOrWhiteSpace(x.Section) ||
                        x.Values is null ||
                        x.Values.Count == 0 ||
                        x.Values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('='))) ||
                    transition.DeleteFiles.Cast<DeleteFileEntry?>().Any(x =>
                        x is null || string.IsNullOrWhiteSpace(x.Target)) ||
                    transition.OptionalDeleteFiles.Cast<DeleteFileEntry?>().Any(x =>
                        x is null || string.IsNullOrWhiteSpace(x.Target)))
                {
                    throw new InvalidDataException($"切换清单缺少必要字段或包含无效值：{path}");
                }

                break;
        }
    }

    private static bool IsSafeDirectoryName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        !Path.IsPathRooted(value) &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
}
