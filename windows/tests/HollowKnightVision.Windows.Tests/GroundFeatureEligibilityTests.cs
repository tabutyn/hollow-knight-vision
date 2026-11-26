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

    internal static void TestLiveAlignmentUsesCapturedFloorAndForeground()
    {
        var frame = EmptyCapture();
        var initial = new VisualGameplayTracker().ObserveUnverified(frame, null);
        var measured = initial with { GroundVerified = true, GroundError = 2, GroundSupport = 4 };
        var signal = new FrameSignalProfile(64, 48, 48, 30, EdgeBlackBands: new(
            new FrameEdgeBlackBand(.125, new(0, 1)), null));
        DetectedFloorLine[] floors = [new(239, new(32, 200)), new(179, new(160, 440))];
        PixelRect[] foreground = [new(120, 250, 20, 10)];
        var pose = new AtlasPoint(100, 40);

        var ordinary = GameplayTrackingPipeline.CreateGroundAlignmentExclusions(frame, pose,
            320, floors, foreground, signal, measured, transitionBridgeActive: false, trackedEstimate: null);
        Check(!ordinary.Any(rect => Contains(rect, 60, 104))
            && !ordinary.Any(rect => Contains(rect, 100, 104))
            && !ordinary.Any(rect => Contains(rect, 220, 164)),
            "Captured floor rows must leave two narrow world-up alignment bands at the original screen columns.");
        Check(ordinary.Any(rect => Contains(rect, 130, 104))
            && ordinary.Any(rect => Contains(rect, 220, 104))
            && ordinary.Any(rect => Contains(rect, 100, 164)),
            "Foreground and unsupported spans must not become scenery references.");

        var transition = GameplayTrackingPipeline.CreateGroundAlignmentExclusions(frame, pose,
            320, floors, foreground, signal, initial, transitionBridgeActive: true, trackedEstimate: null);
        Check(transition.Any(rect => Contains(rect, 60, 104))
            && !transition.Any(rect => Contains(rect, 100, 104))
            && !transition.Any(rect => Contains(rect, 220, 164)),
            "A measured transition must mask the left matte while retaining eligible floor inside the room.");
    }

    internal static void TestLiveAlignmentRejectsUnverifiedFloor()
    {
        var frame = EmptyCapture();
        var initial = new VisualGameplayTracker().ObserveUnverified(frame, null);
        var scenery = initial with
        {
            FloorlessEvidence = new(null, null, null, FloorlessPoseCandidateSource.CalibratedScene, null, false)
        };
        var signal = new FrameSignalProfile(64, 48, 48, 30);
        var staleGround = new GroundReferenceEstimate(100, 40, 260, [new(40, 260)], [], 4, 1);
        var expected = new StencilRect(0, 0, frame.Width, frame.Height);
        var masks = GameplayTrackingPipeline.CreateGroundAlignmentExclusions(frame, default, frame.Width,
            [new(239, new(32, 200))], [], signal, scenery, transitionBridgeActive: true, trackedEstimate: staleGround);
        Check(masks.SequenceEqual([expected]), "Scenery-only continuity must not turn old floor into a new reference.");

        masks = GameplayTrackingPipeline.CreateGroundAlignmentExclusions(frame, default, frame.Width,
            [], [], signal, initial with { GroundVerified = true }, transitionBridgeActive: false, trackedEstimate: null);
        Check(masks.SequenceEqual([expected]), "No usable captured floor must exclude the entire alignment image.");
    }

    private static BgraFrame EmptyCapture() =>
        new(640, 360, 640 * 4, new byte[640 * 360 * 4], 1, DateTimeOffset.UnixEpoch);

    private static bool Contains(StencilRect rect, double x, double y) =>
        x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom;
}
