using System.Runtime.InteropServices;

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

        // Integer-sized captures are common (1280x720 into 640x360). Preserve
        // bilinear center sampling and banker's rounding without floating-point
        // interpolation for every channel. Always return owned pixel storage.
        if (crop.Width == outputWidth && crop.Height == outputHeight)
        {
            for (var y = 0; y < outputHeight; y++)
                source.Pixels.AsSpan((crop.Y + y) * source.Stride + crop.X * 4, outputStride)
                    .CopyTo(output.AsSpan(y * outputStride, outputStride));
            return new(outputWidth, outputHeight, outputStride, output, source.CaptureId, source.ObservedAt);
        }
        if (BitConverter.IsLittleEndian && (long)crop.Width == outputWidth * 2L && (long)crop.Height == outputHeight * 2L)
        {
            const uint channels = 0x00ff00ff;
            const uint rounding = 0x00010001;
            for (var y = 0; y < outputHeight; y++)
            {
                var offset = (crop.Y + y * 2) * source.Stride + crop.X * 4;
                var row0 = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan(offset, outputStride * 2));
                var row1 = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan(offset + source.Stride, outputStride * 2));
                var destination = MemoryMarshal.Cast<byte, uint>(output.AsSpan(y * outputStride, outputStride));
                for (var x = 0; x < outputWidth; x++)
                {
                    var a = row0[x * 2]; var b = row0[x * 2 + 1];
                    var c = row1[x * 2]; var d = row1[x * 2 + 1];
                    // Two independent 16-bit lanes hold four 8-bit channel sums.
                    // Adding 1 plus the quotient's low bit rounds ties to even.
                    var br = (a & channels) + (b & channels) + (c & channels) + (d & channels);
                    var ga = ((a >> 8) & channels) + ((b >> 8) & channels) + ((c >> 8) & channels) + ((d >> 8) & channels);
                    br = ((br + rounding + ((br >> 2) & rounding)) >> 2) & channels;
                    ga = ((ga + rounding + ((ga >> 2) & rounding)) >> 2) & channels;
                    destination[x] = br | (ga << 8);
                }
            }
            return new(outputWidth, outputHeight, outputStride, output, source.CaptureId, source.ObservedAt);
        }

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
