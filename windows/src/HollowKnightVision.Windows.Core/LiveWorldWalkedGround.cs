using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

/// <summary>A horizontal footprint relative to its owning capture pose, in solve pixels.</summary>
public readonly record struct LiveWorldWalkedFloor(
    [property: JsonRequired] double Left,
    [property: JsonRequired] double Right,
    [property: JsonRequired] double Y)
{
    internal bool IsValid(AtlasPoint pose) =>
        double.IsFinite(Left) && double.IsFinite(Right) && double.IsFinite(Y) && Left < Right
        && double.IsFinite(pose.X + Left) && double.IsFinite(pose.X + Right)
        && double.IsFinite(pose.Y + Y);
}

/// <summary>Walked columns in one measured capture space, prior to world placement.</summary>
public sealed record LiveWalkedGroundBatch(long Lineage, IReadOnlyList<GroundHypothesisAtlasLine> Spans);

/// <summary>Projects measured footprints through the same pose as their saved images.</summary>
public static class LiveWorldWalkedGround
{
    public static IReadOnlyList<LiveWalkedGroundBatch> NewCarriedEvidence(
        LiveWorldCaptureEvidence? capture, Func<long, IReadOnlyList<GroundHypothesisAtlasLine>> recorded)
    {
        if (capture is not { MeasuredLineage: { } owner, OtherWalks.Count: > 0 }) return [];
        return capture.OtherWalks.Where(walk => walk.Lineage > 0 && walk.Lineage != owner)
            .Select(walk => new LiveWalkedGroundBatch(walk.Lineage, UnrecordedLines(walk.Spans, recorded(walk.Lineage)).ToArray()))
            .Where(walk => walk.Spans.Count > 0).ToArray();
    }

    internal static IEnumerable<LiveIslandCapture> ReservedOwners(LiveWorldSnapshot snapshot) =>
        snapshot.Observations.SelectMany(row => row.GroundGeometry?.WalkedIslands ?? [])
            .Select(walk => walk.Owner).Where(owner => owner.UnplacedSource is not null);

    internal static LiveIslandCapture? UnboundOwner(LiveWorldSnapshot snapshot, int room, ulong generation, long lineage) =>
        ReservedOwners(snapshot).FirstOrDefault(owner => owner.RoomId == room
            && owner.UnplacedSource is { } source && source.CaptureGeneration == generation && source.Lineage == lineage
            && !snapshot.TrackingIslands.Any(island => island.Id == owner.Id));

    public static bool WasWalkedOwnerPlaced(LiveWorldSnapshot previous, LiveWorldSnapshot next) =>
        next.TrackingIslands.LastOrDefault() is { RequiresPlacement: false } owner
        && (previous.TrackingIslands.LastOrDefault()?.Id != owner.Id)
        && !previous.TrackingIslands.Any(island => island.Id == owner.Id)
        && ReservedOwners(previous).Any(reserved => reserved.Id == owner.Id);

    public static IReadOnlyList<LiveWorldWalkedFloor> NewEvidence(
        IReadOnlyList<GroundHypothesisAtlasLine> walked,
        IReadOnlyList<GroundHypothesisAtlasLine> savedInLocalCoordinates,
        AtlasPoint capturePose)
        => UnrecordedLines(walked, savedInLocalCoordinates)
            .Select(line => new LiveWorldWalkedFloor(line.AtlasStart.X - capturePose.X,
                line.AtlasEnd.X - capturePose.X, line.AtlasStart.Y - capturePose.Y))
            .ToArray();

    private static IEnumerable<GroundHypothesisAtlasLine> UnrecordedLines(
        IReadOnlyList<GroundHypothesisAtlasLine> walked,
        IReadOnlyList<GroundHypothesisAtlasLine> savedInLocalCoordinates)
    {
        if (walked.Count == 0 || savedInLocalCoordinates.Count == 0) return walked;
        var covered = savedInLocalCoordinates.OrderBy(line => line.AtlasStart.X).ToArray();
        return walked.SelectMany(line => GroundStrokeGeometry.ExcludeColumns(line, covered, clearance: Math.BitIncrement(2d)));
    }

    public static IReadOnlyList<GroundHypothesisAtlasLine> ProjectRoom(LiveWorldSnapshot snapshot, int room,
        bool includeImageContacts = true)
    {
        var unplaced = snapshot.UnplacedObservationIds();
        var observations = snapshot.Observations.Where(observation => observation.RoomId == room);
        var nativeIslands = snapshot.TrackingIslands.ToDictionary(island => island.Id);
        return GroundStrokeGeometry.Coalesce(observations.SelectMany(observation =>
            ProjectPlacedWalks(observation, nativeIslands, unplaced, includeImageContacts)));
    }

    internal static IReadOnlyList<GroundHypothesisAtlasLine> ProjectOverlay(
        IEnumerable<LiveWorldObservation> observations,
        IReadOnlyDictionary<int, LiveTrackingIsland> islands, IReadOnlySet<int> unplaced)
    {
        var measurements = observations.SelectMany(observation =>
        {
            var offset = LiveWorldGroundOverlay.ImageOriginOffset(observation);
            return ProjectPlacedWalks(observation, islands, unplaced, includeImageContacts: true)
                .Select(line => new LiveWorldGroundOverlay.Span(observation.Id,
                    line.AtlasStart.X + offset.X, line.AtlasEnd.X + offset.X, line.AtlasStart.Y + offset.Y));
        });
        var strokes = LiveWorldGroundOverlay.Estimate(WalkedGroundMemory.WalkedSegmentId, measurements);
        return GroundStrokeGeometry.Coalesce(strokes);
    }

    private static IEnumerable<GroundHypothesisAtlasLine> ProjectPlacedWalks(LiveWorldObservation observation,
        IReadOnlyDictionary<int, LiveTrackingIsland> islands, IReadOnlySet<int> unplaced, bool includeImageContacts)
    {
        var carrierPlaced = !unplaced.Contains(observation.Id);
        return (carrierPlaced ? ProjectObservation(observation) : [])
            .Concat(ProjectIslandWalks(observation, islands, carrierPlaced, includeImageContacts));
    }

    private static IEnumerable<GroundHypothesisAtlasLine> ProjectIslandWalks(
        LiveWorldObservation observation,
        IReadOnlyDictionary<int, LiveTrackingIsland> islands, bool carrierPlaced, bool includeImageContacts)
    {
        if (observation.GroundGeometry is not { } geometry) yield break;

        if (carrierPlaced && includeImageContacts)
        {
            foreach (var line in geometry.Lines.Where(line => line.SegmentId == long.MaxValue))
                yield return ProjectSpan(line, observation.OptimizedPose);
        }

        foreach (var walk in geometry.WalkedIslands ?? [])
        {
            if (walk.Owner.RoomId != observation.RoomId) continue;
            islands.TryGetValue(walk.Owner.Id, out var island);
            var matches = island is not null && island.RoomId == walk.Owner.RoomId && island.StartedAt == walk.Owner.StartedAt
                && (walk.Owner.UnplacedSource is null || island.MeasuredLineage == walk.Owner.UnplacedSource.Lineage);
            // A carrier can still be unplaced while its footprint owner is
            // placed. Missing reservations have no published coordinates.
            if (matches ? island!.RequiresPlacement : walk.Owner.UnplacedSource is not null || !carrierPlaced) continue;
            var origin = matches ? island!.WorldFromLocal : walk.Owner.WorldFromLocal;
            foreach (var span in walk.Spans) yield return ProjectSpan(span, origin);
        }
    }

    private static GroundHypothesisAtlasLine ProjectSpan(LiveGroundLineEvidence line, AtlasPoint origin) =>
        new(WalkedGroundMemory.WalkedSegmentId,
            new(origin.X + line.Start.X, origin.Y + line.Start.Y),
            new(origin.X + line.End.X, origin.Y + line.End.Y));

    public static IEnumerable<GroundHypothesisAtlasLine> ProjectObservation(LiveWorldObservation observation) =>
        (observation.WalkedGround ?? []).Select(floor => new GroundHypothesisAtlasLine(
            WalkedGroundMemory.WalkedSegmentId,
            new(observation.OptimizedPose.X + floor.Left, observation.OptimizedPose.Y + floor.Y),
            new(observation.OptimizedPose.X + floor.Right, observation.OptimizedPose.Y + floor.Y)));

    public static IReadOnlyList<GroundHypothesisAtlasLine> MergeForDisplay(
        IReadOnlyList<GroundHypothesisAtlasLine> unsaved,
        IReadOnlyList<GroundHypothesisAtlasLine> savedInLocalCoordinates,
        IReadOnlyList<GroundHypothesisAtlasLine> durableWorld,
        AtlasPoint worldFromLocal)
    {
        if (durableWorld.Count == 0) return unsaved;
        return GroundStrokeGeometry.Coalesce(InLocalCoordinates(durableWorld, worldFromLocal)
            .Concat(UnrecordedLines(unsaved, savedInLocalCoordinates)));
    }

    private static IEnumerable<GroundHypothesisAtlasLine> InLocalCoordinates(
        IReadOnlyList<GroundHypothesisAtlasLine> world, AtlasPoint worldFromLocal) =>
        world.Select(line => line with
        {
            AtlasStart = new(line.AtlasStart.X - worldFromLocal.X, line.AtlasStart.Y - worldFromLocal.Y),
            AtlasEnd = new(line.AtlasEnd.X - worldFromLocal.X, line.AtlasEnd.Y - worldFromLocal.Y)
        });
}
