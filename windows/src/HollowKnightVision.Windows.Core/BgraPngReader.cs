using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Read-only decoder for non-interlaced RGBA8 PNG evidence. Decoding preserves
/// source dimensions and alpha while converting packed RGBA bytes to BGRA.
/// </summary>
public static class BgraPngReader
{
    private static readonly byte[] Signature =
        [137, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];

    public static BgraFrame Read(string path, long captureId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return Read(stream, captureId);
    }

    public static BgraFrame Read(Stream stream, long captureId = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("PNG source stream is invalid.");
        }
        stream.Position = 0;
        var encoded = new byte[checked((int)stream.Length)];
        stream.ReadExactly(encoded);
        if (encoded.Length < Signature.Length
            || !encoded.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("PNG signature is invalid.");
        }

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var compressed = new MemoryStream();
        while (offset < encoded.Length)
        {
            if (encoded.Length - offset < 12)
            {
                throw new InvalidDataException("PNG chunk is truncated.");
            }
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(
                encoded.AsSpan(offset, 4));
            if (lengthValue > int.MaxValue)
            {
                throw new InvalidDataException("PNG chunk is too large.");
            }
            var length = (int)lengthValue;
            offset += 4;
            if (length > encoded.Length - offset - 8)
            {
                throw new InvalidDataException("PNG chunk data is truncated.");
            }
            var type = encoded.AsSpan(offset, 4);
            offset += 4;
            var data = encoded.AsSpan(offset, length);
            offset += length;
            var actualCrc = BinaryPrimitives.ReadUInt32BigEndian(
                encoded.AsSpan(offset, 4));
            offset += 4;
            var crc = UpdateCrc(0xffffffffu, type);
            var expectedCrc = UpdateCrc(crc, data) ^ 0xffffffffu;
            if (actualCrc != expectedCrc)
            {
                throw new InvalidDataException("PNG chunk checksum is invalid.");
            }

            var chunkType = Encoding.ASCII.GetString(type);
            if (chunkType == "IHDR")
            {
                if (sawHeader || data.Length != 13)
                {
                    throw new InvalidDataException("PNG header is invalid.");
                }
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                if (width <= 0 || height <= 0
                    || data[8] != 8 || data[9] != 6
                    || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    throw new InvalidDataException("Only non-interlaced RGBA8 PNG is supported.");
                }
                sawHeader = true;
            }
            else if (chunkType == "IDAT")
            {
                if (!sawHeader || sawEnd)
                {
                    throw new InvalidDataException("PNG data ordering is invalid.");
                }
                compressed.Write(data);
            }
            else if (chunkType == "IEND")
            {
                if (data.Length != 0)
                {
                    throw new InvalidDataException("PNG end chunk is invalid.");
                }
                sawEnd = true;
                break;
            }
        }
        if (!sawHeader || !sawEnd || compressed.Length == 0)
        {
            throw new InvalidDataException("PNG is incomplete.");
        }

        var stride = checked(width * BgraFrame.BytesPerPixel);
        var expectedLength = checked((stride + 1) * height);
        compressed.Position = 0;
        using var raw = new MemoryStream(expectedLength);
        using (var inflater = new ZLibStream(
                   compressed,
                   CompressionMode.Decompress,
                   leaveOpen: true))
        {
            inflater.CopyTo(raw);
        }
        if (raw.Length != expectedLength)
        {
            throw new InvalidDataException("PNG pixel data length is invalid.");
        }

        var filtered = raw.GetBuffer();
        var rgba = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * (stride + 1);
            var filter = filtered[sourceRow];
            if (filter > 4)
            {
                throw new InvalidDataException("PNG filter is invalid.");
            }
            var targetRow = y * stride;
            for (var x = 0; x < stride; x++)
            {
                var encodedByte = filtered[sourceRow + 1 + x];
                var left = x >= 4 ? rgba[targetRow + x - 4] : (byte)0;
                var above = y > 0 ? rgba[targetRow - stride + x] : (byte)0;
                var upperLeft = y > 0 && x >= 4
                    ? rgba[targetRow - stride + x - 4]
                    : (byte)0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Paeth(left, above, upperLeft),
                    _ => throw new InvalidDataException("PNG filter is invalid.")
                };
                rgba[targetRow + x] = unchecked((byte)(encodedByte + predictor));
            }
        }

        var bgra = new byte[rgba.Length];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            bgra[index] = rgba[index + 2];
            bgra[index + 1] = rgba[index + 1];
            bgra[index + 2] = rgba[index];
            bgra[index + 3] = rgba[index + 3];
        }
        return new BgraFrame(
            width,
            height,
            stride,
            bgra,
            captureId,
            DateTimeOffset.UnixEpoch);
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var estimate = left + above - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var aboveDistance = Math.Abs(estimate - above);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }
        return crc;
    }
}
