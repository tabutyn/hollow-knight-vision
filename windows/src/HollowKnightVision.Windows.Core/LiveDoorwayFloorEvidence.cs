namespace HollowKnightVision.Windows.Core;

/// <summary>A visually confirmed cut with source and target identities in the active capture.</summary>
public sealed record LiveDoorwayTransition(
    int SourceRoomId,
    int TargetRoomId,
    ulong CaptureGeneration,
    double DepartureCaptureTimestamp,
    double ArrivalCaptureTimestamp,
    VisualRoomDirection Direction);

internal static class LiveDoorwayFloorEvidence
{
    private sealed record Surface(AtlasPoint Point, double WorldY);

    internal static bool AtDepartureEdge(double midpoint, double width, bool rightSide) =>
        rightSide ? midpoint >= width * .82 : midpoint <= width * .18;

    public static LiveDoorwayFloorConstraint? Propose(LiveWorldSnapshot snapshot, LiveDoorwayTransition transition)
    {
        if (transition.SourceRoomId == transition.TargetRoomId
            || !double.IsFinite(transition.DepartureCaptureTimestamp)
            || !double.IsFinite(transition.ArrivalCaptureTimestamp)
            || transition.ArrivalCaptureTimestamp <= transition.DepartureCaptureTimestamp
            || transition.Direction is not (VisualRoomDirection.Left or VisualRoomDirection.Right)
            || snapshot.Observations.LastOrDefault() is not { CaptureTimestamp: { } newestTime } newest
            || newest.RoomId != transition.TargetRoomId || newest.CaptureGeneration != transition.CaptureGeneration
            || newestTime - transition.ArrivalCaptureTimestamp is < 0 or > 3)
        {
            return null;
        }

        var sourceFrames = Frames(transition.SourceRoomId, transition.DepartureCaptureTimestamp - 2,
            transition.DepartureCaptureTimestamp + .01);
        var targetFrames = Frames(transition.TargetRoomId, transition.ArrivalCaptureTimestamp,
            transition.ArrivalCaptureTimestamp + 3);
        if (sourceFrames.Length < 3 || targetFrames.Length < 3) return null;
        var exitsRight = transition.Direction == VisualRoomDirection.Right;

        var departure = Endpoint(sourceFrames, exitsRight, departing: true);
        var arrival = Endpoint(targetFrames, !exitsRight, departing: false);
        if (departure is not { } source || arrival is not { } target
            || transition.DepartureCaptureTimestamp - source.Frame.CaptureTimestamp!.Value > .75)
        {
            return null;
        }

        var observations = snapshot.Observations.ToDictionary(observation => observation.Id);
        if (snapshot.DoorwayFloorConstraints.Any(edge =>
        {
            var from = observations[edge.DepartureObservationId];
            var to = observations[edge.ArrivalObservationId];
            return from.RoomId == transition.SourceRoomId && to.RoomId == transition.TargetRoomId
                && from.CaptureGeneration == transition.CaptureGeneration && to.CaptureGeneration == transition.CaptureGeneration
                && from.CaptureTimestamp is { } time && Math.Abs(time - transition.DepartureCaptureTimestamp) <= .75
                && to.CaptureTimestamp >= transition.ArrivalCaptureTimestamp;
        }))
        {
            return null;
        }

        var owners = snapshot.TrackingIslands.SelectMany(island =>
            island.Members.Select(member => (Island: island, Member: member)))
            .ToDictionary(value => value.Member.ObservationId);
        var departureSupport = Support(sourceFrames, source.Frame, source.Floor, exitsRight);
        var arrivalSupport = Support(targetFrames, target.Frame, target.Floor, !exitsRight);
        if (!ProvesFootContact(departureSupport, source.Frame, source.Floor)
            || !ProvesFootContact(arrivalSupport, target.Frame, target.Floor))
        {
            return null;
        }

        var lastId = snapshot.DoorwayFloorConstraints.Select(edge => edge.Id).DefaultIfEmpty(-1).Max();
        if (lastId == int.MaxValue) return null;
        var candidate = new LiveDoorwayFloorConstraint(lastId + 1, source.Frame.Id, target.Frame.Id,
            source.Floor.Point, target.Floor.Point, (int)transition.Direction,
            departureSupport.Select(frame => frame.Id).ToArray(), arrivalSupport.Select(frame => frame.Id).ToArray());
        return candidate.IsValid(observations, owners, requiringAlignment: false) ? candidate : null;

        LiveWorldObservation[] Frames(int roomId, double minimum, double maximum) =>
            snapshot.Observations.Where(frame => frame.RoomId == roomId
                && frame.CaptureGeneration == transition.CaptureGeneration
                && frame.CaptureTimestamp is { } time && time >= minimum && time <= maximum)
                .TakeLast(16).ToArray();

        IReadOnlyList<LiveWorldObservation> Support(LiveWorldObservation[] frames, LiveWorldObservation endpoint,
            Surface floor, bool rightSide)
        {
            if (!owners.TryGetValue(endpoint.Id, out var owner)
                || owner.Island.Placement == LiveIslandPlacement.LegacyUnknown || owner.Island.RequiresPlacement || !owner.Member.Measured)
            {
                return [];
            }

            var unit = endpoint.SolveWidth / 640;
            var result = new List<LiveWorldObservation>();
            var endpointTime = endpoint.CaptureTimestamp!.Value;
            var previousTime = endpointTime;
            foreach (var frame in Enumerable.Reverse(frames).Where(frame => frame.CaptureTimestamp <= endpointTime))
            {
                var time = frame.CaptureTimestamp!.Value;
                if (endpointTime - time > 2 || previousTime - time > .35
                    || !owners.TryGetValue(frame.Id, out var current) || !current.Member.Measured
                    || current.Island.Id != owner.Island.Id || frame.SolveWidth != endpoint.SolveWidth
                    || Math.Abs(frame.OptimizedPose.Y - endpoint.OptimizedPose.Y) > 4 * unit
                    || FindSurface(frame, rightSide) is not { } measured
                    || Math.Abs(measured.WorldY - floor.WorldY) > 2 * unit)
                {
                    break;
                }
                result.Add(frame);
                previousTime = time;
                if (result.Count >= 3 && endpointTime - time >= .25) break;
            }
            result.Reverse();
            return result;
        }
    }

    private static (LiveWorldObservation Frame, Surface Floor)? Endpoint(
        IReadOnlyList<LiveWorldObservation> frames, bool rightSide, bool departing)
    {
        foreach (var frame in frames.Reverse())
        {
            if (frame.ObservedPlayerBounds is not { } player || FindSurface(frame, rightSide) is not { } floor)
                continue;
            var midpoint = player.X + player.Width * .5;
            var atDoor = departing ? AtDepartureEdge(midpoint, frame.SolveWidth, rightSide)
                : rightSide ? midpoint >= frame.SolveWidth * .5 : midpoint <= frame.SolveWidth * .5;
            if (atDoor && FootHeight(frame, floor) is not null)
                return (frame, floor);
        }
        return null;
    }

    private static bool ProvesFootContact(IReadOnlyList<LiveWorldObservation> frames,
        LiveWorldObservation endpoint, Surface floor)
    {
        var contacts = frames.Select(frame => (Time: frame.CaptureTimestamp!.Value, Height: FootHeight(frame, floor)))
            .Where(value => value.Height is not null).ToArray();
        return contacts.Length >= 2 && contacts[^1].Time == endpoint.CaptureTimestamp
            && contacts[^1].Time - contacts[0].Time >= .15
            && contacts.Max(value => value.Height) - contacts.Min(value => value.Height) <= 6 * endpoint.SolveWidth / 640;
    }

    private static double? FootHeight(LiveWorldObservation frame, Surface floor)
    {
        var unit = frame.SolveWidth / 640;
        var height = frame.SolveWidth * frame.SourceHeight / frame.SourceWidth;
        if (frame.ObservedPlayerBounds is not { } player || player.Width < 3 * unit
            || player.Width > frame.SolveWidth * .3 || player.Height < 8 * unit || player.Height > height * .5)
        {
            return null;
        }
        var footY = frame.OptimizedPose.Y + player.Y;
        var footX = player.X + player.Width * .5;
        return Math.Abs(footY - floor.WorldY) <= 6 * unit && frame.GroundGeometry?.Lines.Any(line =>
            Math.Abs(frame.OptimizedPose.Y + line.Start.Y - floor.WorldY) <= 2 * unit
            && footX >= line.Start.X - 4 * unit && footX <= line.End.X + 4 * unit) == true ? footY : null;
    }

    private static Surface? FindSurface(LiveWorldObservation frame, bool rightSide)
    {
        if (frame.GroundGeometry is not { } geometry) return null;

        var unit = frame.SolveWidth / 640;
        var cameraMiddle = frame.SolveWidth * .5;
        var imageHeight = frame.SourceHeight * frame.SolveWidth / frame.SourceWidth;
        var rows = geometry.Lines.Where(line => line.Start.Y < imageHeight * .5
            && (rightSide ? line.End.X >= cameraMiddle : line.Start.X <= cameraMiddle))
            .GroupBy(line => line.Start.Y);
        Surface? accepted = null;
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(line => line.Start.X).ToArray();
            var start = ordered[0].Start.X;
            var end = start;
            var length = 0d;
            foreach (var line in ordered)
            {
                length += Math.Max(0, line.End.X - Math.Max(end, line.Start.X));
                end = Math.Max(end, line.End.X);
            }
            if (length < 48 * unit) continue;
            if (accepted is not null) return null;
            accepted = new Surface(new AtlasPoint(rightSide ? end : start, row.Key), frame.OptimizedPose.Y + row.Key);
        }
        return accepted;
    }
}
