namespace HollowKnightVision.Windows.Core;

/// <summary>Image-local evidence carried by a capture with independently measured camera motion.</summary>
public sealed record LiveWorldCaptureEvidence(
    double CaptureTimestamp,
    LiveWorldGroundGeometry? GroundGeometry = null,
    LiveObservedFloorCandidates? FloorCandidates = null,
    WorldRect? PlayerBounds = null)
{
    internal static LiveWorldCaptureEvidence FromGround(
        double timestamp,
        int width,
        int height,
        IReadOnlyList<DetectedFloorLine> detections,
        IReadOnlyList<GroundLinePresenceReview> reviews,
        PixelRect? knight,
        AtlasPoint? measuredFoot,
        CleanFloorLine? supportedFloor = null)
    {
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
                var y = height - floor.Row;
                observed.Add(new LiveGroundLineEvidence(long.MaxValue,
                    new(floor.XSpan.Start, y), new(right + 1, y)));
            }
        }
        WorldRect? player = null;
        if (knight is { Width: > 0, Height: > 0 } box && measuredFoot is { } foot
            && foot.X >= box.X && foot.X <= box.Right && foot.Y > box.Y && foot.Y <= height)
        {
            player = new WorldRect(new(box.X, height - foot.Y), new(box.Width, foot.Y - box.Y));
        }

        return new LiveWorldCaptureEvidence(timestamp,
            observed.Count == 0 ? null : new LiveWorldGroundGeometry(observed),
            candidates.Length == 0 ? null : new LiveObservedFloorCandidates(candidates), player);

        IEnumerable<LiveGroundLineEvidence> Capture(DetectedFloorLine floor, int segment, PixelSpan range)
        {
            if (floor.Row >= height) yield break;
            var y = height - floor.Row;
            foreach (var span in floor.EvidenceSpans)
            {
                var start = Math.Max(span.Start, range.Start);
                var end = Math.Min(Math.Min(span.EndInclusive, range.EndInclusive), width - 1);
                if (end - start >= 16)
                    yield return new LiveGroundLineEvidence(segment, new(start, y), new(end + 1, y));
            }
        }
    }
}
