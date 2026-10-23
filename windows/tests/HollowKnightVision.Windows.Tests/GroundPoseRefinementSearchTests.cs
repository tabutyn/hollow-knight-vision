using HollowKnightVision.Windows.Core;

internal static class GroundPoseRefinementSearchTests
{
    private static void Check(bool value)
    {
        if (!value) throw new Exception("Ground pose refinement search parity assertion failed.");
    }

    public static void TestFindsSubCellXYOffset()
    {
        var expected = new GroundPoseRefinementSearch.Offset(4, -5);
        var actual = GroundPoseRefinementSearch.Find((dx, dy) => new(
            Tested: 20,
            Inliers: 18,
            HorizontalSpread: 320,
            Error: (Math.Abs(dx - expected.Dx) + Math.Abs(dy - expected.Dy)) * 2));
        Check(actual == expected);
    }

    public static void TestRepeatedTextureCannotAuthorizePoseChange()
    {
        Check(GroundPoseRefinementSearch.Find((_, _) => new(
            Tested: 20,
            Inliers: 18,
            HorizontalSpread: 320,
            Error: 2)) is null);
    }

    public static void TestSmallOccludedConsensusIsRejected()
    {
        Check(GroundPoseRefinementSearch.Find((dx, dy) => new(
            Tested: 20,
            Inliers: 5,
            HorizontalSpread: 320,
            Error: Math.Abs(dx - 3) + Math.Abs(dy + 2))) is null);
    }
}
