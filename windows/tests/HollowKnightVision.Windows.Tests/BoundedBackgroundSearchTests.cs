using HollowKnightVision.Windows.Core;

internal static class BoundedBackgroundSearchTests
{
    private static void Check(bool value, string message = "Bounded background search parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    public static void TestBusySearchDoesNotQueueAndInvalidatedResultCannotPublish()
    {
        var worker = new BoundedBackgroundSearch<int>();
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        Check(worker.Submit(() =>
        {
            started.Set();
            _ = gate.Wait(TimeSpan.FromSeconds(2));
            finished.Set();
            return 41;
        }));
        Check(started.Wait(TimeSpan.FromSeconds(2)), "Initial search did not start.");
        Check(!worker.Submit(() => 99));
        worker.Invalidate();
        gate.Set();
        Check(finished.Wait(TimeSpan.FromSeconds(2)), "Initial search did not finish.");

        // Wait until the worker's completion transaction has finished by
        // submitting the next job; do not infer completion from its closure.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var submitted = false;
        while (!(submitted = worker.Submit(() => 42)) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(1));
        }
        Check(submitted, "Replacement search was not accepted.");

        var received = false;
        var value = 0;
        while (!(received = worker.TryTake(out value)) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(1));
        }
        Check(received && value == 42);
        Check(!worker.TryTake(out _));
    }

    public static void TestBusySearchNeverPreparesRejectedSnapshots()
    {
        var worker = new BoundedBackgroundSearch<int>();
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var submittingThread = Environment.CurrentManagedThreadId;
        var prepared = 0;
        Check(worker.SubmitPrepared(() =>
        {
            Check(Environment.CurrentManagedThreadId == submittingThread,
                "Snapshot must be prepared on its foreground owner.");
            prepared++;
            return () =>
            {
                started.Set();
                if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return 17;
            };
        }));
        try
        {
            Check(started.Wait(TimeSpan.FromSeconds(2)));
            for (var frame = 0; frame < 100; frame++)
                Check(!worker.SubmitPrepared(() => { prepared++; return () => 99; }));
            worker.Invalidate();
            Check(!worker.SubmitPrepared(() => { prepared++; return () => 99; }));
            Check(prepared == 1, "Busy or invalidated worker copied rejected history.");
        }
        finally { gate.Set(); }
        Check(SpinWait.SpinUntil(() => worker.Submit(() => 42), TimeSpan.FromSeconds(2)));
        var value = 0;
        Check(SpinWait.SpinUntil(() => worker.TryTake(out value), TimeSpan.FromSeconds(2)));
        Check(value == 42, "Obsolete prepared search escaped invalidation.");
    }

    public static void TestSnapshotFailureReleasesWorkerSlot()
    {
        var worker = new BoundedBackgroundSearch<int>();
        var failed = false;
        try { worker.SubmitPrepared(() => throw new InvalidOperationException("fixture")); }
        catch (InvalidOperationException ex) when (ex.Message == "fixture") { failed = true; }
        Check(failed);
        Check(worker.SubmitPrepared(() => () => 19));
        var value = 0;
        Check(SpinWait.SpinUntil(() => worker.TryTake(out value), TimeSpan.FromSeconds(2)));
        Check(value == 19);
    }

    public static void TestMaterialCorrectionRequiresIndependentConsistentObservations()
    {
        var gate = new GroundCorrectionConfirmation();
        Check(!gate.Accepts(new CameraVector(80, 0), 1));
        Check(!gate.Accepts(new CameraVector(-80, 0), 1.3));
        Check(gate.Accepts(new CameraVector(-79, 1), 1.6));
        gate.Reset();
        Check(!gate.Accepts(new CameraVector(80, 0), 2));
        Check(!gate.Accepts(new CameraVector(80, 0), 4));
        Check(!gate.Accepts(new CameraVector(3, 1), 4.1));
        Check(gate.Accepts(new CameraVector(3.2, 1.1), 4.35));
    }

    public static void TestCorrectionNoiseFloorNeverMovesCamera()
    {
        var gate = new GroundCorrectionConfirmation();
        Check(!gate.Accepts(new CameraVector(1.9, 0), 1));
        Check(!gate.Accepts(new CameraVector(1.8, 0.1), 1.3));
        Check(!gate.NeedsConfirmation);
    }

    public static void TestMidSizedCorrectionsMustAgreeClosely()
    {
        var gate = new GroundCorrectionConfirmation();
        Check(!gate.Accepts(new CameraVector(4, 0), 1));
        Check(!gate.Accepts(new CameraVector(2.5, 0), 1.3));
        Check(gate.Accepts(new CameraVector(2.6, 0.1), 1.6));
        gate.Reset();
        Check(!gate.Accepts(new CameraVector(3, 0), 2));
        Check(!gate.Accepts(new CameraVector(4.5, 0), 2.3));
        Check(gate.Accepts(new CameraVector(4.4, 0.1), 2.6));
    }
}
