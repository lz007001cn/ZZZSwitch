using System.Text.Json.Serialization;

namespace ZZZSwitch.ManifestTool.Sophon;

public sealed record GameBranchCatalog(
    SophonRegion Region,
    IReadOnlyList<GameBranch> Branches);

public sealed record GameBranch(
    string GameId,
    BranchPackage Main,
    BranchPackage? PreDownload);

public sealed class BranchPackage
{
    public BranchPackage(string packageId, string password, string version)
    {
        PackageId = packageId;
        Password = password;
        Version = version;
    }

    public string PackageId { get; }

    [JsonIgnore]
    public string Password { get; }

    public string Version { get; }
    public bool HasPassword => !string.IsNullOrWhiteSpace(Password);
}

public sealed record SophonBuild(
    SophonRegion Region,
    string RequestedVersion,
    string Version,
    IReadOnlyList<ManifestCategory> Manifests);

public sealed record ManifestCategory(
    string CategoryId,
    string MatchingField,
    string ManifestId,
    string ManifestUrlPrefix,
    string ManifestUrlSuffix,
    string ChunkUrlPrefix,
    string ChunkUrlSuffix);

public sealed class SophonApiException : Exception
{
    public SophonApiException(string operation, int? retCode, string? apiMessage)
        : base(BuildMessage(operation, retCode, apiMessage))
    {
        RetCode = retCode;
    }

    public int? RetCode { get; }

    private static string BuildMessage(string operation, int? retCode, string? apiMessage)
    {
        var code = retCode.HasValue ? $" (retcode {retCode.Value})" : string.Empty;
        var message = string.IsNullOrWhiteSpace(apiMessage) ? string.Empty : $": {apiMessage}";
        return $"Sophon {operation} failed{code}{message}";
    }
}
