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

/// <summary>Projects measured footprints through the same pose as their saved images.</summary>
public static class LiveWorldWalkedGround
{
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

    public static IReadOnlyList<GroundHypothesisAtlasLine> ProjectRoom(LiveWorldSnapshot snapshot, int room)
    {
        var unplaced = snapshot.UnplacedObservationIds();
        var observations = snapshot.Observations.Where(observation => observation.RoomId == room && !unplaced.Contains(observation.Id));
        var nativeIslands = snapshot.TrackingIslands.ToDictionary(island => island.Id);
        return GroundStrokeGeometry.Coalesce(observations.SelectMany(observation =>
            ProjectObservation(observation).Concat(ProjectIslandWalks(observation, nativeIslands))));
    }

    private static IEnumerable<GroundHypothesisAtlasLine> ProjectIslandWalks(
        LiveWorldObservation observation,
        IReadOnlyDictionary<int, LiveTrackingIsland> islands)
    {
        if (observation.GroundGeometry is not { } geometry) yield break;

        foreach (var line in geometry.Lines.Where(line => line.SegmentId == long.MaxValue))
            yield return ProjectSpan(line, observation.OptimizedPose);

        foreach (var walk in geometry.WalkedIslands ?? [])
        {
            if (walk.Owner.RoomId != observation.RoomId) continue;
            var origin = islands.TryGetValue(walk.Owner.Id, out var island)
                && island.RoomId == walk.Owner.RoomId && island.StartedAt == walk.Owner.StartedAt
                ? island.WorldFromLocal : walk.Owner.WorldFromLocal;
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
