using ILGPU;
using ILGPU.Runtime;

namespace HollowKnightVision.Windows.Core;

/// <summary>Scores scenery candidates on an existing accelerator. The CPU refines and admits the matches.</summary>
public sealed class ScenePatchAccelerator : IDisposable
{
    internal const double RescoreOnCpu = -1.5;
    // The generated ILGPU launcher needs public, blittable argument types.
    public readonly record struct Point(int X, int Y);
    public readonly record struct Mask(double Left, double Top, double Right, double Bottom);
    public readonly record struct Search(int Width, int Height, int CenterX, int CenterY, int RadiusX, int RadiusY);
    private readonly Accelerator accelerator;
    private readonly Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<Point>, ArrayView<Mask>,
        ArrayView<Mask>, ArrayView<double>, Search> kernel;
    private MemoryBuffer1D<float, Stride1D.Dense>? sourceBuffer, targetBuffer;
    private MemoryBuffer1D<Point, Stride1D.Dense>? pointsBuffer;
    private MemoryBuffer1D<Mask, Stride1D.Dense>? sourceMaskBuffer, targetMaskBuffer;
    private MemoryBuffer1D<double, Stride1D.Dense>? scoresBuffer;

    internal ScenePatchAccelerator(Accelerator accelerator)
    {
        this.accelerator = accelerator;
        kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<Point>,
            ArrayView<Mask>, ArrayView<Mask>, ArrayView<double>, Search>(Correlate);
    }

    internal void Score(CalibratedSceneMotionTracker.Grid source, CalibratedSceneMotionTracker.Grid target,
        IReadOnlyList<AtlasPoint> points, Search search, Span<double> scores)
    {
        var centers = points.Select(point => new Point(
            (int)Math.Round(point.X, MidpointRounding.AwayFromZero),
            (int)Math.Round(point.Y, MidpointRounding.AwayFromZero))).ToArray();
        var a = Upload(ref sourceBuffer, source.Pixels);
        var b = Upload(ref targetBuffer, target.Pixels);
        var p = Upload(ref pointsBuffer, centers);
        var excludeA = Upload(ref sourceMaskBuffer, PrepareMasks(source.Exclusions));
        var excludeB = Upload(ref targetMaskBuffer, PrepareMasks(target.Exclusions));
        var output = View(ref scoresBuffer, scores.Length);
        kernel(scores.Length, a, b, p, excludeA, excludeB, output, search);
        output.CopyToCPU(scores);
    }

    private static Mask[] PrepareMasks(IReadOnlyList<StencilRect> exclusions) =>
        exclusions.Where(rect => rect.IsFinite && !rect.IsEmpty)
            .Select(rect => new Mask(rect.X, rect.Y, rect.Right, rect.Bottom)).ToArray();

    private ArrayView<T> Upload<T>(ref MemoryBuffer1D<T, Stride1D.Dense>? storage, T[] data) where T : unmanaged
    {
        var view = View(ref storage, data.Length);
        if (data.Length != 0) view.CopyFromCPU(data);
        return view;
    }

    private ArrayView<T> View<T>(ref MemoryBuffer1D<T, Stride1D.Dense>? storage, int length) where T : unmanaged
    {
        if (storage is null || storage.Length < length)
        {
            var replacement = accelerator.Allocate1D<T>(Math.Max(1, length));
            storage?.Dispose();
            storage = replacement;
        }
        return storage.View.SubView(0, length).BaseView;
    }

    private static void Correlate(Index1D index, ArrayView<float> source, ArrayView<float> target,
        ArrayView<Point> points, ArrayView<Mask> sourceMasks, ArrayView<Mask> targetMasks, ArrayView<double> output, Search search)
    {
        var columns = 2 * search.RadiusX + 1;
        var candidates = columns * (2 * search.RadiusY + 1);
        var offset = (int)index;
        var point = points[offset / candidates];
        var candidate = offset % candidates;
        var targetX = point.X + candidate % columns + search.CenterX - search.RadiusX;
        var targetY = point.Y + candidate / columns + search.CenterY - search.RadiusY;
        output[index] = -2;
        if (point.X < 9 || point.X >= search.Width - 9 || point.Y < 7 || point.Y >= search.Height - 7
            || targetX < 8 || targetX >= search.Width - 8 || targetY < 6 || targetY >= search.Height - 6
            || IsMasked(sourceMasks, point.X, point.Y) || IsMasked(targetMasks, targetX, targetY)) return;

        double sumA = 0, sumB = 0, aa = 0, bb = 0, ab = 0;
        for (var row = -6; row < 6; row++)
        for (var column = -8; column < 8; column++)
        {
            double a = source[(point.Y + row) * search.Width + point.X + column];
            double b = target[(targetY + row) * search.Width + targetX + column];
            sumA += a; sumB += b; aa += a * a; bb += b * b; ab += a * b;
        }
        var varianceA = aa - sumA * sumA / 192;
        var varianceB = bb - sumB * sumB / 192;
        if (Math.Abs(varianceA - 768) <= 2 || Math.Abs(varianceB - 768) <= 2)
            output[index] = RescoreOnCpu;
        else if (varianceA >= 768 && varianceB >= 768)
            output[index] = (ab - sumA * sumB / 192) / Math.Sqrt(varianceA * varianceB);
    }

    private static bool IsMasked(ArrayView<Mask> masks, int x, int y)
    {
        for (var index = 0; index < masks.Length; index++)
        {
            var mask = masks[index];
            if (x - 8 < mask.Right && x + 8 > mask.Left && y - 6 < mask.Bottom && y + 6 > mask.Top) return true;
        }
        return false;
    }

    public void Dispose()
    {
        sourceBuffer?.Dispose();
        targetBuffer?.Dispose();
        pointsBuffer?.Dispose();
        sourceMaskBuffer?.Dispose();
        targetMaskBuffer?.Dispose();
        scoresBuffer?.Dispose();
    }
}
