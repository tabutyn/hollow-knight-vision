namespace HollowKnightVision.Windows.Core;

public readonly record struct RecentCapturedFrameIdentifier(long CaptureGeneration, long SourceFrameIdentifier);
public sealed record RecentCapturedFrame(long CaptureGeneration, long SourceFrameIdentifier,
    BgraFrame Image, IReadOnlyList<ObjectDetection> Detections)
{
    public RecentCapturedFrameIdentifier Id => new(CaptureGeneration, SourceFrameIdentifier);
}

/// <summary>Capture-owned pixels with exact-frame prediction replacement; never persists user data.</summary>
public sealed class RecentCapturedFrameBuffer(int capacity = RecentCapturedFrameBuffer.DefaultCapacity)
{
    public const int DefaultCapacity = 64;
    private readonly object gate = new();
    private readonly List<RecentCapturedFrame> frames = [];
    private long? generation;
    public int Capacity { get; } = Math.Max(1, capacity);
    public IReadOnlyList<RecentCapturedFrame> Frames { get { lock (gate) return frames.ToArray(); } }
    public void Append(RecentCapturedFrame frame)
    {
        lock (gate)
        {
            if (generation is { } current && frame.CaptureGeneration < current) return;
            if (generation != frame.CaptureGeneration) { frames.Clear(); generation = frame.CaptureGeneration; }
            frames.RemoveAll(item => item.Id == frame.Id);
            frames.Insert(0, frame with { Detections = frame.Detections.ToArray() });
            if (frames.Count > Capacity) frames.RemoveRange(Capacity, frames.Count - Capacity);
        }
    }
    public void ReplaceDetections(IReadOnlyList<ObjectDetection> detections, long captureGeneration, long sourceFrameIdentifier)
    {
        lock (gate)
        {
            var id = new RecentCapturedFrameIdentifier(captureGeneration, sourceFrameIdentifier);
            var index = frames.FindIndex(item => item.Id == id);
            if (index >= 0) frames[index] = frames[index] with { Detections = detections.ToArray() };
        }
    }
    public void RemoveAll() { lock (gate) { frames.Clear(); generation = null; } }
}
