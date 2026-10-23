using HollowKnightVision.Windows.Core;

public static class VisualRoomTransitionPortalFadeParityTests
{
    private static readonly WorldSize Size = new(640, 360);
    private static readonly StencilRect LeftKnight = new(10, 100, 40, 50);
    private static readonly StencilRect RightKnight = new(590, 100, 40, 50);
    private static readonly FrameSignalProfile Visible = new(1_000, 800, 700, 80);
    private static readonly FrameSignalProfile NearBlack = new(1_000, 80, 60, 8);
    private static readonly TransitionFrameDecision Admitted = new(true, false);

    public static void TestOppositeTraversalCannotUseKnownLeftPortalToEnterRoomTwo()
    {
        var tracker = new VisualRoomTransitionTracker();
        _ = tracker.BootstrapTwoRoomTopology(
            1, 0, -8, 0,
            new FrameFractionRange(100, 180),
            new FrameFractionRange(100, 180),
            new AtlasPoint(-648, 0), default,
            activeRoomID: 0);
        _ = tracker.Observe(
            Visible, Admitted, VisualRoomDirection.Left, LeftKnight, Size,
            default, 640, 1);
        _ = tracker.Observe(
            NearBlack, Admitted, VisualRoomDirection.Left, LeftKnight, Size,
            default, 640, 1.1);
        for (var index = 0;
             index < VisualRoomTransitionTracker.AppearanceStableFrames;
             index++)
        {
            _ = tracker.Observe(
                NearBlack, Admitted, VisualRoomDirection.Right, RightKnight, Size,
                default, 640, 1.2 + index / 60d);
        }

        Equal(0, tracker.Snapshot.RoomID);
        Equal(1UL, tracker.Snapshot.Revision);
        Check(!tracker.Snapshot.IsTransitioning);
    }

    public static void TestKnownRoomActivationKeepsContinuousPoseInsteadOfSnappingToStoredEntry()
    {
        var tracker = new VisualRoomTransitionTracker();
        _ = tracker.BootstrapTwoRoomTopology(
            1, 0, -8, 0,
            new FrameFractionRange(100, 180),
            new FrameFractionRange(100, 180),
            new AtlasPoint(-648, 0), default,
            activeRoomID: 0);
        _ = tracker.Observe(
            BandProfile(80, 700, 0), Admitted,
            VisualRoomDirection.Left, LeftKnight, Size,
            default, 640, 1, groundTrackingReliable: true);
        _ = tracker.Observe(
            BandProfile(80, 700, 2), Admitted,
            VisualRoomDirection.Left, LeftKnight, Size,
            default, 640, 1.1, groundTrackingReliable: false);
        _ = tracker.Observe(
            BandProfile(8, 60, 4), Admitted,
            VisualRoomDirection.Left, LeftKnight, Size,
            new AtlasPoint(-40, 0), 640, 1.2,
            groundTrackingReliable: false);
        for (var index = 1; index <= 2; index++)
        {
            _ = tracker.Observe(
                BandProfile(8, 60, 4), Admitted,
                VisualRoomDirection.Left, RightKnight, Size,
                new AtlasPoint(-80, 0), 640, 1.2 + index * .1,
                groundTrackingReliable: false);
        }

        Equal(1, tracker.Snapshot.RoomID);
        Near(-648, tracker.Snapshot.EntryWorldPose.X);
        Near(-80, tracker.Snapshot.ActivationWorldPose?.X
            ?? throw new InvalidOperationException("Expected activation pose."));
    }

    public static void TestNearBlackFadeCanTransitionWithoutZeroSignalFrame()
    {
        var tracker = new VisualRoomTransitionTracker();
        _ = tracker.Observe(
            DoorwayProfile(Visible, 0), Admitted,
            VisualRoomDirection.Left, LeftKnight, Size,
            default, 640, 1);
        var pending = tracker.Observe(
            DoorwayProfile(NearBlack, 4), Admitted,
            VisualRoomDirection.Left, LeftKnight, Size,
            default, 640, 1.1);
        Check(pending.IsTransitioning);
        for (var index = 1;
             index < VisualRoomTransitionTracker.AppearanceStableFrames;
             index++)
        {
            _ = tracker.Observe(
                DoorwayProfile(NearBlack, 4), Admitted,
                VisualRoomDirection.Left, RightKnight, Size,
                default, 640, 1.1 + index / 60d);
        }
        Equal(1, tracker.Snapshot.RoomID);
    }

    public static void TestRemainingInDarkRoomCannotCreateAnotherRoom()
    {
        var tracker = new VisualRoomTransitionTracker();
        TransitionAppearance(
            tracker, Visible, NearBlack,
            VisualRoomDirection.Left, default, 1);
        Equal(1, tracker.Snapshot.RoomID);
        for (var index = 0; index < 120; index++)
        {
            _ = tracker.Observe(
                NearBlack, Admitted,
                VisualRoomDirection.Left, LeftKnight, Size,
                new AtlasPoint(-700, 0), 640, 2 + index / 60d);
        }
        Equal(1, tracker.Snapshot.RoomID);
        Equal(1UL, tracker.Snapshot.Revision);
    }

    public static void TestBrightDarkBoundaryReusesRoomOnReturn()
    {
        var tracker = new VisualRoomTransitionTracker();
        var departure = new AtlasPoint(-180, 0);
        TransitionAppearance(
            tracker, Visible, NearBlack,
            VisualRoomDirection.Left, departure, 1);
        Equal(1, tracker.Snapshot.RoomID);
        var darkPose = tracker.Snapshot.EntryWorldPose;
        TransitionAppearance(
            tracker, NearBlack, Visible,
            VisualRoomDirection.Right, darkPose, 2);
        Equal(0, tracker.Snapshot.RoomID);
        Equal(departure, tracker.Snapshot.EntryWorldPose);
        Equal(2UL, tracker.Snapshot.Revision);
    }

    public static void TestOrdinaryCameraPanAtBoundaryDoesNotSplitRoom()
    {
        var tracker = new VisualRoomTransitionTracker();
        for (var index = 0; index < 30; index++)
        {
            _ = tracker.Observe(
                Visible, Admitted,
                index < 15 ? VisualRoomDirection.Left : VisualRoomDirection.Right,
                index < 15 ? LeftKnight : RightKnight,
                Size, new AtlasPoint(-index * 12, 0), 640, index / 30d);
        }
        Equal(0, tracker.Snapshot.RoomID);
        Equal(0UL, tracker.Snapshot.Revision);
    }

    public static void TestDirectionalDarkeningWithoutDepartureEdgeRemainsCurrentRoom()
    {
        var tracker = new VisualRoomTransitionTracker();
        _ = tracker.Observe(
            DoorwayProfile(Visible, 0), Admitted,
            VisualRoomDirection.Right, null, Size,
            default, 640, 1);
        var pending = tracker.Observe(
            DoorwayProfile(NearBlack, -4), Admitted,
            VisualRoomDirection.Right, null, Size,
            default, 640, 1.1);
        Check(!pending.IsTransitioning);

        for (var index = 0;
             index < VisualRoomTransitionTracker.ArrivalStableFrames;
             index++)
        {
            _ = tracker.Observe(
                DoorwayProfile(Visible, -4), Admitted,
                VisualRoomDirection.Right, LeftKnight, Size,
                default, 640, 1.2 + index / 60d);
        }
        Equal(0, tracker.Snapshot.RoomID);
        Equal(0UL, tracker.Snapshot.Revision);
        Check(tracker.Snapshot.Portals.Count == 0);
    }

    public static void TestShrinkingVisibilityWhileKnightCenteredNeverSuspendsRoomTracking()
    {
        var tracker = new VisualRoomTransitionTracker();
        var centeredKnight = new StencilRect(230, 100, 40, 50);
        _ = tracker.Observe(
            DoorwayProfile(Visible, 0), Admitted,
            VisualRoomDirection.Right, centeredKnight, Size,
            default, 640, 1);
        for (var index = 0; index < 120; index++)
        {
            var snapshot = tracker.Observe(
                DoorwayProfile(NearBlack, -4), Admitted,
                VisualRoomDirection.Right, centeredKnight, Size,
                new AtlasPoint(index * 3, 0), 640,
                1.1 + index / 60d);
            Check(!snapshot.IsTransitioning);
            Equal(0, snapshot.RoomID);
        }
        Equal(0UL, tracker.Snapshot.Revision);
        Check(tracker.Snapshot.Portals.Count == 0);
    }

    public static void TestFadeAndOppositeArrivalCanCreateRoomWithoutTextureMotion()
    {
        var tracker = new VisualRoomTransitionTracker();
        var departureKnight = new StencilRect(480, 100, 40, 50);
        _ = tracker.Observe(
            DoorwayProfile(Visible, 0), Admitted,
            VisualRoomDirection.Right, departureKnight, Size,
            default, 640, 1);
        var pending = tracker.Observe(
            DoorwayProfile(NearBlack, 0), Admitted,
            VisualRoomDirection.Right, null, Size,
            default, 640, 1.1);
        Check(pending.IsTransitioning);
        for (var index = 0;
             index < VisualRoomTransitionTracker.ArrivalStableFrames;
             index++)
        {
            _ = tracker.Observe(
                DoorwayProfile(Visible, 0), Admitted,
                VisualRoomDirection.Right, LeftKnight, Size,
                default, 640, 1.2 + index / 60d);
        }

        Equal(1, tracker.Snapshot.RoomID);
        Equal(1UL, tracker.Snapshot.Revision);
        Equal(1, tracker.Snapshot.Portals.Count);
        Near(640 * VisualRoomTransitionTracker.FadeFallbackDepartureFraction
            - MidX(LeftKnight),
            tracker.Snapshot.ActivationWorldPose?.X
            ?? throw new InvalidOperationException("Expected activation pose."));
    }

    public static void TestFadePlacesArrivalFromKnightScreenPositionAcrossRooms()
    {
        var tracker = new VisualRoomTransitionTracker();
        var sourcePose = new AtlasPoint(100, 200);
        var sourceKnight = new StencilRect(480, 60, 40, 40);
        _ = tracker.Observe(
            DoorwayProfile(Visible, 0), Admitted,
            VisualRoomDirection.Right, sourceKnight, Size,
            sourcePose, 640, 1);
        _ = tracker.Observe(
            DoorwayProfile(NearBlack, 0), Admitted,
            VisualRoomDirection.Right, null, Size,
            sourcePose, 640, 1.1);
        for (var index = 0;
             index < VisualRoomTransitionTracker.ArrivalStableFrames;
             index++)
        {
            _ = tracker.Observe(
                DoorwayProfile(Visible, 0), Admitted,
                VisualRoomDirection.Right, LeftKnight, Size,
                sourcePose, 640, 1.2 + index / 60d);
        }

        var expected = new AtlasPoint(
            sourcePose.X
                + 640 * VisualRoomTransitionTracker.FadeFallbackDepartureFraction
                - MidX(LeftKnight),
            sourcePose.Y + MidY(sourceKnight) - MidY(LeftKnight));
        Equal(1, tracker.Snapshot.RoomID);
        Near(expected.X, tracker.Snapshot.EntryWorldPose.X);
        Near(expected.Y, tracker.Snapshot.EntryWorldPose.Y);
        Near(expected.X, tracker.Snapshot.ActivationWorldPose?.X
            ?? throw new InvalidOperationException("Expected activation pose."));
        Near(expected.Y, tracker.Snapshot.ActivationWorldPose?.Y
            ?? throw new InvalidOperationException("Expected activation pose."));
    }

    public static void TestRouteCalibratedFadeJoinsTownAtHackerProjectedOffset()
    {
        var tracker = new VisualRoomTransitionTracker();
        var sourcePose = new AtlasPoint(2_000, 1_000);
        var departure = new AtlasPoint(
            1_643.8387451171875 / 3,
            200.51010131835938 / 3);
        var arrival = new AtlasPoint(
            19.91363525390625 / 3,
            367.1662292480469 / 3);
        static StencilRect Knight(AtlasPoint point) =>
            new(point.X - 20, point.Y - 20, 40, 40);

        _ = tracker.Observe(
            DoorwayProfile(Visible, 0), Admitted,
            VisualRoomDirection.Right, Knight(departure), Size,
            sourcePose, 640, 1);
        _ = tracker.Observe(
            DoorwayProfile(NearBlack, 0), Admitted,
            VisualRoomDirection.Right, null, Size,
            sourcePose, 640, 1.1);
        for (var index = 0;
             index < VisualRoomTransitionTracker.ArrivalStableFrames;
             index++)
        {
            _ = tracker.Observe(
                DoorwayProfile(Visible, 0), Admitted,
                VisualRoomDirection.Right, Knight(arrival), Size,
                sourcePose, 640, 1.2 + index / 60d);
        }

        var expected = new AtlasPoint(
            sourcePose.X + departure.X - arrival.X,
            sourcePose.Y + departure.Y - arrival.Y);
        Equal(1, tracker.Snapshot.RoomID);
        Near(expected.X, tracker.Snapshot.EntryWorldPose.X);
        Near(expected.Y, tracker.Snapshot.EntryWorldPose.Y);
        Near(541.308, expected.X - sourcePose.X, .01);
        Near(-55.552, expected.Y - sourcePose.Y, .01);
    }

    public static void TestStableDarkeningDuringSameRoomTravelCannotCreateDoorway()
    {
        var tracker = new VisualRoomTransitionTracker();
        _ = tracker.Observe(
            DoorwayProfile(Visible, 0), Admitted,
            VisualRoomDirection.Right, RightKnight, Size,
            new AtlasPoint(877, -352), 640, 1);
        for (var index = 0;
             index < VisualRoomTransitionTracker.AppearanceStableFrames + 8;
             index++)
        {
            _ = tracker.Observe(
                DoorwayProfile(NearBlack, -4), Admitted,
                VisualRoomDirection.Right, RightKnight, Size,
                new AtlasPoint(880 + index * 3, -352), 640,
                1.1 + index / 60d);
        }
        Equal(0, tracker.Snapshot.RoomID);
        Equal(0UL, tracker.Snapshot.Revision);
        Check(tracker.Snapshot.Portals.Count == 0);
    }

    private static void TransitionAppearance(
        VisualRoomTransitionTracker tracker,
        FrameSignalProfile source,
        FrameSignalProfile target,
        VisualRoomDirection heldDirection,
        AtlasPoint pose,
        double start)
    {
        _ = tracker.Observe(
            DoorwayProfile(source, 0), Admitted,
            heldDirection,
            heldDirection == VisualRoomDirection.Left ? LeftKnight : RightKnight,
            Size, pose, 640, start);
        for (var index = 0;
             index < VisualRoomTransitionTracker.AppearanceStableFrames;
             index++)
        {
            var departureEdge = heldDirection == VisualRoomDirection.Left
                ? LeftKnight : RightKnight;
            var arrivalEdge = heldDirection == VisualRoomDirection.Left
                ? RightKnight : LeftKnight;
            _ = tracker.Observe(
                DoorwayProfile(
                    target, heldDirection == VisualRoomDirection.Left ? 4 : -4),
                Admitted,
                heldDirection,
                index == 0 ? departureEdge : arrivalEdge,
                Size, pose, 640, start + .1 + index / 60d);
        }
    }

    private static FrameSignalProfile BandProfile(
        double mean,
        int visibleCount,
        int shift)
    {
        var band = new FrameEdgeBlackBand(
            .45, new FrameFractionRange(.35, .65));
        return new FrameSignalProfile(
            1_000,
            mean < 18 ? 80 : 800,
            visibleCount,
            mean,
            MotionGrid(shift),
            new FrameEdgeBlackBands(band, band));
    }

    private static FrameSignalProfile DoorwayProfile(
        FrameSignalProfile profile,
        int shift)
    {
        var band = new FrameEdgeBlackBand(
            .45, new FrameFractionRange(.35, .65));
        return new FrameSignalProfile(
            profile.SampleCount,
            profile.SignaledCount,
            profile.VisibleCount,
            profile.MeanPeak,
            MotionGrid(shift),
            new FrameEdgeBlackBands(band, band));
    }

    private static LowResolutionMotionGrid MotionGrid(
        int shift,
        int verticalShift = 0,
        double gain = 1,
        int width = 32,
        int height = 18)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - shift;
                var sourceY = y - verticalShift;
                if (sourceX < 0 || sourceX >= width
                    || sourceY < 0 || sourceY >= height)
                    continue;
                var value = (sourceX * 37 + sourceY * 61 + sourceX * sourceY * 3)
                    % 211 + 22;
                pixels[y * width + x] = (byte)Math.Clamp(
                    Math.Round(value * gain, MidpointRounding.AwayFromZero), 0, 255);
            }
        }
        return new LowResolutionMotionGrid(width, height, pixels);
    }

    private static double MidX(StencilRect rect) => rect.X + rect.Width * .5;
    private static double MidY(StencilRect rect) => rect.Y + rect.Height * .5;

    private static void Near(double expected, double actual, double tolerance = .001)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected condition to be true.");
    }
}
