using HollowKnightVision.Windows.Core;

internal static class LiveTrackingTimingParityTests
{
    private static readonly AtlasPoint Zero = new(0, 0);

    private static void Check(bool value)
    {
        if (!value) throw new Exception("Live tracking timing parity assertion failed.");
    }

    private static void Close(double expected, double actual) =>
        Check(Math.Abs(expected - actual) <= .001);

    public static void TestFloorlessSelectorRejectsStaleCoarsePoseAfterDirectionReversal()
    {
        var result = FloorlessPoseCandidateSelector.Select(
            Zero, new(-20, 0), new(8, 0), VisualRoomDirection.Right, false);
        Check(result == new FloorlessPoseCandidate(
            new AtlasPoint(8, 0), FloorlessPoseCandidateSource.MaskedRegistration));
    }

    public static void TestFloorlessSelectorUsesLargerAgreeingCaptureRateStep()
    {
        var result = FloorlessPoseCandidateSelector.Select(
            Zero, new(18, 1), new(5, 0), VisualRoomDirection.Right, false);
        Check(result == new FloorlessPoseCandidate(
            new AtlasPoint(18, 1), FloorlessPoseCandidateSource.CoarseCaptureRate));
    }

    public static void TestFloorlessSelectorKeepsMaskedHorizontalStopAndCoarseVerticalTravel()
    {
        Check(FloorlessPoseCandidateSelector.Select(
            Zero, new(-20, 0), new(-4, 0), null, false)?.Source
            == FloorlessPoseCandidateSource.MaskedRegistration);
        Check(FloorlessPoseCandidateSelector.Select(
            Zero, new(1, 15), new(0, 1), null, false)?.Source
            == FloorlessPoseCandidateSource.CoarseCaptureRate);
    }

    public static void TestFloorlessSelectorAlwaysAcceptsAbsolutePlaceMatch()
    {
        Check(FloorlessPoseCandidateSelector.Select(
            Zero, new(-30, 4), new(8, 0), VisualRoomDirection.Right, true)?.Source
            == FloorlessPoseCandidateSource.CoarseCaptureRate);
    }

    public static void TestGroundMotionEvidenceRejectsCoordinateSeedWithoutPixelMeasurement()
    {
        Check(!GroundMotionEvidence.IsMeasured(true, true, 0, 0, 0));
        Check(GroundMotionEvidence.IsMeasured(true, true, 1, 0, 0));
        Check(GroundMotionEvidence.IsMeasured(true, true, 0, 0, 4));
    }

    public static void TestPredictionAdvancesCurrentFrameButIsBoundedAndExpires()
    {
        var speed = PresentationPosePrediction.Velocity(
            Zero, 1, new AtlasPoint(8, 0), 1.02, true);
        Close(400, speed.X);
        var predicted = PresentationPosePrediction.Position(
            new AtlasPoint(8, 0), speed, 1.02, 1.04);
        Close(16, predicted.X);
        var capped = PresentationPosePrediction.Position(Zero, speed, 1, 1.08);
        Close(8, capped.X);
        Check(PresentationPosePrediction.Position(Zero, speed, 1, 1.2) == Zero);
    }

    public static void TestCorrectionAndLossNeverBecomePresentationVelocity()
    {
        Check(PresentationPosePrediction.Velocity(
            Zero, 1, new AtlasPoint(80, 0), 1.02, true) == CameraVector.Zero);
        Check(PresentationPosePrediction.Velocity(
            Zero, 1, new AtlasPoint(4, 0), 1.02, false) == CameraVector.Zero);
    }

    public static void TestCaptureGroundReliabilityRequiresFreshExactTrackingState()
    {
        Check(CaptureGroundReliability.IsReliable(true, 10, 10.12, true));
        Check(!CaptureGroundReliability.IsReliable(true, 10, 10.121, true));
        Check(!CaptureGroundReliability.IsReliable(true, 10, 10.05, false));
        Check(!CaptureGroundReliability.IsReliable(null, null, 10, true));
        Check(!CaptureGroundReliability.IsReliable(false, 10, 10.01, true));
    }

    public static void TestTentativeTransitionPreservesVerifiedHandoffOrigin()
    {
        Check(!TransitionPosePublication.ReplacesTrackingState(false, false));
        Check(TransitionPosePublication.ReplacesTrackingState(false, true));
        Check(TransitionPosePublication.ReplacesTrackingState(true, false));
    }

    public static void TestFloorlessHandoffAdvancesDelayedPoseFromRetainedGroundVelocity()
    {
        var seed = FloorlessMotionHandoff.Seed(new CameraVector(-400, 0), 5.211, 5.386)
            ?? throw new Exception("Expected floorless velocity seed.");
        var predicted = FloorlessMotionHandoff.Position(
            new AtlasPoint(-192, 10), 5.229, seed, 5.386, 640);
        Close(-254.8, predicted.X);
        Close(10, predicted.Y);
    }

    public static void TestFloorlessHandoffExpiresAndCapsTravel()
    {
        Check(FloorlessMotionHandoff.Seed(new CameraVector(-400, 0), 1, 1.481) is null);
        var seed = FloorlessMotionHandoff.Seed(new CameraVector(1_000, 0), 1, 1.1)
            ?? throw new Exception("Expected floorless velocity seed.");
        Close(115.2, FloorlessMotionHandoff.Position(Zero, 1, seed, 1.2, 640).X);
        Check(FloorlessMotionHandoff.Position(Zero, 1, seed, 1.251, 640) == Zero);
    }
}
