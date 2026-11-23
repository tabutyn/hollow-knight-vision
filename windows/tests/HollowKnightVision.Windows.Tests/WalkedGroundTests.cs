using HollowKnightVision.Windows.Core;

internal static class WalkedGroundTests
{
    private const int Width = 640;
    private const int Height = 360;
    private static readonly PixelRect Knight = new(183, 90, 34, 47);
    private static readonly GroundEdgeAnalysis Hidden = Image([]);

    public static void TestWalkedDarkGroundSurvivesOcclusionAndReversal()
    {
        var tracker = new WalkedGroundTracker();
        for (var i = 0; i <= 35; i++)
        {
            var hidden = i is >= 10 and <= 30;
            tracker.Observe("room A", hidden ? Hidden : Mask(200), Knight, new(i * 3, 0),
                InputButtons.Right, i * .03, []);
            if (i == 10 || i == 30)
            {
                var held = tracker.InRoom("room A").Single();
                Check(held.AtlasEnd.X == 232, "A missing sprite removed or added walked ground.");
            }
        }
        var before = tracker.InRoom("room A").Single();
        Check(before.AtlasStart == new AtlasPoint(196, 230) && before.AtlasEnd == new AtlasPoint(310, 230),
            "Same-level observations did not verify the floor behind the occluder.");
        for (var i = 36; i <= 75; i++)
        {
            var cameraX = 105 - (i - 35) * 3;
            tracker.Observe("room A", Mask(200), Knight, new(cameraX, 0),
                InputButtons.Left, i * .03, []);
        }
        tracker.Observe("room A", Hidden, Knight, new(-15, 0), InputButtons.None, 76 * .03, []);
        var after = tracker.InRoom("room A").Single();
        Check(after.AtlasStart == new AtlasPoint(181, 230) && after.AtlasEnd == before.AtlasEnd,
            "Walking back left separated an already verified floor.");
        tracker.BreakContinuity();
        tracker.Observe("room B", Mask(200), Knight, new(500, 0), InputButtons.Right, 77 * .03, []);
        Check(tracker.InRoom("room B").Count == 0 && tracker.InRoom("room A").Single() == after,
            "Entering another room moved or deleted verified ground.");
        tracker.Reset();
        Check(tracker.InRoom("room A").Count == 0, "Reset retained the prior atlas floor.");
    }

    public static void TestOccludedWalkNeedsInputAndAContinuousLevelCamera()
    {
        void Reject(Func<int, InputButtons?> input, Func<int, AtlasPoint?> camera, Func<int, GroundEdgeAnalysis> returning)
        {
            var tracker = new WalkedGroundTracker();
            for (var i = 0; i <= 35; i++)
            {
                var image = i is >= 10 and <= 30 ? Hidden : i > 30 ? returning(i) : Mask(200);
                tracker.Observe("room A", image, Knight, camera(i), input(i), i * .03, []);
            }
            var retained = tracker.InRoom("room A").Single();
            Check(retained.AtlasEnd.X == 232, "An unsupported crossing extended the persistent floor.");
        }

        static AtlasPoint? Level(int i) => new(i * 3, 0);
        static InputButtons? Right(int _) => InputButtons.Right;
        static GroundEdgeAnalysis SameLevel(int _) => Mask(200);
        Reject(i => i is >= 10 and <= 30 ? InputButtons.None : InputButtons.Right, Level, SameLevel);
        Reject(i => i == 22 ? InputButtons.Right | InputButtons.ActionZ : InputButtons.Right, Level, SameLevel);
        Reject(i => i is >= 10 and <= 30 ? null : InputButtons.Right, Level, SameLevel);
        Reject(Right, i => i is >= 10 and <= 30 ? null : Level(i), SameLevel);
        Reject(Right, i => new(i * 3, i == 21 ? 8 : 0), SameLevel);
        Reject(Right, Level, _ => Mask(200, 9));

        var still = new KnightLevelWalk();
        for (var i = 0; i <= 12; i++)
            Check(still.Observe(new AtlasPoint(100 + 2 * i, 130), new AtlasPoint(0, 0),
                InputButtons.Left | InputButtons.Right, i * .04, Width, Height) is null,
                "Opposing directions recorded motion.");
    }

    public static void TestFootUsesCurrentPixelsAndRejectsAmbiguousFaces()
    {
        var locator = new KnightFootLocator();
        Check(locator.Locate(Mask(200), Knight, 1, .04) == new KnightFootLocator.Measurement(new(200, 130), false),
            "The mask did not yield a foot measurement.");
        Check(locator.Locate(Hidden, Knight, 1, .08) is null, "The previous mask was reused as a measurement.");
        Check(locator.Locate(Mask(207), new PixelRect(225, 95, 4, 15), 1, .12)?.Foot == new AtlasPoint(207, 130),
            "A stale detection crop prevented reacquisition from the current image.");
        locator.Reset();
        Check(locator.Locate(Image([180, 209]), new PixelRect(174, 90, 48, 47), 1, .16) is null,
            "Ambiguous bright masks supplied a foot measurement.");
    }

    public static void TestWalkSnapsToAnObservedFloorAndReplacesOverlappingStrokes()
    {
        var tracker = new WalkedGroundTracker();
        for (var i = 0; i <= 12; i++)
            tracker.Observe("room A", Mask(200), Knight, new(i * 3, 0), InputButtons.Right,
                i * .03, [new DetectedFloorLine(132, new(90, 400))]);
        var walked = tracker.InRoom("room A").Single();
        Check(walked.AtlasStart.Y == 228 && walked.AtlasEnd.X == 241,
            "The walked footprint did not snap to the detected edge at the same elevation.");

        var falseEdge = Line(1, 165, 275, 214);
        var upper = Line(2, 195, 240, 280);
        var duplicate = Line(3, 197, 238, 230);
        var current = new GroundLinePresenceReview(4, new CleanFloorLine(132, new(160, 204)),
            GroundLinePresenceState.Confirmed, 1, 1);
        var strokes = GroundStrokeGeometry.VisibleLines([falseEdge, upper, duplicate], [current],
            [walked], new(36, 0), Height, Knight);
        Check(strokes.Contains(walked) && strokes.Contains(upper)
            && strokes.Count(line => line.AtlasStart.X < walked.AtlasEnd.X && line.AtlasEnd.X > walked.AtlasStart.X
                && Math.Abs(line.AtlasStart.Y - walked.AtlasStart.Y) < 40) == 1,
            "The display retained a competing detector stroke across walked ground.");
        Check(strokes.Any(line => line.AtlasEnd.X == walked.AtlasStart.X && line.AtlasStart.X == 165)
            && strokes.Any(line => line.AtlasStart.X == walked.AtlasEnd.X && line.AtlasEnd.X == 275),
            "The display lost the unvisited ends of a detector stroke.");
        var adjacent = GroundStrokeGeometry.Coalesce([Line(1, 0, 10, 50),
            Line(2, 8, 23, 50), Line(3, 24, 60, 50), Line(4, 60, 100, 52)]);
        Check(adjacent.Length == 3 && adjacent.Any(line => line.AtlasStart.X == 0 && line.AtlasEnd.X == 23),
            "Stroke cleanup bridged an empty column or a step.");
    }

    private static GroundHypothesisAtlasLine Line(int id, double left, double right, double y) =>
        new(id, new(left, y), new(right, y));

    private static GroundEdgeAnalysis Mask(int center, int rowOffset = 0) => Image([center], rowOffset);

    private static GroundEdgeAnalysis Image(int[] centers, int rowOffset = 0)
    {
        var luma = new byte[Width * Height];
        foreach (var center in centers)
        {
            for (var y = 104 + rowOffset; y <= 115 + rowOffset; y++)
                Array.Fill(luma, (byte)230, y * Width + center - 6, 13);
            luma[(111 + rowOffset) * Width + center] = 0;
            luma[(111 + rowOffset) * Width + center + 1] = 0;
        }
        return new(Width, Height, new byte[luma.Length], luma);
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
