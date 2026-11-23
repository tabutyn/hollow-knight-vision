using HollowKnightVision.Windows.Core;
using static HollowKnightVision.Windows.Core.AtlasPathKind;

internal static class GameplayPathTests
{
    private const int Width = 640;
    private const int Height = 360;

    internal static void TestCombatEdgesKeepPreviouslyExposedWalkContinuous()
    {
        var tracker = new GameplayPathTracker();
        var level = new[] { Floor(-40, 300, 180) };
        var clear = new[] { Line(0, 300, 180) };
        var start = new GameplayPathFrame("room", 0, Width, Height, new(0, 0), level, clear, TerrainWallDetection.Empty,
            new(40, 180), new(34, 160, 12, 20), null);
        for (var i = 0; i < 6; i++) tracker.Observe(start with { Time = i / 30.0 });
        var slash = start with
        {
            VisibleFloors = [Line(0, 126, 180), Line(171, 300, 180), Line(181, 239, 192)],
            Terrain = new([new(146, 100, 182, true)], [new(0, 0, 146, 180, 125)]),
            Ground = [..level, Floor(25, 105, 180, walked: true)],
        };
        GameplayPathView? view = null;
        for (var i = 6; i < 24; i++) view = tracker.Observe(slash with { Time = i / 30.0 });
        Require(view is not null && view.Strokes.Count == 1 && view.Strokes.Single().Kind == Walk
            && view.Strokes[0].A == new AtlasPathPoint(-40, 200)
            && view.Strokes[0].B == new AtlasPathPoint(300, 200),
            "A sustained foreground edge split a walked and independently exposed floor.");
    }

    internal static void TestLedgeAndContactOverrulePreviouslyExposedFloor()
    {
        var tracker = new GameplayPathTracker();
        var ground = new[] { Floor(100, 350, 92), Floor(320, 420, 138) };
        var lower = new DetectedFloorLine(268, new(100, 349), [new(100, 178), new(205, 349)]);
        var clear = new GameplayPathFrame("room", 0, Width, Height, new(0, 0), ground,
            [lower, Line(320, 420, 138)], TerrainWallDetection.Empty, new(180, 268), new(171, 234, 18, 34), null);
        for (var i = 0; i < 3; i++) tracker.Observe(clear with { Time = i * .1 });
        var obstruction = new DetectedWall(200, 222, 268, true);
        var step = new DetectedWall(320, 222, 268, true);
        var faces = clear with { VisibleFloors = [], Terrain = new([obstruction, step], []) };
        GameplayPathView? view = null;
        for (var i = 3; i <= 9; i++) view = tracker.Observe(faces with { Time = i * .1 });
        Require(Crosses(view, 200) && !Crosses(view, 320),
            "An upper landing should support its wall while the slash preserves the already exposed crossing.");
        for (var i = 10; i <= 15; i++) view = tracker.Observe(faces with { Time = i * .1, ContactWall = obstruction });
        Require(!Crosses(view, 200), "Repeated movement contact should establish a blocking face.");

        static bool Crosses(GameplayPathView? sample, int x) => sample?.Strokes.Any(stroke =>
            stroke.Kind == Walk && stroke.A.Y == 126 && stroke.A.X < x && stroke.B.X > x) == true;
    }

    internal static void TestAStationaryFaceNeedsContinuousWitnessesAndCanBeRetired()
    {
        var tracker = new GameplayPathTracker();
        var ground = new[] { Floor(0, 350, 100) };
        var closed = new GameplayPathFrame("room", 0, Width, Height, new(0, 0), ground,
            [Line(0, 128, 100), Line(150, 350, 100)],
            new([new(138, 141, 260, true)], []), new(70, 260), new(64, 240, 12, 20), null);
        var first = tracker.Observe(closed);
        Require(first?.Strokes.Count(stroke => stroke.Kind == Walk) == 1,
            "The first wall candidate cut a route without corroboration.");
        GameplayPathView? last = null;
        for (var i = 1; i <= 12; i++) last = tracker.Observe(closed with { Time = i / 30.0 });
        Require(last?.Strokes.Count(stroke => stroke.Kind == Walk) == 2
            && !last.Strokes.Any(stroke => stroke.Kind == Jump), "A high face corroborated at 30 Hz should block travel.");
        var clear = closed with { Terrain = TerrainWallDetection.Empty, VisibleFloors = [Line(0, 350, 100)] };
        for (var i = 13; i <= 25; i++) last = tracker.Observe(clear with { Time = i / 30.0 });
        Require(last?.Strokes.Count == 1 && last.Strokes[0].Kind == Walk,
            "A verified open crossing should retire the standing wall portion.");
    }

    internal static void TestFrameLocalPathsAndOtherRoomsDoNotPolluteRegisteredTerrain()
    {
        var tracker = new GameplayPathTracker();
        var based = new GameplayPathFrame("room", 0, Width, Height, new(20, -90),
            [Floor(-100, 140, 70, walked: true)], [], TerrainWallDetection.Empty, null, null, null);
        var original = tracker.Observe(based);
        Require(original?.Strokes.Count == 1 && original.Strokes[0].A.X == -100 && original.Camera == new AtlasPoint(20, -90),
            "Registered walked ground was lost in a dark frame.");
        var local = tracker.Observe(based with
        {
            Time = .16,
            VerifiedCamera = null,
            VisibleFloors = [Line(230, 430, 160)],
        });
        Require(local is not null && local.Camera is null && local.Strokes.Count == 1 && local.Strokes[0].A.X == 230,
            "An unplaced frame reused stale world geometry.");
        var resumed = tracker.Observe(based with { Time = .35 });
        Require(resumed?.Strokes.SequenceEqual(original!.Strokes) == true,
            "Screen-space evidence changed a registered road.");
        var newRoom = tracker.Observe(based with { Room = "other", Time = .48, Ground = [], VisibleFloors = [] });
        Require(newRoom is null, "A new room inherited geometry from the previous room.");
        tracker.Suspend();
        tracker.Reset();
    }

    internal static void TestTheAutomaticObjectiveContinuesAcrossTheJumpShelf()
    {
        var tracker = new GameplayPathTracker();
        var upper = new[] { Line(0, 100, 70), Line(114, 175, 95) };
        var sample = new GameplayPathFrame("room", 0, Width, Height, new(0, 0), [],
            [upper[0]], TerrainWallDetection.Empty, new(20, 290), new(15, 270, 10, 20), null);
        var first = tracker.Observe(sample);
        Require(first?.Hint is { Objective.X: 100, Next.X: > 20 }, "The first frontier should lie along the starting floor.");
        var approach = tracker.Observe(sample with { Time = .18, VisibleFloors = upper, KnightFoot = new(86, 290) });
        Require(approach?.Hint is { Objective.X: 175 }, "The new raised platform should replace the nearby old lip.");
        var takeoff = tracker.Observe(sample with { Time = .24, VisibleFloors = upper, KnightFoot = new(94, 290) });
        Require(takeoff?.Hint is { Current.Y: 90, Next.Y: > 90 }, "The arrow should turn upward at the shelf.");
        var landing = tracker.Observe(sample with { Time = .36, VisibleFloors = upper, KnightFoot = new(130, 265) });
        Require(landing?.Hint is { Objective.X: 175, Current.Y: 115, Next.X: > 130 },
            "The arrow should move off the connecting shelf as the Knight lands.");
    }

    internal static void TestUnvisitedSpaceCannotConnectADropBetweenRegisteredPlatforms()
    {
        var tracker = new GameplayPathTracker();
        var floors = new[] { Floor(0, 130, 0), Floor(25, 75, 840) };
        var top = new GameplayPathFrame("room", 0, Width, Height, new(0, 800), floors,
            [], TerrainWallDetection.Empty, new(74, 320), new(69, 300, 10, 20), null);
        // Walked top floor makes the lower landing the only automatic candidate.
        top = top with { Ground = [floors[0], Floor(25, 75, 840, walked: true)] };
        var unseen = tracker.Observe(top);
        Require(unseen?.Strokes.Any(stroke => stroke.Kind == Drop) == true && unseen.Hint is null,
            "Geometry alone should not route an automatic drop through unvisited air.");
        tracker.Observe(top with { Time = .14, VerifiedCamera = new(0, 0), KnightFoot = null });
        tracker.Observe(top with { Time = .28, VerifiedCamera = new(0, 350), KnightFoot = null });
        tracker.Observe(top with { Time = .42, VerifiedCamera = new(0, 650), KnightFoot = null });
        var registered = tracker.Observe(top with { Time = .56 });
        Require(registered?.Hint is { Objective.Y: 20 }, "Once the whole descent is observed the lower landing should be reachable.");
    }

    private static GroundHypothesisAtlasLine Floor(int x1, int x2, int y, bool walked = false) =>
        new(walked ? WalkedGroundMemory.WalkedSegmentId : 1, new(x1, y), new(x2, y));

    private static DetectedFloorLine Line(int x1, int x2, int y) =>
        new(Height - y, new(x1, x2 - 1));

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
