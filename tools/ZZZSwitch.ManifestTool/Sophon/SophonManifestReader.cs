using System.Text;
using ZstdSharp;
using ZZZSwitch.ManifestTool.Diff;

namespace ZZZSwitch.ManifestTool.Sophon;

public sealed class SophonManifestReader
{
    private const int MaximumCompressedBytes = 256 * 1024 * 1024;
    private const int MaximumDecompressedBytes = 1024 * 1024 * 1024;
    private readonly ISophonTransport _transport;
    private readonly Action<string>? _verbose;

    public SophonManifestReader(ISophonTransport transport, Action<string>? verbose = null)
    {
        _transport = transport;
        _verbose = verbose;
    }

    public async Task<ManifestSnapshot> DownloadAsync(
        SophonRegion region,
        string version,
        ManifestCategory category,
        CancellationToken cancellationToken = default)
    {
        var uri = SophonClient.BuildManifestUri(category);
        _verbose?.Invoke($"manifest URL: {HttpSophonTransport.Redact(uri)}");
        var compressed = await _transport.GetBytesAsync(uri, cancellationToken).ConfigureAwait(false);
        return ParseCompressed(compressed, region, version, category);
    }

    public ManifestSnapshot ParseCompressed(
        ReadOnlyMemory<byte> compressed,
        SophonRegion region,
        string version,
        ManifestCategory category)
    {
        if (compressed.IsEmpty)
        {
            throw new InvalidDataException("Downloaded Sophon manifest is empty.");
        }

        if (compressed.Length > MaximumCompressedBytes)
        {
            throw new InvalidDataException(
                $"Compressed Sophon manifest exceeds {MaximumCompressedBytes:N0} bytes.");
        }

        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var zstd = new DecompressionStream(input);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = zstd.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumDecompressedBytes)
                {
                    throw new InvalidDataException(
                        $"Decompressed Sophon manifest exceeds {MaximumDecompressedBytes:N0} bytes.");
                }

                output.Write(buffer, 0, read);
            }

            return ParseProtobuf(output.ToArray(), region, version, category);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Sophon Zstd decompression failed: {ex.Message}", ex);
        }
    }

    public static ManifestSnapshot ParseProtobuf(
        ReadOnlySpan<byte> protobuf,
        SophonRegion region,
        string version,
        ManifestCategory category)
    {
        try
        {
            var entries = new List<ManifestEntry>();
            var reader = new ProtoReader(protobuf);
            while (!reader.End)
            {
                var tag = reader.ReadTag();
                var field = (int)(tag >> 3);
                var wire = (int)(tag & 7);
                if (field == 1 && wire == 2)
                {
                    entries.Add(ParseAsset(reader.ReadLengthDelimited()));
                }
                else
                {
                    reader.Skip(wire);
                }
            }

            if (entries.Count == 0)
            {
                throw new InvalidDataException("Sophon protobuf manifest contains no assets.");
            }

            return new ManifestSnapshot(
                SophonRegionConfig.Game,
                region,
                version,
                category.CategoryId,
                category.ManifestId,
                DateTimeOffset.UtcNow,
                entries);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Sophon protobuf parsing failed: {ex.Message}", ex);
        }
    }

    private static ManifestEntry ParseAsset(ReadOnlySpan<byte> bytes)
    {
        string? path = null;
        string? md5 = null;
        long size = 0;
        var chunks = new List<ManifestChunk>();
        var reader = new ProtoReader(bytes);
        while (!reader.End)
        {
            var tag = reader.ReadTag();
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (field, wire)
            {
                case (1, 2):
                    path = DecodeUtf8(reader.ReadLengthDelimited(), "asset name");
                    break;
                case (2, 2):
                    chunks.Add(ParseChunk(reader.ReadLengthDelimited()));
                    break;
                case (4, 0):
                    var rawSize = reader.ReadVarint();
                    size = rawSize <= long.MaxValue
                        ? (long)rawSize
                        : throw new InvalidDataException("Asset size exceeds Int64.MaxValue.");
                    break;
                case (5, 2):
                    md5 = DecodeUtf8(reader.ReadLengthDelimited(), "asset MD5");
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (path is null || md5 is null)
        {
            throw new InvalidDataException("A Sophon asset is missing path, size, or MD5.");
        }

        return new ManifestEntry(path, size, md5, chunks.Count, chunks);
    }

    private static ManifestChunk ParseChunk(ReadOnlySpan<byte> bytes)
    {
        string? name = null;
        string? decompressedMd5 = null;
        long fileOffset = 0;
        long compressedSize = 0;
        long decompressedSize = 0;
        var reader = new ProtoReader(bytes);
        while (!reader.End)
        {
            var tag = reader.ReadTag();
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            switch (field, wire)
            {
                case (1, 2):
                    name = DecodeUtf8(reader.ReadLengthDelimited(), "chunk name");
                    break;
                case (2, 2):
                    decompressedMd5 = DecodeUtf8(
                        reader.ReadLengthDelimited(), "chunk decompressed MD5");
                    break;
                case (3, 0):
                    fileOffset = ToInt64(reader.ReadVarint(), "chunk file offset");
                    break;
                case (4, 0):
                    compressedSize = ToInt64(reader.ReadVarint(), "chunk compressed size");
                    break;
                case (5, 0):
                    decompressedSize = ToInt64(reader.ReadVarint(), "chunk decompressed size");
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (name is null || decompressedMd5 is null)
        {
            throw new InvalidDataException("A Sophon chunk is missing required metadata.");
        }

        return new ManifestChunk(
            name, decompressedMd5, fileOffset, compressedSize, decompressedSize);
    }

    private static long ToInt64(ulong value, string field) => value <= long.MaxValue
        ? (long)value
        : throw new InvalidDataException($"Sophon protobuf {field} exceeds Int64.MaxValue.");

    private static string DecodeUtf8(ReadOnlySpan<byte> value, string field)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(value);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"Sophon protobuf {field} is not valid UTF-8.", ex);
        }
    }

    private ref struct ProtoReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _offset;

        public ProtoReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _offset = 0;
        }

        public bool End => _offset == _buffer.Length;

        public uint ReadTag()
        {
            var tag = ReadVarint();
            if (tag is 0 or > uint.MaxValue)
            {
                throw new InvalidDataException("Sophon protobuf contains an invalid tag.");
            }

            return (uint)tag;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                EnsureAvailable(1);
                var value = _buffer[_offset++];
                result |= (ulong)(value & 0x7f) << shift;
                if ((value & 0x80) == 0)
                {
                    return result;
                }
            }

            throw new InvalidDataException("Sophon protobuf contains an oversized varint.");
        }

        public ReadOnlySpan<byte> ReadLengthDelimited()
        {
            var rawLength = ReadVarint();
            if (rawLength > int.MaxValue)
            {
                throw new InvalidDataException("Sophon protobuf field is too large.");
            }

            var length = (int)rawLength;
            EnsureAvailable(length);
            var result = _buffer.Slice(_offset, length);
            _offset += length;
            return result;
        }

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    ReadVarint();
                    return;
                case 1:
                    Advance(sizeof(long));
                    return;
                case 2:
                    ReadLengthDelimited();
                    return;
                case 5:
                    Advance(sizeof(int));
                    return;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
            }
        }

        private void Advance(int count)
        {
            EnsureAvailable(count);
            _offset += count;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || _offset > _buffer.Length - count)
            {
                throw new InvalidDataException("Sophon protobuf ended unexpectedly.");
            }
        }
    }
}
