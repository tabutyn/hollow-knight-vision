using System.Diagnostics;

namespace HollowKnightVision.Windows.Core;

public sealed record LiveObjectInferenceDiagnostics(long? SourceFrameIdentifier = null,
    double? ModelSeconds = null, double? RefinementSeconds = null, string? Issue = null);
public sealed record LiveObjectInferencePublication(BgraFrame Frame, long Generation, IReadOnlyList<ObjectDetection> Detections);

/// <summary>
/// Serial model ownership with one active/newest pending capture. Snapshot reads,
/// suspension and invalidation never wait for ONNX or pixel refinement. Callers
/// stop submitting and await WhenIdleAsync before disposing the loaded model.
/// </summary>
public sealed class LiveObjectInferenceWorker
{
    // Mac samples every fourth 60 Hz frame. Windows captures at ~30 Hz, so
    // preserve the same nominal inference rate rather than copying stride 4.
    public const double MinimumLiveInterval = 4d / 60;
    public readonly record struct Statistics(ulong Submitted, ulong Started, ulong Completed,
        ulong Superseded, ulong Cancelled, ulong StaleSubmissions, ulong StaleCompletions);
    private sealed record Work(BgraFrame Frame, long Generation, Func<IReadOnlyList<ObjectDetection>> Infer,
        double Extension, bool CreatesPixels, Action<LiveObjectInferencePublication>? Publish);
    private readonly object gate = new();
    private readonly LatestFramePump<Work> pump = new();
    private readonly LiveObjectDetectionPipeline pipeline = new();
    private TaskCompletionSource idle = CompletedSource();
    private ulong epoch;
    private long? generation;
    private double? lastSubmitted;
    private bool suspended;
    private LiveObjectInferenceDiagnostics diagnostics = new();
    public int PendingFrameCount => pump.PendingCount;
    public bool IsProcessing => pump.IsProcessing;
    public LiveObjectInferenceDiagnostics Diagnostics { get { lock (gate) return diagnostics; } }
    public Statistics Counters
    {
        get { var value = pump.Counters; return new(value.Submitted, value.Started, value.Completed,
            value.Superseded, value.Cancelled, value.StaleSubmissions, value.StaleCompletions); }
    }
    public Task WhenIdleAsync() { lock (gate) return idle.Task; }

    public LiveObjectDetectionSnapshot Observe(BgraFrame frame, long captureGeneration,
        Func<IReadOnlyList<ObjectDetection>> infer, double extension = .12, bool createsPixels = false,
        bool asynchronously = true, double minimumInterval = MinimumLiveInterval,
        Action<LiveObjectInferencePublication>? onPublished = null)
    {
        ArgumentNullException.ThrowIfNull(infer);
        if (!double.IsFinite(minimumInterval) || minimumInterval < 0) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        var timestamp = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
        Task completion;
        lock (gate)
        {
            // A delayed caller cannot move an established generation backwards.
            if (generation is { } existing && captureGeneration < existing) return LiveObjectDetectionSnapshot.Empty;
            if (generation != captureGeneration) { InvalidateLocked(); generation = captureGeneration; }
            if (suspended) return LiveObjectDetectionSnapshot.Empty;
            if (lastSubmitted is null || timestamp > lastSubmitted &&
                (!asynchronously || timestamp - lastSubmitted + 1e-9 >= minimumInterval))
            {
                lastSubmitted = timestamp;
                var work = new Work(frame, captureGeneration, infer, extension, createsPixels, onPublished);
                if (pump.Submit(work, epoch) is { } ticket)
                {
                    idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = Task.Run(() => Process(ticket));
                }
            }
            if (asynchronously) return pipeline.Snapshot(captureGeneration, timestamp);
            completion = idle.Task;
        }
        // Exact/isolated analysis shares the live worker, not a separate model path.
        completion.GetAwaiter().GetResult();
        return Snapshot(captureGeneration, timestamp);
    }

    public LiveObjectDetectionSnapshot Snapshot(long captureGeneration, double timestamp)
    {
        lock (gate) return suspended || generation != captureGeneration ? LiveObjectDetectionSnapshot.Empty
            : pipeline.Snapshot(captureGeneration, timestamp);
    }
    public void Reset() { lock (gate) InvalidateLocked(); }
    public void SetSuspended(bool value)
    {
        lock (gate)
        {
            if (suspended == value) return;
            suspended = value; InvalidateLocked();
        }
    }
    private void InvalidateLocked()
    {
        pump.Invalidate(++epoch); pipeline.Reset(); generation = null; lastSubmitted = null; diagnostics = new();
    }
    private bool IsCurrent(LatestFramePump<Work>.Ticket ticket) =>
        ticket.Epoch == epoch && ticket.Item.Generation == generation && !suspended;

    private void Process(LatestFramePump<Work>.Ticket ticket)
    {
        while (true)
        {
            IReadOnlyList<ObjectDetection>? raw = null;
            LiveObjectPixelRefinement? refinement = null;
            Exception? failure = null;
            LiveObjectInferencePublication? publication = null;
            double modelSeconds = 0, refinementSeconds = 0;
            try
            {
                bool active;
                lock (gate) active = IsCurrent(ticket);
                if (active)
                {
                    var started = Stopwatch.GetTimestamp();
                    raw = ticket.Item.Infer();
                    modelSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    lock (gate) active = IsCurrent(ticket);
                    if (active)
                    {
                        started = Stopwatch.GetTimestamp();
                        refinement = LiveObjectPixelRefiner.Refine(raw, ticket.Item.Frame,
                            ticket.Item.Extension, ticket.Item.CreatesPixels);
                        refinementSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    }
                }
            }
            catch (Exception error) { failure = error; }
            lock (gate)
            {
                if (IsCurrent(ticket))
                {
                    if (failure is null && raw is not null && refinement is not null)
                    {
                        try
                        {
                            pipeline.AcceptRefinement(raw, refinement, ticket.Item.Frame, ticket.Item.Generation, modelSeconds);
                            diagnostics = new(ticket.Item.Frame.CaptureId, modelSeconds, refinementSeconds);
                            publication = new(ticket.Item.Frame, ticket.Item.Generation,
                                pipeline.Snapshot(ticket.Item.Generation,
                                    (ticket.Item.Frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds).Tracked);
                        }
                        catch (Exception error) { failure = error; }
                    }
                    // Like Mac, retain prior results only for their existing freshness
                    // interval. A failed model cannot stop capture/menu/HUD processing.
                    if (failure is not null) diagnostics = diagnostics with { Issue = $"Inference failed: {failure.Message}" };
                }
            }
            // Buffer updates belong to this exact capture, not whichever frame
            // the UI is displaying now. Keep callbacks outside the state lock,
            // but inside the task's lifetime so shutdown drains them too.
            if (publication is not null && ticket.Item.Publish is { } publish)
                try { publish(publication); }
                catch (Exception error)
                {
                    lock (gate) if (IsCurrent(ticket)) diagnostics = diagnostics with { Issue = $"Frame prediction update failed: {error.Message}" };
                }
            lock (gate)
            {
                var completion = pump.Complete(ticket);
                if (completion.Next is not { } next)
                {
                    idle.TrySetResult();
                    return;
                }
                ticket = next;
            }
        }
    }
    private static TaskCompletionSource CompletedSource()
    { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
}
