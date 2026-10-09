namespace HollowKnightVision.Windows.Core;

public static class CpuBgraNormalizer
{
    public static BgraFrame Normalize(
        BgraFrame source,
        int outputWidth = FrameGeometry.ReferenceWidth,
        int outputHeight = FrameGeometry.ReferenceHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        var crop = FrameGeometry.CenterCrop(
            source.Width,
            source.Height,
            outputWidth,
            outputHeight);
        var outputStride = checked(outputWidth * BgraFrame.BytesPerPixel);
        var output = new byte[checked(outputStride * outputHeight)];

        var x0 = new int[outputWidth];
        var x1 = new int[outputWidth];
        var xWeight = new double[outputWidth];
        for (var x = 0; x < outputWidth; x++)
        {
            var sourceX = crop.X + ((x + 0.5) * crop.Width / outputWidth) - 0.5;
            sourceX = Math.Clamp(sourceX, crop.X, crop.Right - 1.0);
            x0[x] = (int)Math.Floor(sourceX);
            x1[x] = Math.Min(crop.Right - 1, x0[x] + 1);
            xWeight[x] = sourceX - x0[x];
        }

        for (var y = 0; y < outputHeight; y++)
        {
            var sourceY = crop.Y + ((y + 0.5) * crop.Height / outputHeight) - 0.5;
            sourceY = Math.Clamp(sourceY, crop.Y, crop.Bottom - 1.0);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(crop.Bottom - 1, y0 + 1);
            var yWeight = sourceY - y0;
            var row0 = checked(y0 * source.Stride);
            var row1 = checked(y1 * source.Stride);
            var outputRow = checked(y * outputStride);

            for (var x = 0; x < outputWidth; x++)
            {
                var sourceX0 = checked(row0 + x0[x] * BgraFrame.BytesPerPixel);
                var sourceX1 = checked(row0 + x1[x] * BgraFrame.BytesPerPixel);
                var sourceY0 = checked(row1 + x0[x] * BgraFrame.BytesPerPixel);
                var sourceY1 = checked(row1 + x1[x] * BgraFrame.BytesPerPixel);
                var destination = checked(outputRow + x * BgraFrame.BytesPerPixel);
                for (var channel = 0; channel < BgraFrame.BytesPerPixel; channel++)
                {
                    var top = Lerp(
                        source.Pixels[sourceX0 + channel],
                        source.Pixels[sourceX1 + channel],
                        xWeight[x]);
                    var bottom = Lerp(
                        source.Pixels[sourceY0 + channel],
                        source.Pixels[sourceY1 + channel],
                        xWeight[x]);
                    output[destination + channel] = (byte)Math.Clamp(
                        (int)Math.Round(Lerp(top, bottom, yWeight)),
                        byte.MinValue,
                        byte.MaxValue);
                }
            }
        }

        return new BgraFrame(
            outputWidth,
            outputHeight,
            outputStride,
            output,
            source.CaptureId,
            source.ObservedAt);
    }

    private static double Lerp(double left, double right, double amount) =>
        left + (right - left) * amount;
}
