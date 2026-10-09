namespace HollowKnightVision.Windows.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
}

public readonly record struct SourcePoint(double X, double Y);

public readonly record struct CaptureProjection(
    PixelRect SourceCrop,
    int OutputWidth,
    int OutputHeight)
{
    public SourcePoint OutputToSource(double x, double y)
    {
        if (OutputWidth <= 0 || OutputHeight <= 0)
        {
            throw new InvalidOperationException("Projection output dimensions must be positive.");
        }

        return new SourcePoint(
            SourceCrop.X + x * SourceCrop.Width / OutputWidth,
            SourceCrop.Y + y * SourceCrop.Height / OutputHeight);
    }
}

public static class FrameGeometry
{
    public const int ReferenceWidth = 640;
    public const int ReferenceHeight = 360;

    public static PixelRect CenterCrop(
        int sourceWidth,
        int sourceHeight,
        int outputWidth = ReferenceWidth,
        int outputHeight = ReferenceHeight)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (outputWidth <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputHeight));

        var sourceIsWider = checked((long)sourceWidth * outputHeight)
            > checked((long)sourceHeight * outputWidth);
        if (sourceIsWider)
        {
            var width = Math.Max(1, (int)((long)sourceHeight * outputWidth / outputHeight));
            return new PixelRect((sourceWidth - width) / 2, 0, width, sourceHeight);
        }

        var height = Math.Max(1, (int)((long)sourceWidth * outputHeight / outputWidth));
        return new PixelRect(0, (sourceHeight - height) / 2, sourceWidth, height);
    }

    public static CaptureProjection Projection(
        int sourceWidth,
        int sourceHeight,
        int outputWidth = ReferenceWidth,
        int outputHeight = ReferenceHeight) =>
        new(CenterCrop(sourceWidth, sourceHeight, outputWidth, outputHeight), outputWidth, outputHeight);
}
