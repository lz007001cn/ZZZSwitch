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

    internal static TransitionManifest ResolveBilibiliVersion(
        TransitionManifest direct, string? gameVersion,
        IReadOnlyList<ProfileDefinition> profiles, IReadOnlyList<TransitionManifest> transitions,
        bool hasOnlineBase = false)
    {
        if (gameVersion is null ||
            (direct.SourceProfile != ProfileIds.Bilibili && direct.TargetProfile != ProfileIds.Bilibili) ||
            (!hasOnlineBase && ProfileIds.ToResourceProfile(direct.SourceProfile) !=
                               ProfileIds.ToResourceProfile(direct.TargetProfile))) return direct;

        var overlay = direct;
        if (direct.GameVersion != gameVersion)
        {
            var bilibiliProfiles = profiles.Where(profile => profile.Id == ProfileIds.Bilibili).Take(2).ToArray();
            var bilibili = bilibiliProfiles.Length == 1 ? bilibiliProfiles[0] : null;
            if (bilibili is null || bilibili.GameVersion != direct.GameVersion ||
                !bilibili.SupportsOverlayVersion(gameVersion)) return direct;

            // Reuse the CN/Bilibili overlay-only template, never an old regional core.
            var toBilibili = direct.TargetProfile == ProfileIds.Bilibili;
            var templates = transitions.Where(item =>
                item.SourceProfile == (toBilibili ? ProfileIds.CnOfficial : ProfileIds.Bilibili) &&
                item.TargetProfile == (toBilibili ? ProfileIds.Bilibili : ProfileIds.CnOfficial) &&
                item.GameVersion == direct.GameVersion).Take(2).ToArray();
            if (templates.Length != 1) return direct;
            overlay = templates[0];
        }
        var patches = direct.IniPatches;
        if (direct.TargetProfile == ProfileIds.Bilibili)
        {
            patches = direct.IniPatches.Select(patch => new IniFilePatch
            {
                Target = patch.Target, Section = patch.Section,
                Values = new Dictionary<string, string>(patch.Values, StringComparer.OrdinalIgnoreCase)
            }).ToList();
            var general = patches.FirstOrDefault(patch =>
                string.Equals(patch.Target, "config.ini", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(patch.Section, "General", StringComparison.OrdinalIgnoreCase));
            if (general is null)
            {
                general = new IniFilePatch { Target = "config.ini", Section = "General" };
                patches.Add(general);
            }
            general.Values["game_version"] = gameVersion;
        }
        return new TransitionManifest
        {
            SourceProfile = direct.SourceProfile,
            TargetProfile = direct.TargetProfile,
            GameVersion = gameVersion,
            Enabled = direct.Enabled && overlay.Enabled,
            DisabledReason = direct.DisabledReason ?? overlay.DisabledReason,
            ReplaceFiles = overlay.ReplaceFiles,
            IniPatches = patches,
            DeleteFiles = overlay.DeleteFiles,
            OptionalDeleteFiles = overlay.OptionalDeleteFiles,
            ExpectedReplaceCount = overlay.ReplaceFiles.Count + patches.Count,
            ExpectedDeleteCount = overlay.DeleteFiles.Count,
            Notes = direct.Notes
        };
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
                ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
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

    internal static void ValidateConfiguration<T>(T value, string path)
    {
        // required 只能保证 JSON 中出现属性；显式 null 和危险目录段仍需在这里拦截。
        switch (value)
        {
            case ProfileDefinition profile:
                if (!ProfileIds.All.Contains(profile.Id, StringComparer.Ordinal) ||
                    string.IsNullOrWhiteSpace(profile.DisplayName) ||
                    !IsSafeDirectoryName(profile.PackageDirectoryName) ||
                    profile.OverlayCompatibleGameVersions is null ||
                    profile.OverlayCompatibleGameVersions.Any(version => !IsSafeDirectoryName(version)) ||
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

                var targets = transition.ReplaceFiles.Select(x => x.Target)
                    .Concat(transition.IniPatches.Select(x => x.Target))
                    .Concat(transition.DeleteFiles.Select(x => x.Target))
                    .Concat(transition.OptionalDeleteFiles.Select(x => x.Target)).ToArray();
                var validationRoot = Path.GetFullPath(Path.GetTempPath());
                var normalized = targets.Select(x => PathSafety.ResolveOrThrow(validationRoot, x)).ToArray();
                if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
                    throw new InvalidDataException($"切换清单存在重复或冲突目标：{path}");
                foreach (var entry in transition.ReplaceFiles)
                {
                    _ = PathSafety.ResolveOrThrow(validationRoot, entry.Source);
                    if (entry.Sha256 is not null && !FileIntegrityService.IsValidSha256(entry.Sha256))
                        throw new InvalidDataException($"切换清单 SHA-256 无效：{entry.Source}");
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
