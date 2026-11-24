namespace HollowKnightVision.Windows.Core;

/// <summary>Provides horizontal display spacing for a newly observed room.</summary>
public static class LiveEstimatedRoomLayout
{
    public static double? ArrivalCenterX(LiveWorldSnapshot snapshot, LiveDoorwayTransition transition,
        double captureTimestamp, double solveWidth)
    {
        if (!double.IsFinite(captureTimestamp) || !double.IsFinite(solveWidth) || solveWidth <= 0
            || !double.IsFinite(transition.DepartureCaptureTimestamp) || !double.IsFinite(transition.ArrivalCaptureTimestamp)
            || transition.ArrivalCaptureTimestamp <= transition.DepartureCaptureTimestamp
            || captureTimestamp - transition.ArrivalCaptureTimestamp is < 0 or > 3
            || transition.SourceRoomId == transition.TargetRoomId
            || transition.Direction is not (VisualRoomDirection.Left or VisualRoomDirection.Right)
            || snapshot.Observations.Any(row => row.RoomId == transition.TargetRoomId))
            return null;

        var unplaced = snapshot.UnplacedObservationIds();
        var departure = snapshot.Observations.Where(row =>
            row.RoomId == transition.SourceRoomId && !unplaced.Contains(row.Id)).ToArray();
        if (departure.Length == 0) return null;

        // One tenth of the arrival image width separates the room footprints.
        // The graph position is the center of the arrival image.
        var offset = solveWidth * .5 + solveWidth * .1;
        var center = transition.Direction == VisualRoomDirection.Right
            ? departure.Max(row => row.OptimizedPose.X + row.SolveWidth * .5) + offset
            : departure.Min(row => row.OptimizedPose.X - row.SolveWidth * .5) - offset;
        return double.IsFinite(center) ? center : null;
    }
}
