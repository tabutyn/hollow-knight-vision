using HollowKnightVision.Windows.Core;

internal static class PoseCorrectionInterpolationTests
{
    private static readonly Guid Visit =
        Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherVisit =
        Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static void Equal(AtlasPoint expected, AtlasPoint actual)
    {
        if (expected != actual)
        {
            throw new Exception($"Expected {expected}; received {actual}.");
        }
    }

    public static void TestExactTimestampReturnsMatchingCorrection()
    {
        var samples = new[] { Sample(2, 2, new AtlasPoint(4, -3)) };
        Equal(
            new AtlasPoint(4, -3),
            PoseCorrectionInterpolator.Correction(2, Visit, samples));
    }

    public static void TestMidpointLinearlyInterpolatesCorrectionAndRefinesRawPose()
    {
        var samples = new[]
        {
            Sample(1, 0, new AtlasPoint(0, 0)),
            Sample(2, 4, new AtlasPoint(8, -4))
        };
        Equal(
            new AtlasPoint(4, -2),
            PoseCorrectionInterpolator.Correction(2, Visit, samples));
        Equal(
            new AtlasPoint(14, 3),
            PoseCorrectionInterpolator.RefinedPose(
                new AtlasPoint(10, 5), 2, Visit, samples));
    }

    public static void TestOutsideIntervalUsesNearestEndpointWithinVisit()
    {
        var samples = new[]
        {
            Sample(1, 10, new AtlasPoint(1, 2)),
            Sample(2, 20, new AtlasPoint(3, 4))
        };
        Equal(
            new AtlasPoint(1, 2),
            PoseCorrectionInterpolator.Correction(1, Visit, samples));
        Equal(
            new AtlasPoint(3, 4),
            PoseCorrectionInterpolator.Correction(30, Visit, samples));
    }

    public static void TestVisitIsolationAndEmptyVisitReturnIdentity()
    {
        var samples = new[]
        {
            Sample(1, 0, new AtlasPoint(99, 99), OtherVisit)
        };
        Equal(
            new AtlasPoint(0, 0),
            PoseCorrectionInterpolator.Correction(0, Visit, samples));
    }

    public static void TestInvalidInputsAreIgnored()
    {
        var samples = new[]
        {
            Sample(1, double.PositiveInfinity, new AtlasPoint(10, 10)),
            Sample(2, 1, new AtlasPoint(double.NaN, 10)),
            Sample(3, 2, new AtlasPoint(6, 7))
        };
        Equal(
            new AtlasPoint(6, 7),
            PoseCorrectionInterpolator.Correction(2, Visit, samples));
        Equal(
            new AtlasPoint(0, 0),
            PoseCorrectionInterpolator.Correction(double.NaN, Visit, samples));
    }

    public static void TestOrderingIsDeterministicIncludingSameTimestamp()
    {
        var early = Sample(4, 1, new AtlasPoint(2, 4));
        var laterAtSameTime = Sample(8, 1, new AtlasPoint(8, 16));
        var end = Sample(9, 3, new AtlasPoint(10, 20));
        var forward = PoseCorrectionInterpolator.Correction(
            1, Visit, new[] { laterAtSameTime, end, early });
        var reverse = PoseCorrectionInterpolator.Correction(
            1, Visit, new[] { early, end, laterAtSameTime });
        Equal(new AtlasPoint(2, 4), forward);
        Equal(forward, reverse);
    }

    private static PoseCorrectionSample Sample(
        int id,
        double time,
        AtlasPoint point,
        Guid? visit = null) =>
        new(id, visit ?? Visit, time, point);
}
