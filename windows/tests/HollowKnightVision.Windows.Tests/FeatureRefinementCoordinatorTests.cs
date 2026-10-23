using HollowKnightVision.Windows.Core;

internal static class FeatureRefinementCoordinatorTests
{
    public static void TestSlowRefinementDoesNotBlockRegistrationPublication()
    {
        var owner = OwnerQueue();
        var coordinator = new FeatureRefinementCoordinator<int>(owner.ExclusiveScheduler);
        using var started = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        using var returnedImmediately = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var request = new FeatureRefinementRequest(1, 1);

        OwnerAsync(owner, () =>
        {
            coordinator.DidPublishRegistration(request);
            coordinator.Submit(request, () =>
            {
                started.Set();
                Wait(release);
                return 7;
            }, _ => completed.Set());
            returnedImmediately.Set();
        });

        try
        {
            Wait(started);
            Wait(returnedImmediately);
            Check(!completed.IsSet);
            release.Set();
            Wait(completed);
        }
        finally
        {
            release.Set();
            owner.Complete();
        }
    }

    public static void TestSlowCorrectionReturnsInCurrentGenerationThenRunsNewestWork()
    {
        var owner = OwnerQueue();
        var coordinator = new FeatureRefinementCoordinator<int>(owner.ExclusiveScheduler);
        using var started = new ManualResetEventSlim();
        using var oldDelivered = new ManualResetEventSlim();
        using var newestDelivered = new ManualResetEventSlim();
        using var releaseOldWork = new ManualResetEventSlim();
        var old = new FeatureRefinementRequest(4, 10);
        var newer = new FeatureRefinementRequest(4, 11);

        try
        {
            OwnerSync(owner, () =>
            {
                coordinator.DidPublishRegistration(old);
                coordinator.Submit(old, () =>
                {
                    started.Set();
                    Wait(releaseOldWork);
                    return 1;
                }, _ => oldDelivered.Set());
            });
            Wait(started);
            OwnerSync(owner, () =>
            {
                coordinator.DidPublishRegistration(newer);
                coordinator.Submit(newer, () => 2, _ => newestDelivered.Set());
            });
            releaseOldWork.Set();
            Wait(oldDelivered);
            Wait(newestDelivered);
        }
        finally
        {
            releaseOldWork.Set();
            owner.Complete();
        }
    }

    public static void TestOutOfOrderCurrentGenerationResultIsRejected()
    {
        var owner = OwnerQueue();
        var coordinator = new FeatureRefinementCoordinator<int>(owner.ExclusiveScheduler);
        using var newerDelivered = new ManualResetEventSlim();
        using var oldWorked = new ManualResetEventSlim();
        using var oldDelivered = new ManualResetEventSlim();
        var newer = new FeatureRefinementRequest(4, 11);
        var old = new FeatureRefinementRequest(4, 10);

        try
        {
            OwnerSync(owner, () =>
            {
                coordinator.DidPublishRegistration(newer);
                coordinator.Submit(newer, () => 2, _ => newerDelivered.Set());
            });
            Wait(newerDelivered);
            OwnerSync(owner, () => coordinator.Submit(old, () =>
            {
                oldWorked.Set();
                return 1;
            }, _ => oldDelivered.Set()));
            Wait(oldWorked);
            Check(!oldDelivered.Wait(TimeSpan.FromMilliseconds(150)));
        }
        finally
        {
            owner.Complete();
        }
    }

    public static void TestGenerationInvalidationRejectsCompletedCorrection()
    {
        var owner = OwnerQueue();
        var coordinator = new FeatureRefinementCoordinator<int>(owner.ExclusiveScheduler);
        using var started = new ManualResetEventSlim();
        using var rejected = new ManualResetEventSlim();
        using var releaseWork = new ManualResetEventSlim();
        var request = new FeatureRefinementRequest(3, 1);

        try
        {
            OwnerSync(owner, () =>
            {
                coordinator.DidPublishRegistration(request);
                coordinator.Submit(request, () =>
                {
                    started.Set();
                    Wait(releaseWork);
                    return 1;
                }, _ => rejected.Set());
            });
            Wait(started);
            OwnerSync(owner, () => coordinator.Invalidate(4));
            releaseWork.Set();
            Check(!rejected.Wait(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            releaseWork.Set();
            owner.Complete();
        }
    }

    private static ConcurrentExclusiveSchedulerPair OwnerQueue() =>
        new(TaskScheduler.Default, maxConcurrencyLevel: 1);

    private static void OwnerAsync(
        ConcurrentExclusiveSchedulerPair owner,
        Action work) =>
        _ = Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            owner.ExclusiveScheduler);

    private static void OwnerSync(
        ConcurrentExclusiveSchedulerPair owner,
        Action work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            owner.ExclusiveScheduler).GetAwaiter().GetResult();

    private static void Wait(ManualResetEventSlim signal) =>
        Check(signal.Wait(TimeSpan.FromSeconds(2)));

    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Feature refinement coordinator assertion failed.");
        }
    }
}
