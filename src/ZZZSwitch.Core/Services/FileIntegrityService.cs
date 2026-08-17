using System.Security.Cryptography;

namespace ZZZSwitch.Core.Services;

public enum FileIntegrityStatus
{
    Valid,
    MetadataMissing,
    MetadataInvalid,
    FileMissing,
    LengthMismatch,
    HashMismatch,
    Unreadable
}

public readonly record struct FileIntegrityResult(
    FileIntegrityStatus Status,
    string Message)
{
    public bool IsValid => Status == FileIntegrityStatus.Valid;
}

public sealed class FileIntegrityService
{
    private readonly IFileOperations _files;

    public FileIntegrityService(IFileOperations files) => _files = files;

    public FileIntegrityResult Validate(
        string path,
        long? expectedLength,
        string? expectedSha256)
    {
        if (!expectedLength.HasValue || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return new(
                FileIntegrityStatus.MetadataMissing,
                "清单缺少文件长度或完整性数据。");
        }

        if (expectedLength.Value < 0 || !IsValidSha256(expectedSha256))
        {
            return new(
                FileIntegrityStatus.MetadataInvalid,
                "清单中的文件长度或完整性数据格式无效。");
        }

        if (!_files.FileExists(path))
        {
            return new(FileIntegrityStatus.FileMissing, "文件不存在。");
        }

        try
        {
            var actualLength = _files.GetLength(path);
            if (actualLength != expectedLength.Value)
            {
                return new(
                    FileIntegrityStatus.LengthMismatch,
                    $"文件长度不匹配：应为 {expectedLength.Value:N0}，实际为 {actualLength:N0} 字节。");
            }

            var actualSha256 = ComputeSha256(path);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(FileIntegrityStatus.HashMismatch, "文件完整性不匹配。");
            }

            return new(FileIntegrityStatus.Valid, "完整性校验通过。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(FileIntegrityStatus.Unreadable, $"无法读取文件：{ex.Message}");
        }
    }

    public string ComputeSha256(string path)
    {
        using var stream = _files.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static bool IsValidSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
