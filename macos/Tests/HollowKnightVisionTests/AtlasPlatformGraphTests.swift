import XCTest

@testable import HollowKnightVision

final class AtlasPlatformGraphTests: XCTestCase {
    private let lower = PathPlatform(left: 0, right: 100, y: 0)
    private let upper = PathPlatform(left: 112, right: 175, y: 25)

    func testDetectedExtensionKeepsTheWalkedRowInEitherDirection() {
        let walked = PathPlatform(left: 275, right: 485, y: 93)
        let graph = AtlasPlatformGraph(floors: [.init(left: 0, right: 275, y: 95),
                                              .init(left: 485, right: 800, y: 94)],
                                      walls: [], height: 35, width: 17, detectedGround: true, walked: [walked])
        XCTAssertEqual(graph.floors, [.init(left: 0, right: 800, y: 93)])
        XCTAssertEqual(graph.strokes.filter { $0.kind == .walk }.count, 1)
        XCTAssertFalse(graph.strokes.contains { $0.kind == .jump })
    }

    func testVisualWallAtEndOfWalkCannotCutBackIntoItsFootprint() {
        let walked = PathPlatform(left: 276, right: 613, y: 92)
        let wall = PathWall(x: 609, low: 70, high: 180, solidSide: 1)
        let graph = AtlasPlatformGraph(floors: [.init(left: 200, right: 685, y: 93)],
                                      walls: [wall], height: 35, width: 16, detectedGround: true, walked: [walked])
        XCTAssertEqual(graph.floors, [.init(left: 200, right: 613, y: 92), .init(left: 617, right: 685, y: 92)])
        XCTAssertFalse(graph.strokes.contains { $0.kind == .jump && $0.horizontal && $0.b.x <= 613 })
    }

    func testGroundClustersAreLocalAndPitRemainsOpen() {
        let parts = [
            PathPlatform(left: -200, right: -110, y: -3),
            PathPlatform(left: 0, right: 40, y: 0),
            PathPlatform(left: 2, right: 55, y: 1),
            PathPlatform(left: 53, right: 70, y: 2),
            PathPlatform(left: 86, right: 110, y: 1),
        ]
        let ground = AtlasPlatformGraph.merge(parts, height: 20)
        XCTAssertEqual(ground.count, 3)
        XCTAssertTrue(ground.contains(.init(left: 0, right: 70, y: 2)))
    }

    func testPitShelfMeetsBothPlatformsFromAboveAndCannotCrossHighWall() {
        let g = AtlasPlatformGraph(floors: [lower, upper], walls: [], height: 20, width: 10)
        let jumps = g.strokes.filter { $0.kind == .jump }
        XCTAssertEqual(jumps.count, 3)
        XCTAssertEqual(jumps.filter(\.horizontal).map(\.a.y), [50])
        XCTAssertEqual(Set(jumps.filter { !$0.horizontal }.map(\.a.y)), [20, 45])
        var navigation = AtlasPathNavigation()
        navigation.setObjective(.init(160, 45))
        XCTAssertEqual(
            navigation.observe(g, feet: .init(10, 0), viewWidth: 200, timestamp: 0, measured: true).0?.dx, 1)
        let slow = AtlasPlatformGraph(
            floors: [lower, upper], walls: [.init(x: 105, low: 0, high: 160, solidSide: 1)], height: 20, width: 10)
        XCTAssertFalse(slow.strokes.contains { $0.kind == .jump })
        XCTAssertEqual(slow.strokes.first { $0.kind == .wall }?.a.x, 95)
        XCTAssertNil(navigation.observe(slow, feet: .init(10, 0), viewWidth: 200, timestamp: 0.1, measured: true).0)
        let sealed = AtlasPlatformGraph(
            floors: [lower], walls: [.init(x: 60, low: 0, high: 180, solidSide: 1)], height: 20, width: 10)
        XCTAssertEqual(sealed.strokes.filter { $0.kind == .walk }.count, 2)
        XCTAssertFalse(sealed.strokes.contains { $0.kind == .jump })
    }

    func testRouteHeadsTowardTheJumpEvenWhenTheDestinationIsBehindTheKnight() throws {
        let high = PathPlatform(left: 0, right: 120, y: 200)
        let connection = PathConnection(first: .init(90, 0), second: .init(100, 200), doubleJump: true)
        let graph = AtlasPlatformGraph(
            floors: [lower, high], walls: [], height: 20, width: 10, connections: [connection])
        var nav = AtlasPathNavigation()
        nav.setObjective(.init(5, 220))
        let approach = try XCTUnwrap(
            nav.observe(graph, feet: .init(20, 0), viewWidth: 200, timestamp: 0, measured: true).0)
        XCTAssertEqual(approach.dx, 1)
        let takeoff = try XCTUnwrap(
            nav.observe(graph, feet: .init(90, 0), viewWidth: 200, timestamp: 1, measured: true).0)
        XCTAssertEqual(takeoff.dy, 1)
        XCTAssertEqual(graph.strokes.filter { $0.kind == .doubleJump }.count, 3)
    }

    func testOverhangingPlatformCanBeApproachedFromEitherExposedSide() {
        let graph = AtlasPlatformGraph(
            floors: [lower, .init(left: 25, right: 75, y: 25)], walls: [], height: 20, width: 10)
        let starts = graph.strokes.filter { $0.kind == .jump && !$0.horizontal && $0.a.y == 20 }
        XCTAssertEqual(Set(starts.map(\.a.x)), [15, 85])
    }

    func testWallOffsetsFollowInteriorAboveAndBelowTheLedge() {
        let walls = [
            PathWall(x: 0, low: 0, high: 35, solidSide: 0), PathWall(x: 100, low: -35, high: 0, solidSide: 0),
        ]
        let graph = AtlasPlatformGraph(floors: [lower], walls: walls, height: 20, width: 10)
        XCTAssertEqual(Set(graph.strokes.filter { $0.kind == .wall }.map(\.a.x)), [10, 110])
        XCTAssertNil(graph.nodes[PathPoint(110, -35)])
    }

    func testIncrementalGraphMatchesColdConstructionAfterExtendingAndRemovingEdges() {
        let before = AtlasPlatformGraph(floors: [lower, upper], walls: [], height: 20, width: 10)
        let inputs = [lower, upper, PathPlatform(left: 186, right: 240, y: 32)]
        let next = AtlasPlatformGraph(floors: inputs, walls: [], height: 20, width: 10, previous: before)
        let cold = AtlasPlatformGraph(floors: inputs, walls: [], height: 20, width: 10)
        XCTAssertEqual(next.nodes, cold.nodes)
        XCTAssertEqual(next.walkNodes, cold.walkNodes)
        let removed = AtlasPlatformGraph(floors: [lower, upper], walls: [], height: 20, width: 10, previous: next)
        XCTAssertEqual(removed.nodes, before.nodes)
    }

    func testSignedMotionUsesLastVectorAndClearsAcrossObjectiveOrTrackingChanges() throws {
        var nav = AtlasPathNavigation()
        let graph = AtlasPlatformGraph(floors: [.init(left: 0, right: 100, y: 0)], walls: [], height: 10, width: 5)
        nav.setObjective(.init(95, 10))
        func sample(_ x: Int, _ time: Double, measured: Bool = true) -> Double? {
            _ = nav.observe(graph, feet: .init(x, 0), viewWidth: 100, timestamp: time, measured: measured)
            return nav.alignment
        }
        XCTAssertNil(sample(30, 1))
        XCTAssertEqual(sample(36, 1.1), 0.6)
        XCTAssertEqual(sample(36, 1.2), 0)
        XCTAssertEqual(sample(32, 1.3), -0.4)
        nav.setObjective(.init(0, 10))
        XCTAssertNil(sample(30, 1.4))
        XCTAssertEqual(sample(25, 1.5), 0.5)
        XCTAssertNil(sample(23, 1.6, measured: false))
        XCTAssertNil(sample(20, 1.7))
        XCTAssertNil(sample(10, 3))
    }

    func testMapUpdatesKeepTheChosenFrontierAndRepeatedTravelHasNoExtraReward() throws {
        var nav = AtlasPathNavigation()
        let g = AtlasPlatformGraph(floors:[.init(left:0,right:100,y:0)],walls:[],height:10,width:5)
        _ = nav.observe(g,feet:.init(48,0),viewWidth:100,timestamp:1,measured:true)
        XCTAssertEqual(nav.objective,PathPoint(100,10))
        let rescanned = AtlasPlatformGraph(floors:[.init(left:-30,right:102,y:1)],walls:[],height:10,width:5)
        let forward = nav.observe(rescanned,feet:.init(54,1),viewWidth:100,timestamp:1.1,measured:true,revision:1)
        XCTAssertEqual(nav.objective,PathPoint(100,11))
        XCTAssertEqual(forward.0?.dx,1)
        XCTAssertGreaterThan(try XCTUnwrap(forward.1),0)
        // Force a new exploration choice over already rewarded rightward travel.
        nav.setObjective(nil)
        _ = nav.observe(g,feet:.init(48,0),viewWidth:100,timestamp:1.2,measured:true)
        let repeatStep = nav.observe(g,feet:.init(54,0),viewWidth:100,timestamp:1.3,measured:true)
        XCTAssertEqual(repeatStep.1,0)
    }

    func testExplorationUsesReachableRegisteredUnwalkedGroundAndCreditsPreviousGoal() throws {
        let g = AtlasPlatformGraph(
            floors: [lower, .init(left: 450, right: 900, y: 0)], walls: [], height: 10, width: 5)
        var nav = AtlasPathNavigation()
        let start = nav.observe(
            g, feet: .init(25, 0), viewWidth: 100, timestamp: 1, measured: true, registered: { $0.x <= 80 })
        XCTAssertEqual(nav.objective, PathPoint(80, 10))
        XCTAssertEqual(start.0?.dx, 1)
        let movement = nav.observe(
            g, feet: .init(32, 0), viewWidth: 100, timestamp: 1.1, measured: true, revision: 1)
        XCTAssertEqual(nav.objective, PathPoint(80, 10))
        XCTAssertEqual(try XCTUnwrap(movement.1), 0.7, accuracy: 0.001)
        nav.setObjective(.init(0, 10))
        XCTAssertEqual(nav.observe(g, feet: .init(32, 0), viewWidth: 100, timestamp: 1.2, measured: true).0?.dx, -1)
        let forward = nav.observe(g, feet: .init(22, 0), viewWidth: 100, timestamp: 1.3, measured: true)
        XCTAssertEqual(forward.1, 1)
        XCTAssertNil(nav.observe(g, feet: .init(30, 0), viewWidth: 100, timestamp: 1.4, measured: false).1)
        XCTAssertEqual(nav.observe(g, feet: .init(32, 0), viewWidth: 100, timestamp: 1.5, measured: true).1, 0)
        XCTAssertEqual(nav.observe(g, feet: .init(22, 0), viewWidth: 100, timestamp: 1.6, measured: true).1, 0)
        XCTAssertEqual(
            nav.observe(g, feet: .init(22, 0), viewWidth: 100, timestamp: 1.7, measured: true, revision: 2).1, 0)
        nav.setObjective(nil)
        XCTAssertTrue(nav.automatic)
    }
    func testRecordedHighLandingRemainsWithinMeasuredApex() {
        let scale: CGFloat = 22.22426
        func px(_ value: CGFloat) -> Int { Int((value * scale).rounded()) }
        // A recorded single jump rose 5.497 world units with 2.302 horizontal travel.
        let graph = AtlasPlatformGraph(floors: [
            .init(left: px(88), right: px(91), y: px(36.96)),
            .init(left: px(85), right: px(88.5), y: px(42.457)),
        ], walls: [], height: 28, width: 11, worldScale: CGPoint(x: scale, y: scale))
        XCTAssertTrue(graph.strokes.contains { $0.kind == .jump })
    }

    func testRecordedKingsPassZigzagJumpsClearAuthoredWalls() {
        let scale: CGFloat = 22.22426
        func px(_ value: CGFloat) -> Int { Int((value * scale).rounded()) }
        let floors: [PathPlatform] = [
            .init(left: px(156.024), right: px(158.903), y: px(44.081)),
            .init(left: px(158.903), right: px(161.881), y: px(49.125)),
            .init(left: px(152.098), right: px(157.123), y: px(54.143)),
        ]
        let walls: [PathWall] = [
            .init(x: px(158.903), low: px(44.081), high: px(49.125), solidSide: 0),
            .init(x: px(161.881), low: px(49.125), high: px(54.644), solidSide: 0),
            .init(x: px(157.123), low: px(51.933), high: px(54.143), solidSide: 0),
        ]
        let graph = AtlasPlatformGraph(floors: floors, walls: walls, height: 28, width: 11,
                                       worldScale: CGPoint(x: scale, y: scale))
        func hasJump(_ a: PathPlatform, _ b: PathPlatform) -> Bool {
            var edges = [PathPoint: Set<PathPoint>]()
            for stroke in graph.strokes where stroke.kind == .jump {
                let points = graph.vertices(on: stroke).sorted()
                for (first, second) in zip(points, points.dropFirst()) {
                    edges[first, default: []].insert(second)
                    edges[second, default: []].insert(first)
                }
            }
            var seen = Set(edges.keys.filter { $0.y == a.y + 28 && a.contains($0.x) })
            var pending = Array(seen)
            while let point = pending.popLast() {
                for next in edges[point] ?? [] where seen.insert(next).inserted { pending.append(next) }
            }
            return seen.contains { $0.y == b.y + 28 && b.contains($0.x) }
        }
        XCTAssertTrue(hasJump(floors[0], floors[1]))
        XCTAssertTrue(hasJump(floors[1], floors[2]))
    }

}
