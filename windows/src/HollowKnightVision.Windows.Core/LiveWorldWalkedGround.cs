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

    public static IReadOnlyList<GroundHypothesisAtlasLine> ProjectRoom(LiveWorldSnapshot snapshot, int room) =>
        GroundStrokeGeometry.Coalesce(snapshot.Observations
            .Where(observation => observation.RoomId == room)
            .SelectMany(ProjectObservation));

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
