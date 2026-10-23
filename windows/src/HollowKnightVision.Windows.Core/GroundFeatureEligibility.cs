namespace HollowKnightVision.Windows.Core;

public readonly record struct GroundReferenceSegment(
    double MinimumWorldX,
    double MaximumWorldX,
    double? WorldY = null);

public sealed record GroundReferenceEstimate(
    double WorldY,
    double MinimumWorldX,
    double MaximumWorldX,
    IReadOnlyList<GroundReferenceSegment> Segments,
    IReadOnlyList<double> NotchWorldXs,
    int SupportingObservationCount,
    double Confidence);

/// <summary>
/// Converts segmented ground evidence into an exclusion mask. A feature center
/// is eligible only when its entire descriptor square fits inside the narrow
/// band immediately below the line and inside a supported horizontal segment.
/// </summary>
public static class GroundFeatureEligibility
{
    public const double DescriptorHalfSize = 12;
    public const double TrackingBandDepth = 32;

    public static IReadOnlyList<StencilRect> AllowedRects(
        WorldSize frameSize,
        AtlasPoint cameraPosition,
        double solveWidth,
        GroundReferenceEstimate ground)
    {
        ArgumentNullException.ThrowIfNull(ground);
        if (!double.IsFinite(frameSize.Width) || !double.IsFinite(frameSize.Height)
            || frameSize.Width <= 0 || frameSize.Height <= 0
            || !double.IsFinite(cameraPosition.X) || !double.IsFinite(cameraPosition.Y)
            || !double.IsFinite(solveWidth) || solveWidth <= 0
            || !double.IsFinite(ground.WorldY)
            || ground.Segments is null
            || ground.Segments.Any(segment => !double.IsFinite(segment.WorldY ?? ground.WorldY)))
        {
            return [];
        }

        var scale = frameSize.Width / solveWidth;
        var bounds = new StencilRect(0, 0, frameSize.Width, frameSize.Height);
        var result = new List<StencilRect>();
        foreach (var segment in ground.Segments)
        {
            var lineWorldY = segment.WorldY ?? ground.WorldY;
            var lineFrameY = (lineWorldY - cameraPosition.Y) * scale;
            var minimumFeatureY = lineFrameY - TrackingBandDepth + DescriptorHalfSize;
            var maximumFeatureY = lineFrameY - DescriptorHalfSize;
            if (maximumFeatureY <= minimumFeatureY) continue;
            var minimumX = (segment.MinimumWorldX - cameraPosition.X) * scale
                + DescriptorHalfSize;
            var maximumX = (segment.MaximumWorldX - cameraPosition.X) * scale
                - DescriptorHalfSize;
            if (!double.IsFinite(minimumX) || !double.IsFinite(maximumX)
                || maximumX <= minimumX)
            {
                continue;
            }
            var rect = new StencilRect(
                minimumX,
                minimumFeatureY,
                maximumX - minimumX,
                maximumFeatureY - minimumFeatureY).Intersect(bounds);
            if (rect.IsFinite && !rect.IsEmpty) result.Add(rect);
        }
        return result.OrderBy(rect => rect.Y).ThenBy(rect => rect.X).ToArray();
    }

    public static IReadOnlyList<StencilRect> AlignmentExclusions(
        WorldSize frameSize,
        AtlasPoint cameraPosition,
        double solveWidth,
        GroundReferenceEstimate? ground,
        IReadOnlyList<StencilRect> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var bounds = new StencilRect(0, 0, frameSize.Width, frameSize.Height);
        if (ground is null) return [bounds];
        var allowed = AllowedRects(frameSize, cameraPosition, solveWidth, ground);
        if (allowed.Count == 0) return [bounds];
        return existing.Concat(Complement(allowed, bounds))
            .Where(rect => rect.IsFinite && !rect.IsEmpty)
            .ToArray();
    }

    /// <summary>
    /// Exact axis-aligned complement of all permitted bands. Horizontal strips
    /// let separate platform heights remain eligible without opening the space
    /// between them to feature extraction.
    /// </summary>
    private static IReadOnlyList<StencilRect> Complement(
        IReadOnlyList<StencilRect> rects,
        StencilRect bounds)
    {
        var clipped = rects.Select(rect => rect.Intersect(bounds))
            .Where(rect => rect.IsFinite && !rect.IsEmpty)
            .ToArray();
        if (clipped.Length == 0) return [bounds];
        var yCoordinates = new[] { bounds.Y, bounds.Bottom }
            .Concat(clipped.SelectMany(rect => new[] { rect.Y, rect.Bottom }))
            .Distinct()
            .Order()
            .ToArray();
        var result = new List<StencilRect>();
        for (var index = 0; index < yCoordinates.Length - 1; index++)
        {
            var minimumY = yCoordinates[index];
            var maximumY = yCoordinates[index + 1];
            if (maximumY <= minimumY) continue;
            var middleY = (minimumY + maximumY) * 0.5;
            var intervals = clipped.Where(rect => rect.Y <= middleY && rect.Bottom >= middleY)
                .OrderBy(rect => rect.X)
                .Select(rect => (Lower: rect.X, Upper: rect.Right))
                .ToArray();
            var merged = new List<(double Lower, double Upper)>();
            foreach (var interval in intervals)
            {
                if (merged.Count > 0 && interval.Lower <= merged[^1].Upper)
                {
                    var last = merged[^1];
                    merged[^1] = (last.Lower, Math.Max(last.Upper, interval.Upper));
                }
                else
                {
                    merged.Add(interval);
                }
            }
            var cursor = bounds.X;
            foreach (var interval in merged)
            {
                if (interval.Lower > cursor)
                {
                    result.Add(new StencilRect(
                        cursor,
                        minimumY,
                        interval.Lower - cursor,
                        maximumY - minimumY));
                }
                cursor = Math.Max(cursor, interval.Upper);
            }
            if (cursor < bounds.Right)
            {
                result.Add(new StencilRect(
                    cursor,
                    minimumY,
                    bounds.Right - cursor,
                    maximumY - minimumY));
            }
        }
        return result;
    }
}
