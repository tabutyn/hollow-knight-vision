namespace HollowKnightVision.Windows.Core;

/// <summary>Image-local evidence carried by a capture with independently measured camera motion.</summary>
public sealed record LiveWorldCaptureEvidence(
    double CaptureTimestamp,
    LiveWorldGroundGeometry? GroundGeometry = null,
    LiveObservedFloorCandidates? FloorCandidates = null,
    WorldRect? PlayerBounds = null)
{
    public double? LocalReferenceTimestamp { get; init; }
    public bool HasMeasuredSceneryContinuity { get; internal init; }
    public long? MeasuredLineage { get; internal init; }
    public IReadOnlyList<LiveWalkedGroundBatch> OtherWalks { get; internal init; } = [];

    internal static LiveWorldCaptureEvidence FromGround(
        double timestamp,
        int width,
        int height,
        double solveWidth,
        IReadOnlyList<DetectedFloorLine> detections,
        IReadOnlyList<GroundLinePresenceReview> reviews,
        PixelRect? knight,
        AtlasPoint? measuredFoot,
        CleanFloorLine? supportedFloor = null,
        StencilRect? observedPlayerBounds = null)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(solveWidth) || solveWidth <= 0)
            return new(timestamp);
        var scale = solveWidth / width;
        var imageSpan = new PixelSpan(0, width - 1);
        var candidates = detections.SelectMany((floor, index) => Capture(floor, index, imageSpan))
            .Take(128).ToArray();
        var observed = reviews.Where(review => review.State == GroundLinePresenceState.Confirmed)
            .SelectMany(review => detections.Where(floor => Math.Abs(floor.Row - review.Line.Row) <= 2)
                .SelectMany(floor => Capture(floor, review.Id, review.Line.XSpan)))
            .Distinct().ToList();
        if (supportedFloor is { } floor && floor.Row >= 0 && floor.Row < height)
        {
            // Grounded traversal provides independent evidence even when the
            // sprite or foreground hides the edge beneath the Knight.
            var right = Math.Min(floor.XSpan.EndInclusive, width - 1);
            if (floor.XSpan.Start < right)
            {
                var y = (height - floor.Row) * scale;
                observed.Add(new LiveGroundLineEvidence(long.MaxValue,
                    new(floor.XSpan.Start * scale, y), new((right + 1) * scale, y)));
            }
        }
        WorldRect? player = null;
        if (observedPlayerBounds is { IsFinite: true, IsEmpty: false } bounds)
        {
            // A body box can end above the feet. Keep its exact horizontal
            // bounds and use the image-measured foot when the two agree.
            var bottom = measuredFoot is { } observedFoot && observedFoot.X >= bounds.X
                && observedFoot.X <= bounds.Right && observedFoot.Y > bounds.Y && observedFoot.Y <= height
                ? observedFoot.Y : bounds.Bottom;
            player = new WorldRect(new(bounds.X * scale, (height - bottom) * scale),
                new(bounds.Width * scale, (bottom - bounds.Y) * scale));
        }
        else if (knight is { Width: > 0, Height: > 0 } box && measuredFoot is { } foot
            && foot.X >= box.X && foot.X <= box.Right && foot.Y > box.Y && foot.Y <= height)
        {
            player = new WorldRect(new(box.X * scale, (height - foot.Y) * scale),
                new(box.Width * scale, (foot.Y - box.Y) * scale));
        }

        return new LiveWorldCaptureEvidence(timestamp,
            observed.Count == 0 ? null : new LiveWorldGroundGeometry(observed),
            candidates.Length == 0 ? null : new LiveObservedFloorCandidates(candidates), player);

        IEnumerable<LiveGroundLineEvidence> Capture(DetectedFloorLine floor, int segment, PixelSpan range)
        {
            if (floor.Row >= height) yield break;
            var y = (height - floor.Row) * scale;
            foreach (var span in floor.EvidenceSpans)
            {
                var start = Math.Max(span.Start, range.Start);
                var end = Math.Min(Math.Min(span.EndInclusive, range.EndInclusive), width - 1);
                if (end - start >= 16)
                    yield return new LiveGroundLineEvidence(segment, new(start * scale, y), new((end + 1) * scale, y));
            }
        }
    }
}
