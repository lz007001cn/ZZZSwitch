using System.Buffers;
using System.Security.Cryptography;

namespace ZZZSwitch.Core.Services;

public readonly record struct VerifiedFileCopyResult(
    long BytesCopied,
    string Sha256);

public sealed class VerifiedFileTransfer
{
    private const int BufferSize = 1024 * 1024;
    private readonly IFileOperations _files;
    private readonly FileIntegrityService _integrity;

    public VerifiedFileTransfer(IFileOperations files)
    {
        _files = files;
        _integrity = new FileIntegrityService(files);
    }

    public VerifiedFileCopyResult CopyAndVerify(
        string source,
        string target,
        bool overwrite,
        long? expectedLength = null,
        string? expectedSha256 = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copied = 0;
        string sourceSha256;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var input = _files.OpenRead(source);
            using var output = _files.OpenWrite(target, overwrite);
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                copied = checked(copied + read);
            }

            output.Flush();
            sourceSha256 = Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var requiredLength = expectedLength ?? copied;
        var requiredSha256 = string.IsNullOrWhiteSpace(expectedSha256)
            ? sourceSha256
            : expectedSha256;
        if (copied != requiredLength ||
            !string.Equals(sourceSha256, requiredSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"复制源文件完整性不匹配：{source}");
        }

        var destinationIntegrity = _integrity.Validate(target, requiredLength, requiredSha256);
        if (!destinationIntegrity.IsValid)
        {
            throw new IOException($"复制目标完整性校验失败：{target}；{destinationIntegrity.Message}");
        }

        return new(copied, sourceSha256);
    }
}
