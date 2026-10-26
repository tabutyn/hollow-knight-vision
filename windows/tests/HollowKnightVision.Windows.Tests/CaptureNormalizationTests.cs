using HollowKnightVision.Windows.Core;

internal static class CaptureNormalizationTests
{
    public static void TestPixelsMatchBilinearReference()
    {
        var random = new Random(412);
        foreach (var (width, height, outputWidth, outputHeight) in new[]
        {
            (1280, 720, 640, 360), (1294, 720, 640, 360), (1280, 734, 640, 360),
            (640, 360, 640, 360), (654, 360, 640, 360), (640, 374, 640, 360),
            (1920, 1080, 640, 360), (801, 603, 123, 67), (1, 1, 4, 3)
        })
        {
            var stride = width * 4 + 12;
            var pixels = new byte[stride * height]; random.NextBytes(pixels);
            var source = new BgraFrame(width, height, stride, pixels, 37, DateTimeOffset.UnixEpoch);
            var actual = CpuBgraNormalizer.Normalize(source, outputWidth, outputHeight);
            var crop = FrameGeometry.CenterCrop(width, height, outputWidth, outputHeight);
            for (var y = 0; y < outputHeight; y++)
            for (var x = 0; x < outputWidth; x++)
            {
                var sx = Math.Clamp(crop.X + (x + .5) * crop.Width / outputWidth - .5, crop.X, crop.Right - 1d);
                var sy = Math.Clamp(crop.Y + (y + .5) * crop.Height / outputHeight - .5, crop.Y, crop.Bottom - 1d);
                var x0 = (int)Math.Floor(sx); var x1 = Math.Min(crop.Right - 1, x0 + 1);
                var y0 = (int)Math.Floor(sy); var y1 = Math.Min(crop.Bottom - 1, y0 + 1);
                for (var c = 0; c < 4; c++)
                {
                    var a = pixels[y0 * stride + x0 * 4 + c]; var b = pixels[y0 * stride + x1 * 4 + c];
                    var d = pixels[y1 * stride + x0 * 4 + c]; var e = pixels[y1 * stride + x1 * 4 + c];
                    var top = a + (b - a) * (sx - x0); var bottom = d + (e - d) * (sx - x0);
                    var expected = (byte)Math.Round(top + (bottom - top) * (sy - y0));
                    if (actual.Pixels[(y * outputWidth + x) * 4 + c] != expected)
                        throw new Exception($"Capture normalization differs at {width}x{height}, {x},{y}, channel {c}.");
                }
            }
            if (ReferenceEquals(actual.Pixels, pixels) || actual.CaptureId != 37 || actual.ObservedAt != source.ObservedAt)
                throw new Exception("Capture normalization lost frame ownership or identity.");
        }
    }
}
