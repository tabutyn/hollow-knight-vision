using HollowKnightVision.Windows.Core;

internal static class MenuWorkerTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Menu worker assertion failed."); }
    private static BgraFrame Frame() => new(1, 1, 4, new byte[] { 255, 255, 255, 255 }, 0, DateTimeOffset.UnixEpoch);
    public static void TestPumpTelemetry()
    {
        var pump = new LatestFramePump<string>(1);
        var active = pump.Submit("active", 1)!; pump.Submit("overloaded", 1); pump.Submit("cancelled on restart", 1);
        pump.Invalidate(2); pump.Submit("stale producer", 1); pump.Submit("new generation", 2);
        var current = pump.Complete(active).Next!; pump.Complete(current); pump.Complete(current);
        Check(pump.Counters == new LatestFramePump<string>.Statistics(4, 2, 2, 1, 1, 1, 1));
    }
    public static void TestPumpBounds()
    {
        var discarded = new List<string>(); var pump = new LatestFramePump<string>(4, discarded.Add);
        var first = pump.Submit("first", 4)!;
        Check(first.Item == "first" && pump.IsProcessing && pump.PendingCount == 0);
        var done = pump.Complete(first); Check(done.ShouldAcceptResult && done.Next is null && !pump.IsProcessing);
        first = pump.Submit("active", 4)!;
        Check(pump.Submit("obsolete", 4) is null && pump.Submit("latest", 4) is null && pump.PendingCount == 1);
        Check(discarded.SequenceEqual(new[] { "obsolete" }));
        done = pump.Complete(first); Check(done.ShouldAcceptResult && done.Next?.Item == "latest" && pump.IsProcessing);
        done = pump.Complete(done.Next!); Check(done.ShouldAcceptResult && done.Next is null && !pump.IsProcessing);
        first = pump.Submit("active", 4)!; pump.Submit("pending", 4); pump.ClearPending(); done = pump.Complete(first);
        Check(done.ShouldAcceptResult && done.Next is null && !pump.IsProcessing && pump.PendingCount == 0);
    }
    public static void TestPumpInvalidation()
    {
        var pump = new LatestFramePump<string>(7); var old = pump.Submit("old", 7)!;
        Check(pump.Submit("stale pending", 7) is null); pump.Invalidate(8);
        Check(pump.PendingCount == 0 && pump.Submit("obsolete epoch", 7) is null && pump.Submit("current", 8) is null);
        var done = pump.Complete(old); Check(!done.ShouldAcceptResult && done.Next?.Item == "current" && done.Next.Epoch == 8);
        pump.Invalidate(6); // An older invalidation cannot rewind the epoch.
        done = pump.Complete(done.Next!); Check(done.ShouldAcceptResult && done.Next is null && !pump.IsProcessing);
    }
    public static void TestHackerLatestOnly()
    {
        using var started = new ManualResetEventSlim(); using var latestStarted = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var seen = new List<double>(); var frame = Frame();
        var worker = new LiveMenuStencilWorker((_, time) =>
        {
            seen.Add(time); if (time == 1) { started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            if (time == 4) latestStarted.Set(); return new(null, time.ToString());
        });
        try
        {
            Check(worker.Observe(frame, 1, true) is null && started.Wait(TimeSpan.FromSeconds(2)));
            foreach (var time in new[] { 2, 3, 4 }) Check(worker.Observe(frame, time, true) is null);
            release.Set(); Check(latestStarted.Wait(TimeSpan.FromSeconds(2)));
            Check(worker.Observe(frame, 4, false) is null && worker.SnapshotAt(4)?.Observation.Diagnostic == "4");
            Check(seen.SequenceEqual(new[] { 1d, 4d }));
        }
        finally { release.Set(); worker.Reset(); }
    }
    public static void TestExactAndMonotonic()
    {
        var seen = new List<double>(); var worker = new LiveMenuStencilWorker((_, time) => { seen.Add(time); return new(null, time.ToString()); });
        var frame = Frame(); var first = worker.Observe(frame, 10, false); var second = worker.Observe(frame, 11, false);
        Check(first?.Timestamp == 10 && first.Observation.Diagnostic == "10" && second?.Timestamp == 11 && second.Observation.Diagnostic == "11");
        Check(worker.Observe(frame, 9, false) is null && worker.Observe(frame, 11, false) is null && worker.SnapshotAt(11)?.Timestamp == 11);
        Check(seen.SequenceEqual(new[] { 10d, 11d }));
    }
    public static void TestFreshness()
    {
        var worker = new LiveMenuStencilWorker((_, _) => new(null, "menu")); worker.Observe(Frame(), 10, false);
        Check(worker.SnapshotAt(10.25) is not null && worker.SnapshotAt(10.251) is null && worker.SnapshotAt(9.99) is null && worker.SnapshotAt(double.NaN) is null);
        Check(worker.Observe(Frame(), double.NaN, true) is null && worker.Observe(Frame(), double.PositiveInfinity, false) is null);
    }
    public static void TestResetAndMissingEvidence()
    {
        var resets = 0; var replacements = 0;
        var worker = new LiveMenuStencilWorker((_, time) => new(null, time == 2 ? null : "menu"), () => resets++, _ => replacements++);
        worker.Observe(Frame(), 10, false); worker.Reset(); Check(resets == 1 && worker.SnapshotAt(10) is null);
        Check(worker.Observe(Frame(), 1, false)?.Timestamp == 1); worker.Observe(Frame(), 2, false);
        Check(worker.SnapshotAt(2)?.Timestamp == 2 && worker.SnapshotAt(2)?.Observation.Diagnostic is null);
        worker.ReplaceCatalog(new([], 1, 1, 0, 1, []));
        Check(replacements == 1 && worker.SnapshotAt(2) is null && worker.Observe(Frame(), 0, false)?.Timestamp == 0);
    }
    public static void TestAsynchronousFailureRecovery()
    {
        var fails = true; var worker = new LiveMenuStencilWorker((_, _) => fails ? throw new InvalidOperationException("test scan") : new(null, "recovered"));
        worker.Observe(Frame(), 1, true);
        // A synchronous queue barrier drains the first task; its exception must
        // be delivered on the caller, not silently kill background recognition.
        try { worker.Observe(Frame(), 2, false); throw new Exception("Missing scan failure."); }
        catch (InvalidOperationException error) when (error.Message == "test scan") { }
        fails = false; worker.Reset(); Check(worker.Observe(Frame(), 1, false)?.Observation.Diagnostic == "recovered");
    }
    public static void TestInFlightInvalidation()
    {
        foreach (var replace in new[] { false, true })
        {
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var seen = new List<double>();
            var worker = new LiveMenuStencilWorker((_, time) =>
            {
                seen.Add(time); started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return new(null, "old generation");
            });
            Task? invalidation = null;
            try
            {
                worker.Observe(Frame(), 10, true); Check(started.Wait(TimeSpan.FromSeconds(2)));
                worker.Observe(Frame(), 11, true); Check(worker.PendingFrameCount == 1);
                invalidation = Task.Run(() => { if (replace) worker.ReplaceCatalog(new([], 1, 1, 0, 1, [])); else worker.Reset(); });
                Check(SpinWait.SpinUntil(() => worker.PendingFrameCount == 0, TimeSpan.FromSeconds(2)));
                release.Set(); Check(invalidation.Wait(TimeSpan.FromSeconds(2)));
                Check(worker.SnapshotAt(10) is null && seen.SequenceEqual(new[] { 10d }));
                Check(worker.Observe(Frame(), 1, false)?.Timestamp == 1);
            }
            finally { release.Set(); invalidation?.GetAwaiter().GetResult(); worker.Reset(); }
        }
    }
}
