using System.Diagnostics;
using HollowKnightVision.Windows.Core;

internal static class GroundDetectionEfficiencyTests
{
    public static void TestRasterizedMasksArePixelExactIncludingClippedAndOffscreenRectangles()
    {
        foreach (var (width, height) in new[] { (2, 2), (32, 8), (101, 77), (640, 360) })
        {
            var frame = Image(width, height);
            IReadOnlyList<PixelRect>[] maskSets =
            [
                [],
                [new PixelRect(0, 0, 0, 0)],
                [new PixelRect(-80, -30, 65, 47)],
                [new PixelRect(37, 15, 11, 5), new PixelRect(46, 25, 23, 31)],
                [new PixelRect(0, 0, width, height)]
            ];
            foreach (var masks in maskSets)
            {
                var actual = GroundLineDetector.Analyze(frame, masks);
                Check(actual.GroundEdgePixels.SequenceEqual(ReferenceEdges(frame, masks)),
                    $"Rasterized edge mask differs at {width}x{height}.");
            }
        }
    }

    public static void TestPairedKernelIsPixelExactForNoiseAndBorders()
    {
        foreach (var (width, height) in new[] { (8, 4), (32, 8), (33, 9), (97, 67), (640, 360) })
        {
            var pixels = Enumerable.Range(0, checked(width * height))
                .Select(index => unchecked((byte)(index * 1237 ^ index >> 3)))
                .ToArray();
            var input = new GroundEdgeAnalysis(width, height, pixels);
            var actual = GroundLineDetector.GroundDetectPixels(input);
            var expected = ReferenceKernel(input);
            var difference = Enumerable.Range(0, actual.Length)
                .FirstOrDefault(index => actual[index] != expected[index], -1);
            Check(difference < 0,
                $"Paired kernel differs at {width}x{height}, index {difference}: "
                + $"actual={actual[Math.Max(0, difference)]}, expected={expected[Math.Max(0, difference)]}.");
        }
    }

    public static void TestReleaseEdgeAndKernelTimingAtCaptureResolution()
    {
        var frame = Image(640, 360);
        PixelRect[] masks =
        [
            new(12, 15, 160, 45),
            new(280, 245, 40, 50)
        ];
        var referenceTimes = new List<double>();
        var productionTimes = new List<double>();
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var timer = Stopwatch.StartNew();
            var expected = ReferenceKernel(new GroundEdgeAnalysis(
                frame.Width,
                frame.Height,
                ReferenceEdges(frame, masks)));
            timer.Stop();
            referenceTimes.Add(timer.Elapsed.TotalMilliseconds);

            timer.Restart();
            var actual = GroundLineDetector.GroundDetectPixels(
                GroundLineDetector.Analyze(frame, masks));
            timer.Stop();
            productionTimes.Add(timer.Elapsed.TotalMilliseconds);
            Check(actual.SequenceEqual(expected), "Optimized edge/kernel output differs from oracle.");
        }
        referenceTimes.Sort();
        productionTimes.Sort();
        Console.WriteLine(
            $"GROUND_EDGE_KERNEL_640x360_MS referenceMedian={referenceTimes[6]:F3} productionMedian={productionTimes[6]:F3}");
    }

    private static BgraFrame Image(int width, int height)
    {
        var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
        for (var index = 0; index < width * height; index++)
        {
            var value = unchecked((byte)(index * 73 ^ index / width * 131));
            var offset = index * BgraFrame.BytesPerPixel;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(width, height, width * BgraFrame.BytesPerPixel,
            pixels, 1, DateTimeOffset.UnixEpoch);
    }

    private static byte[] ReferenceEdges(BgraFrame frame, IReadOnlyList<PixelRect> excludedRects)
    {
        var width = frame.Width;
        var height = frame.Height;
        var luma = new byte[checked(width * height)];
        for (var row = 0; row < height; row++)
        {
            var source = frame.Row(row);
            for (var x = 0; x < width; x++)
            {
                var offset = x * BgraFrame.BytesPerPixel;
                luma[row * width + x] = (byte)((19 * source[offset]
                    + 183 * source[offset + 1]
                    + 54 * source[offset + 2]
                    + 128) >> 8);
            }
        }
        var result = new byte[luma.Length];
        for (var index = 0; index < width * (height - 1); index++)
        {
            result[index] = (byte)Math.Abs(luma[index] - luma[index + width]);
        }
        foreach (var rect in excludedRects)
        {
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var left = Math.Clamp((long)rect.X - 24, 0, width);
            var right = Math.Clamp((long)rect.Right + 24, 0, width);
            var top = Math.Clamp((long)rect.Y - 12, 0, height);
            var bottom = Math.Clamp((long)rect.Bottom + 12, 0, height);
            if (left >= right || top >= bottom) continue;
            for (var row = (int)top; row < bottom; row++)
            {
                Array.Clear(result, row * width + (int)left, (int)(right - left));
            }
        }
        return result;
    }

    private static byte[] ReferenceKernel(GroundEdgeAnalysis input)
    {
        var width = input.Width;
        var height = input.Height;
        var output = new byte[checked(width * height)];
        if (width < 32 || height < 8) return output;
        var sums = new int[output.Length];
        for (var row = 0; row < height; row++)
        {
            var offset = row * width;
            var sum = input.GroundEdgePixels.AsSpan(offset, 32).ToArray().Sum(value => (int)value);
            for (var x = 16; x <= width - 16; x++)
            {
                sums[offset + x] = sum;
                if (x < width - 16)
                {
                    sum -= input.GroundEdgePixels[offset + x - 16];
                    sum += input.GroundEdgePixels[offset + x + 16];
                }
            }
        }
        int[] weights = [-1, -2, -3, -4, 4, 3, 2, 1];
        for (var row = 4; row <= height - 4; row++)
        {
            for (var x = 16; x <= width - 16; x++)
            {
                var response = 0;
                for (var index = 0; index < weights.Length; index++)
                {
                    response += weights[index] * sums[(row - 4 + index) * width + x];
                }
                if (response > 0)
                {
                    output[row * width + x] = (byte)Math.Clamp(response / 320, 0, 255);
                }
            }
        }
        return output;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
