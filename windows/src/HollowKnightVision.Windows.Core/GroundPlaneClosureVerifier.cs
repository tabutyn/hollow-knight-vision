namespace HollowKnightVision.Windows.Core;

/// <summary>Checks that a visual match also aligns independently observed floor texture.</summary>
internal static class GroundPlaneClosureVerifier
{
    internal sealed record Measurement(
        AtlasPoint CameraPose, int SupportedPatches, double HorizontalSpan, double MeanCorrelation);

    private readonly record struct FloorSpan(double Y, double Left, double Right);
    private sealed record Candidate(double X, double Y, int Step, int Count, double Span, double Correlation, double Score);

    internal static Measurement? Verify(
        BgraFrame reference, IReadOnlyList<LiveGroundLineEvidence> referenceGround, AtlasPoint referencePose,
        BgraFrame current, IReadOnlyList<LiveGroundLineEvidence> currentGround, AtlasPoint proposedPose, double solveWidth)
    {
        if (!double.IsFinite(solveWidth) || solveWidth <= 0
            || !LiveWorldSnapshot.Finite(referencePose) || !LiveWorldSnapshot.Finite(proposedPose)
            || !ValidGround(reference, referenceGround, solveWidth) || !ValidGround(current, currentGround, solveWidth))
            return null;

        var width = Math.Min(640, Math.Min(reference.Width, current.Width));
        var height = ScaledHeight(reference, width);
        if (width < 160 || height < 13 || height != ScaledHeight(current, width)) return null;

        var source = Merge(referenceGround);
        var target = Merge(currentGround);
        var scale = width / solveWidth;
        var unit = solveWidth / 640;
        var dx = proposedPose.X - referencePose.X;
        var dy = proposedPose.Y - referencePose.Y;
        var vertical = source.SelectMany(a => target.Select(b => a.Y - b.Y))
            .Where(shift => Math.Abs(shift - dy) <= 8 * unit)
            .OrderBy(shift => Math.Abs(shift - dy)).ThenBy(shift => shift);
        var shifts = new List<double>(6);
        foreach (var shift in vertical)
        {
            if (shifts.Any(existing => Math.Abs(existing - shift) < 0.25 * unit)) continue;
            shifts.Add(shift);
            if (shifts.Count == 6) break;
        }
        if (shifts.Count == 0) return null;

        var a = Luma(reference, width, height);
        var b = Luma(current, width, height);
        var radius = Math.Max(2, (int)Math.Round(8 * unit * scale, MidpointRounding.AwayFromZero));
        var candidates = new List<Candidate>();
        foreach (var y in shifts)
        for (var step = -radius; step <= radius; step++)
        {
            var x = dx + step / scale;
            if (Evaluate(a, b, width, height, source, target, x, y, step, scale, unit) is { } candidate)
                candidates.Add(candidate);
        }
        var ranked = candidates.OrderByDescending(c => c.Score).ThenBy(c => Math.Abs(c.Step))
            .ThenBy(c => c.Y).ThenBy(c => c.X).ToArray();
        if (ranked.FirstOrDefault() is not { } best || Math.Abs(best.Step) == radius) return null;
        var alternative = ranked.Skip(1).FirstOrDefault(c =>
            Math.Sqrt((c.X - best.X) * (c.X - best.X) + (c.Y - best.Y) * (c.Y - best.Y)) > 3 * unit);
        if (alternative is not null && best.Score - alternative.Score < 0.025) return null;

        return new(new AtlasPoint(referencePose.X + best.X, referencePose.Y + best.Y),
            best.Count, best.Span, best.Correlation);
    }

    private static bool ValidGround(BgraFrame frame, IReadOnlyList<LiveGroundLineEvidence> lines, double width) =>
        lines.Count != 0 && lines.All(line => line.IsInImage(width, frame.Width, frame.Height));

    private static int ScaledHeight(BgraFrame frame, int width) =>
        (int)Math.Round(frame.Height * (double)width / frame.Width, MidpointRounding.AwayFromZero);

    private static byte[] Luma(BgraFrame frame, int width, int height)
    {
        var luma = GroundPixels.Luma(frame)!;
        if (frame.Width == width && frame.Height == height) return luma;
        var resized = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceY = (int)((y + 0.5) * frame.Height / height);
            var sourceX = (int)((x + 0.5) * frame.Width / width);
            resized[y * width + x] = luma[sourceY * frame.Width + sourceX];
        }
        return resized;
    }

    private static IReadOnlyList<FloorSpan> Merge(IReadOnlyList<LiveGroundLineEvidence> geometry)
    {
        var result = new List<FloorSpan>();
        foreach (var line in geometry.OrderBy(line => line.Start.Y).ThenBy(line => line.Start.X))
        {
            if (result.Count != 0 && result[^1] is var last
                && last.Y == line.Start.Y && last.Right >= line.Start.X)
                result[^1] = last with { Right = Math.Max(last.Right, line.End.X) };
            else result.Add(new(line.Start.Y, line.Start.X, line.End.X));
        }
        return result;
    }

    private static Candidate? Evaluate(byte[] a, byte[] b, int width, int height,
        IReadOnlyList<FloorSpan> source, IReadOnlyList<FloorSpan> target, double dx, double dy,
        int step, double scale, double unit)
    {
        var points = new List<AtlasPoint>();
        var stride = 16 / scale;
        var halfWidth = 8 / scale;
        foreach (var first in source)
        foreach (var second in target)
        {
            if (Math.Abs(first.Y - dy - second.Y) > unit) continue;
            var left = Math.Max(first.Left, second.Left + dx) + halfWidth;
            var right = Math.Min(first.Right, second.Right + dx) - halfWidth;
            for (var x = Math.Ceiling(left / stride) * stride; x <= right; x += stride)
                points.Add(new(x, first.Y));
        }
        if (points.Count < 4) return null;

        var selectedCount = Math.Min(points.Count, 16);
        var count = 0;
        var leftmost = double.PositiveInfinity;
        var rightmost = double.NegativeInfinity;
        var sum = 0.0;
        for (var i = 0; i < selectedCount; i++)
        {
            var point = points[(int)Math.Round(i * (points.Count - 1.0) / (selectedCount - 1),
                MidpointRounding.AwayFromZero)];
            var correlation = Correlation(a, b, width, height,
                point.X * scale - 8, height - point.Y * scale - 2,
                (point.X - dx) * scale - 8, height - (point.Y - dy) * scale - 2);
            if (correlation is not >= 0.84) continue;
            sum += correlation.Value;
            count++;
            leftmost = Math.Min(leftmost, point.X);
            rightmost = Math.Max(rightmost, point.X);
        }
        var span = rightmost - leftmost;
        if (count < 4 || count < selectedCount * 0.65 || span < 48 * unit || sum < count * 0.88)
            return null;
        return new(dx, dy, step, count, span + stride, sum / count, sum / selectedCount);
    }

    private static double? Correlation(byte[] a, byte[] b, int width, int height,
        double ax, double ay, double bx, double by)
    {
        if (Math.Min(ax, bx) < 0 || Math.Min(ay, by) < 0
            || Math.Max(ax, bx) + 16 >= width || Math.Max(ay, by) + 12 >= height)
            return null;
        var sumA = 0.0;
        var sumB = 0.0;
        var aa = 0.0;
        var bb = 0.0;
        var ab = 0.0;
        for (var y = 0; y < 12; y++)
        for (var x = 0; x < 16; x++)
        {
            var av = Sample(a, width, ax + x, ay + y);
            var bv = Sample(b, width, bx + x, by + y);
            sumA += av;
            sumB += bv;
            aa += av * av;
            bb += bv * bv;
            ab += av * bv;
        }
        const int count = 16 * 12;
        var varianceA = aa - sumA * sumA / count;
        var varianceB = bb - sumB * sumB / count;
        return varianceA < count * 4 || varianceB < count * 4
            ? null : (ab - sumA * sumB / count) / Math.Sqrt(varianceA * varianceB);
    }

    private static double Sample(byte[] pixels, int width, double x, double y)
    {
        var left = (int)x;
        var top = (int)y;
        var fx = x - left;
        var fy = y - top;
        var i = top * width + left;
        var upper = pixels[i] * (1 - fx) + pixels[i + 1] * fx;
        var lower = pixels[i + width] * (1 - fx) + pixels[i + width + 1] * fx;
        return upper * (1 - fy) + lower * fy;
    }
}
