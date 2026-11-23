namespace HollowKnightVision.Windows.Core;

/// <summary>Confirms new visual ownership after a loading cut when the Knight is missing.</summary>
public sealed class VisualSceneDiscontinuityTracker
{
    public sealed record Cut(double DepartureTimestamp, double ArrivalTimestamp, AtlasPoint DepartureWorldPose,
        VisualRoomDirection Direction);

    private sealed record VisibleReference(LowResolutionMotionGrid Grid, double Timestamp, AtlasPoint WorldPose,
        VisualRoomDirection Direction);

    private sealed class Loading(VisibleReference source, double timestamp)
    {
        internal VisibleReference Source { get; } = source;
        internal double StartedAt { get; set; } = timestamp;
        internal double LastBlackAt { get; set; } = timestamp;
        internal bool SustainedBlackoutObserved { get; set; }
        internal LowResolutionMotionGrid? Arrival { get; set; }
        internal double ArrivalStartedAt { get; set; }
        internal int StableFrames { get; set; }

        internal void ClearArrival()
        {
            Arrival = null;
            ArrivalStartedAt = 0;
            StableFrames = 0;
        }
    }

    private enum ImageHalf { Whole, Left, Right }
    private VisibleReference? reference;
    private Loading? loading;
    private double? previousTimestamp;
    private (VisualRoomDirection Direction, double Timestamp)? intent;

    public bool IsPending => loading is not null;
    internal VisualRoomDirection? PendingDirection => loading?.Source.Direction;
    internal AtlasPoint? DepartureWorldPose => loading?.Source.WorldPose;

    public void Reset()
    {
        reference = null;
        loading = null;
        previousTimestamp = null;
        intent = null;
    }

    public Cut? Observe(LowResolutionMotionGrid? grid, double timestamp, VisualRoomDirection? direction,
        AtlasPoint worldPose)
    {
        if (!double.IsFinite(timestamp) || !double.IsFinite(worldPose.X) || !double.IsFinite(worldPose.Y)
            || grid is null || grid.Width < 24 || grid.Height < 18)
        {
            Reset();
            return null;
        }

        var blackout = IsCompleteBlackout(grid);
        if (previousTimestamp is { } previous)
        {
            var gap = timestamp - previous;
            if (gap <= 0) Reset();
            else if (gap > .25)
            {
                // A gap can interrupt confirmed loading. It supplies no new blackout evidence.
                if (gap <= 3 && loading is { } pending
                    && pending.Source.Grid.Width == grid.Width && pending.Source.Grid.Height == grid.Height
                    && (blackout || pending.SustainedBlackoutObserved))
                {
                    pending.StartedAt = timestamp;
                    pending.ClearArrival();
                }
                else
                {
                    Reset();
                }
            }
        }
        previousTimestamp = timestamp;
        if (direction is { } currentDirection) intent = (currentDirection, timestamp);
        var recentDirection = intent is { } timed && timestamp - timed.Timestamp <= 1.5
            ? timed.Direction : (VisualRoomDirection?)null;

        if (blackout)
        {
            if (loading is { } pending)
            {
                pending.LastBlackAt = timestamp;
                pending.SustainedBlackoutObserved |= timestamp - pending.StartedAt >= .15;
                pending.ClearArrival();
            }
            else if (recentDirection.HasValue && reference is { } departure && timestamp - departure.Timestamp <= 1)
            {
                loading = new Loading(departure, timestamp);
            }
            return null;
        }
        if (loading is { } active) return ConfirmArrival(active, grid, timestamp);
        if (recentDirection is { } recent && HasRecoveredImage(grid))
            reference = new VisibleReference(grid, timestamp, worldPose, recent);
        return null;
    }

    private Cut? ConfirmArrival(Loading active, LowResolutionMotionGrid grid, double timestamp)
    {
        if (!active.SustainedBlackoutObserved || timestamp - active.LastBlackAt > 3)
        {
            loading = null;
            reference = null;
            return null;
        }
        if (!HasRecoveredImage(grid)) return null;
        var resemblance = Similarity(active.Source.Grid, grid);
        if (resemblance >= .80)
        {
            loading = null;
            reference = null;
            return null;
        }
        if (resemblance is not < .55 || !BothSidesChanged(active.Source.Grid, grid))
        {
            active.ClearArrival();
            return null;
        }
        if (active.Arrival is { } prior && Similarity(prior, grid, requiringStructure: false) >= .90)
            active.StableFrames++;
        else
        {
            active.ArrivalStartedAt = timestamp;
            active.StableFrames = 1;
        }
        active.Arrival = grid;
        if (active.StableFrames < 6 || timestamp - active.ArrivalStartedAt < .20) return null;

        var cut = new Cut(active.Source.Timestamp, timestamp, active.Source.WorldPose, active.Source.Direction);
        Reset();
        return cut;
    }

    public static bool IsCompleteBlackout(LowResolutionMotionGrid grid)
    {
        if (grid.Height <= 1) return false;
        var luma = grid.Luma;
        // The first row may carry capture markers.
        var belowMarker = luma.AsSpan(grid.Width);
        if (CountLit(belowMarker, 5) <= Math.Max(1, belowMarker.Length / 1_000)) return true;

        var headerEnd = grid.Width * Math.Max(2, grid.Height / 4);
        var header = luma.AsSpan(grid.Width, headerEnd - grid.Width);
        if (CountLit(header, 5) > Math.Max(1, header.Length / 1_000)) return false;

        // A compact loading glyph over black can survive the disappearing HUD.
        var lit = 0;
        var minimumX = grid.Width;
        var maximumX = -1;
        var minimumY = grid.Height;
        var maximumY = -1;
        var limit = Math.Max(1, (luma.Length - headerEnd) / 100);
        for (var index = headerEnd; index < luma.Length; index++)
        {
            if (luma[index] < 5) continue;
            if (++lit > limit) return false;
            var x = index % grid.Width;
            var y = index / grid.Width;
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumY = Math.Max(maximumY, y);
        }
        return lit > 0 && maximumX - minimumX + 1 <= Math.Max(3, grid.Width / 8)
            && maximumY - minimumY + 1 <= Math.Max(3, grid.Height / 4);
    }

    private static int CountLit(ReadOnlySpan<byte> luma, byte threshold)
    {
        var count = 0;
        foreach (var value in luma)
            if (value >= threshold) count++;
        return count;
    }

    private static bool HasRecoveredImage(LowResolutionMotionGrid grid)
    {
        var header = grid.Luma.AsSpan(grid.Width, grid.Width * (Math.Max(2, grid.Height / 4) - 1));
        if (CountLit(header, 128) < 4) return false;

        var body = grid.Luma.AsSpan(grid.Width * (grid.Height / 3));
        var sum = 0d;
        var sumSquared = 0d;
        foreach (var value in body)
        {
            sum += value;
            sumSquared += value * value;
        }
        var mean = sum / body.Length;
        return sumSquared / body.Length - mean * mean >= 16;
    }

    private static bool BothSidesChanged(LowResolutionMotionGrid first, LowResolutionMotionGrid second) =>
        Similarity(first, second, ImageHalf.Left) < .70 && Similarity(first, second, ImageHalf.Right) < .70;

    // Compare intensity and edge structure across small offsets, independent of exposure.
    private static double? Similarity(LowResolutionMotionGrid first, LowResolutionMotionGrid second,
        ImageHalf half = ImageHalf.Whole, bool requiringStructure = true)
    {
        if (first.Width != second.Width || first.Height != second.Height) return null;
        var width = first.Width;
        var height = first.Height;
        var xMargin = Math.Max(2, width / 12);
        var yMargin = Math.Max(2, height / 9);
        var xLow = half == ImageHalf.Right ? width / 2 : xMargin;
        var xHigh = half == ImageHalf.Left ? width / 2 : width - xMargin;
        var yLow = height / 3;
        var yHigh = height - yMargin - 1;
        var count = (double)(xHigh - xLow) * (yHigh - yLow);
        var maxY = Math.Min(3, yMargin);
        var maxX = Math.Min(4, xMargin - 1);
        double? best = null;
        var firstLuma = first.Luma;
        var secondLuma = second.Luma;
        for (var dy = -maxY; dy <= maxY; dy++)
        for (var dx = -maxX; dx <= maxX; dx++)
        {
            double sumA = 0, sumB = 0, aa = 0, bb = 0, ab = 0;
            double gradientAA = 0, gradientBB = 0, gradientAB = 0;
            for (var y = yLow; y < yHigh; y++)
            for (var x = xLow; x < xHigh; x++)
            {
                var aIndex = y * width + x;
                var bIndex = (y + dy) * width + x + dx;
                var a = firstLuma[aIndex];
                var b = secondLuma[bIndex];
                sumA += a;
                sumB += b;
                aa += a * a;
                bb += b * b;
                ab += a * b;
                if (!requiringStructure) continue;
                var ax = firstLuma[aIndex + 1] - firstLuma[aIndex - 1];
                var ay = firstLuma[aIndex + width] - firstLuma[aIndex - width];
                var bx = secondLuma[bIndex + 1] - secondLuma[bIndex - 1];
                var by = secondLuma[bIndex + width] - secondLuma[bIndex - width];
                gradientAA += ax * ax + ay * ay;
                gradientBB += bx * bx + by * by;
                gradientAB += ax * bx + ay * by;
            }
            var varianceA = aa - sumA * sumA / count;
            var varianceB = bb - sumB * sumB / count;
            if (varianceA < count * 4 || varianceB < count * 4) continue;
            var correlation = (ab - sumA * sumB / count) / Math.Sqrt(varianceA * varianceB);
            if (requiringStructure)
            {
                if (gradientAA < count * 4 || gradientBB < count * 4) continue;
                correlation = Math.Min(correlation, gradientAB / Math.Sqrt(gradientAA * gradientBB));
            }
            best = Math.Max(best ?? -1, correlation);
        }
        return best;
    }
}
