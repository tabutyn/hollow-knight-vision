using HollowKnightVision.Windows.Core;

internal static class GroundEarlyRecognitionTests
{
    private static void Check(bool value, string message = "Ground early recognition parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    private static GroundComparisonAnalysis Comparison(bool includeLowerFloor)
    {
        const int width = 320;
        const int height = 140;
        var pixels = new byte[width * height];
        void Line(int row, int start, int endInclusive)
        {
            for (var x = start; x <= endInclusive; x++)
            {
                pixels[row * width + x] = 80;
            }
        }

        Line(40, 0, 111);
        Line(40, 208, 319);
        Line(4, 260, 289); // Short foreground decoration, not a floor.
        Line(67, 20, 79); // Weaker parallel edge within the ground body.
        if (includeLowerFloor)
        {
            Line(85, 128, 191);
        }
        return new GroundComparisonAnalysis(width, height, pixels);
    }

    private static GroundLinePresenceReview Review(GroundLinePresence ledger)
    {
        var reviews = ledger.Reviews(new AtlasPoint(0, 0), 160, 100);
        Check(reviews.Count > 0, "Expected a ground presence review.");
        return reviews[0];
    }

    public static void TestLowerSurfaceSplitsBridgeAndRejectsDecorativeEdges()
    {
        var lines = GroundLineDetector.CleanFloorLines(
            Comparison(includeLowerFloor: true),
            GroundTheoryTuning.Default);

        Check(lines.Select(line => line.Row).SequenceEqual([40, 40, 85]));
        Check(lines.Where(line => line.Row == 40).Select(line => line.XSpan)
            .SequenceEqual([new PixelSpan(0, 111), new PixelSpan(208, 319)]));
    }

    public static void TestOrdinaryOcclusionStillBridgesWithoutLowerSurfaceEvidence()
    {
        var lines = GroundLineDetector.CleanFloorLines(
            Comparison(includeLowerFloor: false),
            GroundTheoryTuning.Default);

        Check(lines.Select(line => line.Row).SequenceEqual([40]));
        Check(lines[0].XSpan == new PixelSpan(0, 319));
    }

    public static void TestShortObservationAdmitsNewGroundAndPreservesItAcrossOcclusion()
    {
        var ledger = new GroundLinePresence(minimumObservationSeconds: 0.2);
        var line = new CleanFloorLine(40, new PixelSpan(16, 143));
        for (var index = 0; index <= 12; index++)
        {
            ledger.Update(
                [line],
                new AtlasPoint(0, 0),
                160,
                100,
                index / 60d,
                poseVerified: true,
                exclusions: []);
        }
        var confirmed = Review(ledger);
        Check(confirmed.State == GroundLinePresenceState.Confirmed);

        for (var index = 13; index <= 120; index++)
        {
            ledger.Update(
                [],
                new AtlasPoint(0, 0),
                160,
                100,
                index / 60d,
                poseVerified: true,
                exclusions: [new PixelRect(0, 30, 160, 40)]);
        }
        var retained = Review(ledger);
        Check(retained.Id == confirmed.Id);
        Check(retained.State == GroundLinePresenceState.Confirmed);
        Check(retained.DetectedFraction == 1);
    }

    public static void TestConfirmedGroundSurvivesBriefMissButRejectsPersistentAbsence()
    {
        var ledger = new GroundLinePresence(
            minimumObservationSeconds: 0.2,
            confirmedLossGraceSeconds: 3);
        var line = new CleanFloorLine(40, new PixelSpan(16, 143));
        void Update(int index, bool detected)
        {
            ledger.Update(
                detected ? [line] : [],
                new AtlasPoint(0, 0),
                160,
                100,
                index / 60d,
                poseVerified: true,
                exclusions: []);
        }

        for (var index = 0; index <= 60; index++)
        {
            Update(index, detected: true);
        }
        var original = Review(ledger);
        for (var index = 61; index <= 220; index++)
        {
            Update(index, detected: false);
        }
        var hidden = Review(ledger);
        Check(hidden.State == GroundLinePresenceState.Confirmed);
        Check(hidden.Id == original.Id);

        Update(221, detected: true);
        Check(Review(ledger).State == GroundLinePresenceState.Confirmed);
        for (var index = 222; index <= 410; index++)
        {
            Update(index, detected: false);
        }
        Check(Review(ledger).State == GroundLinePresenceState.Rejected);
    }
}
