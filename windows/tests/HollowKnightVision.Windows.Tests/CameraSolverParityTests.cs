using HollowKnightVision.Windows.Core;

internal static class CameraSolverParityTests
{
    private static void Check(bool value, string message = "Camera solver parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Near(double actual, double expected, double tolerance = .001)
    {
        Check(Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected}, got {actual}.");
    }

    public static void TestIsolatedWeakRegistrationDoesNotDiscardVerifiedAtlasPlacement()
    {
        var continuity = new LiveRegistrationContinuity();
        var weak = new CameraUpdate(
            CameraUpdateState.HeldLowConfidence,
            CameraVector.Zero,
            CameraVector.Zero,
            new AtlasPoint(0, 0));
        var accepted = new CameraUpdate(
            CameraUpdateState.Accepted,
            CameraVector.Zero,
            CameraVector.Zero,
            new AtlasPoint(0, 0));

        Check(!continuity.ShouldEnterRecovery(weak, hasGameplaySignal: true));
        Check(!continuity.ShouldEnterRecovery(null, hasGameplaySignal: true));
        Check(!continuity.ShouldEnterRecovery(accepted, hasGameplaySignal: true));
        Check(continuity.ConsecutiveSoftFailures == 0);
        Check(!continuity.ShouldEnterRecovery(weak, hasGameplaySignal: true));
        Check(!continuity.ShouldEnterRecovery(weak, hasGameplaySignal: true));
        Check(continuity.ShouldEnterRecovery(weak, hasGameplaySignal: true));
    }

    public static void TestHardSceneJumpStillEntersRecoveryImmediately()
    {
        var continuity = new LiveRegistrationContinuity();
        var rejected = new CameraUpdate(
            CameraUpdateState.HeldSceneChange,
            new CameraVector(0, 120),
            CameraVector.Zero,
            new AtlasPoint(0, 0));

        Check(continuity.ShouldEnterRecovery(rejected, hasGameplaySignal: true));
        continuity.Reset();
        Check(!continuity.ShouldEnterRecovery(null, hasGameplaySignal: false));
        Check(!continuity.ShouldEnterRecovery(null, hasGameplaySignal: false));
        Check(continuity.ShouldEnterRecovery(null, hasGameplaySignal: false));
    }

    public static void TestFreshAtlasBootstrapJumpRestartsStabilityWindow()
    {
        var gate = new FreshAtlasBootstrapGate();
        for (var index = 0; index < 12; index++)
        {
            _ = gate.AllowsFirstAtlasWrite(
                new AtlasPoint(index % 2, 0),
                registrationAccepted: true,
                groundVerified: true,
                hasCommittedEvidence: false);
        }

        Check(!gate.AllowsFirstAtlasWrite(
            new AtlasPoint(80, 30),
            registrationAccepted: true,
            groundVerified: true,
            hasCommittedEvidence: false));
        Check(gate.StableSampleCount == 1);
        for (var index = 1; index < FreshAtlasBootstrapGate.RequiredStableSamples; index++)
        {
            var allowed = gate.AllowsFirstAtlasWrite(
                new AtlasPoint(80 + index % 2, 30),
                registrationAccepted: true,
                groundVerified: true,
                hasCommittedEvidence: false);
            Check(allowed == (index == FreshAtlasBootstrapGate.RequiredStableSamples - 1));
        }
    }

    public static void TestCommittedAtlasBypassesBootstrapDelay()
    {
        var gate = new FreshAtlasBootstrapGate();
        Check(gate.AllowsFirstAtlasWrite(
            position: null,
            registrationAccepted: false,
            groundVerified: false,
            hasCommittedEvidence: true));
    }

    public static void TestRejectsCutSizedJumpWithoutMovingPath()
    {
        var accumulator = new CameraAccumulator();
        var update = accumulator.Ingest(400, 0, 1, 960);

        Check(update.State == CameraUpdateState.HeldSceneChange);
        Check(update.Position == new AtlasPoint(0, 0));
        Check(accumulator.Samples.Count == 1);
    }

    public static void TestRejectsLargeVerticalRegistrationJumpAtLiveFrameWidth()
    {
        var accumulator = new CameraAccumulator { Smoothing = 1 };

        var stable = accumulator.Ingest(24, -8, 1, 960);
        var jump = accumulator.Ingest(0, -150, 1, 960);

        Check(stable.State == CameraUpdateState.Accepted);
        Check(jump.State == CameraUpdateState.HeldSceneChange);
        Near(jump.Position.X, 24);
        Near(jump.Position.Y, -8);
        Check(accumulator.Samples.Count == 2);
    }

    public static void TestAcceptsOrdinaryLiveCameraMovementAtLiveFrameWidth()
    {
        var accumulator = new CameraAccumulator { Smoothing = 1 };

        var update = accumulator.Ingest(48, -24, 1, 960);

        Check(update.State == CameraUpdateState.Accepted);
        Near(update.Position.X, 48);
        Near(update.Position.Y, -24);
    }

    public static void TestAcceptsRecordedSparseLiveHallwayPanWithoutAdmittingVerticalJump()
    {
        var accumulator = new CameraAccumulator { Smoothing = 1 };

        var update = accumulator.Ingest(-153, 0, 1, 960);

        Check(update.State == CameraUpdateState.Accepted);
        Near(update.Position.X, -153);

        var verticalJump = accumulator.Ingest(0, -150, 1, 960);
        Check(verticalJump.State == CameraUpdateState.HeldSceneChange);
        Near(verticalJump.Position.X, -153);
        Near(verticalJump.Position.Y, 0);
    }

    public static void TestInvertMotionFlipsCameraDirection()
    {
        var accumulator = new CameraAccumulator
        {
            Smoothing = 1,
            InvertMotion = true
        };
        var update = accumulator.Ingest(12, -3, 1, 960);

        Near(update.Position.X, -12);
        Near(update.Position.Y, 3);
    }
}
