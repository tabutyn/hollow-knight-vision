namespace HollowKnightVision.Windows.Core;

/// <summary>Uses a narrower kernel to refine the ends of a measured ground line.</summary>
internal sealed class GroundEdgeExtent(GroundComparisonAnalysis analysis)
{
    private int cachedRow = -1;
    private int[]? prefix;

    internal IReadOnlyList<PixelSpan> Refine(IReadOnlyList<PixelSpan> spans, int row, int threshold)
    {
        if (spans.Count == 0 || analysis.SourceLuma is not { } luma
            || row < 4 || row + 4 >= analysis.Height) return spans;

        var width = analysis.Width;
        var valid = analysis.ValidSourcePixels;
        var sums = prefix ??= new int[width + 1];
        if (cachedRow != row)
        {
            cachedRow = row;
            var weights = GroundLineDetector.ComparisonWeights;
            for (var x = 0; x < width; x++)
            {
                var response = 0;
                for (var dy = 0; dy < weights.Length; dy++)
                {
                    var i = (row + dy - 4) * width + x;
                    if (valid?[i] == 0 || valid?[i + width] == 0)
                    {
                        response = 0;
                        break;
                    }
                    response += weights[dy] * Math.Abs(luma[i] - luma[i + width]);
                }
                sums[x + 1] = sums[x] + response;
            }
        }

        bool Strong(int x)
        {
            if (valid?[row * width + x] == 0) return false;
            var low = Math.Max(0, x - 3);
            var high = Math.Min(width, x + 4);
            return sums[high] - sums[low] >= 10 * threshold * (high - low);
        }

        int Edge(PixelSpan span, bool left)
        {
            const int radius = GroundLineDetector.ComparisonKernelSpan / 2;
            var end = left ? span.Start : span.EndInclusive;
            var other = left ? span.EndInclusive : span.Start;
            var direction = left ? -1 : 1;
            var x = end;
            while (!Strong(x))
            {
                if (x == other || Math.Abs(x - end) == radius) return end;
                x -= direction;
            }

            var next = x + direction;
            while ((uint)next < (uint)width && Math.Abs(next - end) <= radius && Strong(next))
            {
                x = next;
                next += direction;
            }

            // Snap past the narrow window's blur to the nearest measured column.
            for (var offset = 0; offset <= 3; offset++)
            {
                var column = x - direction * offset;
                if ((uint)column >= (uint)width || Math.Abs(column - end) > radius
                    || (left ? column > other : column < other)) break;
                if (sums[column + 1] - sums[column] >= 5 * threshold) return column;
            }
            return x;
        }

        var first = spans[0];
        var last = spans[^1];
        var start = Edge(first, left: true);
        var end = Edge(last, left: false);
        if (start > end) return spans;
        if (spans.Count == 1) return [new(start, end)];
        var result = spans.ToArray();
        result[0] = new(start, first.EndInclusive);
        result[^1] = new(last.Start, end);
        return result;
    }
}
