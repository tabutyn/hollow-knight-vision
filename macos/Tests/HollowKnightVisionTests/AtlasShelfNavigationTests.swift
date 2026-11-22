import XCTest
@testable import HollowKnightVision

final class AtlasShelfNavigationTests: XCTestCase {
    private let ground = PathPlatform(left: 0, right: 100, y: 0)
    private let ledge = PathPlatform(left: 112, right: 225, y: 25)

    func testAutomaticGuidanceCrossesShelfWhenTakeoffWasBetweenSamples() throws {
        let graph = AtlasPlatformGraph(floors: [ground, ledge], walls: [], height: 20, width: 10)
        var nav = AtlasPathNavigation()
        func hint(_ x: Int, _ y: Int, _ t: Double, grounded: Bool) throws -> BotRoadHint {
            try XCTUnwrap(nav.observe(graph, feet: .init(x, y), viewWidth: 200, timestamp: t,
                                      measured: true, grounded: grounded).0)
        }
        XCTAssertGreaterThan(try hint(15, 0, 1, grounded: true).dx, 0)
        XCTAssertEqual(nav.objective, PathPoint(225, 45))
        // The takeoff is x=95. The Knight can jump from x=98.
        let launch = try hint(98, 0, 1.1, grounded: true)
        XCTAssertEqual(launch.dy, 1)
        let flight = try hint(106, 58, 1.2, grounded: false)
        XCTAssertEqual(flight.dx, 1)
        XCTAssertEqual(nav.objective, PathPoint(225, 45))
        XCTAssertGreaterThan(try hint(124, 25, 1.4, grounded: true).dx, 0)
        _ = try hint(225, 25, 1.5, grounded: true)
        XCTAssertNotEqual(nav.objective, PathPoint(225, 45))
    }

    func testRegisteredLandingsAreConnectedAcrossDiagramHeadroom() {
        let graph = AtlasPlatformGraph(floors: [ground, ledge], walls: [], height: 20, width: 10)
        var nav = AtlasPathNavigation()
        _ = nav.observe(graph, feet: .init(15, 0), viewWidth: 200, timestamp: 1, measured: true,
                        registered: { $0.y <= 45 })
        XCTAssertEqual(nav.objective, PathPoint(225, 45))
        var blocked = AtlasPathNavigation()
        _ = blocked.observe(graph, feet: .init(15, 0), viewWidth: 200, timestamp: 1, measured: true,
                            registered: { $0.y <= 20 })
        XCTAssertEqual(blocked.objective, PathPoint(100, 20))
    }

    func testNewlyRegisteredShelfMovesAutomaticObjectiveBeyondOldLip() {
        var nav = AtlasPathNavigation()
        let first = AtlasPlatformGraph(floors: [ground], walls: [], height: 20, width: 10)
        _ = nav.observe(first, feet: .init(15, 0), viewWidth: 200, timestamp: 1, measured: true)
        XCTAssertEqual(nav.objective, PathPoint(100, 20))
        let extended = AtlasPlatformGraph(floors: [ground, ledge], walls: [], height: 20, width: 10, previous: first)
        _ = nav.observe(extended, feet: .init(84, 0), viewWidth: 200, timestamp: 1.2, measured: true)
        XCTAssertEqual(nav.objective, PathPoint(225, 45))
    }

    func testRecordedKingsPassJumpKeepsAdviceAboveAndPastTheShelf() throws {
        // Capture 2468a3a5, 32.45–33.35 s. Room-pixel feet and grounded flags
        // from the receiver, with just the departure and landing floors exposed.
        let graph = AtlasPlatformGraph(floors: [.init(left: 3245, right: 3291, y: 291),
                                                .init(left: 3324, right: 3384, y: 378)],
                                       walls: [], height: 28, width: 11, worldScale: .init(x: 22.224, y: 22.224))
        let frames: [(Int, Int, Bool)] = [(3267,291,true), (3267,291,true), (3267,312,false), (3282,347,false),
                                          (3301,379,false), (3319,401,false), (3338,413,false), (3349,412,false),
                                          (3349,400,false), (3349,380,true)]
        var nav = AtlasPathNavigation()
        for (i, frame) in frames.enumerated() {
            let hint = try XCTUnwrap(nav.observe(graph, feet: .init(frame.0, frame.1), viewWidth: 640,
                                                 timestamp: 32.45 + Double(i)/10, measured: true, grounded: frame.2).0,
                                     "Lost the route on recorded sample \(i)")
            XCTAssertEqual(nav.objective, PathPoint(3384, 406))
            if i == 6 || i == 7 { XCTAssertLessThan(hint.dy, 0) }
            if i == 9 { XCTAssertGreaterThan(hint.dx, 0) }
        }
    }

    func testAutomaticObjectiveDescendsAndMidairTargetsCannotReverseTheDrop() throws {
        let graph = AtlasPlatformGraph(floors: [ground, .init(left: 25, right: 75, y: 200)],
                                       walls: [], height: 20, width: 10)
        var nav = AtlasPathNavigation()
        let above = nav.observe(graph, feet: .init(50, 200), viewWidth: 200, timestamp: 1, measured: true, grounded: true)
        XCTAssertNotNil(above.0)
        XCTAssertEqual(nav.objective?.y, 20)

        // Both positions are inside the same long drop edge (x=80).
        nav.setObjective(.init(80, 160))
        XCTAssertNil(nav.observe(graph, feet: .init(80, 100), viewWidth: 200, timestamp: 2, measured: true, grounded: false).0)
        nav.setObjective(.init(80, 60))
        let descent = nav.observe(graph, feet: .init(80, 100), viewWidth: 200, timestamp: 3, measured: true, grounded: false)
        XCTAssertEqual(try XCTUnwrap(descent.0).dy, -1)
        nav.setObjective(.init(50, 220))
        XCTAssertNil(nav.observe(graph, feet: .init(80, 0), viewWidth: 200, timestamp: 4, measured: true, grounded: true).0)
    }

    func testAutomaticDropRequiresRegisteredSpaceAlongTheDescent() {
        let graph = AtlasPlatformGraph(floors: [ground, .init(left: 25, right: 75, y: 320)],
                                       walls: [], height: 20, width: 10)
        var nav = AtlasPathNavigation()
        _ = nav.observe(graph, feet: .init(50, 320), viewWidth: 200, timestamp: 1, measured: true,
                        grounded: true, registered: { !(64..<128).contains($0.y) })
        XCTAssertEqual(nav.objective?.y, 340)
    }

    func testDropHintSteersTowardOffsetLandingWhileStillInTheAir() throws {
        let g = AtlasPlatformGraph(floors: [.init(left: 0, right: 30, y: 200),
                                            .init(left: 65, right: 90, y: 0)],
                                   walls: [], height: 20, width: 10)
        var nav = AtlasPathNavigation()
        nav.setObjective(.init(80, 20))
        _ = nav.observe(g, feet: .init(30, 200), viewWidth: 200, timestamp: 1, measured: true, grounded: true)
        let falling = try XCTUnwrap(nav.observe(g, feet: .init(35, 175), viewWidth: 200,
                                                timestamp: 1.2, measured: true, grounded: false).0)
        XCTAssertGreaterThan(falling.dx, 0)
        XCTAssertLessThan(falling.dy, 0)
    }
    func testRecordedEarlyTakeoffKeepsRouteThroughIntermediateLanding() throws {
        let scale: CGFloat = 22.22426
        func px(_ n: CGFloat) -> Int { Int((n * scale).rounded()) }
        let middle = PathPlatform(left: px(158.903), right: px(161.881), y: px(49.125))
        let graph = AtlasPlatformGraph(floors: [
            .init(left: px(156.024), right: px(158.903), y: px(44.081)), middle,
            .init(left: px(152.098), right: px(157.123), y: px(54.143)),
        ], walls: [
            .init(x: px(158.903), low: px(44.081), high: px(49.125), solidSide: 0),
            .init(x: px(161.881), low: px(49.125), high: px(54.644), solidSide: 0),
            .init(x: px(157.123), low: px(51.933), high: px(54.143), solidSide: 0),
        ], height: 28, width: 11, worldScale: .init(x: scale, y: scale))
        var nav = AtlasPathNavigation()
        nav.setObjective(.init(px(155), px(54.143) + 28))
        _ = try XCTUnwrap(nav.observe(graph, feet: .init(px(156.94), px(44.081)), viewWidth: 640,
                                       timestamp: 1, measured: true, grounded: true).0)
        XCTAssertTrue(nav.selectedRoute.contains { $0.y == middle.y + 28 && middle.contains($0.x) })
        let walking = try XCTUnwrap(nav.observe(graph, feet: .init(px(160.76), px(49.125)), viewWidth: 640,
                                                timestamp: 2, measured: true, grounded: true).0)
        XCTAssertLessThan(walking.dx, 0)
        for (i, frame) in [(160.708, 51.762), (160.439, 52.270)].enumerated() {
            XCTAssertNotNil(nav.observe(graph, feet: .init(px(frame.0), px(frame.1 - 1.28)), viewWidth: 640,
                                        timestamp: 2.02 + Double(i) * 0.02, measured: true, grounded: false).0)
        }
    }

}
