namespace HollowKnightVision.Windows.Core;

/// <summary>Tracks the measured capture references of the active room and capture generation.</summary>
internal sealed class MeasuredAtlasPoseLineage
{
    private readonly VisualMotionPoseHistory measured = new();
    private (long Generation, string Room)? scope;
    private double? lastTimestamp;

    internal bool Observe(long generation, string room, double timestamp, AtlasPoint publishedPose,
        bool groundMeasured, CalibratedSceneMotionEvidence? scene, bool placementDiscontinuity)
    {
        if (!double.IsFinite(timestamp) || !double.IsFinite(publishedPose.X) || !double.IsFinite(publishedPose.Y))
        {
            Reset();
            return false;
        }
        var active = (generation, room);
        if (scope != active || placementDiscontinuity || timestamp <= lastTimestamp)
        {
            Reset();
            scope = active;
        }
        lastTimestamp = timestamp;
        var continues = scene is { HorizontalMeasured: true, VerticalMeasured: true, ReferencePosition: { } origin }
            && scene.Timestamp == timestamp && scene.Position == publishedPose
            && timestamp - scene.ReferenceTimestamp is > 0 and <= CaptureGroundReliability.MaximumAge
            && measured.PoseAt(scene.ReferenceTimestamp) == origin;
        if (groundMeasured || continues) measured.Record(timestamp, publishedPose, completed: null);
        return continues;
    }

    internal void Reset()
    {
        scope = null;
        lastTimestamp = null;
        measured.Reset();
    }
}
