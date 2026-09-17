using System.Text.RegularExpressions;

namespace ZZZSwitch.Update;

public sealed record UpdateVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<UpdateVersion>
{
    public string CoreVersion => $"{Major}.{Minor}.{Patch}";
    public bool IsLocalTestBuild => PreRelease?.StartsWith("test.", StringComparison.Ordinal) == true;

    public static UpdateVersion Parse(string? value)
    {
        var match = Regex.Match(value ?? "", @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch))
            throw new UpdateException(UpdateError.InvalidManifest, "Invalid version: " + value);
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(p => p.All(char.IsAsciiDigit) && p.Length > 1 && p[0] == '0') == true)
            throw new UpdateException(UpdateError.InvalidManifest, "Invalid prerelease version.");
        return new(major, minor, patch, pre);
    }

    public static UpdateVersion ParseForUpdateCheck(string? value, string channel)
    {
        var version = Parse(value);
        return channel == "stable" && version.IsLocalTestBuild
            ? version with { PreRelease = null }
            : version;
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        var left = PreRelease.Split('.'); var right = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var a = left[i]; var b = right[i];
            var na = a.All(char.IsAsciiDigit); var nb = b.All(char.IsAsciiDigit);
            c = na && nb ? (a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b))
                : na != nb ? (na ? -1 : 1) : string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return left.Length.CompareTo(right.Length);
    }
}
