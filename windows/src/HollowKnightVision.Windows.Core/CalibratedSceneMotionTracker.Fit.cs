using System.Buffers;
using System.Numerics;

namespace HollowKnightVision.Windows.Core;

internal sealed partial class CalibratedSceneMotionTracker
{
    internal sealed record Grid(int Width, int Height, float[] Pixels, IReadOnlyList<StencilRect> Exclusions);
    internal readonly record struct PatchFit(CameraVector Motion, double Correlation);
    private readonly record struct Candidate(double Correlation, int X, int Y);
    private const int PatchWidth = 16, PatchHeight = 12, PatchSize = PatchWidth * PatchHeight;
    private const double MinimumCorrelation = .88, MinimumAmbiguityGap = .02, ScoreTolerance = .002;

    internal static CameraVector? RegisterPatch(Grid source, Grid target, AtlasPoint point, CameraVector? expected = null) =>
        FitPatch(source, target, point, expected)?.Motion;

    internal static PatchFit?[] FitBatch(Grid source, Grid target, IReadOnlyList<AtlasPoint> points,
        CameraVector? expected = null, GroundComputeBackend backend = GroundComputeBackend.Automatic,
        ScenePatchAccelerator? testAccelerator = null)
    {
        var fits = new PatchFit?[points.Count];
        var candidatesPerPoint = expected is null ? 17 * 33 : 5 * 5;
        var canAccelerate = points.Count is > 0 and <= 80 && source.Width == 160 && source.Height is >= 24 and <= 256
            && target.Width == source.Width && target.Height == source.Height
            && source.Pixels.Length == source.Width * source.Height && target.Pixels.Length == source.Pixels.Length
            && source.Exclusions.Count <= 128 && target.Exclusions.Count <= 128
            && points.All(point => point.X >= 0 && point.X < source.Width && point.Y >= 0 && point.Y < source.Height)
            && (expected is null || Math.Abs(expected.Value.X) <= source.Width && Math.Abs(expected.Value.Y) <= source.Height);
        var useCuda = backend == GroundComputeBackend.Cuda
            || backend == GroundComputeBackend.Automatic && points.Count * candidatesPerPoint >= 4096;
        if (canAccelerate && (testAccelerator is not null || useCuda && GroundPatchMatcher.Shared.CudaAvailable))
        {
            var count = points.Count * candidatesPerPoint;
            var buffer = ArrayPool<double>.Shared.Rent(count);
            try
            {
                var scores = buffer.AsSpan(0, count);
                var search = new ScenePatchAccelerator.Search(source.Width, source.Height,
                    Round(expected?.X ?? 0), Round(expected?.Y ?? 0), expected is null ? 8 : 2, expected is null ? 16 : 2);
                if (testAccelerator is not null) testAccelerator.Score(source, target, points, search, scores);
                if (testAccelerator is not null || GroundPatchMatcher.TryScoreScene(source, target, points, search, scores))
                {
                    for (var index = 0; index < points.Count; index++)
                        fits[index] = FitPatch(source, target, points[index], expected,
                            scores.Slice(index * candidatesPerPoint, candidatesPerPoint));
                    return fits;
                }
            }
            finally { ArrayPool<double>.Shared.Return(buffer); }
        }
        for (var index = 0; index < points.Count; index++)
            fits[index] = FitPatch(source, target, points[index], expected);
        return fits;
    }

    internal static PatchFit? FitPatch(Grid source, Grid target, AtlasPoint point, CameraVector? expected = null,
        ReadOnlySpan<double> scores = default)
    {
        if (source.Width != target.Width || source.Height != target.Height || source.Width <= 0 || source.Height <= 0
            || source.Pixels.LongLength != (long)source.Width * source.Height
            || target.Pixels.LongLength != source.Pixels.LongLength
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || point.X < 0 || point.Y < 0 || point.X >= source.Width || point.Y >= source.Height)
            return null;
        if (expected is { } displacement
            && (!double.IsFinite(displacement.X) || !double.IsFinite(displacement.Y)
                || Math.Abs(displacement.X) > source.Width || Math.Abs(displacement.Y) > source.Height))
            return null;

        var width = source.Width;
        var height = source.Height;
        var x = Round(point.X);
        var y = Round(point.Y);
        if (x < 9 || x >= width - 9 || y < 7 || y >= height - 7) return null;
        var bounds = new StencilRect(x - PatchWidth / 2, y - PatchHeight / 2, PatchWidth, PatchHeight);
        if (IsExcluded(source.Exclusions, bounds)) return null;

        Span<float> normalized = stackalloc float[PatchSize];
        Span<double> horizontalGradient = stackalloc double[PatchSize];
        Span<double> verticalGradient = stackalloc double[PatchSize];
        var mean = 0d;
        var sample = 0;
        for (var row = y - 6; row < y + 6; row++)
        for (var column = x - 8; column < x + 8; column++)
        {
            var index = row * width + column;
            normalized[sample] = source.Pixels[index];
            mean += normalized[sample];
            horizontalGradient[sample] = (source.Pixels[index + 1] - source.Pixels[index - 1]) * .5;
            verticalGradient[sample] = (source.Pixels[index + width] - source.Pixels[index - width]) * .5;
            sample++;
        }
        mean /= PatchSize;
        var sourceEnergy = 0d;
        for (var index = 0; index < normalized.Length; index++)
        {
            var centered = normalized[index] - mean;
            normalized[index] = (float)centered;
            sourceEnergy += centered * centered;
        }
        if (sourceEnergy < PatchSize * 4) return null;

        var centerX = Round(expected?.X ?? 0);
        var centerY = Round(expected?.Y ?? 0);
        var radiusX = expected is null ? 8 : 2;
        var radiusY = expected is null ? 16 : 2;
        Span<Candidate> candidates = stackalloc Candidate[(2 * radiusX + 1) * (2 * radiusY + 1)];
        var count = 0;
        var scoreIndex = 0;
        var best = new Candidate(double.NegativeInfinity, 0, 0);
        for (var dy = centerY - radiusY; dy <= centerY + radiusY; dy++)
        for (var dx = centerX - radiusX; dx <= centerX + radiusX; dx++)
        {
            double correlation;
            if (!scores.IsEmpty)
            {
                correlation = scores[scoreIndex++];
                if (correlation == ScenePatchAccelerator.RescoreOnCpu) return FitPatch(source, target, point, expected);
                if (!double.IsFinite(correlation) || correlation < -1.1) continue;
            }
            else
            {
                if (x + dx < 8 || x + dx >= width - 8 || y + dy < 6 || y + dy >= height - 6
                    || IsExcluded(target.Exclusions, bounds.Offset(dx, dy))) continue;
                correlation = Correlation(normalized, target.Pixels, width, x + dx, y + dy, sourceEnergy);
                if (double.IsNaN(correlation)) continue;
            }
            var candidate = new Candidate(correlation, dx, dy);
            candidates[count++] = candidate;
            if (correlation > best.Correlation || correlation == best.Correlation
                && (dx > best.X || dx == best.X && dy > best.Y)) best = candidate;
        }
        if (!scores.IsEmpty && Math.Abs(best.Correlation - MinimumCorrelation) <= ScoreTolerance)
            return FitPatch(source, target, point, expected);
        if (best.Correlation < MinimumCorrelation) return null;
        for (var index = 0; index < count; index++)
        {
            var rival = candidates[index];
            var distance = Math.Abs(rival.X - best.X) + Math.Abs(rival.Y - best.Y);
            if (distance == 0) continue;
            var gap = best.Correlation - rival.Correlation;
            if (!scores.IsEmpty && (gap <= ScoreTolerance || distance >= 2 && Math.Abs(gap - MinimumAmbiguityGap) <= ScoreTolerance))
                return FitPatch(source, target, point, expected);
            if (distance >= 2 && gap < MinimumAmbiguityGap) return null;
        }
        if (Math.Abs(best.X - centerX) >= radiusX || Math.Abs(best.Y - centerY) >= radiusY)
            return null;
        if (!scores.IsEmpty)
        {
            var exact = Correlation(normalized, target.Pixels, width, x + best.X, y + best.Y, sourceEnergy);
            if (!double.IsFinite(exact) || Math.Abs(exact - best.Correlation) > ScoreTolerance)
                return FitPatch(source, target, point, expected);
            best = best with { Correlation = exact };
        }

        Span<double> matched = stackalloc double[PatchSize];
        var matchedSum = 0d;
        var matchedEnergy = 0d;
        var position = 0;
        for (var row = y - 6 + best.Y; row < y + 6 + best.Y; row++)
        for (var column = x - 8 + best.X; column < x + 8 + best.X; column++)
        {
            double value = target.Pixels[row * width + column];
            matched[position++] = value;
            matchedSum += value;
            matchedEnergy += value * value;
        }
        var matchedMean = matchedSum / PatchSize;
        var matchedSigma = Math.Sqrt(matchedEnergy / PatchSize - matchedMean * matchedMean);
        var sourceSigma = Math.Sqrt(sourceEnergy / PatchSize);
        double xx = 0, xy = 0, yy = 0, bx = 0, by = 0;
        for (var index = 0; index < PatchSize; index++)
        {
            var gradientX = horizontalGradient[index] / sourceSigma;
            var gradientY = verticalGradient[index] / sourceSigma;
            var error = (matched[index] - matchedMean) / matchedSigma - normalized[index] / sourceSigma;
            xx += gradientX * gradientX;
            xy += gradientX * gradientY;
            yy += gradientY * gradientY;
            bx -= gradientX * error;
            by -= gradientY * error;
        }
        var determinant = xx * yy - xy * xy;
        var separation = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
        var smallEigenvalue = xx + yy - separation;
        if (determinant <= 0 || smallEigenvalue <= 0 || (xx + yy + separation) / smallEigenvalue > 100)
            return null;
        var refineX = Math.Clamp((yy * bx - xy * by) / determinant, -.5, .5);
        var refineY = Math.Clamp((xx * by - xy * bx) / determinant, -.5, .5);
        return new(new CameraVector(best.X + refineX, best.Y + refineY), best.Correlation);
    }

    private static int Round(double coordinate) => (int)Math.Round(coordinate, MidpointRounding.AwayFromZero);

    private static double Correlation(ReadOnlySpan<float> normalized, ReadOnlySpan<float> target,
        int width, int x, int y, double sourceEnergy)
    {
        var (cross, variance) = ComparePatch(normalized, target,
            (y - PatchHeight / 2) * width + x - PatchWidth / 2, width);
        return variance >= PatchSize * 4 ? cross / Math.Sqrt(sourceEnergy * variance) : double.NaN;
    }

    private static (double Cross, double Variance) ComparePatch(
        ReadOnlySpan<float> source, ReadOnlySpan<float> target, int start, int stride)
    {
        var cross = Vector<float>.Zero;
        var sum = Vector<float>.Zero;
        var squares = Vector<float>.Zero;
        for (var row = 0; row < PatchHeight; row++)
        for (var column = 0; column < PatchWidth; column += Vector<float>.Count)
        {
            var image = new Vector<float>(target.Slice(start + row * stride + column));
            var template = new Vector<float>(source.Slice(row * PatchWidth + column));
            cross += template * image;
            sum += image;
            squares += image * image;
        }
        double total = Vector.Sum(sum);
        return (Vector.Sum(cross), Vector.Sum(squares) - total * total / PatchSize);
    }

    private static bool IsExcluded(IReadOnlyList<StencilRect> exclusions, StencilRect bounds)
    {
        for (var index = 0; index < exclusions.Count; index++)
            if (!bounds.Intersect(exclusions[index]).IsEmpty) return true;
        return false;
    }
}
