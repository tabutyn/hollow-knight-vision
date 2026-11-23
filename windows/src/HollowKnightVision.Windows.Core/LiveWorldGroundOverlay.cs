namespace HollowKnightVision.Windows.Core;

public sealed record LiveWorldGroundProjection(
    IReadOnlyList<GroundHypothesisAtlasLine> DisplayLines,
    IReadOnlyDictionary<int, GameplaySavedRoomPath> PathRooms)
{
    public static LiveWorldGroundProjection Empty { get; } = new([], new Dictionary<int, GameplaySavedRoomPath>());
}

/// <summary>Projects durable green strokes from the same poses used to place Atlas images.</summary>
public static class LiveWorldGroundOverlay
{
    private const double MaximumHeightSpread = 8;
    private readonly record struct Span(int Observation, double Left, double Right, double Y);

    public static IReadOnlyList<GroundHypothesisAtlasLine> Project(LiveWorldSnapshot snapshot) =>
        ProjectWorld(snapshot).DisplayLines;

    public static LiveWorldGroundProjection ProjectWorld(LiveWorldSnapshot snapshot)
    {
        var owners = snapshot.TrackingIslands
            .SelectMany(island => island.Members.Select(member => (island, member)))
            .ToDictionary(owner => owner.member.ObservationId);
        var usesLegacyWindowsPoses = owners.Count == 0 && !snapshot.UsesSwiftWireFormat;
        var placed = snapshot.Observations.Where(observation =>
            !owners.TryGetValue(observation.Id, out var owner) || !owner.island.RequiresPlacement);
        var islands = snapshot.TrackingIslands.ToDictionary(island => island.Id);
        var result = new List<GroundHypothesisAtlasLine>();
        var pathRooms = new Dictionary<int, GameplaySavedRoomPath>();
        foreach (var room in placed.GroupBy(observation => observation.RoomId))
        {
            var walked = LiveWorldWalkedGround.ProjectPlaced(room, islands, centered: true)
                .OrderBy(line => line.AtlasStart.X).ToArray();
            var pathFloors = new List<GroundHypothesisAtlasLine>(walked);
            var edges = room.Where(observation =>
                    observation.GroundGeometry is { Lines.Count: > 0 }
                    && (!owners.TryGetValue(observation.Id, out var owner)
                        ? !snapshot.UsesSwiftWireFormat : owner.member.Measured))
                .GroupBy(observation => owners.TryGetValue(observation.Id, out var owner)
                    ? (Island: owner.island.Id, LegacyObservation: -1)
                    : (Island: -1, LegacyObservation: observation.Id));
            var clearance = GroundClearance.KnightHeight((int)Math.Round(SolveHeight(room.First())), null);
            result.AddRange(walked);
            foreach (var island in edges)
            {
                var strokes = GroundStrokeGeometry.Coalesce(island
                    .SelectMany(observation => observation.GroundGeometry!.Lines
                        .Where(line => line.SegmentId != long.MaxValue)
                        .Select(line => (line.SegmentId, Span: InAtlas(observation, line))))
                    .GroupBy(evidence => evidence.SegmentId)
                    .SelectMany(segment => Estimate(segment.Key, segment.Select(evidence => evidence.Span))));
                if (HasMeasuredPlacement(island.First())) pathFloors.AddRange(strokes);
                result.AddRange(strokes.SelectMany(line => GroundStrokeGeometry.ExcludeColumns(line, walked, clearance)));
            }
            pathRooms.Add(room.Key, new(room.Key, pathFloors, room.Where(HasMeasuredPlacement)
                .Select(ImageBounds).Distinct().ToArray()));
        }
        return new(result, pathRooms);

        bool HasMeasuredPlacement(LiveWorldObservation observation) =>
            owners.TryGetValue(observation.Id, out var owner)
                ? owner.member.Measured && (owner.island.Placement is LiveIslandPlacement.Reference or LiveIslandPlacement.Connected
                    || owner.island is { Placement: LiveIslandPlacement.Estimated, BreakReason: LiveTrackingIsland.PlacedTrackingBreakReason })
                : usesLegacyWindowsPoses;
    }

    public static IReadOnlyList<GroundHypothesisAtlasLine> CombineWithLive(
        IReadOnlyList<GroundHypothesisAtlasLine> saved, IReadOnlyList<GroundHypothesisAtlasLine> live,
        int imageHeight, PixelRect? knight)
    {
        if (saved.Count == 0) return live;
        if (live.Count == 0) return saved;
        var clearance = GroundClearance.KnightHeight(imageHeight, knight);
        var liveWalked = live.Where(IsWalked).OrderBy(line => line.AtlasStart.X).ToArray();
        var savedWalked = saved.Where(IsWalked).OrderBy(line => line.AtlasStart.X).ToArray();
        var savedEdges = saved.Where(line => !IsWalked(line)).OrderBy(line => line.AtlasStart.X).ToArray();
        var edges = savedEdges.SelectMany(line => GroundStrokeGeometry.ExcludeColumns(line, liveWalked, clearance));
        var newWalks = liveWalked.SelectMany(line => GroundStrokeGeometry.ExcludeColumns(line, savedWalked,
            Math.BitIncrement(MaximumHeightSpread)));
        var walkedCoverage = savedWalked.Concat(liveWalked).OrderBy(line => line.AtlasStart.X).ToArray();
        var newEdges = live.Where(line => !IsWalked(line))
            .SelectMany(line => GroundStrokeGeometry.ExcludeColumns(line, walkedCoverage, clearance))
            .SelectMany(line => GroundStrokeGeometry.ExcludeColumns(line, savedEdges, Math.BitIncrement(MaximumHeightSpread)));
        return [.. savedWalked, .. newWalks, .. edges, .. newEdges];
    }

    private static bool IsWalked(GroundHypothesisAtlasLine line) =>
        line.SegmentId == WalkedGroundMemory.WalkedSegmentId;

    // The Windows mosaic centers each image on its optimized pose. Its saved
    // measurements have the source image's bottom-left origin.
    internal static AtlasPoint ImageOriginOffset(LiveWorldObservation observation) =>
        new(-observation.SolveWidth / 2, -SolveHeight(observation) / 2);

    private static double SolveHeight(LiveWorldObservation observation) =>
        observation.SourceHeight * observation.SolveWidth / observation.SourceWidth;

    private static WorldRect ImageBounds(LiveWorldObservation observation)
    {
        var offset = ImageOriginOffset(observation);
        return new(new(observation.OptimizedPose.X + offset.X, observation.OptimizedPose.Y + offset.Y),
            new(observation.SolveWidth, SolveHeight(observation)));
    }

    private static Span InAtlas(LiveWorldObservation observation, LiveGroundLineEvidence line)
    {
        var offset = ImageOriginOffset(observation);
        var left = observation.OptimizedPose.X + offset.X;
        return new(observation.Id, left + line.Start.X, left + line.End.X,
            observation.OptimizedPose.Y + offset.Y + line.Start.Y);
    }

    private static IEnumerable<GroundHypothesisAtlasLine> Estimate(long segment, IEnumerable<Span> evidence)
    {
        var cluster = new List<Span>();
        foreach (var span in evidence.OrderBy(span => span.Y))
        {
            if (cluster.Count != 0 && span.Y - cluster[0].Y > MaximumHeightSpread)
            {
                foreach (var stroke in ConnectedStrokes(segment, cluster)) yield return stroke;
                cluster.Clear();
            }
            cluster.Add(span);
        }
        foreach (var stroke in ConnectedStrokes(segment, cluster)) yield return stroke;
    }

    private static IEnumerable<GroundHypothesisAtlasLine> ConnectedStrokes(long segment, IEnumerable<Span> cluster)
    {
        var component = new List<Span>();
        var right = double.NegativeInfinity;
        foreach (var span in cluster.OrderBy(span => span.Left))
        {
            if (component.Count != 0 && span.Left > right)
            {
                yield return Stroke(segment, component, right);
                component.Clear();
            }
            right = component.Count == 0 ? span.Right : Math.Max(right, span.Right);
            component.Add(span);
        }
        if (component.Count != 0) yield return Stroke(segment, component, right);
    }

    private static GroundHypothesisAtlasLine Stroke(long segment, IReadOnlyList<Span> component, double right)
    {
        // Occlusion can fragment an image into several spans. Every image
        // contributes exactly one height vote to the connected floor.
        var height = Median(component.GroupBy(span => span.Observation)
            .Select(observation => Median(observation.Select(span => span.Y))));
        var y = Math.Round(height * 2, MidpointRounding.AwayFromZero) / 2;
        return new(segment, new(component[0].Left, y), new(right, y));
    }

    private static double Median(IEnumerable<double> heights)
    {
        var values = heights.Order().ToArray();
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }
}
