using HollowKnightVision.Windows.Core;

internal static class GroundFeatureEligibilityTests
{
    private static void Check(bool value, string message = "Ground feature eligibility parity assertion failed.")
    { if (!value) throw new Exception(message); }

    public static void TestOnlyDescriptorSquaresFullyBelowSupportedSegmentsAreEligible()
    {
        var ground = new GroundReferenceEstimate(
            100,
            40,
            260,
            [
                new GroundReferenceSegment(40, 120),
                new GroundReferenceSegment(180, 260)
            ],
            [40, 80, 120, 180, 220, 260],
            6,
            0.8);
        var allowed = GroundFeatureEligibility.AllowedRects(
            new WorldSize(320, 180),
            new AtlasPoint(0, 0),
            320,
            ground);
        Check(allowed.SequenceEqual(
        [
            new StencilRect(52, 80, 56, 8),
            new StencilRect(192, 80, 56, 8)
        ]));

        var exclusions = GroundFeatureEligibility.AlignmentExclusions(
            new WorldSize(320, 180),
            new AtlasPoint(0, 0),
            320,
            ground,
            []);
        Check(!exclusions.Any(rect => Contains(rect, 80, 84)));
        Check(exclusions.Any(rect => Contains(rect, 150, 84)));
        Check(exclusions.Any(rect => Contains(rect, 80, 50)));
        Check(exclusions.Any(rect => Contains(rect, 80, 100)));
    }

    public static void TestMissingGroundExcludesWholeFrame()
    {
        Check(GroundFeatureEligibility.AlignmentExclusions(
            new WorldSize(320, 180),
            new AtlasPoint(0, 0),
            320,
            null,
            []).SequenceEqual([new StencilRect(0, 0, 320, 180)]));
    }

    public static void TestSeparatePlatformHeightsCreateSeparateEligibleBands()
    {
        var ground = new GroundReferenceEstimate(
            100,
            40,
            260,
            [
                new GroundReferenceSegment(40, 120, 100),
                new GroundReferenceSegment(180, 260, 140)
            ],
            [40, 120, 180, 260],
            4,
            0.8);
        var exclusions = GroundFeatureEligibility.AlignmentExclusions(
            new WorldSize(320, 180),
            new AtlasPoint(0, 0),
            320,
            ground,
            []);
        Check(!exclusions.Any(rect => Contains(rect, 80, 84)));
        Check(!exclusions.Any(rect => Contains(rect, 220, 124)));
        Check(exclusions.Any(rect => Contains(rect, 80, 124)));
        Check(exclusions.Any(rect => Contains(rect, 220, 84)));
        Check(exclusions.Any(rect => Contains(rect, 150, 104)));
    }

    private static bool Contains(StencilRect rect, double x, double y) =>
        x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom;
}
