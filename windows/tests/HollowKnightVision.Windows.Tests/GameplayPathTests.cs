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

    internal static void TestSavedFloorKeepsAContinuousWorldPathAcrossReloadAndCorrection()
    {
        var tracker = new GameplayPathTracker();
        var room = new GameplaySavedRoomPath(7, [Floor(900, 1500, 400)],
            [new(new(800, 300), new(800, 360))]);
        var sample = new GameplayPathFrame("return", 0, Width, Height, new(25, 10),
            [Floor(125, 300, 80), Floor(355, 515, 80)],
            [Line(100, 275, 70), Line(330, 490, 70)], TerrainWallDetection.Empty,
            new(110, 290), new(104, 270, 12, 20), null)
        { Atlas = new(room, new(1095, 500), 1) };
        Require(tracker.Observe(sample with { VerifiedCamera = null, VisibleFloors = [] }) is null,
            "An unplaced view must not borrow the saved route.");
        var restored = tracker.Observe(sample with { Time = .2 });
        Require(restored is { Camera: { X: 800, Y: 330 }, Strokes.Count: 1 }
            && restored.Strokes[0] == new AtlasPathStroke(new(900, 420), new(1500, 420), Walk, true),
            "The first placed frame must recover a continuous walk through the occluded region.");
        var correctedRoom = room with { Ground = [Floor(1100, 1700, 400)] };
        var corrected = tracker.Observe(sample with
        {
            Time = .4, Atlas = new(correctedRoom, new(1295, 500), 2), Ground = [], VisibleFloors = []
        });
        Require(corrected is { Camera: { X: 1000 }, Strokes.Count: 1 }
            && corrected.Strokes[0].A == new AtlasPathPoint(1100, 420),
            "A newer world placement must retire the live fragments from the old placement.");
        var next = tracker.Observe(sample with
        {
            Time = .5, Atlas = new(new GameplaySavedRoomPath(8, [], []), new(1295, 500), 2),
            Ground = [], VisibleFloors = []
        });
        Require(next is null, "Changing durable room IDs must retire even a chart with the same live room label.");
    }

    internal static void TestRestoredCameraFootprintsAdmitOnlyThePreviouslyObservedDescent()
    {
        var tracker = new GameplayPathTracker();
        var saved = new GameplaySavedRoomPath(3, [Floor(0, 130, 0), Floor(25, 75, 840, walked: true)],
            [new(new(0, 0), new(640, 360))]);
        var top = new GameplayPathFrame("return", 0, Width, Height, new(0, 800), [],
            [], TerrainWallDetection.Empty, new(74, 320), new(69, 300, 10, 20), null)
        { Atlas = new(saved, new(320, 180), 1) };
        var unknown = tracker.Observe(top);
        Require(unknown?.Strokes.Any(stroke => stroke.Kind == Drop) == true && unknown.Hint is null,
            "Two saved endpoints alone cannot make an unseen descent an automatic objective.");
        var visited = saved with { CameraFootprints = [.. saved.CameraFootprints,
            new(new(0, 330), new(640, 360)), new(new(0, 650), new(640, 360))] };
        var known = tracker.Observe(top with { Time = .2, Atlas = new(visited, new(320, 180), 1) });
        Require(known?.Hint is { Objective.Y: 20 }, "The known descent should work without retraversing the archived room.");
    }

    internal static void TestShortCombatEdgeAtWalkedEndpointDoesNotCreateAnotherLanding()
    {
        var sample = new GameplayPathFrame("fight", 0, Width, Height, default(AtlasPoint),
            [Floor(534, 779, 176), Floor(531, 592, 166), Floor(580, 690, 175, walked: true)],
            [], TerrainWallDetection.Empty, new(600, 185), new(592, 150, 16, 35), null);
        var view = new GameplayPathTracker().Observe(sample);
        Require(view?.Strokes.SequenceEqual([new(new(534, 210), new(779, 210), Walk, true)]) == true,
            "A three-pixel overhang at a walked endpoint must not make a false lower landing.");
    }

    internal static void TestLongNearbySavedFloorsPreserveBothPlatformsAndDivideTheirSmallOverlap()
    {
        var saved = new GameplaySavedRoomPath(6, [Floor(-20, 180, 120), Floor(160, 330, 170)], []);
        var sample = new GameplayPathFrame("returned", 0, Width, Height, default(AtlasPoint), [],
            [], TerrainWallDetection.Empty, new(10, 240), new(0, 180, 20, 60), null)
        { Atlas = new(saved, new(320, 180), 1) };
        var view = new GameplayPathTracker().Observe(sample);
        Require(view?.Strokes.Where(stroke => stroke.Kind == Walk).SequenceEqual([
            new(new(-20, 180), new(170, 180), Walk, true), new(new(170, 230), new(330, 230), Walk, true)]) == true,
            "Nearby long platforms must meet at a shared column without dropping a platform.");
    }

    internal static void TestUnplacedSuccessorKeepsDirectionWithoutImportingCoordinates()
    {
        var tracker = new GameplayPathTracker();
        var first = new GameplayPathFrame("room", 1, Width, Height, default(AtlasPoint),
            [Floor(0, 200, 20)], [], TerrainWallDetection.Empty,
            new(50, Height - 20), new(46, Height - 30, 8, 10), null) { CaptureGeneration = 1 };
        var old = tracker.Observe(first)?.Hint;
        Require(old.HasValue && old.Value.Next.X > old.Value.Current.X, "The registered route should start facing right.");
        var successor = first with
        {
            Time = 1.1, VerifiedCamera = null, Ground = [], VisibleFloors = [Line(0, 300, 20)],
            KnightFoot = new(180, Height - 20), LocalCamera = new(2, new(450, 0))
        };
        var next = tracker.Observe(successor);
        var hint = next?.Hint;
        Require(next is { Camera: null, LocalOrigin: { X: 450, Y: 0 } }
            && next.Strokes.SequenceEqual([new(new(450, 30), new(750, 30), Walk, true)])
            && hint.HasValue && hint.Value.Next.X > hint.Value.Current.X,
            "A continuous successor must keep the heading and use only its own measured coordinates.");
        var later = tracker.Observe(successor with { Time = 1.6, LocalCamera = new(3, new(450, 0)) })?.Hint;
        Require(later.HasValue && later.Value.Next.X < later.Value.Current.X,
            "A new chart after a capture gap should choose its own direction.");
    }

    internal static void TestPromotionCarriesMeasuredFloorAndSurvivesNextRegisteredFrame()
    {
        var tracker = new GameplayPathTracker();
        var provisional = new GameplayPathFrame("room", 1, Width, Height, null,
            [], [Line(5, 90, 20)], TerrainWallDetection.Empty, new(30, Height - 20),
            new(20, Height - 40, 10, 20), null)
        { CaptureGeneration = 7, LocalCamera = new(4, default) };
        var registration = provisional with
        {
            Time = 1.1, VerifiedCamera = default(AtlasPoint), VisibleFloors = [],
            Atlas = new(new(1, [], []), new(420, 230), 8)
        };
        Require(tracker.Observe(provisional) is { Camera: null }, "Measured local ground should be displayed before placement.");
        var placed = tracker.Observe(registration);
        var expected = new AtlasPathStroke(new(105, 90), new(190, 90), Walk, true);
        Require(placed is { Camera: { X: 100, Y: 50 } } && placed.Strokes.SequenceEqual([expected]),
            "A matching registration should project the measured floor into the Atlas without new edge evidence.");
        Require(tracker.Observe(registration with { Time = 1.2 })?.Strokes.SequenceEqual([expected]) == true,
            "The promoted floor should survive the next registered capture without duplication.");

        foreach (var unrelated in new[]
        {
            registration with { LocalCamera = new(5, default) },
            registration with { LocalCamera = new(4, new(3, 0)) },
            registration with { CaptureGeneration = 8 },
            registration with { Room = "other" }
        })
        {
            tracker.Reset();
            tracker.Observe(provisional);
            Require(tracker.Observe(unrelated) is null, "A different room, capture or coordinate epoch adopted unrelated ground.");
        }
        tracker.Reset();
        tracker.Observe(provisional with { LocalCamera = null });
        Require(tracker.Observe(registration) is null, "Screen-only ground should disappear when the map registers.");
    }

    internal static void TestPromotionProjectsExplicitGoalOntoRegisteredGround()
    {
        var tracker = new GameplayPathTracker();
        var local = new GameplayPathFrame("room", 1, Width, Height, null,
            [], [Line(0, 100, 20)], TerrainWallDetection.Empty, new(20, Height - 20), null, null)
        { CaptureGeneration = 1, LocalCamera = new(4, default) };
        var initial = tracker.Observe(local)!;
        var preview = tracker.SelectObjective(initial, new(70, 63), 2);
        Require(preview?.SelectedObjective == new AtlasPathPoint(70, 63)
            && preview.SelectedRoute.LastOrDefault() == new AtlasPathPoint(70, 63)
            && initial.PreserveNewerSelection(preview).SelectedObjective == preview.SelectedObjective,
            "The path click should immediately select and display the complete route.");
        var registered = local with
        {
            Time = 1.1, VerifiedCamera = default(AtlasPoint), VisibleFloors = [],
            KnightSize = new(12, 310, 16, 30), Atlas = new(new(1, [], []), new(420, 230), 8)
        };
        var placed = tracker.Observe(registered);
        var goal = new AtlasPathPoint(170, 100);
        Require(placed?.SelectedObjective == goal && placed.SelectedRoute.LastOrDefault() == goal,
            "Registration must apply the measured translation and the measured Knight-height correction.");
        var cleared = tracker.SelectObjective(placed!, new(170, 100), 2);
        Require(cleared is { SelectedObjective: null, SelectedRoute.Count: 0 }
            && placed!.PreserveNewerSelection(cleared).SelectedObjective is null,
            "Clicking the selected destination should clear it, even while the analysis frame is unchanged.");
        Require(tracker.Observe(registered with { Time = 1.2 })?.SelectedObjective is null,
            "The capture worker should acknowledge the cleared destination.");
    }

    internal static void TestPromotionKeepsAnExistingManualGoalAndDoesNotRepeat()
    {
        var tracker = new GameplayPathTracker();
        var registered = new GameplayPathFrame("room", 1, Width, Height, default(AtlasPoint),
            [], [], TerrainWallDetection.Empty, new(35, Height - 20), new(30, 320, 10, 20), null)
        { Atlas = new(new(1, [Floor(100, 200, 70)], []), new(420, 230), 8) };
        var initial = tracker.Observe(registered)!;
        Require(tracker.SelectObjective(initial, new(120, 90), 2)?.SelectedObjective == new AtlasPathPoint(120, 90),
            "A registered chart should accept an explicit selection.");
        var local = registered with
        {
            Time = 1.1, VerifiedCamera = null, Atlas = null, VisibleFloors = [Line(0, 100, 20)],
            LocalCamera = new(4, default)
        };
        var provisional = tracker.Observe(local)!;
        Require(tracker.SelectObjective(provisional, new(70, 40), 2)?.SelectedObjective == new AtlasPathPoint(70, 40),
            "An unplaced chart should have its own selectable path.");
        var placed = tracker.Observe(registered with { Time = 1.2, LocalCamera = local.LocalCamera });
        Require(placed?.SelectedObjective == new AtlasPathPoint(120, 90),
            "Promoting geometry should preserve the registered chart's existing objective.");
        _ = tracker.SelectObjective(placed!, new(140, 90), 2);
        placed = tracker.Observe(registered with { Time = 1.3, LocalCamera = local.LocalCamera });
        Require(placed?.SelectedObjective == new AtlasPathPoint(140, 90),
            "A later choice must survive subsequent registration samples.");
    }

    internal static void TestDiscardingFrameChartDropsScreenGoalAndKeepsRegisteredMap()
    {
        var tracker = new GameplayPathTracker();
        var screen = new GameplayPathFrame("room", 1, Width, Height, null,
            [], [Line(0, 100, 20)], TerrainWallDetection.Empty, new(40, Height - 20),
            new(35, 320, 10, 20), null);
        var view = tracker.Observe(screen)!;
        Require(tracker.SelectObjective(view, new(20, 40), 2)?.SelectedObjective == new AtlasPathPoint(20, 40),
            "A short-lived screen chart should accept a destination on its current floor.");
        tracker.Suspend();
        var registered = tracker.Observe(screen with
        {
            Time = 1.1, VerifiedCamera = default(AtlasPoint),
            Atlas = new(new(1, [Floor(200, 500, 20)], []), new(320, 180), 1), VisibleFloors = []
        });
        Require(registered is { SelectedObjective: null }
            && registered.Strokes.SequenceEqual([new(new(200, 40), new(500, 40), Walk, true)]),
            "Discarding the screen chart must keep the registered map and leave its objective untouched.");
        var stale = tracker.SelectObjective(view, new(70, 40), 2)!;
        var resumed = tracker.Observe(screen with { Time = 1.2 });
        Require(resumed is { SelectedObjective: null }
            && resumed.PreserveNewerSelection(stale).SelectedObjective is null,
            "A delayed edit from a retired screen chart cannot become the new chart's objective.");
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
