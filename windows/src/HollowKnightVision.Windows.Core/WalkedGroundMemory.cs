namespace HollowKnightVision.Windows.Core;

/// <summary>Floor previously measured by foot contact in each registered room.</summary>
public sealed class WalkedGroundMemory
{
    public const int WalkedSegmentId = int.MaxValue;
    private readonly Dictionary<string, GroundHypothesisAtlasLine[]> rooms = new(StringComparer.Ordinal);

    public void Reset() => rooms.Clear();

    public IReadOnlyList<GroundHypothesisAtlasLine> InRoom(string room) =>
        rooms.TryGetValue(room, out var lines) ? lines : [];

    public void Record(string room, CleanFloorLine floor, AtlasPoint measuredCamera, int imageHeight)
    {
        if (string.IsNullOrEmpty(room) || !double.IsFinite(measuredCamera.X) || !double.IsFinite(measuredCamera.Y)
            || floor.Row < 0 || floor.Row >= imageHeight) return;

        var row = imageHeight + measuredCamera.Y - floor.Row;
        var left = measuredCamera.X + floor.XSpan.Start;
        var right = measuredCamera.X + floor.XSpan.EndInclusive + 1;
        var known = InRoom(room);
        if (known.Any(line => Math.Abs(line.AtlasStart.Y - row) <= 2
            && line.AtlasStart.X <= left + .5 && line.AtlasEnd.X >= right - .5)) return;

        rooms[room] = GroundStrokeGeometry.Coalesce(known.Append(new(WalkedSegmentId,
            new(left, row), new(right, row))));
    }
}

/// <summary>Combines overlapping evidence and removes conflicting strokes only in walked columns.</summary>
public static class GroundStrokeGeometry
{
    private sealed class Stroke(GroundHypothesisAtlasLine line)
    {
        internal double Left = line.AtlasStart.X;
        internal double Right = line.AtlasEnd.X;
        internal double Low = line.AtlasStart.Y;
        internal double High = line.AtlasStart.Y;
        internal GroundHypothesisAtlasLine Representative = line;

        internal GroundHypothesisAtlasLine Line =>
            new(Representative.SegmentId, new(Left, Representative.AtlasStart.Y), new(Right, Representative.AtlasStart.Y));

        internal bool Overlaps(Stroke other)
        {
            if (Math.Max(High, other.High) - Math.Min(Low, other.Low) > 3) return false;
            var overlap = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
            return Representative.AtlasStart.Y == other.Representative.AtlasStart.Y
                ? overlap >= 0 : overlap >= Math.Min(3, Math.Min(Right - Left, other.Right - other.Left));
        }

        internal void Absorb(Stroke other)
        {
            var mine = Representative.AtlasEnd.X - Representative.AtlasStart.X;
            var theirs = other.Representative.AtlasEnd.X - other.Representative.AtlasStart.X;
            if (theirs > mine || theirs == mine && other.Representative.AtlasStart.Y < Representative.AtlasStart.Y)
                Representative = other.Representative;
            Left = Math.Min(Left, other.Left);
            Right = Math.Max(Right, other.Right);
            Low = Math.Min(Low, other.Low);
            High = Math.Max(High, other.High);
        }
    }

    public static GroundHypothesisAtlasLine[] Coalesce(IEnumerable<GroundHypothesisAtlasLine> lines)
    {
        var ordered = lines.Where(line => double.IsFinite(line.AtlasStart.X)
                && double.IsFinite(line.AtlasStart.Y) && double.IsFinite(line.AtlasEnd.X)
                && line.AtlasStart.Y == line.AtlasEnd.Y && line.AtlasStart.X < line.AtlasEnd.X)
            .OrderBy(line => line.AtlasStart.X).ThenBy(line => line.AtlasStart.Y)
            .ThenByDescending(line => line.AtlasEnd.X).ThenBy(line => line.SegmentId);
        var active = new List<Stroke>();
        var result = new List<GroundHypothesisAtlasLine>();
        foreach (var line in ordered)
        {
            var next = new Stroke(line);
            active.RemoveAll(prior =>
            {
                if (prior.Right < line.AtlasStart.X)
                {
                    result.Add(prior.Line);
                    return true;
                }
                if (!next.Overlaps(prior)) return false;
                next.Absorb(prior);
                return true;
            });
            active.Add(next);
        }
        result.AddRange(active.Select(stroke => stroke.Line));
        return result.ToArray();
    }

    public static GroundHypothesisAtlasLine[] VisibleLines(
        IReadOnlyList<GroundHypothesisAtlasLine> confirmed,
        IReadOnlyList<GroundLinePresenceReview> current,
        IReadOnlyList<GroundHypothesisAtlasLine> walked,
        AtlasPoint camera,
        int imageHeight,
        PixelRect? knight)
    {
        var live = GroundLineDetector.FeatureHypothesisPresenceReviews(current).Select(review =>
        {
            var y = imageHeight + camera.Y - review.Line.Row - .5;
            return new GroundHypothesisAtlasLine(review.Id, new(camera.X + review.Line.XSpan.Start, y),
                new(camera.X + review.Line.XSpan.EndInclusive + 1, y));
        });
        var edges = Coalesce(confirmed.Concat(live));
        if (walked.Count == 0) return edges;
        var knightHeight = GroundClearance.KnightHeight(imageHeight, knight);
        var footprints = walked.OrderBy(line => line.AtlasStart.X).ToArray();
        return [.. walked, .. edges.SelectMany(line => ExcludeColumns(line, footprints, knightHeight))];
    }

    // Each real footprint supersedes detector strokes in its columns, without
    // erasing an unvisited platform next to it or above the available headroom.
    internal static IEnumerable<GroundHypothesisAtlasLine> ExcludeColumns(GroundHypothesisAtlasLine line,
        IReadOnlyList<GroundHypothesisAtlasLine> walked, double clearance)
    {
        var start = line.AtlasStart.X;
        var end = line.AtlasEnd.X;
        var y = line.AtlasStart.Y;
        foreach (var footprint in walked)
        {
            if (footprint.AtlasStart.X >= end) break;
            if (footprint.AtlasEnd.X <= start || Math.Abs(footprint.AtlasStart.Y - y) >= clearance) continue;
            if (footprint.AtlasStart.X > start) yield return new(line.SegmentId, new(start, y), new(footprint.AtlasStart.X, y));
            start = Math.Max(start, footprint.AtlasEnd.X);
            if (start >= end) yield break;
        }
        if (start < end) yield return new(line.SegmentId, new(start, y), new(end, y));
    }
}

/// <summary>Uses frame-matched input, foot, and camera evidence to retain walked floor.</summary>
public sealed class WalkedGroundTracker
{
    private readonly KnightFootLocator locator = new();
    private readonly KnightLevelWalk walk = new();
    private readonly KnightWallContact wallContact = new();
    private readonly WalkedGroundMemory memory = new();
    private string? activeRoom;

    public IReadOnlyList<GroundHypothesisAtlasLine> InRoom(string room) => memory.InRoom(room);

    public KnightTerrainContact Observe(string room, GroundEdgeAnalysis frame, PixelRect? knight, AtlasPoint? measuredCamera,
        InputButtons? buttons, double time, IReadOnlyList<DetectedFloorLine> detected, IReadOnlyList<DetectedWall>? walls = null)
    {
        if (room != activeRoom)
        {
            BreakContinuity();
            activeRoom = room;
        }
        var direction = buttons is { } held ? KnightLevelWalk.Direction(held) : 0;
        var actor = locator.Locate(frame, knight, direction, time);
        var wall = wallContact.Observe(actor, measuredCamera, direction, time, frame.Width, frame.Height, walls ?? []);
        var foot = actor is { Partial: false } observed ? observed.Foot : (AtlasPoint?)null;
        if (walk.Observe(foot, measuredCamera, buttons, time, frame.Width, frame.Height) is not { } floor
            || measuredCamera is not { } pose) return new(null, wall);

        // Keep the walked extent and use a matching visible edge for elevation.
        var nearest = detected.Where(line =>
                line.XSpan.Start <= floor.XSpan.EndInclusive && line.XSpan.EndInclusive >= floor.XSpan.Start
                && Math.Abs(line.Row - floor.Row) <= Math.Max(1, (int)(2 * frame.Width / 640d)))
            .MinBy(line => Math.Abs(line.Row - floor.Row));
        var snapped = nearest is null ? floor : floor with { Row = nearest.Row };
        memory.Record(room, snapped, pose, frame.Height);
        return new(snapped, wall);
    }

    public void BreakContinuity()
    {
        locator.Reset();
        walk.Reset();
        wallContact.Reset();
        activeRoom = null;
    }

    public void Reset()
    {
        BreakContinuity();
        memory.Reset();
    }
}
