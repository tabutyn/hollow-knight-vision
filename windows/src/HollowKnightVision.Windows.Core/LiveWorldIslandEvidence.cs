using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public enum LiveIslandPlacement { Reference, Connected, Estimated, LegacyUnknown }

public readonly record struct LiveIslandUncertainty(double? X, double? Y)
{
    internal bool IsValid => ValidAxis(X) && ValidAxis(Y);

    private static bool ValidAxis(double? value) =>
        value is null || (double.IsFinite(value.Value) && value.Value >= 0);
}

public sealed record LiveIslandMember(
    [property: JsonRequired] int ObservationId,
    [property: JsonRequired] AtlasPoint LocalPose,
    [property: JsonRequired] bool Measured);

public sealed record LiveTrackingIsland(
    [property: JsonRequired] int Id,
    [property: JsonRequired] int RoomId,
    [property: JsonRequired] double StartedAt,
    [property: JsonRequired] AtlasPoint WorldFromLocal,
    [property: JsonRequired] LiveIslandPlacement Placement,
    [property: JsonRequired] LiveIslandUncertainty Uncertainty,
    [property: JsonRequired] string BreakReason,
    [property: JsonRequired] IReadOnlyList<LiveIslandMember> Members)
{
    internal const string RecoveryBreakReason = "local-tracking-recovery";
    internal const string PlacedTrackingBreakReason = "measured-local-tracking";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MeasuredLineage { get; init; }

    internal bool RequiresPlacement => Placement == LiveIslandPlacement.Estimated && BreakReason == RecoveryBreakReason;

    internal bool IsValid => Id >= 0 && RoomId >= 0 && double.IsFinite(StartedAt)
        && LiveWorldSnapshot.Finite(WorldFromLocal) && Enum.IsDefined(Placement)
        && Uncertainty.IsValid && BreakReason is not null && Members is { Count: > 0 }
        && MeasuredLineage is null or > 0;

    internal static LiveTrackingIsland Legacy(int islandId, LiveWorldObservation observation) =>
        new(islandId, observation.RoomId, observation.Timestamp, observation.OptimizedPose,
            LiveIslandPlacement.LegacyUnknown, default, "legacy-unverified",
            [new LiveIslandMember(observation.Id, default, false)]);
}

public sealed record LiveIslandCapture(
    [property: JsonRequired] int Id,
    [property: JsonRequired] int RoomId,
    [property: JsonRequired] double StartedAt,
    [property: JsonRequired] AtlasPoint WorldFromLocal,
    [property: JsonRequired] AtlasPoint LocalPose,
    [property: JsonRequired] LiveIslandPlacement Placement,
    [property: JsonRequired] LiveIslandUncertainty Uncertainty,
    [property: JsonRequired] string BreakReason,
    [property: JsonRequired] bool Measured)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveWalkedGroundSource? UnplacedSource { get; init; }

    internal bool IsValid => Measured && Id >= 0 && RoomId >= 0 && double.IsFinite(StartedAt)
        && LiveWorldSnapshot.Finite(WorldFromLocal) && LiveWorldSnapshot.Finite(LocalPose)
        && Enum.IsDefined(Placement) && Uncertainty.IsValid && BreakReason is not null
        && (UnplacedSource is null || UnplacedSource.Lineage > 0);
}

/// <summary>The capture stream and image lineage of a foot measurement awaiting its first saved image.</summary>
public sealed record LiveWalkedGroundSource(
    [property: JsonRequired] ulong CaptureGeneration,
    [property: JsonRequired] long Lineage);

public sealed record LiveGroundLineEvidence(
    [property: JsonRequired] long SegmentId,
    [property: JsonRequired] AtlasPoint Start,
    [property: JsonRequired] AtlasPoint End)
{
    internal bool IsHorizontal => SegmentId >= 0 && LiveWorldSnapshot.Finite(Start)
        && LiveWorldSnapshot.Finite(End) && Start.X < End.X && Start.Y == End.Y;

    internal bool IsInImage(LiveWorldObservation observation) =>
        IsInImage(observation.SolveWidth, observation.SourceWidth, observation.SourceHeight);

    internal bool IsInImage(double solveWidth, int sourceWidth, int sourceHeight)
    {
        var height = sourceHeight * solveWidth / sourceWidth;
        return IsHorizontal && Start.X >= 0 && End.X <= solveWidth + 0.000001
            && Start.Y >= 0 && Start.Y <= height + 0.000001;
    }
}

public sealed record LiveIslandWalkedGround(
    [property: JsonRequired] LiveIslandCapture Owner,
    [property: JsonRequired] IReadOnlyList<LiveGroundLineEvidence> Spans)
{
    internal bool IsValid => Owner is { IsValid: true } && Spans is { Count: > 0 }
        && Spans.All(span => span is { IsHorizontal: true, SegmentId: long.MaxValue });
}

public sealed record LiveWorldGroundGeometry(
    [property: JsonRequired] IReadOnlyList<LiveGroundLineEvidence> Lines,
    IReadOnlyList<LiveIslandWalkedGround>? WalkedIslands = null)
{
    internal bool IsValid(LiveWorldObservation observation) => Lines is not null
        && Lines.All(line => line?.IsInImage(observation) == true)
        && (WalkedIslands is null || WalkedIslands.All(walk => walk?.IsValid == true));
}

public sealed record LiveObservedFloorCandidates(
    [property: JsonRequired] IReadOnlyList<LiveGroundLineEvidence> Lines)
{
    internal bool IsValid(LiveWorldObservation observation) => Lines is { Count: > 0 and <= 128 }
        && Lines.All(line => line?.IsInImage(observation) == true);
}

public sealed record LiveDoorwayFloorConstraint(
    [property: JsonRequired] int Id,
    [property: JsonRequired] int DepartureObservationId,
    [property: JsonRequired] int ArrivalObservationId,
    [property: JsonRequired] AtlasPoint DepartureFloor,
    [property: JsonRequired] AtlasPoint ArrivalFloor,
    [property: JsonRequired] int Direction,
    [property: JsonRequired] IReadOnlyList<int> DepartureSupport,
    [property: JsonRequired] IReadOnlyList<int> ArrivalSupport)
{
    internal bool IsValid(
        IReadOnlyDictionary<int, LiveWorldObservation> observations,
        IReadOnlyDictionary<int, (LiveTrackingIsland Island, LiveIslandMember Member)> owners,
        bool requiringAlignment = true)
    {
        if (Id < 0 || Direction is not (-1 or 1)
            || !observations.TryGetValue(DepartureObservationId, out var departure)
            || !observations.TryGetValue(ArrivalObservationId, out var arrival)
            || departure.RoomId == arrival.RoomId || departure.SolveWidth != arrival.SolveWidth
            || !owners.TryGetValue(departure.Id, out var source)
            || !owners.TryGetValue(arrival.Id, out var target)
            || source.Island.Placement == LiveIslandPlacement.LegacyUnknown || !source.Member.Measured
            || target.Island.Placement == LiveIslandPlacement.LegacyUnknown || !target.Member.Measured
            || !OnGround(DepartureFloor, departure) || !OnGround(ArrivalFloor, arrival))
        {
            return false;
        }

        var middle = departure.SolveWidth * 0.5;
        if (Direction == 1 ? DepartureFloor.X < middle || ArrivalFloor.X > middle
            : DepartureFloor.X > middle || ArrivalFloor.X < middle)
        {
            return false;
        }
        var unit = departure.SolveWidth / 640;
        var sourceY = departure.OptimizedPose.Y + DepartureFloor.Y;
        var targetY = arrival.OptimizedPose.Y + ArrivalFloor.Y;
        return (!requiringAlignment || Math.Abs(sourceY - targetY) <= 4 * unit)
            && StableSupport(DepartureSupport, departure, source.Island.Id, sourceY, unit,
                observations, owners, out _)
            && StableSupport(ArrivalSupport, arrival, target.Island.Id, targetY, unit,
                observations, owners, out var arrivingAt)
            && departure.Timestamp < arrivingAt;
    }

    private static bool OnGround(AtlasPoint point, LiveWorldObservation observation) =>
        LiveWorldSnapshot.Finite(point) && observation.GroundGeometry?.Lines.Any(line =>
            Math.Abs(line.Start.Y - point.Y) <= 0.000001
            && point.X >= line.Start.X && point.X <= line.End.X) == true;

    private static bool StableSupport(
        IReadOnlyList<int>? ids,
        LiveWorldObservation endpoint,
        int islandId,
        double worldY,
        double unit,
        IReadOnlyDictionary<int, LiveWorldObservation> observations,
        IReadOnlyDictionary<int, (LiveTrackingIsland Island, LiveIslandMember Member)> owners,
        out double firstTime)
    {
        firstTime = double.NaN;
        if (ids is not { Count: >= 3 and <= 16 } || ids.Distinct().Count() != ids.Count
            || !ids.Contains(endpoint.Id))
        {
            return false;
        }

        var frames = new List<LiveWorldObservation>(ids.Count);
        foreach (var id in ids)
        {
            if (!observations.TryGetValue(id, out var frame)
                || !owners.TryGetValue(id, out var owner)
                || owner.Island.Id != islandId || !owner.Member.Measured
                || frame.RoomId != endpoint.RoomId || frame.SolveWidth != endpoint.SolveWidth
                || frame.GroundGeometry?.Lines.Any(line =>
                    Math.Abs(frame.OptimizedPose.Y + line.Start.Y - worldY) <= 2 * unit) != true)
            {
                return false;
            }
            frames.Add(frame);
        }

        var times = frames.Select(frame => frame.Timestamp).Order().ToArray();
        firstTime = times[0];
        var duration = times[^1] - firstTime;
        return times[^1] == endpoint.Timestamp && duration is >= 0.25 and <= 2
            && frames.Max(frame => frame.OptimizedPose.Y) - frames.Min(frame => frame.OptimizedPose.Y) <= 4 * unit
            && times.Zip(times.Skip(1)).All(pair => pair.Second - pair.First is > 0 and <= 0.35);
    }
}
