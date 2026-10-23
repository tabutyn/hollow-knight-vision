namespace HollowKnightVision.Windows.Core;

public sealed record LiveObjectDetectionSnapshot(IReadOnlyList<ObjectDetection> Tracked,
    IReadOnlyList<ObjectDetection> Raw, IReadOnlyList<ObjectDetection> Masks, BgraFrame? PixelDiagnostic)
{
    public static LiveObjectDetectionSnapshot Empty { get; } = new([], [], [], null);
}

/// <summary>Owned by one capture worker. Display switches never change inference or masks.</summary>
public sealed class LiveObjectDetectionPipeline
{
    private readonly LiveObjectDetectionTracker tracker = new();
    private LiveObjectDetectionBatch? tracked, raw, measured, previous;
    private BgraFrame? pixels;
    public void Reset() { tracker.Reset(); tracked = raw = measured = previous = null; pixels = null; }

    public void Accept(IReadOnlyList<ObjectDetection> rawDetections, BgraFrame frame, long generation,
        double extensionFraction = .12, bool createsDiagnosticImage = false, double inferenceDuration = 0)
        => AcceptRefinement(rawDetections, LiveObjectPixelRefiner.Refine(rawDetections, frame,
            extensionFraction, createsDiagnosticImage), frame, generation, inferenceDuration);

    // The inference worker computes expensive pixel support outside its state lock.
    public void AcceptRefinement(IReadOnlyList<ObjectDetection> rawDetections, LiveObjectPixelRefinement refinement,
        BgraFrame frame, long generation, double inferenceDuration = 0)
    {
        var timestamp = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
        if (raw is not null && raw.CaptureGeneration == generation && timestamp < raw.SourceTimestamp) return;
        if (raw is not null && raw.CaptureGeneration != generation) Reset();
        LiveObjectDetectionBatch Batch(IReadOnlyList<ObjectDetection> detections) =>
            new(detections, frame.CaptureId, timestamp, generation, inferenceDuration);
        tracked = Batch(tracker.Update(refinement.Detections, generation));
        previous = measured; measured = Batch(refinement.Detections);
        raw = Batch(rawDetections.ToArray()); pixels = refinement.DiagnosticImage;
    }

    public LiveObjectDetectionSnapshot Snapshot(long generation, double timestamp)
    {
        var displayed = LiveObjectDetectionFreshness.Detections(tracked, generation, timestamp);
        var model = LiveObjectDetectionFreshness.Detections(raw, generation, timestamp);
        return new(displayed, model, MovingObjectMask.Detections(displayed, measured, previous, generation, timestamp),
            model.Count > 0 ? pixels : null);
    }
}
