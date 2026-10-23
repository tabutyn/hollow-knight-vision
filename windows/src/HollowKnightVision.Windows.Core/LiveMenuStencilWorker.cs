using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace HollowKnightVision.Windows.Core;

/// <summary>Exact-frame Gameplay recognition, bounded asynchronous Hacker recognition.</summary>
public sealed class LiveMenuStencilWorker
{
    public sealed record Observation(MenuStencilResult? Result, string? Diagnostic);
    public sealed record Snapshot(Observation Observation, double Timestamp);
    private sealed record Frame(BgraFrame Image, double Timestamp);
    public const double MaximumResultAge = .25;
    private readonly LatestFramePump<Frame> pump = new();
    private readonly object admissionGate = new(), queueGate = new(), snapshotGate = new();
    private Task tail = Task.CompletedTask;
    private Snapshot? latest;
    private ExceptionDispatchInfo? failure;
    private ulong generation, workerGeneration;
    private double? lastProcessedTimestamp;
    private readonly Func<BgraFrame, double, Observation> recognize;
    private readonly Action reset;
    private readonly Action<MenuStencilCatalog> replaceCatalog;
    private readonly Action<double>? didProcess;
    public int PendingFrameCount => pump.PendingCount;
    public LiveMenuStencilWorker(Func<BgraFrame, double, Observation> recognize, Action? reset = null,
        Action<MenuStencilCatalog>? replaceCatalog = null, Action<double>? didProcess = null)
    { this.recognize = recognize; this.reset = reset ?? (() => { }); this.replaceCatalog = replaceCatalog ?? (_ => { }); this.didProcess = didProcess; }
    public LiveMenuStencilWorker(MenuStencilTracker tracker, Action<double>? didProcess = null)
        : this((frame, time) => new(tracker.Observe(frame, time), tracker.SelectorCalibrationDiagnostic),
            () => tracker.Reset(preservingLanguage: true), tracker.ReplaceCatalog, didProcess) { }
    public Snapshot? Observe(BgraFrame image, double timestamp, bool asynchronously)
    {
        if (!double.IsFinite(timestamp)) return null;
        lock (admissionGate)
        {
            lock (snapshotGate) failure?.Throw();
            var frame = new Frame(image, timestamp);
            if (asynchronously)
            {
                if (pump.Submit(frame, generation) is { } ticket) Enqueue(ticket);
                return SnapshotAt(timestamp);
            }
            pump.ClearPending();
            return Schedule(() => { var result = Process(frame); if (result is not null) Publish(result); return result; }).GetAwaiter().GetResult();
        }
    }
    public Snapshot? SnapshotAt(double timestamp)
    {
        lock (snapshotGate) return latest is { } value && double.IsFinite(timestamp) && timestamp >= value.Timestamp
            && timestamp - value.Timestamp <= MaximumResultAge ? value : null;
    }
    public void Reset() => Invalidate(reset);
    public void ReplaceCatalog(MenuStencilCatalog catalog) => Invalidate(() => replaceCatalog(catalog));
    private void Invalidate(Action update)
    {
        lock (admissionGate)
        {
            var next = ++generation; pump.Invalidate(next);
            Schedule(() =>
            {
                workerGeneration = next; lastProcessedTimestamp = null;
                lock (snapshotGate) { latest = null; failure = null; }
                update(); return true;
            }).GetAwaiter().GetResult();
        }
    }
    private Task<T> Schedule<T>(Func<T> action)
    {
        lock (queueGate)
        {
            var task = tail.ContinueWith(_ => action(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            tail = task; return task;
        }
    }
    private void Enqueue(LatestFramePump<Frame>.Ticket ticket)
    {
        _ = Schedule(() =>
        {
            Snapshot? result = null; ExceptionDispatchInfo? error = null;
            try { if (ticket.Epoch == workerGeneration) result = Process(ticket.Item); }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
            var completion = pump.Complete(ticket);
            if (completion.ShouldAcceptResult)
            {
                if (error is not null) { lock (snapshotGate) { failure = error; latest = null; } }
                else if (result is not null) Publish(result);
            }
            if (completion.Next is { } next) Enqueue(next);
            return true;
        });
    }
    private Snapshot? Process(Frame frame)
    {
        if (lastProcessedTimestamp is { } previous && frame.Timestamp <= previous) return null;
        lastProcessedTimestamp = frame.Timestamp; var started = Stopwatch.GetTimestamp();
        var observation = recognize(frame.Image, frame.Timestamp);
        didProcess?.Invoke(Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new(observation, frame.Timestamp);
    }
    private void Publish(Snapshot result) { lock (snapshotGate) latest = result; }
}
