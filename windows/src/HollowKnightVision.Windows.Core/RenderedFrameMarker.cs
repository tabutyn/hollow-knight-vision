namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Diagnostic association between Unity rendering and captured frames.
/// This value is recorded only; camera/feature algorithms must not consume it.
/// </summary>
public static class RenderedFrameMarker
{
    public static bool Enabled { get; } = Environment.GetCommandLineArgs()
        .Contains("--render-frame-marker", StringComparer.Ordinal);

    public static int? Decode(GroundEdgeAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.SourceLuma is { } pixels
            ? Decode(pixels, analysis.Width, analysis.Height)
            : null;
    }

    /// <summary>
    /// Reads the diagnostic barcode without running any visual tracker. Hacker
    /// uses this only to join a capture to telemetry from the same Unity frame.
    /// </summary>
    public static int? Decode(BgraFrame image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var width = image.Width;
        var height = image.Height;
        if (width < 320 || height < 180)
        {
            return null;
        }

        var scaleX = width / 640d;
        var scaleY = height / 360d;
        ulong word = 0;
        for (var bit = 0; bit < 40; bit++)
        {
            var x = (int)((bit * 3 + 1.5) * scaleX);
            var top = Luma(image, x, (int)(1.5 * scaleY));
            var bottom = Luma(image, x, (int)(4.5 * scaleY));
            if (!ValidPair(top, bottom))
            {
                return null;
            }
            if (top > bottom)
            {
                word |= 1UL << bit;
            }
        }
        return ValidatedFrame(word);
    }

    public static int? Decode(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width < 320 || height < 180
            || pixels.LongLength != (long)width * height)
        {
            return null;
        }

        var scaleX = width / 640d;
        var scaleY = height / 360d;
        ulong word = 0;
        for (var bit = 0; bit < 40; bit++)
        {
            var x = (int)((bit * 3 + 1.5) * scaleX);
            var top = pixels[(int)(1.5 * scaleY) * width + x];
            var bottom = pixels[(int)(4.5 * scaleY) * width + x];
            if (!ValidPair(top, bottom))
            {
                return null;
            }
            if (top > bottom)
            {
                word |= 1UL << bit;
            }
        }
        return ValidatedFrame(word);
    }

    private static bool ValidPair(byte top, byte bottom) =>
        top > 210 && bottom < 45 || top < 45 && bottom > 210;

    private static byte Luma(BgraFrame image, int x, int y)
    {
        var offset = checked(y * image.Stride + x * BgraFrame.BytesPerPixel);
        return (byte)((19 * image.Pixels[offset]
            + 183 * image.Pixels[offset + 1]
            + 54 * image.Pixels[offset + 2]
            + 128) >> 8);
    }

    private static int? ValidatedFrame(ulong word)
    {
        if ((word & 255) != 0xD3)
        {
            return null;
        }
        var frame = (int)((word >> 8) & 0xFFFFFF);
        var checksum = 0x5A
            ^ (frame & 255)
            ^ ((frame >> 8) & 255)
            ^ ((frame >> 16) & 255);
        return (int)(word >> 32) == checksum ? frame : null;
    }
}
