using HollowKnightVision.Windows.Core;

internal static class GroundLinePresenceTests
{
    private static readonly CleanFloorLine Line = new(40, new PixelSpan(16, 143));

    private static void Check(bool value, string message = "Ground line presence parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    private static void CheckClose(double actual, double expected, double tolerance, string message)
    {
        Check(Math.Abs(actual - expected) <= tolerance,
            $"{message} Expected {expected}, got {actual}.");
    }

    private static void Update(
        GroundLinePresence presence,
        double time,
        bool seen,
        AtlasPoint? camera = null,
        bool verified = true,
        IReadOnlyList<PixelRect>? exclusions = null)
    {
        presence.Update(
            seen ? [Line] : [],
            camera ?? new AtlasPoint(0, 0),
            160,
            100,
            time,
            verified,
            exclusions ?? []);
    }

    private static GroundLinePresenceReview Review(GroundLinePresence presence)
    {
        var reviews = presence.Reviews(new AtlasPoint(0, 0), 160, 100);
        Check(reviews.Count > 0, "Expected a ground line presence review.");
        return reviews[0];
    }

    public static void TestThreeFramesAreNotEnoughAndTwoSecondsConfirmStableLine()
    {
        var presence = new GroundLinePresence();
        for (var frame = 0; frame <= 2; frame++)
        {
            Update(presence, frame / 60d, seen: true);
        }
        Check(Review(presence).State == GroundLinePresenceState.Observing);
        for (var frame = 3; frame <= 120; frame++)
        {
            Update(presence, frame / 60d, seen: true);
        }
        Check(Review(presence).State == GroundLinePresenceState.Confirmed);
        CheckClose(Review(presence).VisibleSeconds, 2, 0.001,
            "Stable line observation duration differed.");
    }

    public static void TestAboveHalfConfirmsBelowHalfRejectsAndExactlyHalfWaits()
    {
        foreach (var (hits, expected) in new[]
        {
            (6, GroundLinePresenceState.Confirmed),
            (4, GroundLinePresenceState.Rejected),
            (5, GroundLinePresenceState.Observing),
        })
        {
            var presence = new GroundLinePresence();
            for (var frame = 0; frame <= 120; frame++)
            {
                Update(presence, frame / 60d, seen: frame % 10 < hits);
            }
            var result = Review(presence);
            CheckClose(result.DetectedFraction, hits / 10d, 0.001,
                "Detected-time fraction differed.");
            Check(result.State == expected,
                $"Expected {expected}, got {result.State} for {hits} hits.");
        }
    }

    public static void TestRejectedLineRecoversAfterEnoughVerifiedDetections()
    {
        var presence = new GroundLinePresence();
        for (var frame = 0; frame <= 120; frame++)
        {
            Update(presence, frame / 60d, seen: frame % 10 < 4);
        }
        for (var frame = 121; frame <= 600; frame++)
        {
            Update(presence, frame / 60d, seen: true);
        }
        Check(Review(presence).DetectedFraction > 0.5);
        Check(Review(presence).State == GroundLinePresenceState.Confirmed);
    }

    public static void TestOcclusionOffscreenUnverifiedAndLongGapDoNotCountAsMisses()
    {
        var presence = new GroundLinePresence();
        for (var frame = 0; frame <= 60; frame++)
        {
            Update(presence, frame / 60d, seen: true);
        }
        var before = Review(presence);
        for (var frame = 61; frame <= 120; frame++)
        {
            Update(presence, frame / 60d, seen: false,
                exclusions: [new PixelRect(0, 30, 160, 40)]);
        }
        for (var frame = 121; frame <= 180; frame++)
        {
            Update(presence, frame / 60d, seen: false, camera: new AtlasPoint(500, 0));
        }
        for (var frame = 181; frame <= 240; frame++)
        {
            Update(presence, frame / 60d, seen: false, verified: false);
        }
        Update(presence, 20, seen: true);
        var after = Review(presence);
        CheckClose(after.VisibleSeconds, before.VisibleSeconds, 0,
            "Non-observable time changed visible duration.");
        CheckClose(after.DetectedFraction, 1, 0,
            "Non-observable time changed detection fraction.");
        Check(after.State == GroundLinePresenceState.Observing);
    }

    public static void TestVotesMeasureTimeNotFrameCount()
    {
        var presence = new GroundLinePresence();
        // Six sparse hit observations cover one second; 100 missing
        // observations cover only half a second. Frame voting would reject.
        for (var frame = 0; frame <= 5; frame++)
        {
            Update(presence, frame * 0.2, seen: true);
        }
        for (var frame = 1; frame <= 100; frame++)
        {
            Update(presence, 1 + frame * 0.005, seen: false);
        }
        for (var frame = 1; frame <= 5; frame++)
        {
            Update(presence, 1.5 + frame * 0.1, seen: true);
        }
        Check(Review(presence).DetectedFraction > 0.7);
        Check(Review(presence).State == GroundLinePresenceState.Confirmed);
    }

    public static void TestConfirmedLineHidesOverlappingStaleRejectedProjection()
    {
        var confirmed = new GroundLinePresenceReview(
            1,
            new CleanFloorLine(40, new PixelSpan(32, 95)),
            GroundLinePresenceState.Confirmed,
            4,
            1);
        var staleRejected = new GroundLinePresenceReview(
            2,
            new CleanFloorLine(45, new PixelSpan(64, 143)),
            GroundLinePresenceState.Rejected,
            4,
            0.1);
        var independentRejected = new GroundLinePresenceReview(
            3,
            new CleanFloorLine(70, new PixelSpan(64, 143)),
            GroundLinePresenceState.Rejected,
            4,
            0.1);

        var display = GroundLineDetector.DisplayPresenceReviews(
            [confirmed, staleRejected, independentRejected]);

        Check(display.Select(review => review.Id).SequenceEqual([3, 1]));
    }

    public static void TestFeatureHypothesesRenderOnlyConfirmedLines()
    {
        var reviews = new[]
        {
            new GroundLinePresenceReview(
                1,
                new CleanFloorLine(40, new PixelSpan(0, 63)),
                GroundLinePresenceState.Confirmed,
                4,
                0.9),
            new GroundLinePresenceReview(
                2,
                new CleanFloorLine(60, new PixelSpan(0, 63)),
                GroundLinePresenceState.Observing,
                1,
                0.6),
            new GroundLinePresenceReview(
                3,
                new CleanFloorLine(80, new PixelSpan(0, 63)),
                GroundLinePresenceState.Rejected,
                4,
                0.1),
        };

        Check(GroundLineDetector.FeatureHypothesisPresenceReviews(reviews)
            .Select(review => review.Id).SequenceEqual([1]));
    }
}
