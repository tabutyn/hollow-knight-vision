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
        void Reject(Func<int, InputButtons?> input, Func<int, AtlasPoint?> camera, Func<int, GroundEdgeAnalysis> returning,
            Func<int, bool>? recentlyJumped = null)
        {
            var tracker = new WalkedGroundTracker();
            for (var i = 0; i <= 35; i++)
            {
                var image = i is >= 10 and <= 30 ? Hidden : i > 30 ? returning(i) : Mask(200);
                tracker.Observe("room A", image, Knight, camera(i), input(i), i * .03, [],
                    recentJump: recentlyJumped?.Invoke(i) == true);
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

        var jump = new JumpContactGate();
        jump.Record(.645);
        Reject(Right, Level, SameLevel, i => jump.Observe(false, i * .03));
        Check(jump.Observe(false, 1.04) && !jump.Observe(false, 1.05), "The jump contact cooldown did not expire.");
        Check(jump.Observe(true, 1.19) && jump.Observe(false, 1.23), "Holding jump did not renew the cooldown.");
        jump.Reset();
        Check(!jump.Observe(false, 1.23), "The previous input session kept its jump cooldown.");

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

    public static void TestShortUnmeasuredCameraGapRequiresSameMeasuredBasis()
    {
        Resume(7, 1, 3, resumes: true);
        Resume(8, 1, 3, resumes: false);
        Resume(7, 2, 3, resumes: false);
        Resume(7, 1, 12, resumes: false);

        static void Resume(long epoch, long generation, int gap, bool resumes)
        {
            var tracker = new WalkedGroundTracker();
            for (var i = 0; i < 20; i++)
                tracker.Observe("room", Mask(200), Knight, new(i * 2, 0), InputButtons.Right, i / 40d, [],
                    generation: 1, coordinateEpoch: 7);
            var ownedBeforeGap = tracker.InRoom("room").Single();
            for (var i = 20; i < 20 + gap; i++)
            {
                var estimate = tracker.Observe("room", Mask(200), Knight, null, InputButtons.Right,
                    i / 40d, [], generation: 1, coordinateEpoch: 8);
                Check(estimate.Floor is null && tracker.InRoom("room").Single() == ownedBeforeGap,
                    "An unmeasured camera cannot add a walk or invalidate the owned floor.");
            }

            var returned = tracker.Observe("room", Mask(200), Knight, new(2 * (20 + gap), 0), InputButtons.Right,
                (20 + gap) / 40d, [], generation: generation, coordinateEpoch: epoch);
            Check(returned.Floor.HasValue == resumes, "Only a short gap on the original measured basis can extend the old walk.");
            if (resumes)
                Check(tracker.InRoom("room").Single().AtlasEnd.X > ownedBeforeGap.AtlasEnd.X,
                    "A verified resumption must extend the owned floor.");
            else if (epoch != 7 || generation != 1)
                Check(tracker.InRoom("room").Count == 0, "An old room's coordinates must not be projected into the new capture basis.");
            else
                Check(tracker.InRoom("room").Single() == ownedBeforeGap, "A long gap in the same basis must preserve earlier ground.");
        }
    }

    public static void TestWalkUsesOnlyItsMeasuredIslandAndResumesKnownGround()
    {
        var tracker = new WalkedGroundTracker();
        for (var index = 0; index < 20; index++)
            Observe(index, 1, new(index * 2, 0));
        var established = tracker.InRoom("room").Single();
        for (var index = 20; index < 23; index++)
        {
            Check(Observe(index, null, null).Floor is null && tracker.InRoom("room").Single() == established,
                "An occluded camera can hold the last walked line without extending it.");
        }
        Check(Observe(23, 1, new(46, 0)).Floor.HasValue,
            "A short gap followed by the same measured owner can extend the floor.");
        var beforeIslandChange = tracker.InRoom("room").Single();

        var unsupported = Observe(24, 2, new(48, 0));
        Check(unsupported.Floor is null && tracker.InRoom("room").Count == 0,
            "A nearby pose on a different island must start with its own ground.");
        Check(tracker.OtherOwners("room", 2).Single() is { Lineage: 1 } other
            && other.Spans.SequenceEqual([beforeIslandChange]) && tracker.OtherOwners("next", 2).Count == 0,
            "A carrier gets the earlier owner's measured span only in its own room.");
        for (var index = 25; index < 38; index++)
            Observe(index, 2, new(index * 2, 0));
        Check(tracker.InRoom("room").Single().AtlasStart.X > beforeIslandChange.AtlasStart.X,
            "Newly walked ground on the second island must exclude columns seen on the first.");

        Check(Observe(38, 1, new(76, 0)).Floor is null
            && tracker.InRoom("room").Single() == beforeIslandChange,
            "A return to a measured island restores its own floor without bridging the unsupported handoff.");
        Check(Observe(39, 1, new(78, 0), "next").Floor is null && tracker.InRoom("next").Count == 0,
            "The same lineage value must not project into another room.");

        KnightTerrainContact Observe(int index, long? lineage, AtlasPoint? camera, string room = "room") =>
            tracker.Observe(room, Mask(200), Knight, camera, InputButtons.Right, index / 40d, [],
                generation: 1, coordinateEpoch: 7, measuredLineage: lineage);
    }

    public static void TestBotFloorsUseTheWalkedRowAndKeepOnlySupportedExtensions()
    {
        var pose = new AtlasPoint(20, 10);
        var walked = Line(WalkedGroundMemory.WalkedSegmentId, 120, 240, 240);
        var size = new PixelRect(180, 92, 20, 38);
        DetectedFloorLine[] edges = [new(132, new(70, 249), [new(70, 109), new(200, 249)]),
            new(143, new(80, 239)), new(129, new(310, 371), [new(310, 320), new(350, 371)]), new(80, new(115, 155))];
        var merged = GroundStrokeGeometry.MergeWalkedFloors(edges, [walked], pose, Width, Height, size);
        var spans = merged.Select(floor => (floor.Row, floor.XSpan.Start, floor.XSpan.EndInclusive)).ToHashSet();
        Check(spans.SetEquals([(130, 70, 249), (129, 310, 320), (129, 350, 371), (80, 115, 155)]),
            "The bot should retain the walked row, nearby measured extensions, real gaps and other-height platforms.");
        var dark = GroundStrokeGeometry.MergeWalkedFloors([], [walked], pose, Width, Height, size).Single();
        Check(dark.Row == 130 && dark.XSpan == new PixelSpan(100, 219), "The bot must still see verified ground when the edge is hidden.");
        Check(ReferenceEquals(edges, GroundStrokeGeometry.MergeWalkedFloors(edges, [walked], null, Width, Height, size))
            && ReferenceEquals(edges, GroundStrokeGeometry.MergeWalkedFloors(edges, [walked], new(2000, 0), Width, Height, size))
            && ReferenceEquals(edges, GroundStrokeGeometry.MergeWalkedFloors(edges, [walked with { SegmentId = 2 }], pose, Width, Height, size)),
            "Without measured placement in the current image, ordinary detector edges must stay intact.");
        var clipped = GroundStrokeGeometry.MergeWalkedFloors([], [walked], new(140, 10), Width, Height, size).Single();
        Check(clipped.XSpan == new PixelSpan(0, 99), "Walking into the next view must clip the old footprint without stretching it.");

        foreach (var second in new[] { 153, 154 })
        {
            var duplicate = GroundStrokeGeometry.MergeWalkedFloors([new(360 - second, new(265, 355))],
                [Line(walked.SegmentId, 267, 309, 150), Line(walked.SegmentId, 295, 324, second)], default(AtlasPoint), Width, Height, size);
            Check(duplicate.Count == 1 && duplicate[0].Row == 210 && duplicate[0].XSpan == new PixelSpan(265, 355),
                "Overlapping walks on nearby rows must share one elevation after the camera's projection rounds to pixels.");
        }
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

    private static GroundHypothesisAtlasLine Line(long id, double left, double right, double y) =>
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
