using System.Security.Cryptography;
using System.Text.Json;
using ZZZSwitch.Core.Models;
using ZZZSwitch.ManifestTool;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.Core.Services;

public sealed class ProfileDetector
{
    private readonly AppPaths? _paths;

    public ProfileDetector(AppPaths? paths = null) => _paths = paths;

    internal bool HasPendingTransaction => _paths is not null &&
        (File.Exists(_paths.FileTransactionJournalFile) || File.Exists(_paths.HotUpdateJournalFile));

    // Compare only metadata on the normal path. A changed/missing key file, INI,
    // profile, Manifest, installation, version or operation invalidates the result.
    internal string? GetCacheFingerprint(string gamePath, IReadOnlyList<ProfileDefinition> profiles,
        AppState? state, string? gameVersion)
    {
        if (_paths is null || string.IsNullOrWhiteSpace(gameVersion)) return null;
        try
        {
            var stamps = new List<string>();
            foreach (var relative in profiles.SelectMany(profile => profile.KeyFiles).Select(file => file.Path)
                         .Concat(["version_info", "config.ini"]).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                AddStamp(PathSafety.ResolveOrThrow(gamePath, relative));

            if (!profiles.Any(profile => profile.GameVersion == gameVersion))
            {
                var cache = new ManifestCache(_paths.ManifestCacheRoot, JsonSupport.Options);
                foreach (var region in new[] { SophonRegion.OS, SophonRegion.CN })
                {
                    var root = Path.GetDirectoryName(Path.GetDirectoryName(cache.GetPath(region, gameVersion, "game")))!;
                    if (!Directory.Exists(root)) return null;
                    foreach (var directory in new DirectoryInfo(root).EnumerateDirectories().OrderBy(item => item.Name))
                    {
                        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                        AddStamp(cache.GetPath(region, gameVersion, directory.Name));
                    }
                }
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1, GamePath = Path.GetFullPath(gamePath).TrimEnd('\\', '/').ToUpperInvariant(),
                GameVersion = gameVersion, Profiles = profiles, Stamps = stamps,
                SavedPath = state?.GamePath, SavedVersion = state?.GameVersion, state?.CurrentProfile, state?.LastOperationId
            }, JsonSupport.Options);
            return Convert.ToHexString(SHA256.HashData(bytes));

            void AddStamp(string path)
            {
                var file = new FileInfo(path);
                if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("不能复用重解析点识别结果。");
                stamps.Add(file.Exists
                    ? $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{file.CreationTimeUtc.Ticks}"
                    : $"{path}|missing");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    internal static bool HasConfirmedSource(AppPaths paths, SwitchPlan plan, AppState? state)
    {
        if (state?.ClientDetection?.Result?.Profile.ToProfileId() != plan.Manifest.SourceProfile) return false;
        var profiles = new ConfigurationRepository(paths).LoadProfilesWithStatus();
        if (profiles.Errors.Count > 0) return false;
        var fingerprint = new ProfileDetector(paths).GetCacheFingerprint(plan.GamePath, profiles.Items, state, plan.Manifest.GameVersion);
        return fingerprint is not null && fingerprint == state.ClientDetection.Fingerprint;
    }

    internal static void RememberSuccessfulSwitch(AppPaths paths, AppState state)
    {
        // Called only after the engine has validated the committed target files.
        // Plain saved state and restore/recovery records do not establish a cache.
        var profiles = new ConfigurationRepository(paths).LoadProfilesWithStatus();
        if (profiles.Errors.Count > 0 || state.GamePath is null || state.CurrentProfile is null) return;
        var fingerprint = new ProfileDetector(paths).GetCacheFingerprint(state.GamePath, profiles.Items, state, state.GameVersion);
        if (fingerprint is not null)
            state.ClientDetection = new ClientDetectionSnapshot
            {
                Fingerprint = fingerprint,
                Result = new DetectionResult { Profile = FromId(state.CurrentProfile) }
            };
    }

    public DetectionResult Detect(string gamePath, IReadOnlyList<ProfileDefinition> profiles, AppState? state = null,
        string? gameVersion = null)
    {
        var configuredProfiles = profiles;
        var usesManifest = false;
        if (gameVersion is not null && profiles.Any(profile => profile.GameVersion is not null))
        {
            profiles = profiles.Where(profile => string.Equals(profile.GameVersion, gameVersion, StringComparison.Ordinal)).ToArray();
            if (profiles.Count == 0)
            {
                try
                {
                    profiles = LoadManifestProfiles(gameVersion, configuredProfiles);
                    usesManifest = true;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                {
                    return new DetectionResult
                    {
                        Profile = DetectedProfile.Unknown,
                        NeedsManifestRefresh = true,
                        Issues = [new(IssueSeverity.Warning, "detection.manifest.required",
                            $"需要 {gameVersion} 的识别清单。请在差异包管理中更新 Manifest 后重试：{ex.Message}")]
                    };
                }
            }
        }

        var matches = profiles.Where(x => x.Enabled && x.KeyFiles.Count > 0)
            .Select(profile => Match(gamePath, profile))
            .ToList();

        var exact = matches.Where(x => x.IsExact).ToList();
        // Overlay profiles (currently B 服) intentionally include the base profile's
        // signatures. Prefer the single exact profile with the most evidence; equal
        // specificity is still ambiguous and must remain Mixed.
        var mostSpecificExact = exact.Count == 0
            ? []
            : exact.Where(x => x.TotalFiles == exact.Max(y => y.TotalFiles)).ToList();
        var detected = mostSpecificExact.Count switch
        {
            1 => FromId(mostSpecificExact[0].ProfileId),
            > 1 => DetectedProfile.Mixed,
            _ => DetectNonExact(matches)
        };

        // A regional Manifest does not describe the Bilibili overlay. Its presence
        // must not silently turn an unsupported Bilibili client into CN Official.
        var issues = new List<ValidationIssue>();
        if (usesManifest)
        {
            var cnPaths = configuredProfiles.Where(p => p.Id == ProfileIds.CnOfficial)
                .SelectMany(p => p.KeyFiles).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlayPresent = configuredProfiles.Where(p => p.Id == ProfileIds.Bilibili)
                .SelectMany(p => p.KeyFiles).Where(file => !cnPaths.Contains(file.Path))
                .Any(file => File.Exists(PathSafety.ResolveOrThrow(gamePath, file.Path)));
            if (overlayPresent && detected != DetectedProfile.Bilibili)
            {
                detected = DetectedProfile.Unknown;
                var supported = profiles.Any(profile => profile.Id == ProfileIds.Bilibili);
                issues.Add(new(IssueSeverity.Warning,
                    supported ? "detection.bilibili.inconsistent" : "detection.bilibili.unsupported",
                    supported
                        ? $"检测到 B 服渠道组件，但与 {gameVersion} 国服核心或组件特征不一致；请检查客户端文件。"
                        : $"检测到 B 服渠道组件，但没有 {gameVersion} 的组件识别清单；暂不支持切换此客户端。"));
            }
        }

        // The persisted state is a hint only. It can confirm an exact physical match,
        // but can never override mismatching game files.
        var stateHint = state is not null &&
                        (gameVersion is null || string.Equals(state.GameVersion, gameVersion, StringComparison.Ordinal)) &&
                        string.Equals(Path.GetFullPath(state.GamePath ?? string.Empty), Path.GetFullPath(gamePath), StringComparison.OrdinalIgnoreCase)
            ? state.CurrentProfile
            : null;

        if (mostSpecificExact.Count == 1 && stateHint is not null &&
            !string.Equals(mostSpecificExact[0].ProfileId, stateHint, StringComparison.OrdinalIgnoreCase))
        {
            stateHint = $"{stateHint}（状态记录与文件不一致，以文件为准）";
        }

        return new()
        {
            Profile = detected,
            Issues = issues,
            StateHint = stateHint,
            Matches = matches,
            Mismatches = matches.SelectMany(x => x.Mismatches.Select(y => $"{x.ProfileId}: {y}")).ToList()
        };
    }

    private IReadOnlyList<ProfileDefinition> LoadManifestProfiles(
        string gameVersion, IReadOnlyList<ProfileDefinition> configured)
    {
        if (_paths is null)
        {
            throw new InvalidDataException("尚未配置 Manifest 缓存。");
        }

        var regional = new[] { ProfileIds.Global, ProfileIds.CnOfficial }.Select(id =>
            configured.Single(profile => profile.Id == id && profile.Enabled)).ToArray();
        var keyPaths = regional.SelectMany(profile => profile.KeyFiles).Select(file => file.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keyPaths.Length == 0)
        {
            throw new InvalidDataException("没有可用于识别的关键文件路径。");
        }

        var snapshots = new[] { LoadSnapshot(SophonRegion.OS), LoadSnapshot(SophonRegion.CN) };
        var result = regional.Select((profile, index) => new ProfileDefinition
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            PackageDirectoryName = profile.PackageDirectoryName,
            GameVersion = gameVersion,
            KeyFiles = keyPaths.Select(path =>
            {
                var entry = snapshots[index].Entries.SingleOrDefault(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Manifest 缺少关键文件：{path}");
                return new FileSignature { Path = path, Length = entry.Size, Md5 = entry.Md5 };
            }).ToList()
        }).ToArray();
        if (!result[0].KeyFiles.Where((file, index) => file.Md5 != result[1].KeyFiles[index].Md5).Any())
        {
            throw new InvalidDataException("两服关键文件没有可区分的特征。");
        }
        var bilibili = configured.SingleOrDefault(profile => profile.Id == ProfileIds.Bilibili);
        if (bilibili?.SupportsOverlayVersion(gameVersion) == true)
        {
            var cnPaths = regional[1].KeyFiles.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlayKeys = bilibili.KeyFiles.Where(file => !cnPaths.Contains(file.Path)).ToList();
            if (overlayKeys.Count > 0 && overlayKeys.All(file => FileIntegrityService.IsValidSha256(file.Sha256)))
            {
                return [.. result, new ProfileDefinition
                {
                    Id = bilibili.Id, DisplayName = bilibili.DisplayName,
                    PackageDirectoryName = bilibili.PackageDirectoryName, GameVersion = gameVersion,
                    KeyFiles = [.. result[1].KeyFiles, .. overlayKeys]
                }];
            }
        }
        return result;

        ManifestSnapshot LoadSnapshot(SophonRegion region)
        {
            var cache = new ManifestCache(_paths.ManifestCacheRoot, JsonSupport.Options);
            var versionRoot = Path.GetDirectoryName(Path.GetDirectoryName(cache.GetPath(region, gameVersion, "game")))!;
            if (!Directory.Exists(versionRoot))
            {
                throw new InvalidDataException($"尚未缓存 {region} Manifest。");
            }
            // Ignore audio-only categories. Never pick arbitrarily between two
            // game manifests: a conflicting cache needs an explicit refresh.
            var candidates = new List<ManifestSnapshot>();
            foreach (var directory in new DirectoryInfo(versionRoot).EnumerateDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var snapshot = cache.TryLoadAsync(region, gameVersion, directory.Name).GetAwaiter().GetResult();
                if (snapshot is not null && keyPaths.All(path => snapshot.Entries.Any(entry =>
                        string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))))
                {
                    candidates.Add(snapshot);
                }
            }
            return candidates.Count == 1 ? candidates[0]
                : throw new InvalidDataException("没有唯一且完整的游戏 Manifest 缓存。");
        }
    }

    private static ProfileMatch Match(string gamePath, ProfileDefinition profile)
    {
        var matching = 0;
        var mismatches = new List<string>();
        foreach (var signature in profile.KeyFiles)
        {
            string path;
            try
            {
                path = PathSafety.ResolveOrThrow(gamePath, signature.Path);
            }
            catch (InvalidDataException ex)
            {
                mismatches.Add(ex.Message);
                continue;
            }

            if (!File.Exists(path))
            {
                mismatches.Add($"缺少 {signature.Path}");
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length != signature.Length)
            {
                mismatches.Add($"大小不符 {signature.Path}（当前 {info.Length}，预期 {signature.Length}）");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(signature.Sha256))
            {
                using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(actual, signature.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"文件完整性不符 {signature.Path}");
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(signature.Md5))
            {
                using var stream = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(MD5.HashData(stream)), signature.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"文件完整性不符 {signature.Path}");
                    continue;
                }
            }

            matching++;
        }

        return new()
        {
            ProfileId = profile.Id,
            MatchingFiles = matching,
            TotalFiles = profile.KeyFiles.Count,
            IsExact = matching == profile.KeyFiles.Count,
            Mismatches = mismatches
        };
    }

    private static DetectedProfile DetectNonExact(IReadOnlyCollection<ProfileMatch> matches)
    {
        var withEvidence = matches.Where(x => x.MatchingFiles > 0).ToList();
        if (withEvidence.Count >= 2)
        {
            return DetectedProfile.Mixed;
        }

        return DetectedProfile.Unknown;
    }

    private static DetectedProfile FromId(string id) => id switch
    {
        ProfileIds.Global => DetectedProfile.Global,
        ProfileIds.CnOfficial => DetectedProfile.CnOfficial,
        ProfileIds.Bilibili => DetectedProfile.Bilibili,
        _ => DetectedProfile.Unknown
    };
}
