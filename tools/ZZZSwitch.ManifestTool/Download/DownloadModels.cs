using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool.Download;

public sealed record DownloadedManifestFile(
    string Path,
    long Length,
    string SophonMd5,
    string Sha256,
    bool ReusedExistingFile);

public sealed record SophonFileDownloadProgress(
    string Path,
    long FileBytesCompleted,
    long FileBytesTotal,
    int ChunksCompleted,
    int ChunksTotal,
    bool ReusedExistingFile,
    long CurrentChunkBytesDownloaded = 0,
    long CurrentChunkBytesTotal = 0,
    bool ReusedChunkCache = false,
    bool VerifyingExistingFile = false,
    int DownloadAttempt = 1,
    int MaximumDownloadAttempts = 1);

public sealed record SophonDownloadReport(
    string Game,
    SophonRegion Region,
    string Version,
    string CategoryId,
    string ManifestId,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<DownloadedManifestFile> Files);

public sealed record DownloadWorkspaceMarker(
    string Tool,
    string Game,
    SophonRegion Region,
    string Version,
    string CategoryId,
    string ManifestId);
