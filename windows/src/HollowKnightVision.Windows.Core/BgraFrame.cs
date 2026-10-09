namespace HollowKnightVision.Windows.Core;

public sealed class BgraFrame
{
    public const int BytesPerPixel = 4;

    public BgraFrame(
        int width,
        int height,
        int stride,
        byte[] pixels,
        long captureId,
        DateTimeOffset observedAt)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        var minimumStride = checked(width * BytesPerPixel);
        if (stride < minimumStride) throw new ArgumentOutOfRangeException(nameof(stride));
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength < checked((long)stride * height))
        {
            throw new ArgumentException("Pixel storage is shorter than stride × height.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
        CaptureId = captureId;
        ObservedAt = observedAt;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }
    public long CaptureId { get; }
    public DateTimeOffset ObservedAt { get; }

    public ReadOnlySpan<byte> Row(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return Pixels.AsSpan(checked(y * Stride), checked(Width * BytesPerPixel));
    }
}
