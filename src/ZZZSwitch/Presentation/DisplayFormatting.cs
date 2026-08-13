using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Presentation;

internal static class DisplayFormatting
{
    public static string ShortProfileName(string profileId) => profileId switch
    {
        ProfileIds.Global => "国际服",
        ProfileIds.CnOfficial => "国服",
        ProfileIds.Bilibili => "B服",
        _ => profileId
    };

    public static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.00} GiB"
            : $"{bytes / (1024d * 1024):0.0} MiB";
}
