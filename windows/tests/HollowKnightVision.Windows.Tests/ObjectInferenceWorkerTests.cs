using HollowKnightVision.Windows.Core;

internal static class ObjectInferenceWorkerTests
{
    private static void Check(bool value, string message = "Object worker assertion failed.")
    { if (!value) throw new Exception(message); }
    private static BgraFrame Frame(double time, long id = 1) => new(20, 20, 80,
        Enumerable.Range(0, 1600).Select(index => (byte)(index % 4 == 3 ? 255 : 0)).ToArray(),
        id, DateTimeOffset.UnixEpoch.AddSeconds(time));
    private static ObjectDetection Body(long id = 1, double x = .1, float confidence = .9f) =>
        new("game.playable-knight", new(x, .5, .05, .1), confidence)
        { SourceFrameIdentifier = id, ModelVersion = new(1, 0) };
    private static void Wait(ManualResetEventSlim signal) => Check(signal.Wait(TimeSpan.FromSeconds(3)), "Worker signal did not arrive.");
    private static void Drain(LiveObjectInferenceWorker worker) => Check(worker.WhenIdleAsync().Wait(TimeSpan.FromSeconds(3)), "Worker did not drain.");
    private static void Release(ManualResetEventSlim signal)
    { if (!signal.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Isolated model was not released."); }

    public static void TestNonblockingLatestOnly()
    {
        var worker = new LiveObjectInferenceWorker(); var seen = new List<long>();
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var active = 0; var maximumActive = 0;
        IReadOnlyList<ObjectDetection> Infer(long id)
        {
            var concurrent = Interlocked.Increment(ref active); maximumActive = Math.Max(maximumActive, concurrent);
            try { seen.Add(id); if (id == 1) { started.Set(); Release(release); } return [Body(id)]; }
            finally { Interlocked.Decrement(ref active); }
        }
        try
        {
            var first = Task.Run(() => worker.Observe(Frame(1), 1, () => Infer(1), extension: 0, minimumInterval: 0));
            Check(first.Wait(TimeSpan.FromSeconds(2)), "Capture waited for the slow model."); Wait(started);
            Check(first.Result.Raw.Count == 0);
            for (var id = 2; id <= 4; id++)
            { var captured = id; worker.Observe(Frame(1 + id * .01, id), 1, () => Infer(captured), extension: 0, minimumInterval: 0); }
            Check(worker.PendingFrameCount == 1 && worker.IsProcessing && !worker.WhenIdleAsync().IsCompleted);
            release.Set(); Drain(worker);
            Check(seen.SequenceEqual(new long[] { 1, 4 }) && maximumActive == 1, "Obsolete frames ran or model calls overlapped.");
            Check(worker.Snapshot(1, 1.04).Raw.Single().SourceFrameIdentifier == 4);
            Check(worker.Counters == new LiveObjectInferenceWorker.Statistics(4, 2, 2, 2, 0, 0, 0));
            Check(worker.Diagnostics.SourceFrameIdentifier == 4 && worker.Diagnostics.ModelSeconds >= 0 && worker.Diagnostics.RefinementSeconds >= 0);
        }
        finally { release.Set(); worker.Reset(); Drain(worker); }
    }

    public static void TestDelayedMotionMasks()
    {
        var worker = new LiveObjectInferenceWorker();
        worker.Observe(Frame(1), 1, () => [Body()], extension: 0, asynchronously: false);
        worker.Observe(Frame(1.1, 2), 1, () => [Body(2, .15)], extension: 0, asynchronously: false);
        var delayed = worker.Snapshot(1, 1.2);
        Check(delayed.Raw.Single().Box.X == .15 && delayed.Tracked.Single().Box.X < .15);
        var mask = delayed.Masks.Single().Box;
        Check(mask.X < .15 && mask.X + mask.Width > .25, "Async age was lost; moving body remains exposed to mapping.");
        Check(worker.Snapshot(1, 1.501).Tracked.Count == 0 && worker.Snapshot(1, .99).Raw.Count == 0);
        Check(worker.Snapshot(2, 1.2).Masks.Count == 0);
    }

    public static void TestResetRejectsInFlightAndPending()
    {
        foreach (var throws in new[] { false, true })
        {
            var worker = new LiveObjectInferenceWorker(); var seen = new List<long>();
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                worker.Observe(Frame(10), 1, () =>
                {
                    seen.Add(1); started.Set(); Release(release);
                    if (throws) throw new InvalidOperationException("old model failure");
                    return [Body()];
                });
                Wait(started);
                worker.Observe(Frame(10.1, 2), 1, () => { seen.Add(2); return [Body(2)]; });
                var reset = Task.Run(worker.Reset); Check(reset.Wait(TimeSpan.FromSeconds(2)), "Invalidation waited for inference.");
                Check(worker.PendingFrameCount == 0 && worker.Snapshot(1, 10.1).Raw.Count == 0);
                worker.Observe(Frame(1, 3), 2, () => { seen.Add(3); return [Body(3, confidence: .7f)]; });
                release.Set(); Drain(worker);
                var current = worker.Snapshot(2, 1);
                Check(seen.SequenceEqual(new long[] { 1, 3 }) && current.Raw.Single().SourceFrameIdentifier == 3);
                Check(current.Tracked.Count == 0 && worker.Diagnostics.Issue is null, "Old epoch restored tracks or its error after reset.");
                Check(worker.Counters.Cancelled == 1 && worker.Counters.StaleCompletions == 1);
                Check(worker.Observe(Frame(11), 1, () => throw new Exception("stale caller ran")).Raw.Count == 0);
            }
            finally { release.Set(); worker.Reset(); Drain(worker); }
        }
    }

    public static void TestLabelingSuspension()
    {
        var worker = new LiveObjectInferenceWorker(); var calls = 0;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            worker.Observe(Frame(1), 1, () => { Interlocked.Increment(ref calls); started.Set(); Release(release); return [Body()]; });
            Wait(started); worker.SetSuspended(true);
            worker.Observe(Frame(1.1), 1, () => { Interlocked.Increment(ref calls); return [Body()]; });
            worker.SetSuspended(true); // An unchanged pause must not restart anything.
            release.Set(); Drain(worker);
            Check(calls == 1 && worker.Snapshot(1, 1.1).Masks.Count == 0);
            worker.SetSuspended(false);
            var resumed = worker.Observe(Frame(1.2, 2), 1, () => [Body(2, confidence: .7f)], asynchronously: false);
            Check(resumed.Raw.Count == 1 && resumed.Tracked.Count == 0, "Resume reused a pre-label confirmed track.");
        }
        finally { release.Set(); worker.SetSuspended(true); Drain(worker); }
    }

    public static void TestFailuresRecoverWithoutStoppingCapture()
    {
        var worker = new LiveObjectInferenceWorker();
        worker.Observe(Frame(1), 1, () => [Body()], asynchronously: false);
        worker.Observe(Frame(1.1, 2), 1, () => throw new InvalidOperationException("model offline"));
        Drain(worker);
        Check(worker.Diagnostics.Issue == "Inference failed: model offline");
        Check(worker.Snapshot(1, 1.2).Raw.Single().SourceFrameIdentifier == 1 && worker.Snapshot(1, 1.401).Raw.Count == 0);
        worker.Observe(Frame(1.5, 3), 1, () => [Body(3)], asynchronously: false);
        Check(worker.Diagnostics.Issue is null && worker.Diagnostics.SourceFrameIdentifier == 3);
        worker.Reset(); Check(worker.Diagnostics.SourceFrameIdentifier is null && worker.Diagnostics.Issue is null);
    }

    public static void TestLiveCadenceAndMonotonicity()
    {
        var worker = new LiveObjectInferenceWorker(); var seen = new List<long>();
        void Observe(double time, long id)
        { worker.Observe(Frame(time, id), 1, () => { seen.Add(id); return [Body(id)]; }); Drain(worker); }
        Observe(0, 0); Observe(.016, 1); Observe(.032, 2); Observe(.048, 3);
        Observe(.0666667, 4); Observe(.099, 5); Observe(.1333334, 6);
        Observe(.12, 7); Observe(.1333334, 8);
        Check(seen.SequenceEqual(new long[] { 0, 4, 6 }), "Nominal 15 Hz admission duplicated, rewound or overran model calls.");
        // Exact analysis bypasses live cadence, but not duplicate-clock rejection.
        worker.Observe(Frame(.14, 9), 1, () => { seen.Add(9); return [Body(9)]; }, asynchronously: false);
        worker.Observe(Frame(.14, 10), 1, () => throw new Exception("duplicate ran"), asynchronously: false);
        Check(seen.SequenceEqual(new long[] { 0, 4, 6, 9 }));
        worker.Reset(); Observe(0, 11); Check(seen.Last() == 11);
    }

    public static void TestCapturedRefinementSettings()
    {
        var worker = new LiveObjectInferenceWorker();
        using var firstStarted = new ManualResetEventSlim(); using var firstRelease = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim(); using var secondRelease = new ManualResetEventSlim();
        try
        {
            worker.Observe(Frame(1), 1, () => { firstStarted.Set(); Release(firstRelease); return [Body()]; }, extension: 0);
            Wait(firstStarted);
            worker.Observe(Frame(1.1, 2), 1, () => { secondStarted.Set(); Release(secondRelease); return [Body(2)]; }, extension: .5, createsPixels: true);
            firstRelease.Set(); Wait(secondStarted);
            var first = worker.Snapshot(1, 1.1);
            Check(Math.Abs(first.Tracked.Single().Box.Width - .05) < .000001 && first.PixelDiagnostic is null);
            secondRelease.Set(); Drain(worker);
            var second = worker.Snapshot(1, 1.1);
            Check(second.PixelDiagnostic?.CaptureId == 2 && second.Raw.Single().Box.Width == .05);
            Check(Math.Abs(second.Tracked.Single().Box.Width - .079) < .000001, "Settings were read at completion instead of capture admission.");
        }
        finally { firstRelease.Set(); secondRelease.Set(); worker.Reset(); Drain(worker); }
    }

    public static void TestDrainProtectsModelLifetime()
    {
        var worker = new LiveObjectInferenceWorker(); var disposed = false; var usedAfterDispose = false;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            worker.Observe(Frame(1), 1, () => { started.Set(); Release(release); usedAfterDispose = Volatile.Read(ref disposed); return [Body()]; });
            Wait(started);
            worker.SetSuspended(true); var drain = worker.WhenIdleAsync();
            Check(!drain.IsCompleted && worker.Snapshot(1, 1).Raw.Count == 0);
            release.Set(); Check(drain.Wait(TimeSpan.FromSeconds(3))); Volatile.Write(ref disposed, true);
            Check(!usedAfterDispose && !worker.IsProcessing && worker.PendingFrameCount == 0);
            Check(worker.Observe(Frame(2), 1, () => throw new ObjectDisposedException("model")).Raw.Count == 0);
        }
        finally { release.Set(); worker.SetSuspended(true); Drain(worker); }
    }

    public static void TestExactCapturePublication()
    {
        var worker = new LiveObjectInferenceWorker(); var buffer = new RecentCapturedFrameBuffer();
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var publications = new List<LiveObjectInferencePublication>();
        void Publish(LiveObjectInferencePublication publication)
        {
            publications.Add(publication);
            // Capture-session identity is independent of model/reference generations.
            buffer.ReplaceDetections(publication.Detections, 17, publication.Frame.CaptureId);
        }
        var first = Frame(1); buffer.Append(new(17, first.CaptureId, first, []));
        try
        {
            worker.Observe(first, 30, () => { started.Set(); Release(release); return [Body()]; },
                extension: 0, onPublished: Publish); Wait(started);
            for (var id = 2; id <= 3; id++)
            {
                var frame = Frame(1 + id * .1, id); buffer.Append(new(17, id, frame, []));
                worker.Observe(frame, 30, () => [Body(frame.CaptureId)], extension: 0, onPublished: Publish);
            }
            release.Set(); Drain(worker);
            Check(publications.Select(item => item.Frame.CaptureId).SequenceEqual(new long[] { 1, 3 }));
            Check(publications.All(item => item.Generation == 30) && ReferenceEquals(publications[0].Frame, first));
            Check(buffer.Frames.Select(item => item.SourceFrameIdentifier).SequenceEqual(new long[] { 3, 2, 1 }));
            Check(buffer.Frames[0].Detections.Single().SourceFrameIdentifier == 3 && buffer.Frames[1].Detections.Count == 0
                && buffer.Frames[2].Detections.Single().SourceFrameIdentifier == 1, "Late inference attached to the currently displayed frame.");
            var newRun = Frame(2); buffer.Append(new(18, 1, newRun, []));
            Publish(publications[0]); Check(buffer.Frames.Single().Detections.Count == 0, "Late callback crossed capture sessions.");
        }
        finally { release.Set(); worker.Reset(); Drain(worker); }
    }

    public static void TestPublicationDrainAndRecovery()
    {
        var worker = new LiveObjectInferenceWorker();
        using var published = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            worker.Observe(Frame(1), 1, () => [Body()], onPublished: _ => { published.Set(); Release(release); });
            Wait(published); Check(!worker.WhenIdleAsync().IsCompleted, "Shutdown did not include a running publication.");
            var snapshot = Task.Run(() => worker.Snapshot(1, 1));
            Check(snapshot.Wait(TimeSpan.FromSeconds(2)) && snapshot.Result.Tracked.Count == 1, "Callback held the capture-state lock.");
            release.Set(); Drain(worker);
            worker.Observe(Frame(1.1, 2), 1, () => [Body(2)], asynchronously: false,
                onPublished: _ => throw new InvalidOperationException("isolated callback error"));
            Check(worker.Diagnostics.Issue == "Frame prediction update failed: isolated callback error"
                && worker.Snapshot(1, 1.1).Raw.Single().SourceFrameIdentifier == 2);
            var calls = 0;
            worker.Observe(Frame(1.2, 3), 1, () => [Body(3)], asynchronously: false, onPublished: _ => calls++);
            Check(calls == 1 && worker.Diagnostics.Issue is null, "Callback failure stopped the worker or did not recover.");
        }
        finally { release.Set(); worker.Reset(); Drain(worker); }
    }

    public static void TestInvalidatedInferenceDoesNotPublish()
    {
        foreach (var suspend in new[] { false, true })
        {
            var worker = new LiveObjectInferenceWorker(); var calls = 0;
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            try
            {
                worker.Observe(Frame(1), 1, () => { started.Set(); Release(release); return [Body()]; }, onPublished: _ => calls++);
                Wait(started);
                worker.Observe(Frame(1.1, 2), 1, () => [Body(2)], onPublished: _ => calls++);
                if (suspend) worker.SetSuspended(true); else worker.Reset();
                release.Set(); Drain(worker); Check(calls == 0, "Invalidated or paused inference changed a buffered capture.");
            }
            finally { release.Set(); worker.Reset(); Drain(worker); }
        }
    }
}
