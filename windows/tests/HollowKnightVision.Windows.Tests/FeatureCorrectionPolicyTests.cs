using HollowKnightVision.Windows.Core;

internal static class FeatureCorrectionPolicyTests
{
    private static void Check(bool value, string message = "Feature correction policy parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    private static FeatureCorrectionSnapshot Unwrap(FeatureCorrectionSnapshot? snapshot)
    {
        Check(snapshot.HasValue, "Expected a feature correction snapshot.");
        return snapshot.GetValueOrDefault();
    }

    public static void TestSlowCorrectionRebasesNewerQueuedWorkAndAppliesOnce()
    {
        var policy = new FeatureCorrectionPolicy(7);
        var slow = new FeatureRefinementRequest(7, 10);
        var newer = new FeatureRefinementRequest(7, 13);
        var snapshot = Unwrap(policy.CaptureSnapshot(slow));

        Check(policy.Apply(new CameraVector(-8, 3), slow));
        Check(!policy.Apply(new CameraVector(-8, 3), slow));
        Check(!policy.Apply(
            new CameraVector(1, 1),
            new FeatureRefinementRequest(7, 9)));

        // The next tracker job was queued before the slow correction returned.
        // It therefore starts in the corrected world coordinates once it runs.
        Check(policy.RebasedPosition(new AtlasPoint(40, 20), snapshot)
            == new AtlasPoint(32, 23));

        var afterSlowCorrection = Unwrap(policy.CaptureSnapshot(newer));
        Check(policy.RebasedPosition(new AtlasPoint(32, 23), afterSlowCorrection)
            == new AtlasPoint(32, 23));
    }

    public static void TestGenerationResetRejectsSlowOldCorrection()
    {
        var policy = new FeatureCorrectionPolicy(2);
        var old = new FeatureRefinementRequest(2, 4);
        policy.BeginGeneration(3);

        Check(!policy.Apply(new CameraVector(5, 0), old));
        Check(policy.CaptureSnapshot(old) is null);
        Check(policy.Apply(
            new CameraVector(2, -1),
            new FeatureRefinementRequest(3, 1)));
    }

    public static void TestGenerationTransitionWaitsForAcceptedAccumulatorMutation()
    {
        var policy = new FeatureCorrectionPolicy(7);
        var request = new FeatureRefinementRequest(7, 10);
        using var enteredAcceptance = new ManualResetEventSlim();
        using var completedAcceptance = new ManualResetEventSlim();
        using var releaseAcceptance = new ManualResetEventSlim();
        using var transitionReturned = new ManualResetEventSlim();

        var applyTask = Task.Run(() =>
        {
            Check(policy.Apply(new CameraVector(3, 0), request, () =>
            {
                enteredAcceptance.Set();
                Check(releaseAcceptance.Wait(TimeSpan.FromSeconds(1)),
                    "Timed out waiting to release accepted mutation.");
            }));
            completedAcceptance.Set();
        });
        Check(enteredAcceptance.Wait(TimeSpan.FromSeconds(1)),
            "Accepted mutation was not entered.");

        var transitionTask = Task.Run(() =>
        {
            policy.BeginGeneration(8);
            transitionReturned.Set();
        });
        // BeginGeneration must wait; otherwise an old worker could mutate the
        // accumulator after its tracking state was replaced.
        Check(!transitionReturned.Wait(TimeSpan.FromSeconds(0.05)),
            "Generation transition returned before accepted mutation completed.");
        releaseAcceptance.Set();
        Check(transitionReturned.Wait(TimeSpan.FromSeconds(1)),
            "Generation transition did not return after accepted mutation completed.");
        Check(completedAcceptance.Wait(TimeSpan.FromSeconds(1)),
            "Accepted mutation did not complete.");
        Task.WaitAll(applyTask, transitionTask);
    }
}
