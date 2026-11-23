namespace HollowKnightVision.Windows.Core;

/// <summary>Tracks local camera motion when ground registration has dropped out.</summary>
internal sealed class FloorlessCameraMotion
{
    private readonly VisualMotionPoseHistory history = new();
    private LowResolutionMotionGrid? previousGrid;
    private double? previousTimestamp;
    private double? acceptedAt;

    internal LowResolutionTransitionOdometry Odometry { get; } = new();
    internal AtlasPoint? Anchor { get; private set; }
    internal AtlasPoint? Position { get; private set; }
    internal AtlasPoint? PublishedPose => acceptedAt is not null ? Position : null;
    internal ulong Revision { get; private set; }

    internal void Observe(LowResolutionMotionGrid? grid, double timestamp, double solveWidth,
        AtlasPoint current, bool groundReliable, VisualRoomDirection? direction,
        LowResolutionCameraVelocitySeed? velocity, VisualCameraPoseSample? completed = null)
    {
        history.Record(timestamp, current, completed);
        if (groundReliable) ResetTraversal();
        else Track(grid, timestamp, solveWidth, current, direction, velocity);
        if (grid is not null)
        {
            previousGrid = grid;
            previousTimestamp = timestamp;
        }
    }

    internal void RecordPublishedPose(double timestamp, AtlasPoint pose) => history.Record(timestamp, pose, null);

    private void Track(LowResolutionMotionGrid? grid, double timestamp, double solveWidth,
        AtlasPoint current, VisualRoomDirection? direction, LowResolutionCameraVelocitySeed? velocity)
    {
        if (Anchor is null)
        {
            Revision = unchecked(Revision + 1);
            var referenceTime = previousTimestamp ?? timestamp;
            Anchor = history.PoseAt(referenceTime) ?? current;
            Odometry.Arm(previousGrid ?? grid, referenceTime);
            Odometry.PrimeHorizontalVelocity(velocity, direction);
        }
        if (acceptedAt is { } accepted && timestamp - accepted > LowResolutionTransitionOdometry.MaximumSampleAge)
            Release();
        if (acceptedAt is null && history.PoseAt(Odometry.ReferenceTimestamp) is { } reference)
        {
            // A completed solve belongs to the image from which odometry starts.
            Anchor = new AtlasPoint(reference.X - Odometry.WorldDeltaX, reference.Y - Odometry.WorldDeltaY);
        }
        if (Odometry.Observe(grid, timestamp, solveWidth, direction) is not null && Odometry.LastEstimateIsComplete)
            acceptedAt = timestamp;
        else if (Odometry.LastObservationTimestamp == timestamp)
            Release();

        var anchor = Anchor!.Value;
        Position = new AtlasPoint(anchor.X + Odometry.WorldDeltaX, anchor.Y + Odometry.WorldDeltaY);
    }

    internal void ReanchorAtPlace(AtlasPoint pose, LowResolutionMotionGrid? grid, double timestamp)
    {
        Revision = unchecked(Revision + 1);
        Anchor = Position = pose;
        acceptedAt = timestamp;
        Odometry.Arm(grid, timestamp);
    }

    internal bool ApplyCorrection(CameraVector correction, ulong revision)
    {
        if (revision != Revision || !double.IsFinite(correction.X) || !double.IsFinite(correction.Y)
            || Anchor is not { } anchor || PublishedPose is not { } position)
            return false;
        Anchor = new AtlasPoint(anchor.X + correction.X, anchor.Y + correction.Y);
        Position = new AtlasPoint(position.X + correction.X, position.Y + correction.Y);
        Revision = unchecked(Revision + 1);
        return true;
    }

    private void Release()
    {
        if (acceptedAt is not null) Revision = unchecked(Revision + 1);
        acceptedAt = null;
    }

    internal void ResetTraversal()
    {
        if (Anchor is not null) Revision = unchecked(Revision + 1);
        Odometry.Reset();
        Anchor = Position = null;
        acceptedAt = null;
    }

    internal void Reset()
    {
        ResetTraversal();
        history.Reset();
        previousGrid = null;
        previousTimestamp = null;
    }
}
