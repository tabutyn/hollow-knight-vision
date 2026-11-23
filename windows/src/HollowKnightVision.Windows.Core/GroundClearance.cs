namespace HollowKnightVision.Windows.Core;

/// <summary>Resolves nearby floor measurements that compete for the same columns.</summary>
internal static class GroundClearance
{
    private readonly record struct Extent(long Start, long End)
    {
        internal long Width => End - Start;

        internal long Overlap(Extent other) =>
            Math.Max(0, Math.Min(End, other.End) - Math.Max(Start, other.Start));

        internal static Extent FromPixels(PixelSpan pixels) =>
            new(pixels.Start, (long)pixels.EndInclusive + 1);
    }

    private sealed record Surface(int Index, int Row, Extent Bounds, Extent[] Evidence)
    {
        internal long Support(Extent interval) => Evidence.Sum(part => part.Overlap(interval));

        internal bool Excludes(Surface shorter, double height, int rowTolerance = 0, int endpointTolerance = 0)
        {
            var distance = Math.Abs(Row - shorter.Row);
            return distance > rowTolerance && distance < height && Bounds.Width > shorter.Bounds.Width
                && Bounds.Start <= shorter.Bounds.Start + endpointTolerance
                && Bounds.End >= shorter.Bounds.End - endpointTolerance
                && Support(shorter.Bounds) >= shorter.Bounds.Width / 4d;
        }
    }

    internal static double KnightHeight(int imageHeight, PixelRect? knight) =>
        knight is { Width: > 0, Height: > 0 } sprite && sprite.Height < imageHeight / 2d
            ? sprite.Height : imageHeight * .12;

    internal static IReadOnlyList<DetectedFloorLine> SelectReferences(IReadOnlyList<DetectedFloorLine> references,
        IReadOnlyList<DetectedFloorLine> floors, int imageHeight, PixelRect? knight)
    {
        if (references.Count == 0 || floors.Count == 0) return [];
        var height = KnightHeight(imageHeight, knight);
        var visible = floors.Select(ToSurface).ToArray();
        return references.Select(ToSurface).Where(candidate =>
                !visible.Any(floor => floor.Excludes(candidate, height, rowTolerance: 3))
                && visible.Any(floor => Math.Abs(floor.Row - candidate.Row) < height
                    && floor.Bounds.Overlap(candidate.Bounds) >= GroundPixels.PatchWidth))
            .Select(candidate => references[candidate.Index]).ToArray();
    }

    private static Surface ToSurface(DetectedFloorLine floor, int index) =>
        new(index, floor.Row, Extent.FromPixels(floor.XSpan), floor.EvidenceSpans.Select(Extent.FromPixels).ToArray());

    internal static IReadOnlyList<DetectedFloorLine> Filter(IReadOnlyList<DetectedFloorLine> floors,
        int imageHeight, PixelRect? knight)
    {
        if (floors.Count <= 1) return floors;
        var spans = floors.Select(ToSurface).ToArray();
        var bounds = Resolve(spans, KnightHeight(imageHeight, knight));
        var result = new List<DetectedFloorLine>(floors.Count);
        foreach (var span in spans)
        {
            if (bounds[span.Index] is not { Width: > 0 } range) continue;
            if (range == span.Bounds)
            {
                result.Add(floors[span.Index]);
                continue;
            }
            var measured = span.Evidence.Where(part => part.Overlap(range) > 0)
                .Select(part => new PixelSpan((int)Math.Max(part.Start, range.Start), (int)(Math.Min(part.End, range.End) - 1)))
                .ToArray();
            if (measured.Length > 0)
                result.Add(new(span.Row, new((int)range.Start, (int)(range.End - 1)), measured));
        }
        return result;
    }

    internal static IReadOnlyList<AtlasPathPlatform> FilterPaths(IReadOnlyList<AtlasPathPlatform> floors, int knightHeight)
    {
        if (floors.Count <= 1) return floors;
        var spans = floors.Select((floor, index) =>
        {
            var extent = new Extent(floor.Left, floor.Right);
            return new Surface(index, floor.Y, extent, [extent]);
        }).ToArray();
        // Atlas endpoints from different observations may disagree by a few pixels.
        var bounds = Resolve(spans, knightHeight, Math.Clamp(knightHeight / 5, 2, 8), endpointTolerance: 3);
        var result = new List<AtlasPathPlatform>(floors.Count);
        foreach (var span in spans)
            if (bounds[span.Index] is { Width: > 0 } extent)
                result.Add(new((int)extent.Start, (int)extent.End, span.Row));
        return result;
    }

    private static Extent?[] Resolve(Surface[] spans, double height, int rowTolerance = 0, int endpointTolerance = 0)
    {
        var bounds = new Extent?[spans.Length];
        var kept = new List<Surface>(spans.Length);
        foreach (var span in spans.OrderByDescending(span => span.Bounds.Width))
        {
            if (kept.Any(prior => prior.Excludes(span, height, rowTolerance, endpointTolerance))) continue;
            kept.Add(span);
            bounds[span.Index] = span.Bounds;
        }

        var byX = kept.OrderBy(span => span.Bounds.Start).ToArray();
        for (var i = 0; i < byX.Length; i++)
        {
            var left = byX[i];
            for (var j = i + 1; j < byX.Length && byX[j].Bounds.Start < left.Bounds.End; j++)
            {
                var right = byX[j];
                var overlap = new Extent(right.Bounds.Start, left.Bounds.End);
                var distance = Math.Abs(left.Row - right.Row);
                if (distance == 0 || distance >= height || left.Bounds.Start == overlap.Start
                    || overlap.End >= right.Bounds.End
                    || overlap.Width > Math.Min(left.Bounds.Width, right.Bounds.Width) / 4d) continue;
                var cut = ChooseCut(left, right, overlap);
                if (bounds[left.Index] is { } a && bounds[right.Index] is { } b)
                {
                    bounds[left.Index] = a with { End = Math.Min(a.End, cut) };
                    bounds[right.Index] = b with { Start = Math.Max(b.Start, cut) };
                }
            }
        }

        return bounds;
    }

    private static long ChooseCut(Surface left, Surface right, Extent overlap)
    {
        var midpoint = overlap.Start + overlap.Width / 2;
        var cut = midpoint;
        long Cost(long x) => left.Support(new(x, overlap.End)) + right.Support(new(overlap.Start, x));
        var best = Cost(cut);
        foreach (var part in left.Evidence.Concat(right.Evidence))
        {
            Consider(part.Start);
            Consider(part.End);
        }
        return cut;

        void Consider(long x)
        {
            if (x < overlap.Start || x > overlap.End) return;
            var lost = Cost(x);
            if (lost > best || lost == best && Math.Abs(x - midpoint) >= Math.Abs(cut - midpoint)) return;
            cut = x;
            best = lost;
        }
    }
}
