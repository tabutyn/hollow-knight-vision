using System.Buffers.Binary;
using System.IO.Compression;

namespace HollowKnightVision.Windows.Core;

/// <summary>Dependency-free, CPU-only PNG writer for captured BGRA frames.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [
        137, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10
    ];

    public static void Write(BgraFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(Signature);
                Span<byte> header = stackalloc byte[13];
                BinaryPrimitives.WriteUInt32BigEndian(header[..4], checked((uint)frame.Width));
                BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), checked((uint)frame.Height));
                header[8] = 8;
                header[9] = 6; // RGBA
                WriteChunk(stream, "IHDR"u8, header);

                using var compressed = new MemoryStream();
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                {
                    var scanline = new byte[checked(1 + frame.Width * BgraFrame.BytesPerPixel)];
                    for (var y = 0; y < frame.Height; y++)
                    {
                        scanline[0] = 0;
                        var row = frame.Row(y);
                        for (var x = 0; x < frame.Width; x++)
                        {
                            var source = x * BgraFrame.BytesPerPixel;
                            var target = 1 + source;
                            scanline[target] = row[source + 2];
                            scanline[target + 1] = row[source + 1];
                            scanline[target + 2] = row[source];
                            scanline[target + 3] = row[source + 3];
                        }
                        zlib.Write(scanline);
                    }
                }
                WriteChunk(stream, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
                WriteChunk(stream, "IEND"u8, []);
            }
            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
            throw;
        }
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        var crc = 0xffffffffu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data) ^ 0xffffffffu;
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        stream.Write(checksum);
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
