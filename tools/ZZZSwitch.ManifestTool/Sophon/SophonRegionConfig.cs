namespace ZZZSwitch.ManifestTool.Sophon;

public enum SophonRegion
{
    OS,
    CN,
    Bilibili
}

public sealed record SophonRegionConfig(
    SophonRegion Region,
    string GameId,
    string LauncherId,
    string PlatformApp,
    Uri BranchesEndpoint,
    Uri BuildEndpoint)
{
    public const string Game = "nap";

    public static SophonRegionConfig For(SophonRegion region) => region switch
    {
        SophonRegion.OS => new(
            region,
            "U5hbdsT9W7",
            "VYTpXlbWo8",
            "ddxf6vlr1reo",
            new Uri("https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches"),
            new Uri("https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild")),
        SophonRegion.CN => new(
            region,
            "x6znKlJ0xK",
            "jGHBHlcOq1",
            "ddxf5qt290cg",
            new Uri("https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches"),
            new Uri("https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild")),
        SophonRegion.Bilibili => throw new NotSupportedException(
            "Bilibili manifest source has not been identified yet."),
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown Sophon region.")
    };
}
