import XCTest

@testable import HollowKnightVision

final class AtlasDropGraphTests: XCTestCase {
    private func reachable(_ graph: AtlasPlatformGraph, _ start: PathPoint, _ goal: PathPoint) -> Bool {
        var visited: Set<PathPoint> = [start]
        var pending = [start]
        while let point = pending.popLast() {
            if point == goal { return true }
            for next in graph.nodes[point] ?? [] where visited.insert(next).inserted { pending.append(next) }
        }
        return false
    }

    func testBothLipsDescendButCannotClimb() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 100, y: 0), .init(left: 25, right: 75, y: 200),
        ], walls: [], height: 20, width: 10)
        let drops = graph.strokes.filter { $0.kind == .drop }
        XCTAssertEqual(Set(drops.filter { !$0.horizontal }.map(\.a.x)), [20, 80])
        for (lip, x) in [(25, 20), (75, 80)] {
            XCTAssertTrue(reachable(graph, .init(lip, 220), .init(x, 20)))
            XCTAssertFalse(reachable(graph, .init(x, 20), .init(lip, 220)))
            let drop = drops.first { !$0.horizontal && $0.a.x == x }!
            XCTAssertTrue(drop.allowsTraversal(from: .init(x, 150), to: .init(x, 30)))
            XCTAssertFalse(drop.allowsTraversal(from: .init(x, 30), to: .init(x, 150)))
        }
    }

    func testFirstFloorInterceptsFallAndWallBlocksLip() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 100, y: 0), .init(left: 10, right: 80, y: 100),
            .init(left: 25, right: 75, y: 200),
        ], walls: [.init(x: 21, low: 110, high: 200, solidSide: 1)], height: 20, width: 10)
        XCTAssertTrue(reachable(graph, .init(75, 220), .init(80, 120)))
        XCTAssertFalse(graph.strokes.contains { $0.kind == .drop && $0.contains(.init(25, 220)) })
    }

    func testJumpShelfCrossingDropDoesNotCreateClimb() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 100, y: 0), .init(left: 25, right: 75, y: 200),
            .init(left: 120, right: 180, y: 80),
        ], walls: [], height: 20, width: 10, connections: [
            .init(first: .init(10, 0), second: .init(150, 80), doubleJump: true),
        ])
        let crossing = PathPoint(20, 105)
        XCTAssertTrue(graph.strokes.contains { $0.kind == .drop && $0.contains(crossing) })
        XCTAssertTrue(graph.strokes.contains { $0.kind == .doubleJump && $0.contains(crossing) })
        XCTAssertFalse(reachable(graph, .init(20, 20), .init(25, 220)))
        XCTAssertFalse(graph.nodes[crossing]?.contains(.init(20, 220)) ?? false)
    }
    func testLedgeFaceAllowsTangentFallButWallsInExitBlockIt() {
        let floors = [PathPlatform(left: 0, right: 100, y: 0),
                      PathPlatform(left: 25, right: 75, y: 200)]
        func leftFall(_ wall: PathWall) -> Bool {
            let graph = AtlasPlatformGraph(floors: floors, walls: [wall], height: 20, width: 10)
            return graph.strokes.contains { $0.kind == .drop && $0.contains(.init(25, 220)) }
        }
        XCTAssertTrue(leftFall(.init(x: 25, low: 30, high: 200, solidSide: 1)))
        // A lower wall can be avoided by steering before reaching its top.
        XCTAssertTrue(leftFall(.init(x: 21, low: 30, high: 190, solidSide: 1)))
        XCTAssertFalse(leftFall(.init(x: 21, low: 30, high: 200, solidSide: 1)))
        XCTAssertFalse(leftFall(.init(x: 22, low: 200, high: 220, solidSide: 1)))
    }

    func testJumpConnectionReplacesRedundantDrop() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 100, y: 0), .init(left: 25, right: 75, y: 25),
        ], walls: [], height: 20, width: 10)
        XCTAssertFalse(graph.strokes.contains { $0.kind == .drop })
        XCTAssertTrue(reachable(graph, .init(20, 20), .init(25, 45)))
    }

    func testFallCanSteerToSeveralLowerPlatformsAndNeverClimbs() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 30, y: 200),
            .init(left: 40, right: 45, y: 100),
            .init(left: 65, right: 90, y: 0),
        ], walls: [], height: 20, width: 10)
        XCTAssertTrue(reachable(graph, .init(30, 220), .init(40, 120)))
        XCTAssertTrue(reachable(graph, .init(30, 220), .init(65, 20)))
        XCTAssertFalse(reachable(graph, .init(65, 20), .init(30, 220)))
    }

    func testIntermediateFloorInterceptsSteeredFall() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 30, y: 200),
            .init(left: 35, right: 100, y: 100),
            .init(left: 65, right: 90, y: 0),
        ], walls: [], height: 20, width: 10)
        // Descending through the middle floor cannot connect directly to the
        // lower floor; its own exposed lip may still offer a separate route.
        var seen: Set<PathPoint> = [.init(30, 220)]
        var pending = [PathPoint(30, 220)]
        while let point = pending.popLast() {
            for next in graph.nodes[point] ?? [] where !(next.y == 120 && (35...100).contains(next.x)) {
                if seen.insert(next).inserted { pending.append(next) }
            }
        }
        XCTAssertFalse(seen.contains(.init(65, 20)))
    }

    func testManualDoubleJumpSuppressesDropForItsPair() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 100, y: 0), .init(left: 25, right: 75, y: 200),
        ], walls: [], height: 20, width: 10, connections: [
            .init(first: .init(20, 0), second: .init(30, 200), doubleJump: true),
        ])
        XCTAssertFalse(graph.strokes.contains { $0.kind == .drop })
    }

    func testFallCanSkirtAnIntermediateLedgeAndSteerBack() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 30, y: 200),
            .init(left: 35, right: 50, y: 100),
            .init(left: 0, right: 30, y: 0),
        ], walls: [], height: 20, width: 10, worldScale: CGPoint(x: 22, y: 22))
        XCTAssertTrue(reachable(graph, .init(30, 220), .init(30, 20)))
    }

    func testSavedKingsPassFallReturnsAcrossWallCap() {
        let scale: CGFloat = 22.22426
        func px(_ value: CGFloat) -> Int { Int((value * scale).rounded()) }
        let graph = AtlasPlatformGraph(floors: [
            .init(left: px(93.7845), right: px(117.1841), y: px(62.0648)),
            .init(left: px(105.0718), right: px(117.0244), y: px(54.0298)),
        ], walls: [
            .init(x: px(117.1841), low: px(56.9301), high: px(62.0648), solidSide: 0),
            .init(x: px(117.0244), low: px(46.1744), high: px(54.0298), solidSide: 0),
        ], height: 28, width: 11, worldScale: CGPoint(x: scale, y: scale))
        XCTAssertTrue(reachable(graph, .init(px(117.1841), px(62.0648) + 28),
                                .init(px(117.0244), px(54.0298) + 28)))
    }

    func testBypassDiagramTurnsVisiblyAboveInterveningFloor() {
        let graph = AtlasPlatformGraph(floors: [
            .init(left: 0, right: 30, y: 200),
            .init(left: 35, right: 50, y: 100),
            .init(left: 60, right: 90, y: 0),
        ], walls: [], height: 20, width: 10, worldScale: CGPoint(x: 22, y: 22))
        XCTAssertTrue(reachable(graph, .init(30, 220), .init(60, 20)))
        XCTAssertTrue(graph.strokes.contains {
            $0.kind == .drop && $0.horizontal && $0.a.x <= 35 && $0.b.x >= 50
                && $0.a.y >= 100 + 20 + 20 / 4
        })
    }

}
