using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Read-only decoder for non-interlaced RGB8 and RGBA8 PNG evidence.
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

    public static BgraFrame Read(Stream stream, long captureId = 0) => Read(stream, captureId, out _);

    internal static BgraFrame Read(Stream stream, long captureId, out bool hasSrgbProfile)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("PNG source stream is invalid.");
        }
        stream.Position = 0;
        var encoded = new byte[checked((int)stream.Length)];
        stream.ReadExactly(encoded);
        return Decode(encoded, captureId, DateTimeOffset.UnixEpoch,
            minimumDimension: 1, maximumDimension: int.MaxValue, out hasSrgbProfile);
    }

    internal static BgraFrame Decode(ReadOnlySpan<byte> encoded, long captureId, DateTimeOffset observedAt,
        int minimumDimension, int maximumDimension, out bool hasSrgbProfile)
    {
        if (encoded.Length < Signature.Length
            || !encoded[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("PNG signature is invalid.");
        }

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var sourceChannels = 0;
        int? transparentRgb = null;
        var sawHeader = false;
        var sawEnd = false;
        var declaredSrgb = false;
        var hasOtherProfile = false;
        using var compressed = new MemoryStream();
        while (offset < encoded.Length)
        {
            if (encoded.Length - offset < 12)
            {
                throw new InvalidDataException("PNG chunk is truncated.");
            }
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(
                encoded.Slice(offset, 4));
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
            var type = encoded.Slice(offset, 4);
            offset += 4;
            var data = encoded.Slice(offset, length);
            offset += length;
            var actualCrc = BinaryPrimitives.ReadUInt32BigEndian(
                encoded.Slice(offset, 4));
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
                if (width < minimumDimension || width > maximumDimension
                    || height < minimumDimension || height > maximumDimension
                    || data[8] != 8 || data[9] is not (2 or 6)
                    || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    throw new InvalidDataException("PNG dimensions or format are unsupported. Expected non-interlaced RGB8 or RGBA8.");
                }
                sourceChannels = data[9] == 2 ? 3 : 4;
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
            else if (chunkType == "sRGB" && sawHeader && compressed.Length == 0 && data is [<= 3])
            {
                declaredSrgb = true;
            }
            else if (chunkType == "tRNS")
            {
                if (sourceChannels != 3 || compressed.Length != 0 || transparentRgb.HasValue
                    || data is not [0, _, 0, _, 0, _])
                    throw new InvalidDataException("PNG RGB transparency is invalid.");
                transparentRgb = PackRgb(data[1], data[3], data[5]);
            }
            else if (chunkType is "iCCP" or "cICP")
            {
                hasOtherProfile = true;
            }
        }
        if (!sawHeader || !sawEnd || compressed.Length == 0)
        {
            throw new InvalidDataException("PNG is incomplete.");
        }

        var stride = checked(width * BgraFrame.BytesPerPixel);
        var sourceStride = checked(width * sourceChannels);
        var bgra = new byte[checked(stride * height)];
        var scanline = new byte[checked(sourceStride + 1)];
        var previous = new byte[scanline.Length];
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        for (var y = 0; y < height; y++)
        {
            if (inflater.ReadAtLeast(scanline, scanline.Length, false) != scanline.Length)
                throw new InvalidDataException("PNG pixel data length is invalid.");
            var filter = scanline[0];
            if (filter > 4)
            {
                throw new InvalidDataException("PNG filter is invalid.");
            }
            for (var x = 1; x < scanline.Length; x++)
            {
                var hasLeft = x > sourceChannels;
                var left = hasLeft ? scanline[x - sourceChannels] : (byte)0;
                var above = previous[x];
                var upperLeft = hasLeft ? previous[x - sourceChannels] : (byte)0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Paeth(left, above, upperLeft),
                    _ => throw new InvalidDataException("PNG filter is invalid.")
                };
                scanline[x] = unchecked((byte)(scanline[x] + predictor));
            }
            for (int source = 1, target = y * stride; source < scanline.Length;
                source += sourceChannels, target += BgraFrame.BytesPerPixel)
            {
                var red = scanline[source];
                var green = scanline[source + 1];
                var blue = scanline[source + 2];
                bgra[target] = blue;
                bgra[target + 1] = green;
                bgra[target + 2] = red;
                bgra[target + 3] = sourceChannels == 4 ? scanline[source + 3]
                    : transparentRgb == PackRgb(red, green, blue) ? (byte)0 : (byte)255;
            }
            (scanline, previous) = (previous, scanline);
        }
        if (inflater.ReadByte() != -1)
            throw new InvalidDataException("PNG pixel data length is invalid.");
        hasSrgbProfile = declaredSrgb && !hasOtherProfile;
        return new BgraFrame(
            width,
            height,
            stride,
            bgra,
            captureId,
            observedAt);
    }

    private static int PackRgb(byte red, byte green, byte blue) => (red << 16) | (green << 8) | blue;

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
