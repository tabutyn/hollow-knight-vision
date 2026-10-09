using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal static class BmpWriter
{
    internal static void Write(BgraFrame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var outputStride = checked(frame.Width * BgraFrame.BytesPerPixel);
        var pixelBytes = checked(outputStride * frame.Height);
        const int fileHeaderBytes = 14;
        const int infoHeaderBytes = 40;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(checked(fileHeaderBytes + infoHeaderBytes + pixelBytes));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(fileHeaderBytes + infoHeaderBytes);
        writer.Write(infoHeaderBytes);
        writer.Write(frame.Width);
        writer.Write(-frame.Height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        for (var y = 0; y < frame.Height; y++)
        {
            writer.Write(frame.Pixels, checked(y * frame.Stride), outputStride);
        }
    }
}
