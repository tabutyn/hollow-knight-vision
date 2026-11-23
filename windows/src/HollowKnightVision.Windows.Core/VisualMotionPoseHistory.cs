namespace HollowKnightVision.Windows.Core;

public readonly record struct VisualCameraPoseSample(double Timestamp, AtlasPoint CameraPosition);

/// <summary>Reconciles presentation estimates with completed solves at the exact capture timestamp.</summary>
internal sealed class VisualMotionPoseHistory
{
    private const int Capacity = 64;
    private readonly Dictionary<double, AtlasPoint> poses = [];
    private readonly Queue<double> captureOrder = [];

    internal void Reset()
    {
        poses.Clear();
        captureOrder.Clear();
    }

    internal void Record(double timestamp, AtlasPoint estimatedPose, VisualCameraPoseSample? completed)
    {
        if (!double.IsFinite(timestamp) || !IsFinite(estimatedPose)) return;
        if (!poses.ContainsKey(timestamp)) captureOrder.Enqueue(timestamp);
        poses[timestamp] = estimatedPose;
        if (completed is { } sample && double.IsFinite(sample.Timestamp) && sample.Timestamp <= timestamp
            && IsFinite(sample.CameraPosition) && poses.ContainsKey(sample.Timestamp))
        {
            poses[sample.Timestamp] = sample.CameraPosition;
        }
        while (captureOrder.Count > Capacity) poses.Remove(captureOrder.Dequeue());
    }

    internal AtlasPoint? PoseAt(double? timestamp) =>
        timestamp is { } time && poses.TryGetValue(time, out var value) ? value : null;

    private static bool IsFinite(AtlasPoint pose) => double.IsFinite(pose.X) && double.IsFinite(pose.Y);
}
