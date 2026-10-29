using HollowKnightVision.Windows.Core;

internal static class GroundGlobalCorrectionPolicyTests
{
    private static void Check(bool value, string message = "Ground global correction policy parity assertion failed.")
    { if (!value) throw new Exception(message); }

    public static void TestFourWrongVotesCannotOverrideWideStrip()
    {
        var wrong = new GroundGlobalCorrectionPolicy.Quality(24, 4, 80, 10);
        var local = new GroundGlobalCorrectionPolicy.Quality(24, 23, 400, 1);
        Check(!GroundGlobalCorrectionPolicy.Accepts(wrong, local, 80, true));
    }

    public static void TestSimilarRepeatedTextureDoesNotAuthorizePhaseJump()
    {
        var quality = new GroundGlobalCorrectionPolicy.Quality(20, 18, 320, 4);
        Check(!GroundGlobalCorrectionPolicy.Accepts(quality, quality, 80, true));
        Check(GroundGlobalCorrectionPolicy.Accepts(quality, quality, 0, true));
    }

    public static void TestBetterVerifiedStripCanCorrectDriftAndRecoverLostAnchor()
    {
        var good = new GroundGlobalCorrectionPolicy.Quality(20, 19, 320, 2);
        var drift = new GroundGlobalCorrectionPolicy.Quality(20, 10, 160, 16);
        Check(GroundGlobalCorrectionPolicy.Accepts(good, drift, 5, true));
        Check(GroundGlobalCorrectionPolicy.Accepts(good, null, 64, false));
    }

    public static void TestSmallUnambiguousRefinementNeedsOnlyMeasurableImprovement()
    {
        var refined = new GroundGlobalCorrectionPolicy.Quality(20, 18, 320, 4);
        var drifted = new GroundGlobalCorrectionPolicy.Quality(20, 16, 320, 5.2);
        Check(GroundGlobalCorrectionPolicy.Accepts(refined, drifted, 5, true));
        Check(!GroundGlobalCorrectionPolicy.Accepts(refined, drifted, 9, true));
    }

    public static void TestSmallOccludedLoopClosureUsesWideInlierAdvantage()
    {
        var returning = new GroundGlobalCorrectionPolicy.Quality(30, 10, 368, 19.2);
        var drifted = new GroundGlobalCorrectionPolicy.Quality(28, 3, 96, 25.4);
        Check(GroundGlobalCorrectionPolicy.Accepts(returning, drifted, 8, true));
        Check(!GroundGlobalCorrectionPolicy.Accepts(returning, drifted, 13, true));

        var notDecisive = new GroundGlobalCorrectionPolicy.Quality(28, 6, 240, 21);
        Check(!GroundGlobalCorrectionPolicy.Accepts(returning, notDecisive, 8, true));
    }

    public static void TestStrongGlobalStripClosesWhenDriftedLocalPoseHasNoSupport()
    {
        var origin = new GroundGlobalCorrectionPolicy.Quality(13, 11, 224, 9.3);
        Check(GroundGlobalCorrectionPolicy.Accepts(origin, null, 37, true));

        var sparse = new GroundGlobalCorrectionPolicy.Quality(19, 10, 128, 9);
        Check(!GroundGlobalCorrectionPolicy.Accepts(sparse, null, 37, true));
        var noisy = new GroundGlobalCorrectionPolicy.Quality(13, 11, 224, 10.5);
        Check(!GroundGlobalCorrectionPolicy.Accepts(noisy, null, 37, true));
    }
    public static void TestStrictTrackingRejectsMinorityLoopClosureButKeepsStrongCorrections()
    {
        var minority = new GroundGlobalCorrectionPolicy.Quality(30, 10, 368, 19.2);
        var drifted = new GroundGlobalCorrectionPolicy.Quality(28, 3, 96, 25.4);
        Check(GroundGlobalCorrectionPolicy.Accepts(minority, drifted, 11.3, true));
        Check(!GroundGlobalCorrectionPolicy.Accepts(minority, drifted, 11.3, true,
            requireStrongEvidence: true));
        // Saved second-54 transported candidate: a numerical majority of
        // five patches is still insufficient evidence for an 11-pixel jump.
        var transported = new GroundGlobalCorrectionPolicy.Quality(7, 5, 112, 10.8998);
        var current = new GroundGlobalCorrectionPolicy.Quality(9, 2, 16, 16.6632);
        Check(GroundGlobalCorrectionPolicy.Accepts(transported, current, 11.3, true));
        Check(!GroundGlobalCorrectionPolicy.Accepts(transported, current, 11.3, true,
            requireStrongEvidence: true));
        var majority = new GroundGlobalCorrectionPolicy.Quality(30, 25, 368, 6);
        Check(GroundGlobalCorrectionPolicy.Accepts(majority, drifted, 11.3, true,
            requireStrongEvidence: true));
        Check(GroundGlobalCorrectionPolicy.Accepts(majority, null, 80, false,
            requireStrongEvidence: true));
    }
}
