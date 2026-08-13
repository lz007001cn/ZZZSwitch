using System.Text.RegularExpressions;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed partial class GameDirectoryService
{
    private static readonly (string Name, bool Directory)[] Markers =
    [
        ("ZenlessZoneZero.exe", false),
        ("version_info", false),
        ("ZenlessZoneZero_Data", true),
        ("GameAssembly.dll", false)
    ];

    public GameDirectoryResult Validate(string gamePath)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            issues.Add(new(IssueSeverity.Error, "game.directory.missing", "游戏目录不存在。", gamePath));
            return new() { GamePath = gamePath ?? string.Empty, IsValid = false, Issues = issues };
        }

        foreach (var marker in Markers)
        {
            var path = Path.Combine(gamePath, marker.Name);
            var exists = marker.Directory ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
            {
                issues.Add(new(IssueSeverity.Error, "game.marker.missing", $"缺少客户端标记：{marker.Name}", path));
            }
        }

        string? version = null;
        var versionPath = Path.Combine(gamePath, "version_info");
        if (File.Exists(versionPath))
        {
            var content = File.ReadAllText(versionPath);
            version = VersionRegex().Match(content).Value;
            if (string.IsNullOrWhiteSpace(version))
            {
                issues.Add(new(IssueSeverity.Error, "game.version.invalid", "无法从 version_info 读取版本号。", versionPath));
            }
        }

        return new()
        {
            GamePath = Path.GetFullPath(gamePath),
            IsValid = issues.All(x => x.Severity != IssueSeverity.Error),
            GameVersion = version,
            Issues = issues
        };
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
