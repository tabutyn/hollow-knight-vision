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

    internal static void TestWalkedCrossingRemovesFalseWallButPreservesNextGap()
    {
        var tracker = new GameplayPathTracker();
        var first = Floor(275, 485, 93);
        var next = Floor(540, 600, 93);
        var wall = new DetectedWall(460, Height - 125, Height - 83, true);
        var observed = new GameplayPathFrame("room", .1, Width, Height, default(AtlasPoint),
            [first, next], [Line(330, 445, 106)], new([wall], []), new(330, Height - 93),
            new(322, Height - 128, 17, 35), null);
        for (var i = 1; i <= 4; i++) tracker.Observe(observed with { Time = i * .1 });
        var divided = tracker.Observe(observed with { Time = .56 });
        Require(divided?.Graph?.Strokes.Any(stroke => stroke.Kind == Jump && stroke.Horizontal
            && stroke.A.X < 460 && stroke.B.X > 460) == true,
            "The corroborated short wall should initially make a jump across that column.");

        var walked = Floor(275, 485, 93, walked: true);
        var crossing = observed with { Time = .75, Ground = [next, walked] };
        CheckRoad(tracker.Observe(crossing));
        CheckRoad(new GameplayPathTracker().Observe(crossing with { VisibleFloors = [], Terrain = TerrainWallDetection.Empty }));
        CheckRoad(tracker.Observe(crossing with
        {
            Time = .95, VerifiedCamera = new(490, 0), VisibleFloors = [], Terrain = TerrainWallDetection.Empty
        }));

        var corrected = tracker.Observe(observed with { Time = 1.15, Ground = [first, next, Floor(275, 400, 93, walked: true)] });
        Require(corrected?.Graph?.Floors.Any(floor => floor.Y == 93 && floor.Left < 460 && floor.Right > 460) == false,
            "A corrected placement that ends before the wall must revive the real obstruction.");

        static void CheckRoad(GameplayPathView? view)
        {
            Require(view?.Graph?.Floors.SequenceEqual([new(275, 485, 93), new(540, 600, 93)]) == true,
                "A verified crossing should leave two floors with the real gap between them.");
            var jumps = view!.Graph!.Strokes.Where(stroke => stroke.Kind == Jump && stroke.Horizontal).ToArray();
            Require(jumps.Length == 1 && jumps[0].A.X < 485 && jumps[0].B.X > 540,
                "Only the real gap should retain a jump connection.");
        }
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
        var nearby = tracker.Observe(sample with { Time = .13, KnightFoot = new(84, 290) });
        Require(nearby?.Hint is { Objective.X: 100, Next.X: > 84 },
            "Approaching the old lip must keep the reachable goal while there is no onward shelf.");
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

    internal static void TestRegisteredLandingsCanUseSchematicJumpHeadroom()
    {
        var start = new GameplayPathFrame("room", 1, Width, Height, new(0, -297),
            [Floor(0, 100, 15), Floor(112, 225, 40)], [], TerrainWallDetection.Empty,
            new(15, 48), new(10, 28, 10, 20), null);
        var route = new GameplayPathTracker().Observe(start);
        Require(route?.Strokes.Any(stroke => stroke.Kind == Jump && stroke.Horizontal && stroke.A.Y == 65) == true
            && route.Hint is { Objective: { X: 225, Y: 60 } },
            "The drawn shelf may sit above the covered viewport when both actual landings are registered.");

        var unvisited = start with
        {
            Ground = [Floor(0, 100, 30), Floor(112, 225, 55)],
            KnightFoot = new(15, 33), KnightSize = new(10, 13, 10, 20)
        };
        Require(new GameplayPathTracker().Observe(unvisited)?.Hint is { Objective: { X: 100, Y: 50 } },
            "The headroom allowance must not permit an unregistered landing.");
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

    internal static void TestReacquiredCameraCanVerifyBothSidesOfAMaskedFloor()
    {
        var source = new GameplayPathFrame("room", 1, Width, Height, null, [], [Line(22, 498, 92)],
            TerrainWallDetection.Empty, new(40, Height - 92), new(31, Height - 118, 17, 26), null)
        { CaptureGeneration = 1, LocalCamera = new(4, new(600, 0)) };
        var correction = GroundHypothesisTrackingResult.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, CameraPosition = default(AtlasPoint),
            LocalTextureSupport = 4, LocalReferenceTimestamp = 1, LocalReferencePosition = new(600, 0),
            LocalMatchedImageDisplacement = new(150, 0), GlobalCorrection = new(-450, 0)
        };
        var left = Line(172, 550, 93);
        var right = Line(614, 630, 93);
        var next = source with
        {
            Time = 1.1, VisibleFloors = [left, right], LocalCamera = new(5, default),
            KnightFoot = new(260, Height - 93), KnightSize = new(251, Height - 119, 17, 26)
        };

        GameplayPathTracker Recover(GroundHypothesisTrackingResult proof, long generation = 1,
            GameplayPathFrame? start = null)
        {
            var tracker = new GameplayPathTracker();
            tracker.Observe(start ?? source);
            tracker.RecoverMeasuredBasis("room", generation, 4, 5, 1.1, proof);
            return tracker;
        }

        var bridged = Recover(correction);
        Require(bridged.Observe(next)?.Strokes.SequenceEqual([new(new(172, 119), new(630, 119), Walk, true)]) == true,
            "A measured correction should stitch two receiving edges across the occlusion without extending past them.");
        Require(Walks(bridged.Observe(next with { Time = 1.81 })) == 2,
            "The temporary witness should expire without entering the receiving chart's measured ground.");

        var room = new GameplaySavedRoomPath(7, [], []);
        var placed = Recover(correction, start: source with
        {
            VerifiedCamera = new(600, 0), Atlas = new(room, new(520, 280), 1)
        });
        var registered = placed.Observe(next with
        {
            VerifiedCamera = default(AtlasPoint), Atlas = new(room, new(370, 160), 2)
        });
        Require(registered is { Camera: { X: 50, Y: -20 } }
            && registered.Strokes.SequenceEqual([new(new(222, 99), new(680, 99), Walk, true)]),
            "Different registered chart offsets should map the temporary witness in both axes.");
        Require(Recover(correction).Observe(next with { VisibleFloors = [left] })?.Strokes
            .SequenceEqual([new(new(172, 119), new(550, 119), Walk, true)]) == true,
            "A source-chart witness should not create the other side of a missing platform.");
        Require(Walks(Recover(correction).Observe(next with { VisibleFloors = [Line(172, 500, 93), right] })) == 2,
            "A wide gap should still divide the platforms.");
        Require(Walks(Recover(correction).Observe(next with { VisibleFloors = [left, right, Line(569, 593, 28)] })) == 3,
            "A lower landing beneath the seam should keep it open.");

        var wall = new DetectedWall(573, Height - 170, Height - 92, true);
        var blocked = Recover(correction);
        var contact = next with { Terrain = new([wall], []), ContactWall = wall };
        for (var i = 0; i < 4; i++) blocked.Observe(contact with { Time = 1.1 + i * .09 });
        Require(Walks(blocked.Observe(contact with { Time = 1.49 })) == 2,
            "A corroborated wall should keep the seam open while the previous floor witness is still active.");

        Require(Walks(Recover(correction with { LocalReferenceTimestamp = null }).Observe(next)) == 2
            && Walks(Recover(correction with { LocalReferenceTimestamp = .7 }).Observe(next)) == 2
            && Walks(Recover(correction, generation: 2).Observe(next)) == 2
            && Walks(Recover(correction).Observe(next with { Room = "other" })) == 2
            && Walks(Recover(correction).Observe(next with { LocalCamera = new(6, default) })) == 2,
            "A missing reference or another room, generation, or epoch must not transfer the witness.");
        var interrupted = Recover(correction);
        interrupted.Suspend();
        Require(Walks(interrupted.Observe(next)) == 2, "A capture interruption should retire the temporary witness.");

        var chained = Recover(correction);
        chained.Observe(next);
        chained.RecoverMeasuredBasis("room", 1, 5, 6, 1.2, correction with
        {
            LocalReferenceTimestamp = 1.1, LocalReferencePosition = default(CameraVector),
            LocalMatchedImageDisplacement = default(CameraVector), GlobalCorrection = new(10, 0)
        });
        Require(Walks(chained.Observe(next with
        {
            Time = 1.2, LocalCamera = new(6, new(10, 0))
        })) == 2, "A second correction must not treat the first inferred seam as measured ground.");

        static int Walks(GameplayPathView? view) => view?.Strokes.Count(stroke => stroke.Kind == Walk) ?? 0;
    }

    internal static void TestRegisteredObjectiveSurvivesUnplacedHandoffAndSuspension()
    {
        var tracker = new GameplayPathTracker();
        var target = new AtlasPathPoint(20, 40);
        var registered = new GameplayPathFrame("room", 1, Width, Height, default(AtlasPoint),
            [], [], TerrainWallDetection.Empty, new(80, Height - 20), new(75, Height - 40, 10, 20), null)
        { CaptureGeneration = 7, Atlas = new(new(1, [Floor(0, 200, 20)], []), new(320, 180), 1) };
        var view = tracker.Observe(registered)!;
        Require(tracker.SelectObjective(view, new(20, 40), 2)?.SelectedObjective == target,
            "The registered destination should be selected before the capture is interrupted.");
        tracker.Suspend();

        var unplaced = tracker.Observe(registered with
        {
            Time = 1.1, VerifiedCamera = null, Atlas = null, VisibleFloors = [Line(0, 200, 20)],
            LocalCamera = new(1, new(400, 0))
        });
        Require(unplaced is { Camera: null, SelectedObjective: null },
            "An unplaced successor should find its own route without adopting the registered point.");

        var restored = tracker.Observe(registered with { Time = 1.2 });
        Require(restored?.SelectedObjective == target && restored.SelectedRoute.LastOrDefault() == target,
            "An interruption before the next capture dropped the registered destination.");
        Require(tracker.SelectObjective(restored!, new(20, 40), 2) is { SelectedObjective: null },
            "Clicking the destination again should clear it.");
        tracker.Suspend();
        Require(tracker.Observe(registered with { Time = 1.3 }) is { SelectedObjective: null },
            "An interruption before the next capture dropped the clear action.");
    }

    internal static void TestMeasuredRecoveryRetainsOnlyTheSameRegisteredObjective()
    {
        var target = new AtlasPathPoint(20, 40);
        var registered = new GameplayPathFrame("room", 1, Width, Height, default(AtlasPoint),
            [], [Line(0, 200, 20)], TerrainWallDetection.Empty, new(80, Height - 20),
            new(75, Height - 40, 10, 20), null)
        { CaptureGeneration = 7, Atlas = new(new(1, [Floor(0, 200, 20)], []), new(320, 180), 4),
            LocalCamera = new(4, default) };
        var correction = GroundHypothesisTrackingResult.Empty with
        {
            LocalTextureSupport = 4, LocalReferenceTimestamp = 1, LocalReferencePosition = default(CameraVector),
            LocalMatchedImageDisplacement = new(-30, 0), GlobalCorrection = new(30, 0)
        };
        var recovered = registered with
        {
            Time = 1.1, VerifiedCamera = new(30, 0), LocalCamera = new(5, new(30, 0)),
            Atlas = registered.Atlas! with { WorldFromLocal = new(290, 180), GeometryEpoch = 5 }
        };
        Require(Recover(recovered) is { SelectedObjective: { } selected, SelectedRoute: { } route }
            && selected == target && route.LastOrDefault() == target,
            "A local camera correction should preserve the destination on the same registered Atlas room.");
        foreach (var unrelated in new[]
        {
            recovered with { Room = "other" },
            recovered with { CaptureGeneration = 8 },
            recovered with { Atlas = recovered.Atlas! with { Room = new(2, [Floor(0, 200, 20)], []) } },
        })
            Require(Recover(unrelated) is { SelectedObjective: null },
                "A different room, capture, or registration inherited the last room's objective.");

        GameplayPathView? Recover(GameplayPathFrame sample)
        {
            var tracker = new GameplayPathTracker();
            var view = tracker.Observe(registered)!;
            Require(tracker.SelectObjective(view, new(20, 40), 2)?.SelectedObjective == target,
                "The registered destination should be selected before the correction.");
            tracker.RecoverMeasuredBasis("room", 7, 4, 5, 1.05, correction);
            return tracker.Observe(sample);
        }
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
