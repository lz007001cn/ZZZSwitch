using System.Security.Cryptography;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class ProfileDetector
{
    public DetectionResult Detect(string gamePath, IReadOnlyList<ProfileDefinition> profiles, AppState? state = null)
    {
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

        // The persisted state is a hint only. It can confirm an exact physical match,
        // but can never override mismatching game files.
        var stateHint = state is not null &&
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
            StateHint = stateHint,
            Matches = matches,
            Mismatches = matches.SelectMany(x => x.Mismatches.Select(y => $"{x.ProfileId}: {y}")).ToList()
        };
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
                    mismatches.Add($"SHA-256 不符 {signature.Path}");
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
