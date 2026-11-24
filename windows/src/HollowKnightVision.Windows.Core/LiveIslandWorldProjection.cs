namespace HollowKnightVision.Windows.Core;

public readonly record struct LiveIslandPlacementProjection(int IslandId, AtlasPoint WorldFromLocal, bool IsPlaced);

/// <summary>Projects a measured capture through its owner in the current committed world.</summary>
public static class LiveIslandWorldProjection
{
    public static LiveIslandPlacementProjection? Resolve(LiveWorldSnapshot snapshot, ulong generation,
        int room, ulong localEpoch, long lineage, double captureTimestamp)
    {
        if (!double.IsFinite(captureTimestamp)) return null;

        bool Matches(LiveTrackingIsland island) => island.RoomId == room && island.MeasuredLineage == lineage;
        bool Belongs(LiveWorldObservation observation) => observation.CaptureGeneration == generation
            && observation.LocalEpoch == localEpoch && observation.CaptureTimestamp <= captureTimestamp;
        static LiveIslandPlacementProjection Place(LiveTrackingIsland island) =>
            new(island.Id, island.WorldFromLocal, !island.RequiresPlacement);

        if (snapshot.TrackingIslands.LastOrDefault() is { } latestOwner && Matches(latestOwner)
            && snapshot.Observations.LastOrDefault() is { } latest && Belongs(latest)
            && latestOwner.Members[^1] is { Measured: true } member && member.ObservationId == latest.Id)
            return Place(latestOwner);

        // A source lineage can have more than one saved owner after recovery.
        // Use the most recent compatible observation at this capture's time,
        // including an unresolved owner when that is the actual match.
        var members = snapshot.TrackingIslands.Where(Matches)
            .SelectMany(island => island.Members.Where(member => member.Measured)
                .Select(member => (member.ObservationId, Island: island)))
            .ToDictionary(entry => entry.ObservationId, entry => entry.Island);
        for (var index = snapshot.Observations.Count - 1; index >= 0 && members.Count > 0; index--)
        {
            var observation = snapshot.Observations[index];
            if (Belongs(observation) && members.TryGetValue(observation.Id, out var island))
                return Place(island);
        }
        return null;
    }
}
