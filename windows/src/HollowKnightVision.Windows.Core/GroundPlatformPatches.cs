namespace HollowKnightVision.Windows.Core;

/// <summary>Measures platform patches across two neighboring registered frames.</summary>
public sealed class GroundPlatformPatches
{
    private sealed record Frame(byte[] Pixels, int Width, int Height, IReadOnlyList<DetectedFloorLine> Floors,
        IReadOnlyList<PixelRect> Masks, AtlasPoint Camera, double Time);
    private Frame? previous;

    public void Reset() => previous = null;

    public IReadOnlyList<PixelRect> Observe(GroundEdgeAnalysis analysis, IReadOnlyList<DetectedFloorLine> floors,
        IReadOnlyList<PixelRect> masks, AtlasPoint? measuredCamera, double time)
    {
        var width = analysis.Width;
        var height = analysis.Height;
        if (measuredCamera is not { } camera || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y)
            || !double.IsFinite(time) || analysis.SourceLuma is not { } pixels || pixels.LongLength != (long)width * height)
        {
            Reset();
            return [];
        }

        var old = previous;
        previous = new(pixels, width, height, floors, masks, camera, time);
        if (old is null || old.Width != width || old.Height != height || time <= old.Time || time - old.Time > .13)
            return [];

        var shiftX = Math.Round(camera.X - old.Camera.X, MidpointRounding.AwayFromZero);
        var shiftY = Math.Round(camera.Y - old.Camera.Y, MidpointRounding.AwayFromZero);
        if (Math.Abs(shiftX) >= width || Math.Abs(shiftY) >= height) return [];
        var dx = (int)shiftX;
        var dy = (int)shiftY;
        const int patchWidth = GroundPixels.PatchWidth;
        const int patchHeight = GroundPixels.PatchHeight;
        var matches = new List<PixelRect>();
        foreach (var floor in floors)
        {
            var first = floor.XSpan.Start;
            var last = (int)Math.Min(width, (long)floor.XSpan.EndInclusive + 1) - patchWidth;
            if (first > last || floor.Row > height - patchHeight || floor.Row - (long)dy < 0
                || floor.Row - (long)dy > height - patchHeight) continue;
            var oldY = floor.Row - dy;
            var priorFloors = old.Floors.Where(line => Math.Abs(line.Row - oldY) <= 3).ToArray();
            if (priorFloors.Length == 0) continue;

            // Align full boxes in world space and also try both visible tips.
            var phase = (Math.Round(camera.X, MidpointRounding.AwayFromZero) % patchWidth + first) % patchWidth;
            var aligned = first + (patchWidth - (int)phase) % patchWidth;
            var taken = new List<PixelRect>();
            for (var x = aligned; x <= last; x += patchWidth) Match(x);
            Match(first);
            if (last != first) Match(last);

            void Match(int x)
            {
                var box = new PixelRect(x, floor.Row, patchWidth, patchHeight);
                if (taken.Any(used => Overlaps(used, box)) || !Supports(floor, x, floor.Row)
                    || (long)x + dx < 0 || (long)x + dx > width - patchWidth) return;
                var oldX = x + dx;
                if (!priorFloors.Any(line => Supports(line, oldX, oldY))) return;
                var oldBox = box with { X = oldX, Y = oldY };
                if (masks.Any(mask => Overlaps(mask, box)) || old.Masks.Any(mask => Overlaps(mask, oldBox))
                    || GroundPixels.Patch(old.Pixels, width, height, oldX, oldY) is not { } a
                    || GroundPixels.Patch(pixels, width, height, x, floor.Row) is not { } b
                    || a.Max() - a.Min() < 12 || b.Max() - b.Min() < 12
                    || GroundCameraSolver.PatchError(a, b) > 18) return;
                taken.Add(box);
                matches.Add(box);
            }
        }
        return matches;
    }

    public static bool Contains(DetectedFloorLine floor, int x, int y) =>
        Math.Abs((long)floor.Row - y) <= 3 && floor.XSpan.Start <= x
        && floor.XSpan.EndInclusive >= (long)x + GroundPixels.PatchWidth - 1;

    private static bool Supports(DetectedFloorLine floor, int x, int y) => Contains(floor, x, y)
        && floor.EvidenceSpans.Any(span => span.EndInclusive >= x && span.Start < (long)x + GroundPixels.PatchWidth);

    public static bool Overlaps(PixelRect a, PixelRect b) => a.Width > 0 && a.Height > 0
        && b.Width > 0 && b.Height > 0
        && a.X < (long)b.X + b.Width && b.X < (long)a.X + a.Width
        && a.Y < (long)b.Y + b.Height && b.Y < (long)a.Y + a.Height;
}
