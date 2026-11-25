namespace HollowKnightVision.Windows.Core;

internal static class AtlasPathFloorGeometry
{
    private readonly record struct Band(AtlasPathPlatform Span, int Low, int High, int Longest, bool Walked);

    internal static IReadOnlyList<AtlasPathPlatform> Prepare(IReadOnlyList<AtlasPathPlatform> raw,
        IReadOnlyList<AtlasPathWall> walls, IReadOnlyList<AtlasPathPlatform> spikes, IReadOnlyList<AtlasPathPlatform> walked,
        IReadOnlyList<AtlasPathPlatform> handoffSeams, int height, int width, bool detected)
    {
        var merged = detected ? StitchDetected(raw, walked, walls, height, width, handoffSeams, maximumSupportedGap: 3 * height)
            : Merge(raw, height);
        return merged.SelectMany(floor => SubtractSpikes(floor, spikes, height, width))
            .SelectMany(floor => CutStandingWalls(floor, walls, detected ? walked : [], height, width))
            .OrderBy(floor => (floor.Left, floor.Y)).ToArray();
    }

    internal static IReadOnlyList<AtlasPathPlatform> Merge(IEnumerable<AtlasPathPlatform> input, int height, bool bridgeSeams = true)
    {
        var tolerance = Math.Clamp(height / 5, 2, 6);
        var seam = bridgeSeams ? Math.Clamp(height / 6, 2, 6) : 0;
        var bands = new List<(AtlasPathPlatform Span, int Bottom)>();
        foreach (var floor in input.Where(span => span.Left < span.Right).OrderBy(span => (span.Y, span.Left)))
        {
            var joined = floor;
            var bottom = floor.Y;
            bands.RemoveAll(band =>
            {
                if (floor.Y - band.Bottom > tolerance || floor.Left > band.Span.Right + seam || band.Span.Left > floor.Right + seam)
                    return false;
                joined = joined with { Left = Math.Min(joined.Left, band.Span.Left), Right = Math.Max(joined.Right, band.Span.Right) };
                bottom = Math.Min(bottom, band.Bottom);
                return true;
            });
            bands.Add((joined, bottom));
        }
        return bands.Select(band => band.Span).OrderBy(span => (span.Left, span.Y, span.Right)).ToArray();
    }

    internal static IReadOnlyList<AtlasPathPlatform> Extend(IReadOnlyList<AtlasPathPlatform> current,
        IEnumerable<AtlasPathPlatform> samples, int height)
    {
        var dy = Math.Clamp(height / 5, 2, 6);
        var additions = samples.Where(sample => !current.Any(span =>
            Math.Abs(span.Y - sample.Y) <= dy && span.Left <= sample.Left + 1 && span.Right >= sample.Right - 1)).ToArray();
        return additions.Length == 0 ? current : Merge(current.Concat(additions), height, bridgeSeams: false);
    }

    internal static IReadOnlyList<AtlasPathPlatform> StitchDetected(IReadOnlyList<AtlasPathPlatform> raw,
        IReadOnlyList<AtlasPathPlatform> walked, IReadOnlyList<AtlasPathWall> walls, int height, int width,
        IReadOnlyList<AtlasPathPlatform> witnesses, int? maximumSupportedGap = null, bool requireWitness = false)
    {
        var jitter = Math.Clamp(height / 5, 2, 8);
        var seam = requireWitness ? 0 : Math.Max(2, Math.Min(width, height));
        var longestWitness = witnesses.Select(span => span.Right - span.Left).DefaultIfEmpty().Max();
        var searchWidth = Math.Max(seam, Math.Min(maximumSupportedGap ?? longestWitness, longestWitness));
        var margin = maximumSupportedGap.HasValue ? Math.Min(3, width / 2) : 0;
        var pending = new List<Band>();
        var finished = new List<AtlasPathPlatform>();
        var confirmed = walked.ToHashSet();
        var ordered = raw.Concat(walked).Where(span => span.Left < span.Right)
            .OrderBy(span => (span.Left, span.Y, span.Right)).ToArray();
        foreach (var floor in ordered)
        {
            pending.RemoveAll(band =>
            {
                if (band.Span.Right + searchWidth >= floor.Left) return false;
                finished.Add(band.Span);
                return true;
            });

            var best = -1;
            for (var i = 0; i < pending.Count; i++)
            {
                var band = pending[i];
                var gap = floor.Left - band.Span.Right;
                if (Math.Max(band.High, floor.Y) - Math.Min(band.Low, floor.Y) > jitter) continue;
                if (gap > seam && !witnesses.Any(witness =>
                        Math.Abs(witness.Y - floor.Y) <= 2 && Math.Abs(witness.Y - band.Span.Y) <= 2
                        && witness.Left <= band.Span.Right - margin && floor.Left + margin <= witness.Right)) continue;
                if (gap > 0 && (walls.Any(wall =>
                        band.Span.Right <= wall.X && wall.X <= floor.Left && wall.High >= Math.Min(band.Low, floor.Y)
                        && wall.Low <= Math.Max(band.High, floor.Y) + height / 2)
                    || ordered.Any(below => below.Left < floor.Left && below.Right > band.Span.Right
                        && below.Y < Math.Min(band.Low, floor.Y) - jitter))) continue;
                if (best < 0 || Math.Abs(pending[best].Span.Y - floor.Y) > Math.Abs(band.Span.Y - floor.Y)) best = i;
            }

            var length = floor.Right - floor.Left;
            var isWalked = confirmed.Contains(floor);
            if (best < 0)
            {
                pending.Add(new(floor, floor.Y, floor.Y, length, isWalked));
                continue;
            }
            var current = pending[best];
            var preferred = isWalked != current.Walked ? isWalked : length > current.Longest;
            pending[best] = new(
                current.Span with { Right = Math.Max(current.Span.Right, floor.Right), Y = preferred ? floor.Y : current.Span.Y },
                Math.Min(current.Low, floor.Y), Math.Max(current.High, floor.Y),
                preferred ? length : current.Longest, preferred ? isWalked : current.Walked);
        }
        return finished.Concat(pending.Select(band => band.Span)).OrderBy(span => (span.Left, span.Y, span.Right)).ToArray();
    }

    private static IEnumerable<AtlasPathPlatform> SubtractSpikes(AtlasPathPlatform floor,
        IReadOnlyList<AtlasPathPlatform> spikes, int height, int width)
    {
        var tolerance = Math.Clamp(height / 5, 2, 6);
        var half = Math.Max(1, width / 2);
        var left = floor.Left;
        foreach (var spike in spikes.Where(spike => Math.Abs(spike.Y - floor.Y) <= tolerance).OrderBy(spike => spike.Left))
        {
            var low = spike.Left - half;
            var high = spike.Right + half;
            if (high < left || low > floor.Right) continue;
            if (left < low - 1) yield return floor with { Left = left, Right = low - 1 };
            left = Math.Max(left, high + 1);
        }
        if (left < floor.Right) yield return floor with { Left = left };
    }

    private static IEnumerable<AtlasPathPlatform> CutStandingWalls(AtlasPathPlatform floor,
        IReadOnlyList<AtlasPathWall> walls, IReadOnlyList<AtlasPathPlatform> walked, int height, int width)
    {
        var middle = floor.Y + height / 2;
        var cuts = walls.Where(wall => floor.Left < wall.X && wall.X < floor.Right && wall.Low <= middle && middle < wall.High)
            .Select(wall => wall.X).Distinct().Order().ToArray();
        if (cuts.Length == 0) return [floor];

        var pieces = new List<AtlasPathPlatform>();
        var left = floor.Left;
        var half = Math.Max(1, width / 2);
        foreach (var cut in cuts)
        {
            if (left < cut - half) pieces.Add(floor with { Left = left, Right = cut - half });
            left = Math.Max(left, cut + half);
        }
        if (left < floor.Right) pieces.Add(floor with { Left = left });
        if (walked.Count == 0) return pieces;
        // Keep the measured footprint through the conservative clearance at a wall.
        foreach (var walk in walked)
        {
            var low = Math.Max(floor.Left, walk.Left);
            var high = Math.Min(floor.Right, walk.Right);
            if (walk.Y == floor.Y && low < high) pieces.Add(new(low, high, floor.Y));
        }
        return Merge(pieces, height, bridgeSeams: false);
    }
}
