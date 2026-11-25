namespace HollowKnightVision.Windows.Core;

/// <summary>Floor previously measured by foot contact in each room and measured coordinate space.</summary>
public sealed class WalkedGroundMemory
{
    public const long WalkedSegmentId = long.MaxValue;
    private readonly Dictionary<(string Room, long? Lineage), GroundHypothesisAtlasLine[]> rooms = [];

    public void Reset() => rooms.Clear();

    public IReadOnlyList<GroundHypothesisAtlasLine> InRoom(string room, long? lineage = null) =>
        rooms.TryGetValue((room, lineage), out var lines) ? lines : [];

    internal IReadOnlyList<LiveWalkedGroundBatch> OtherOwners(string room, long owner) =>
        rooms.Where(pair => pair.Key.Room == room && pair.Key.Lineage is not null && pair.Key.Lineage != owner)
            .OrderBy(pair => pair.Key.Lineage)
            .Select(pair => new LiveWalkedGroundBatch(pair.Key.Lineage!.Value, pair.Value)).ToArray();

    public void Record(string room, CleanFloorLine floor, AtlasPoint measuredCamera, int imageHeight, long? lineage = null)
    {
        if (string.IsNullOrEmpty(room) || !double.IsFinite(measuredCamera.X) || !double.IsFinite(measuredCamera.Y)
            || floor.Row < 0 || floor.Row >= imageHeight) return;

        var row = imageHeight + measuredCamera.Y - floor.Row;
        var left = measuredCamera.X + floor.XSpan.Start;
        var right = measuredCamera.X + floor.XSpan.EndInclusive + 1;
        var known = InRoom(room, lineage);
        if (known.Any(line => Math.Abs(line.AtlasStart.Y - row) <= 2
            && line.AtlasStart.X <= left + .5 && line.AtlasEnd.X >= right - .5)) return;

        rooms[(room, lineage)] = GroundStrokeGeometry.Coalesce(known.Append(new(WalkedSegmentId,
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

        internal bool Overlaps(Stroke other, int rowTolerance)
        {
            if (Math.Max(High, other.High) - Math.Min(Low, other.Low) > rowTolerance) return false;
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

    public static GroundHypothesisAtlasLine[] Coalesce(IEnumerable<GroundHypothesisAtlasLine> lines) => Coalesce(lines, 3);

    private static GroundHypothesisAtlasLine[] Coalesce(IEnumerable<GroundHypothesisAtlasLine> lines, int rowTolerance)
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
                if (!next.Overlaps(prior, rowTolerance)) return false;
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

    /// <summary>Uses walked floor for the bot wherever the current camera can project that evidence.</summary>
    public static IReadOnlyList<DetectedFloorLine> MergeWalkedFloors(IReadOnlyList<DetectedFloorLine> detected,
        IReadOnlyList<GroundHypothesisAtlasLine> walked, AtlasPoint? camera, int width, int height, PixelRect? knight)
    {
        if (walked.Count == 0 || camera is not { } pose || !double.IsFinite(pose.X) || !double.IsFinite(pose.Y)
            || width <= 0 || height <= 0) return detected;

        var projected = walked.Where(line => line.SegmentId == WalkedGroundMemory.WalkedSegmentId
                && line.AtlasStart.Y == line.AtlasEnd.Y).Select(line =>
            {
                var row = Math.Round(height + pose.Y - line.AtlasStart.Y, MidpointRounding.AwayFromZero);
                return new GroundHypothesisAtlasLine(line.SegmentId,
                    new(Math.Clamp(Math.Ceiling(line.AtlasStart.X - pose.X), 0, width), row),
                    new(Math.Clamp(Math.Floor(line.AtlasEnd.X - pose.X), 0, width), row));
            }).Where(line => line.AtlasStart.Y >= 0 && line.AtlasStart.Y < height
                && line.AtlasStart.X < line.AtlasEnd.X);
        // Allow the extra pixel introduced when two measured ground rows are
        // independently rounded into this frame. Resolve their overlap before
        // choosing which walk row an observed edge should extend.
        const int projectedRowTolerance = 4;
        var footprints = Coalesce(projected, projectedRowTolerance).OrderBy(line => line.AtlasStart.X).ToArray();
        if (footprints.Length == 0) return detected;

        var clearance = GroundClearance.KnightHeight(height, knight);
        var source = detected.Where(floor => floor.Row < height).SelectMany(floor => floor.EvidenceSpans
            .Select(span => new GroundHypothesisAtlasLine(0, new(Math.Max(0, span.Start), floor.Row),
                new(Math.Min((long)width, (long)span.EndInclusive + 1), floor.Row))));
        var merged = Coalesce(footprints.Concat(source.SelectMany(KeepUnwalked)))
            .Select(line => new DetectedFloorLine((int)line.AtlasStart.Y,
                new((int)line.AtlasStart.X, (int)line.AtlasEnd.X - 1))).ToArray();
        return GroundClearance.Filter(merged, height, knight);

        IEnumerable<GroundHypothesisAtlasLine> KeepUnwalked(GroundHypothesisAtlasLine edge)
        {
            if (edge.AtlasStart.X >= edge.AtlasEnd.X) return [];
            var match = footprints.Where(floor => Math.Abs(floor.AtlasStart.Y - edge.AtlasStart.Y) <= projectedRowTolerance
                && floor.AtlasStart.X < edge.AtlasEnd.X && edge.AtlasStart.X < floor.AtlasEnd.X)
                .OrderBy(floor => Math.Abs(floor.AtlasStart.Y - edge.AtlasStart.Y)).FirstOrDefault();
            if (match.SegmentId == WalkedGroundMemory.WalkedSegmentId)
                edge = edge with { AtlasStart = edge.AtlasStart with { Y = match.AtlasStart.Y },
                    AtlasEnd = edge.AtlasEnd with { Y = match.AtlasStart.Y } };
            return ExcludeColumns(edge, footprints, clearance);
        }
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
    internal long? ActiveLineage { get; private set; }
    private (long Generation, int Width, int Height)? owner;
    private long? measuredEpoch;

    public IReadOnlyList<GroundHypothesisAtlasLine> InRoom(string room) =>
        memory.InRoom(room, room == activeRoom ? ActiveLineage : null);

    internal IReadOnlyList<LiveWalkedGroundBatch> OtherOwners(string room, long owner) =>
        memory.OtherOwners(room, owner);

    public KnightTerrainContact Observe(string room, GroundEdgeAnalysis frame, PixelRect? knight, AtlasPoint? measuredCamera,
        InputButtons? buttons, double time, IReadOnlyList<DetectedFloorLine> detected, IReadOnlyList<DetectedWall>? walls = null,
        long generation = 0, long coordinateEpoch = 0, long? measuredLineage = null, bool recentJump = false)
    {
        var nextOwner = (generation, frame.Width, frame.Height);
        var hasPose = measuredCamera is { } camera && double.IsFinite(camera.X) && double.IsFinite(camera.Y);
        if (owner != nextOwner || hasPose && measuredEpoch is { } previous && previous != coordinateEpoch)
        {
            Reset();
            owner = nextOwner;
        }
        if (hasPose) measuredEpoch = coordinateEpoch;
        if (room != activeRoom || hasPose && measuredLineage != ActiveLineage)
        {
            BreakContinuity();
            activeRoom = room;
            if (hasPose) ActiveLineage = measuredLineage;
        }
        var direction = buttons is { } held ? KnightLevelWalk.Direction(held) : 0;
        var actor = locator.Locate(frame, knight, direction, time);
        var wall = wallContact.Observe(actor, measuredCamera, direction, time, frame.Width, frame.Height, walls ?? []);
        var foot = actor is { Partial: false } observed ? observed.Foot : (AtlasPoint?)null;
        if (walk.Observe(foot, measuredCamera, buttons, time, frame.Width, frame.Height, recentJump) is not { } floor
            || measuredCamera is not { } pose) return new(null, wall) { MeasuredFoot = foot };

        // Keep the walked extent and use a matching visible edge for elevation.
        var nearest = detected.Where(line =>
                line.XSpan.Start <= floor.XSpan.EndInclusive && line.XSpan.EndInclusive >= floor.XSpan.Start
                && Math.Abs(line.Row - floor.Row) <= Math.Max(1, (int)(2 * frame.Width / 640d)))
            .MinBy(line => Math.Abs(line.Row - floor.Row));
        var snapped = nearest is null ? floor : floor with { Row = nearest.Row };
        memory.Record(room, snapped, pose, frame.Height, ActiveLineage);
        return new(snapped, wall) { MeasuredFoot = foot };
    }

    public void BreakContinuity()
    {
        locator.Reset();
        walk.Reset();
        wallContact.Reset();
        activeRoom = null;
        ActiveLineage = null;
    }

    public void Reset()
    {
        BreakContinuity();
        memory.Reset();
        owner = null;
        measuredEpoch = null;
    }
}
