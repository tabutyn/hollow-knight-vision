using HollowKnightVision.Windows.Core;
using static HollowKnightVision.Windows.Core.AtlasPathKind;

internal static class AtlasPlatformGraphTests
{
    internal static void TestHackerObjectiveDefaultClearReplacementAndRoomHitOrder()
    {
        AtlasPathPlatform[] floors = [new(10, 100, 101), new(140, 260, 100), new(280, 350, 50)];
        var graph = new AtlasPlatformGraph(floors, [], height: 20, width: 10);
        var room = new HackerObjectivePlacement("active", new(-250, 50), new(10, 10), graph);
        var choices = HackerAtlasObjectives.Default;
        Require(choices.Target(room) == new AtlasPathPoint(260, 120), "The default must use the farthest endpoint of the top tier.");
        var extended = room with { Graph = new([..floors, new(280, 330, 100)], [], 20, 10) };
        Require(choices.Target(extended) == new AtlasPathPoint(330, 120), "The default must follow the edited floor while it is active.");
        choices = choices.Select(new(80, 170), 2, [extended])!;
        Require(choices.IsAutomatic("active") && choices.Target(room) is null,
            "Clicking the default marker must clear it even after the Atlas graph changes.");

        var overlap = room with { Scene = "other" };
        choices = choices.Select(new(-200, 171), 4, [room, overlap])!;
        var longer = room with { Graph = new([..floors, new(380, 440, 105)], [], 20, 10) };
        Require(choices.Target(longer) == new AtlasPathPoint(50, 121) && choices.SelectedFor("other") is null
            && choices.IsAutomatic("other"), "The active room must win equal hits and the manual point must survive a rebuilt graph.");
        choices = choices.Select(new(-199, 171), 4, [longer, overlap])!;
        Require(choices.Target(longer) is null && HackerAtlasObjectives.Default.Target(longer) == new AtlasPathPoint(440, 125),
            "Click near the same target to clear it. A fresh Atlas should restore its authored default.");
        Require(choices.Select(new(4000, 4000), 4, [longer]) is null && choices.Select(new(80, 170), double.NaN, [longer]) is null,
            "A click with no route hit must leave the selection alone.");
    }

    internal static void TestWalkedExtentsPreserveEvidenceAndRejectSpuriousJump()
    {
        var graph = new AtlasPlatformGraph([new(0, 275, 95), new(485, 800, 94)], [],
            height: 35, width: 17, detectedGround: true, walked: [new(275, 485, 93)]);
        Require(graph.Floors.SequenceEqual(new AtlasPathPlatform[] { new(0, 800, 93) })
            && graph.Strokes.Select(stroke => stroke.Kind).SequenceEqual(new[] { Walk }),
            "The tracked floor and both edge fragments should form a single walk stroke on the walked row.");

        graph = new([new(200, 685, 93)], [new(609, 70, 180)], height: 35, width: 16,
            detectedGround: true, walked: [new(276, 613, 92)]);
        Require(graph.Floors.SequenceEqual(new AtlasPathPlatform[] { new(200, 613, 92), new(617, 685, 92) }),
            "The verified footprint should survive through the wall clearance while the unwalked side stays blocked.");

        var tiles = new AtlasPathPlatform[] { new(0, 40, 60), new(42, 72, 61), new(110, 150, 60) };
        graph = new(tiles, [], height: 24, width: 12, detectedGround: true, handoffSeams: [new(0, 150, 60)]);
        Require(graph.Floors.Count == 1, "A verified visual handoff should allow the longer supported join.");
        graph = new(tiles, [new(88, 35, 70)], height: 24, width: 12, detectedGround: true,
            handoffSeams: [new(0, 150, 60)]);
        Require(graph.Floors.Count == 2, "A wall in the gap should prevent joining floors.");
        graph = new([..tiles, new(80, 98, 35)], [], height: 24, width: 12, detectedGround: true,
            handoffSeams: [new(0, 150, 60)]);
        Require(graph.Floors.Count == 3, "A lower floor inside the opening should keep the pit open.");
    }

    internal static void TestFloorClusteringIsLocalAndLeavesThePitOpen()
    {
        var graph = new AtlasPlatformGraph([
            new(-200, -110, -3), new(0, 40, 0), new(2, 55, 1), new(53, 70, 2), new(86, 110, 1)
        ], [], height: 20, width: 10);
        Require(graph.Floors.SequenceEqual([new(-200, -110, -3), new(0, 70, 2), new(86, 110, 1)]),
            "Only nearby pieces belong to the same floor. The pit remains open.");
    }

    internal static void TestOverhangingLedgeIsReachableFromBothLips()
    {
        var graph = new AtlasPlatformGraph([new(0, 100, 0), new(25, 75, 25)], [], height: 20, width: 10);
        var launches = graph.Strokes.Where(stroke => stroke.Kind == Jump && !stroke.Horizontal && stroke.A.Y == 20)
            .Select(stroke => stroke.A.X).ToHashSet();
        Require(launches.SetEquals([15, 85]), "Both exposed lips should permit a jump onto the overhanging platform.");
    }

    internal static void TestRouteApproachesJumpWhenGoalIsBehindTheKnight()
    {
        var graph = new AtlasPlatformGraph([new(0, 100, 0), new(0, 120, 200)], [], height: 20, width: 10,
            connections: [new(new(90, 0), new(100, 200), true)]);
        var navigator = new AtlasPathNavigator();
        var objective = new AtlasPathPoint(5, 220);
        var approach = navigator.Follow(graph, new(20, 0), grounded: true, measured: true,
            admitted: null, walked: [], fixedObjective: objective);
        var takeoff = navigator.Follow(graph, new(90, 0), grounded: true, measured: true,
            admitted: null, walked: [], fixedObjective: objective);
        Require(approach is { } path && BotPathMotion.Road(path, graph.KnightHeight, 200, frontier: false).Dx == 1,
            "The route must go right toward the reachable jump before going left to the upper goal.");
        Require(takeoff is { } atJump && BotPathMotion.Road(atJump, graph.KnightHeight, 200, frontier: false).Dy == 1
            && graph.Strokes.Count(stroke => stroke.Kind == DoubleJump) == 3, "At the shelf the objective must guide upward.");
    }

    internal static void TestJumpShelfUsesMeasuredRiseAndClearsWalls()
    {
        AtlasPathPlatform[] floors = [new(0, 100, 0), new(112, 175, 25)];
        var graph = new AtlasPlatformGraph(floors, [], height: 20, width: 10);
        Require(graph.Strokes.Count(stroke => stroke.Kind == Jump) == 3
            && graph.Strokes.Any(stroke => stroke.Kind == Jump && stroke.Horizontal && stroke.A.Y == 50),
            "A reachable level change should use a three-stroke shelf above its landings.");
        Require(Reachable(graph, new(0, 20), new(175, 45)) && Reachable(graph, new(175, 45), new(0, 20)),
            "A jump shelf should connect in both directions.");
        Require(!graph.Strokes.Any(stroke => stroke.Kind == Drop), "A jump pair should suppress its separate fall.");

        graph = new(floors, [new(105, 0, 160)], height: 20, width: 10);
        Require(!graph.Strokes.Any(stroke => stroke.Kind == Jump), "The ballistic arc should reject walls above the apex.");

        const double scale = 22.22426;
        static int Px(double value) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        graph = new([new(Px(88), Px(91), Px(36.96)), new(Px(85), Px(88.5), Px(42.457))], [],
            height: 28, width: 11, pixelsPerWorldUnit: new(scale, scale));
        Require(graph.Strokes.Any(stroke => stroke.Kind == Jump),
            "The measured 5.497-world-unit rise from King's Pass should be reachable.");

        graph = new([
            new(Px(156.024), Px(158.903), Px(44.081)),
            new(Px(158.903), Px(161.881), Px(49.125)),
            new(Px(152.098), Px(157.123), Px(54.143))
        ], [
            new(Px(158.903), Px(44.081), Px(49.125)),
            new(Px(161.881), Px(49.125), Px(54.644)),
            new(Px(157.123), Px(51.933), Px(54.143))
        ], height: 28, width: 11, pixelsPerWorldUnit: new(scale, scale));
        Require(Reachable(graph, new(Px(156.024), Px(44.081) + 28), new(Px(152.098), Px(54.143) + 28)),
            "The observed zigzag jumps should connect through the wall-lined ledges.");
    }

    internal static void TestFallsDescendFromEitherLipWithoutClimbingOrCrossingShelves()
    {
        AtlasPathPlatform[] floors = [new(0, 100, 0), new(25, 75, 200)];
        var graph = new AtlasPlatformGraph(floors, [], height: 20, width: 10);
        foreach (var (lip, x) in new[] { (25, 20), (75, 80) })
        {
            var drop = graph.Strokes.Single(stroke => stroke.Kind == Drop && !stroke.Horizontal && stroke.A.X == x);
            Require(Reachable(graph, new(lip, 220), new(x, 20)) && !Reachable(graph, new(x, 20), new(lip, 220)),
                "Each exposed lip should connect only from the upper floor.");
            Require(drop.AllowsTraversal(new(x, 150), new(x, 30)) && !drop.AllowsTraversal(new(x, 30), new(x, 150)),
                "A point inside a drop should preserve the direction of travel.");
        }

        graph = new([..floors, new(120, 180, 80)], [], height: 20, width: 10,
            connections: [new(new(10, 0), new(150, 80), true)]);
        var crossing = new AtlasPathPoint(20, 105);
        Require(graph.Strokes.Any(stroke => stroke.Kind == DoubleJump && stroke.Contains(crossing))
            && graph.Strokes.Any(stroke => stroke.Kind == Drop && stroke.Contains(crossing))
            && !graph.Nodes.ContainsKey(crossing) && !Reachable(graph, new(20, 20), new(25, 220)),
            "An incidental crossing of a shelf and a fall should never create a transfer.");
    }

    internal static void TestFallsBranchAndSteerAroundIntermediateLedges()
    {
        AtlasPathPlatform[] floors = [new(0, 30, 200), new(40, 45, 100), new(65, 90, 0)];
        var graph = new AtlasPlatformGraph(floors, [], height: 20, width: 10);
        Require(Reachable(graph, new(30, 220), new(40, 120)) && Reachable(graph, new(30, 220), new(65, 20))
            && !Reachable(graph, new(65, 20), new(30, 220)), "One lip should offer both lower landings.");
        graph = new([floors[0], new(35, 100, 100), floors[2]], [], height: 20, width: 10);
        Require(!Reachable(graph, new(30, 220), new(65, 20), point => !(point.Y == 120 && point.X is >= 35 and <= 100)),
            "A landing intercepted by a wide floor should require walking on that intervening floor.");

        graph = new([floors[0], new(35, 50, 100), new(60, 90, 0)], [], height: 20, width: 10, pixelsPerWorldUnit: new(22, 22));
        Require(Reachable(graph, new(30, 220), new(60, 20))
            && graph.Strokes.Any(stroke => stroke.Kind == Drop && stroke.Horizontal
                && stroke.A.X <= 35 && stroke.B.X >= 50 && stroke.A.Y >= 125),
            "The diagram should shift over an intermediate ledge before descending to the lower landing.");
        graph = new([floors[0], new(35, 50, 100), new(0, 30, 0)], [], height: 20, width: 10, pixelsPerWorldUnit: new(22, 22));
        Require(Reachable(graph, new(30, 220), new(30, 20)), "A fall should be able to steer back after bypassing a ledge.");

        const double scale = 22.22426;
        static int Px(double value) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        graph = new([new(Px(93.7845), Px(117.1841), Px(62.0648)), new(Px(105.0718), Px(117.0244), Px(54.0298))],
            [new(Px(117.1841), Px(56.9301), Px(62.0648)), new(Px(117.0244), Px(46.1744), Px(54.0298))],
            height: 28, width: 11, pixelsPerWorldUnit: new(scale, scale));
        Require(Reachable(graph, new(Px(117.1841), Px(62.0648) + 28), new(Px(117.0244), Px(54.0298) + 28)),
            "The saved King's Pass descent should be able to drift back over the lower wall cap.");
    }

    internal static void TestFallUsesFirstLandingAndRespectsWallsAtTheLip()
    {
        AtlasPathPlatform[] floors = [new(0, 100, 0), new(10, 80, 100), new(25, 75, 200)];
        var blocked = new AtlasPlatformGraph(floors, [new(21, 110, 200, 1)], height: 20, width: 10);
        Require(Reachable(blocked, new(75, 220), new(80, 120))
            && !blocked.Strokes.Any(stroke => stroke.Kind == Drop && stroke.Contains(new(25, 220))),
            "The exposed lip should reach the first landing and the obstructed lip should remain closed.");

        Require(LeftFall(new(25, 30, 200, 1)) && LeftFall(new(21, 30, 190, 1))
            && !LeftFall(new(21, 30, 200, 1)) && !LeftFall(new(22, 200, 220, 1)),
            "A tangent ledge and a lower face can be passed, while a face at the lip or in the exit blocks the fall.");

        bool LeftFall(AtlasPathWall wall) => new AtlasPlatformGraph([floors[0], floors[2]], [wall], height: 20, width: 10)
            .Strokes.Any(stroke => stroke.Kind == Drop && stroke.Contains(new(25, 220)));
    }

    internal static void TestNavigationKeepsAOneWayDescentAndSteersDuringTheFall()
    {
        var graph = new AtlasPlatformGraph([new(0, 100, 0), new(25, 75, 200)], [], height: 20, width: 10);
        var navigator = new AtlasPathNavigator();
        var above = navigator.Follow(graph, new(50, 200), true, true, null, []);
        Require(above is { Objective.Y: 20 }, "An automatic route can select a lower reachable platform.");
        Require(navigator.Follow(graph, new(80, 100), false, true, null, [],
            fixedObjective: new(80, 160), fixedObjectiveOnGround: false) is null,
            "An objective partway up the same drop must not reverse it.");
        Require(navigator.Follow(graph, new(80, 100), false, true, null, [],
            fixedObjective: new(80, 60), fixedObjectiveOnGround: false) is { Next.Y: < 120 },
            "A lower objective on the same drop must remain available.");
        Require(navigator.Follow(graph, new(80, 0), true, true, null, [],
            fixedObjective: new(50, 220)) is null, "The landing cannot route back up the drop.");

        var offset = new AtlasPlatformGraph([new(0, 30, 200), new(65, 90, 0)], [], height: 20, width: 10);
        var steer = new AtlasPathNavigator();
        var target = new AtlasPathPoint(80, 20);
        steer.Follow(offset, new(30, 200), true, true, null, [], fixedObjective: target);
        var falling = steer.Follow(offset, new(35, 175), false, true, null, [], fixedObjective: target);
        Require(falling is { FallSteering: 1 }, "Guidance should steer toward the offset landing during the actual descent.");
    }

    internal static void TestHackerRouteKeepsJumpAcrossFramesAndRejectsAnOldCrossing()
    {
        var graph = new AtlasPlatformGraph([new(0, 100, 0), new(112, 225, 25)], [], height: 20, width: 10,
            connections: [new(new(95, 0), new(117, 25), true)]);
        var target = new AtlasPathPoint(205, 45);
        var route = new HackerAtlasRoute();
        var takeoff = route.Observe(graph, new(94, 0), true, target, 1);
        Require(takeoff.FirstOrDefault() == new AtlasPathPoint(94, 20) && takeoff.LastOrDefault() == target,
            "The authored goal should resolve through the entire jump before takeoff.");

        var highFeet = new AtlasPathPoint(106, 110);
        Require(graph.NearestStroke(highFeet with { Y = highFeet.Y + graph.KnightHeight },
            2 * graph.KnightHeight) is null, "The high jump fixture must defeat frame-local route picking.");
        var airborne = route.Observe(graph, highFeet, false, target, 1.1);
        Require(airborne.Count >= 3 && airborne.FirstOrDefault() == new AtlasPathPoint(106, 50)
            && airborne.LastOrDefault() == target, "The route should remain visible from the planned crossing in the air.");
        var landed = route.Observe(graph, new(142, 25), true, target, 1.2);
        Require(landed.FirstOrDefault() == new AtlasPathPoint(142, 45) && landed.LastOrDefault() == target,
            "After landing, the route should advance onto the destination platform.");
        var below = new HackerAtlasRoute().Observe(graph, new(190, -2), true, target, 1.2);
        Require(below.FirstOrDefault() == new AtlasPathPoint(190, 45) && below.LastOrDefault() == target,
            "A grounded preview should retain the previous two-Knight-height walk tolerance.");

        route.Observe(graph, new(94, 0), true, target, 1.3);
        Require(route.Observe(graph, highFeet, false, target, 1.9).Count == 0,
            "A stale crossing cannot survive a capture interruption.");
        route.Observe(graph, new(94, 0), true, target, 2);
        Require(route.Observe(graph, highFeet, false, new(190, 45), 2.1).Count == 0,
            "Changing objectives in midair should discard the old crossing.");
        route.Observe(graph, new(94, 0), true, target, 2.2);
        Require(route.Observe(graph, highFeet, false, target, double.NaN).Count == 0
            && route.Observe(graph, highFeet, false, target, 2.3).Count == 0,
            "An invalid reading must not leave a cached crossing available.");
    }

    internal static void TestAutomaticGuidanceCrossesShelfAfterAnEarlyTakeoff()
    {
        var graph = new AtlasPlatformGraph([new(0, 100, 0), new(112, 225, 25)], [], height: 20, width: 10);
        var navigator = new AtlasPathNavigator();
        Require(Follow(15, 0, true) is { Objective: { X: 225, Y: 45 }, Next.X: > 15 },
            "The initial route should aim through the shelf toward the upper landing.");
        Require(Follow(98, 0, true) is { Current.Y: 20, Next.Y: > 20 },
            "Taking off just beyond the drawn shelf departure should still advise a jump.");
        var flight = Follow(106, 58, false);
        Require(flight is { Objective: { X: 225, Y: 45 } } && flight.Value.Next.X > flight.Value.Current.X,
            "The arrow should move along the shelf as the Knight passes above it.");
        Require(Follow(124, 25, true) is { Current.Y: 45, Next.X: > 124 },
            "On landing, the arrow must advance along the newly reached platform.");
        Require(Follow(225, 25, true)?.Objective != new AtlasPathPoint(225, 45),
            "The automatic objective should finish once that destination is visited.");

        AtlasPathHint? Follow(int x, int y, bool grounded) => navigator.Follow(graph, new(x, y), grounded, true, null, []);
    }

    internal static void TestDoorsSpikesAndManualConnectionsConstrainTravel()
    {
        AtlasPathPlatform[] floors = [new(0, 100, 0), new(25, 75, 200)];
        foreach (var door in new AtlasPathDoor[] { new(new(75, 200), new(75, 225)), new(new(70, 200), new(85, 200)) })
        {
            var graph = new AtlasPlatformGraph(floors, [], height: 20, width: 10, doors: [door]);
            Require(!graph.Strokes.Any(stroke => stroke.Kind == Drop && stroke.Contains(new(75, 220)))
                && graph.Strokes.Any(stroke => stroke.Kind == Drop && stroke.Contains(new(25, 220))),
                "A vertical or horizontal door at the right lip should leave only the left drop.");
        }
        var hazard = new AtlasPlatformGraph([floors[0]], [], height: 20, width: 10, spikes: [new(40, 60, 0)]);
        Require(hazard.Floors.Count == 2 && !hazard.Floors.Any(floor => floor.Contains(50)),
            "Spike geometry should cut walkable ground.");
        var manual = new AtlasPlatformGraph(floors, [], height: 20, width: 10,
            connections: [new(new(20, 0), new(30, 200), true)]);
        Require(!manual.Strokes.Any(stroke => stroke.Kind == Drop) && Reachable(manual, new(0, 20), new(75, 220)),
            "An authored jump should replace the drop for its pair.");
    }

    internal static void TestIncrementalIntersectionsStayEquivalentThroughFloorEdits()
    {
        AtlasPathPlatform[] original = [new(0, 100, 0), new(112, 175, 25)];
        AtlasPathPlatform[] extended = [..original, new(186, 240, 32)];
        var before = new AtlasPlatformGraph(original, [], height: 20, width: 10);
        var after = new AtlasPlatformGraph(extended, [], height: 20, width: 10, previous: before);
        var cold = new AtlasPlatformGraph(extended, [], height: 20, width: 10);
        Require(after.Strokes.SequenceEqual(cold.Strokes) && after.Nodes.Count == cold.Nodes.Count
            && after.WalkNodes.SetEquals(cold.WalkNodes)
            && after.Nodes.All(pair => cold.Nodes.TryGetValue(pair.Key, out var edges) && edges.SequenceEqual(pair.Value)),
            "An incremental graph should match a cold rebuild after a terrain extension.");
        var removed = new AtlasPlatformGraph(original, [], height: 20, width: 10, previous: after);
        Require(removed.Nodes.Count == before.Nodes.Count && removed.Nodes.All(pair =>
            before.Nodes.TryGetValue(pair.Key, out var edges) && edges.SequenceEqual(pair.Value)),
            "Removing the extension should also remove its stale intersections.");
        Require(cold.UpperRightObjective == new AtlasPathPoint(240, 52), "The default objective should be the top tier's far right.");
        Require(cold.NearestStroke(new(20, 21), 1, walkOnly: true) is { Kind: Walk }, "Objectives should snap to a nearby walk.");
        Require(cold.NearestStroke(new(-200, 40), 20) is null, "Clicks far from the graph should preserve the selection.");
    }

    internal static void TestExplicitRouteConnectsMidpointsAndShowsOneFallShaft()
    {
        var graph = new AtlasPlatformGraph([new(0, 300, 0), new(320, 440, 20)], [], height: 20, width: 10);
        var start = new AtlasPathPoint(50, 20);
        var target = new AtlasPathPoint(395, 40);
        var from = graph.NearestStroke(start, 0)!.Value;
        var to = graph.NearestStroke(target, 0)!.Value;
        var path = AtlasPathRouting.FindRoute(graph, from, start, to, target);
        Require(path.FirstOrDefault() == start && path.LastOrDefault() == target && path.Any(point => point.Y > target.Y),
            "A clicked point beyond the visited tile should have an exact route through the connecting shelf.");

        var fall = new AtlasPlatformGraph([new(0, 100, 0), new(25, 75, 200)], [], height: 20, width: 10);
        var descent = fall.Strokes.First(stroke => stroke.Kind == Drop && !stroke.Horizontal);
        var high = new AtlasPathPoint(descent.A.X, 155);
        var low = new AtlasPathPoint(descent.A.X, 35);
        Require(AtlasPathRouting.FindRoute(fall, descent, high, descent, low).SequenceEqual(new[] { high, low })
            && AtlasPathRouting.FindRoute(fall, descent, low, descent, high).Count == 0,
            "Selecting an interior point on a fall must preserve one-way traversal.");

        var visible = AtlasPathRouting.DisplayStrokes([
            new(new(8, 0), new(8, 120), Drop, false),
            new(new(8, 60), new(8, 120), Drop, false),
            new(new(8, 60), new(30, 60), Drop, true)
        ]);
        Require(visible.Count == 2 && visible.Count(stroke => !stroke.Horizontal) == 1
            && visible.Any(stroke => stroke.Horizontal && stroke.B.X == 30),
            "Overlapping portions of a fall should draw one shaft and keep the landing branch.");
    }

    internal static void TestAnExistingNodeCannotTransferThroughAnOverlappingJump()
    {
        var graph = new AtlasPlatformGraph([new(0, 40, 0), new(65, 105, 80), new(115, 160, 170)], [],
            height: 20, width: 10, connections: [
                new(new(40, 0), new(70, 80), true),
                new(new(70, 80), new(125, 170), true)
            ]);
        var start = new AtlasPathPoint(70, 105);
        var landing = new AtlasPathPoint(70, 100);
        var target = new AtlasPathPoint(160, 190);
        var rise = graph.Strokes.Single(stroke => stroke.Kind == DoubleJump
            && stroke.A == landing && stroke.B == new AtlasPathPoint(70, 195));
        var destination = graph.NearestStroke(target, 0, walkOnly: true)!.Value;
        var route = AtlasPathRouting.FindRoute(graph, rise, start, destination, target);
        Require(route.Take(2).SequenceEqual([start, landing]) && route.LastOrDefault() == target,
            "An existing shelf node must use the landing before transferring to the next jump.");
    }

    internal static void TestRecordedKingsPassJumpKeepsAdviceAboveAndPastTheShelf()
    {
        var graph = new AtlasPlatformGraph([new(3245, 3291, 291), new(3324, 3384, 378)], [],
            height: 28, width: 11, pixelsPerWorldUnit: new(22.224, 22.224));
        (int X, int Y, bool Grounded)[] frames = [
            (3267, 291, true), (3267, 291, true), (3267, 312, false), (3282, 347, false), (3301, 379, false),
            (3319, 401, false), (3338, 413, false), (3349, 412, false), (3349, 400, false), (3349, 380, true)
        ];
        var navigation = new AtlasPathNavigator();
        for (var index = 0; index < frames.Length; index++)
        {
            var frame = frames[index];
            var path = navigation.Follow(graph, new(frame.X, frame.Y), frame.Grounded, measured: true,
                admitted: null, walked: []);
            Require(path is { Objective: { X: 3384, Y: 406 } }, $"Lost the route on recorded sample {index}.");
            var hint = BotPathMotion.Road(path!.Value, graph.KnightHeight, 640, frontier: true);
            if (index is 6 or 7) Require(hint.Dy < 0, "Above the shelf, the route should steer down to the landing.");
            if (index == 9) Require(hint.Dx > 0, "After landing, the route should continue right.");
        }
    }

    internal static void TestRecordedEarlyTakeoffKeepsRouteThroughIntermediateLanding()
    {
        const double scale = 22.22426;
        static int Px(double value) => AtlasPathPoint.Round(value * scale);
        var middle = new AtlasPathPlatform(Px(158.903), Px(161.881), Px(49.125));
        var graph = new AtlasPlatformGraph(
            [new(Px(156.024), Px(158.903), Px(44.081)), middle,
             new(Px(152.098), Px(157.123), Px(54.143))],
            [new(Px(158.903), Px(44.081), Px(49.125)), new(Px(161.881), Px(49.125), Px(54.644)),
             new(Px(157.123), Px(51.933), Px(54.143))],
            height: 28, width: 11, pixelsPerWorldUnit: new(scale, scale));
        var destination = new AtlasPathPoint(Px(155), Px(54.143) + 28);
        var start = new AtlasPathPoint(Px(156.94), Px(44.081) + 28);
        var departure = graph.NearestStroke(start, 0, walkOnly: true)!.Value;
        var arrival = graph.NearestStroke(destination, 0, walkOnly: true)!.Value;
        var path = AtlasPathRouting.FindRoute(graph, departure, start, arrival, destination);
        Require(path.Any(point => point.Y == middle.Y + 28 && middle.Contains(point.X)),
            "The route must cross a measured landing between jumps.");
        var navigation = new AtlasPathNavigator();
        Require(Follow(156.94, 44.081, true).HasValue, "The route is missing at takeoff.");
        var walking = Follow(160.76, 49.125, true);
        Require(walking.HasValue && walking.Value.Next.X < walking.Value.Current.X,
            "The route must head left toward the next takeoff after landing.");
        Require(Follow(160.708, 51.762 - 1.28, false).HasValue
            && Follow(160.439, 52.270 - 1.28, false).HasValue,
            "Early takeoff lost the route to the upper ledge.");

        AtlasPathHint? Follow(double x, double y, bool grounded) =>
            navigation.Follow(graph, new(Px(x), Px(y)), grounded, measured: true,
                admitted: null, walked: [], fixedObjective: destination);
    }

    private static bool Reachable(AtlasPlatformGraph graph, AtlasPathPoint start, AtlasPathPoint goal,
        Func<AtlasPathPoint, bool>? admissible = null)
    {
        var seen = new HashSet<AtlasPathPoint> { start };
        var pending = new Stack<AtlasPathPoint>();
        pending.Push(start);
        while (pending.TryPop(out var point))
        {
            if (point == goal) return true;
            if (!graph.Nodes.TryGetValue(point, out var candidates)) continue;
            foreach (var next in candidates)
                if ((admissible is null || admissible(next)) && seen.Add(next))
                    pending.Push(next);
        }
        return false;
    }

    private static void Require(bool condition, string expectation)
    {
        if (!condition) throw new InvalidOperationException(expectation);
    }
}
