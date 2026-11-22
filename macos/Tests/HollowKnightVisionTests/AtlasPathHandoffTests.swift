import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class AtlasPathHandoffTests: XCTestCase {
    private func observation(at time: Double) -> BotObservation {
        .init(type: "observation", captureTimestamp: time, capturedAt: time, availableAt: time,
              generation: 1, roomID: 0, roomRevision: 1, detectionTimestamp: nil, detectionFrameID: nil,
              modelVersion: nil, bestKnightConfidence: nil,
              features: Array(repeating: 0, count: BotObservationContract.featureCount), issues: [])
    }

    func testCombatEdgesCannotSplitPreviouslySeenPlatform() throws {
        let map = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        let road = PathPlatform(left: 285, right: 530, y: 341)
        let other = PathPlatform(left: 555, right: 635, y: 341)
        map.setBase(room: "room", floors: [road, other], fallbackHeight: 35)
        let wall = BotWallSegment(x: 426, bottomY: 340, topY: 372, solidSide: -1)
        let flash = BotTerrainSegment(minimumX: 284, maximumX: 421, y: 333, confidence: 1)
        let clear = BotTerrainSegment(minimumX: 285, maximumX: 530, y: 341, confidence: 1)
        func see(_ tick: Int, walls: [BotWallSegment], floors: [BotTerrainSegment], edges: [BotTerrainSegment] = []) {
            var row = observation(at: Double(tick) / 10)
            map.observe(room: "room", camera: .zero, width: 640, row: &row,
                        geometry: .init(width: 640, height: 360, floors: floors, walls: walls), observedFloorEdges: edges)
        }
        for tick in 0...5 {
            let attack = (2...3).contains(tick)
            see(tick, walls: attack ? [wall] : [], floors: attack ? [flash] : [clear])
            let graph = try XCTUnwrap(map.graph(room: "room"))
            XCTAssertEqual(graph.floors, [road, other])
            XCTAssertEqual(graph.strokes.filter { $0.kind == .jump && $0.horizontal }.count, 1)
        }
        for tick in 6...9 { see(tick, walls: [wall], floors: []) }
        XCTAssertEqual(map.graph(room: "room")?.floors.count, 3)
        for tick in 10...15 { see(tick, walls: [], floors: [clear]) } // Stitched or occluded floor, no raw edge.
        XCTAssertEqual(map.graph(room: "room")?.floors.count, 3)
        for tick in 16...19 { see(tick, walls: [], floors: [clear], edges: [clear]) }
        XCTAssertEqual(map.graph(room: "room")?.floors, [road, other])
    }

    func testWalkAcrossDetectedWallBecomesOneRoadAndKeepsTheNextGap() throws {
        let map = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        let room = "room/0"
        let floor = PathPlatform(left: 275, right: 485, y: 93)
        let next = PathPlatform(left: 540, right: 600, y: 93)
        map.setBase(room: room, floors: [floor, next], fallbackHeight: 35)
        let detection = BotScreenTerrain(width: 640, height: 360,
            floors: [.init(minimumX: 330, maximumX: 445, y: 106, confidence: 1)],
            walls: [.init(x: 460, bottomY: 83, topY: 125, solidSide: 1)])
        for tick in 1...4 {
            var row = observation(at: Double(tick) / 10)
            map.observe(room: room, camera: .zero, width: 640, row: &row, geometry: detection, space: "registered")
        }
        var row = observation(at: 1)
        XCTAssertTrue(try XCTUnwrap(map.graph(room: room)).strokes.contains {
            $0.kind == .jump && $0.horizontal && $0.a.x < 460 && $0.b.x > 460
        })

        // Check both a reopened Atlas and the live room after the camera moves.
        map.setBase(room: room, floors: [next], walked: [floor], fallbackHeight: 35)
        func checkRoad() throws {
            let graph = try XCTUnwrap(map.graph(room: room))
            XCTAssertEqual(graph.floors, [floor, next])
            XCTAssertEqual(graph.strokes.filter { $0.kind == .walk }.count, 2)
            let jumps = graph.strokes.filter { $0.kind == .jump && $0.horizontal }
            XCTAssertEqual(jumps.count, 1)
            XCTAssertTrue(jumps.contains { $0.a.x < 485 && $0.b.x > 540 })
        }
        try checkRoad()
        map.setBase(room: room, floors: [next], fallbackHeight: 35)
        map.observe(room: room, camera: CGPoint(x: 490, y: 0), width: 640, row: &row,
                    geometry: .init(width: 640, height: 360), walked: [floor], space: "registered")
        try checkRoad()

        // A corrected placement no longer crosses this wall. It must block again.
        let shortWalk = PathPlatform(left: 275, right: 400, y: 93)
        map.setBase(room: room, floors: [floor, next], fallbackHeight: 35)
        map.observe(room: room, camera: .zero, width: 640, row: &row, geometry: detection,
                    walked: [shortWalk], space: "registered")
        XCTAssertFalse(try XCTUnwrap(map.graph(room: room)).floors.contains { $0.y == 93 && $0.left < 460 && $0.right > 460 })
    }

    func testCombatEdgeAtWalkedEndpointDoesNotBecomeAnotherLanding() throws {
        let map = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        let floor = PathPlatform(left: 534, right: 779, y: 176)
        let competing = PathPlatform(left: 531, right: 592, y: 166)
        map.setBase(room: "fight", floors: [floor, competing], walked: [.init(left: 580, right: 690, y: 175)], fallbackHeight: 35)
        let graph = try XCTUnwrap(map.graph(room: "fight"))
        XCTAssertEqual(graph.floors, [.init(left: 534, right: 779, y: 175)])
        XCTAssertFalse(graph.strokes.contains { $0.kind == .jump })
    }

    func testRepeatedForegroundOverAnExposedFloorNeedsACornerOrContact() throws {
        let map = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        let floor = BotTerrainSegment(minimumX: 100, maximumX: 350, y: 92, confidence: 1)
        let wall = BotWallSegment(x: 200, bottomY: 92, topY: 138, solidSide: 1)
        let step = BotWallSegment(x: 320, bottomY: 92, topY: 138, solidSide: 1)
        let upper = BotTerrainSegment(minimumX: 320, maximumX: 420, y: 138, confidence: 1)
        func frame(_ tick: Int, walls: [BotWallSegment], contact: BotWallSegment? = nil) {
            var row = observation(at: Double(tick) / 10)
            map.observe(room: "visual", camera: .zero, width: 640, row: &row,
                        geometry: .init(width: 640, height: 360, floors: [floor, upper], walls: walls),
                        observedFloorEdges: walls.isEmpty ? [
                            .init(minimumX: 100, maximumX: 179, y: 91, confidence: 1),
                            .init(minimumX: 205, maximumX: 350, y: 92, confidence: 1)
                        ] : [], contactWall: contact)
        }
        frame(0, walls: [])
        frame(1, walls: [])
        for t in 2...12 { frame(t, walls: [wall, step]) }
        let ground = try XCTUnwrap(map.graph(room: "visual")).floors.filter { $0.y == 92 }
        XCTAssertTrue(ground.contains { $0.left < 200 && $0.right > 200 })
        XCTAssertFalse(ground.contains { $0.left < 320 && $0.right > 320 })
        for t in 13...16 { frame(t, walls: [wall, step], contact: wall) }
        XCTAssertFalse(try XCTUnwrap(map.graph(room: "visual")).floors.contains { $0.y == 92 && $0.left < 200 && $0.right > 200 })
    }

    func testDiscardingFrameChartDropsScreenGoalAndKeepsRegisteredMap() {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let map = AtlasPathMap(store: AtlasPathConnectionStore(file: file))
        let floor = PathPlatform(left: 0, right: 100, y: 20)
        map.setBase(room: "room/0/frame", floors: [floor], fallbackHeight: 20)
        map.setBase(room: "room/0", floors: [floor], fallbackHeight: 20)
        XCTAssertTrue(map.selectObjective(room: "room/0/frame", point: CGPoint(x: 20, y: 40), tolerance: 2))
        map.discardFrameChart(room: "room/0/frame")
        XCTAssertNil(map.graph(room: "room/0/frame"))
        XCTAssertNotNil(map.graph(room: "room/0"))
        map.setBase(room: "room/0/frame", floors: [floor], fallbackHeight: 20)
        XCTAssertTrue(map.lines(room: "room/0/frame", visible: true).allSatisfy { $0.kind != .atlasPathObjective })
        map.discardFrameChart(room: "room/0")
        XCTAssertNotNil(map.graph(room: "room/0"))
    }

    func testReacquiredCameraCanVerifyBothSidesOfAMaskedFloor() throws {
        let map = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        func island(_ id: Int, origin: Int) -> LiveIslandCapture {
            .init(id: id, roomID: 0, startedAt: Double(id), worldFromLocal: .init(x: origin, y: 0),
                  localPose: .zero, placement: .estimated, uncertainty: .unknown, breakReason: "visual-reacquisition", measured: true)
        }
        let old = island(1, origin: 0), new = island(2, origin: 450)
        let floor = BotTerrainSegment(minimumX: 622, maximumX: 1098, y: 92, confidence: 1)
        var row = observation(at: 1)
        _ = map.handoffSeams(room: "old", island: old, registered: false, projection: nil, scale: 1, generation: 1, time: 1)
        map.observe(room: "old", camera: .zero, width: 640, row: &row, geometry: .init(width: 640, height: 360, floors: [floor]))
        let edges = [PathPlatform(left: 172, right: 550, y: 93), .init(left: 614, right: 630, y: 93)]
        let hints = map.handoffSeams(room: "new", island: new, registered: false, projection: nil, scale: 1, generation: 1, time: 1.1)
        XCTAssertEqual(hints, [.init(left: 172, right: 648, y: 92)])
        func graph(_ floors: [PathPlatform], walls: [PathWall] = []) -> [PathPlatform] {
            AtlasPlatformGraph(floors: floors, walls: walls, height: 26, width: 17, detectedGround: true, handoffSeams: hints).floors
        }
        XCTAssertEqual(graph(edges), [.init(left: 172, right: 630, y: 93)])
        XCTAssertEqual(graph([edges[0]]), [edges[0]])
        XCTAssertEqual(graph([.init(left: 172, right: 500, y: 93), edges[1]]).count, 2)
        XCTAssertEqual(graph(edges + [.init(left: 569, right: 593, y: 28)]).count, 3)
        XCTAssertEqual(graph(edges, walls: [.init(x: 573, low: 92, high: 170, solidSide: 1)]).count, 2)
        XCTAssertTrue(map.handoffSeams(room: "new", island: new, registered: false, projection: nil, scale: 1,
                                      generation: 1, time: 2).isEmpty)
    }

    func testUnplacedSuccessorKeepsOrientationWithoutImportingCoordinates() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let map = AtlasPathMap(store: AtlasPathConnectionStore(file: file))
        func row(x: Double, time: Double) -> BotObservation {
            var f = [Double](repeating: 0, count: BotObservationContract.featureCount)
            f[0] = x/400; f[1] = 25/400; f[2] = 8/400; f[3] = 10/400; f[4] = 1
            return BotObservation(type: "observation", captureTimestamp: time, capturedAt: time,
                availableAt: time, generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: time,
                detectionFrameID: 1, modelVersion: nil, bestKnightConfidence: 1, features: f, issues: [])
        }
        map.setBase(room: "first", floors: [.init(left: 0, right: 200, y: 20)], fallbackHeight: 10)
        var first = row(x: 50, time: 1)
        map.observe(room: "first", camera: .zero, width: 400, row: &first)
        XCTAssertGreaterThan(try XCTUnwrap(first.roadHint).dx, 0)
        map.setBase(room: "successor", floors: [.init(left: 0, right: 300, y: 20)], fallbackHeight: 10)
        map.inheritAutomaticDirection(from: "first", into: "successor")
        var next = row(x: 180, time: 1.1)
        map.observe(room: "successor", camera: .zero, width: 400, row: &next)
        XCTAssertGreaterThan(try XCTUnwrap(next.roadHint).dx, 0)
        XCTAssertEqual(next.pathProgress ?? 0, 0)
        XCTAssertEqual(map.graph(room: "successor")?.floors, [.init(left: 0, right: 300, y: 20)])
    }

    func testUnplacedHandoffPreservesExplicitDestination() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let map = AtlasPathMap(store: AtlasPathConnectionStore(file: file))
        map.setBase(room: "source", floors: [.init(left: 0, right: 100, y: 20)], fallbackHeight: 20)
        map.setBase(room: "destination", floors: [.init(left: 0, right: 100, y: 20)], fallbackHeight: 20)
        XCTAssertTrue(map.selectObjective(room: "destination", point: CGPoint(x: 20, y: 40), tolerance: 2))
        map.inheritAutomaticDirection(from: "source", into: "destination")
        let lines = map.lines(room: "destination", visible: true).filter { $0.kind == .atlasPathObjective }
        XCTAssertEqual((lines[0].start.x + lines[0].end.x) / 2, 20)
    }

    func testPromotionCarriesMeasuredFloorAndSurvivesNextRegisteredFrame() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let map = AtlasPathMap(store: AtlasPathConnectionStore(file: file))
        var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
        features[0] = 0.3; features[1] = 0.3; features[2] = 0.05; features[3] = 0.2; features[4] = 1
        var observation = BotObservation(type: "observation", captureTimestamp: 1, capturedAt: 1,
            availableAt: 1, generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: 1,
            detectionFrameID: 1, modelVersion: nil, bestKnightConfidence: 1, features: features, issues: [])
        let geometry = BotScreenTerrain(width: 100, height: 100,
            floors: [.init(minimumX: 5, maximumX: 90, y: 20, confidence: 1)])
        map.observe(room: "provisional", camera: .zero, width: 100, row: &observation,
            geometry: geometry, space: "island")
        map.setBase(room: "registered", floors: [], fallbackHeight: 20)
        map.promote(from: "provisional", into: "registered", translation: PathPoint(100, 50))
        XCTAssertEqual(map.graph(room: "registered")?.floors, [.init(left: 105, right: 190, y: 70)])
        map.observe(room: "registered", camera: CGPoint(x: 100, y: 50), width: 100, row: &observation,
            geometry: BotScreenTerrain(width: 100, height: 100), space: "registered")
        XCTAssertEqual(map.graph(room: "registered")?.floors, [.init(left: 105, right: 190, y: 70)])
    }

    func testPromotionProjectsExplicitGoalOntoRegisteredGround() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let map = AtlasPathMap(store: AtlasPathConnectionStore(file: file))
        map.setBase(room: "provisional", floors: [.init(left: 0, right: 100, y: 20)], fallbackHeight: 20)
        map.setBase(room: "registered", floors: [.init(left: 100, right: 200, y: 70)], fallbackHeight: 30)
        XCTAssertTrue(map.selectObjective(room: "provisional", point: CGPoint(x: 70, y: 40), tolerance: 2))
        map.promote(from: "provisional", into: "registered", translation: PathPoint(100, 50))
        let lines = map.lines(room: "registered", visible: true).filter { $0.kind == .atlasPathObjective }
        XCTAssertEqual(lines.count, 2)
        XCTAssertEqual((lines[0].start.x + lines[0].end.x) / 2, 170)
        XCTAssertEqual((lines[1].start.y + lines[1].end.y) / 2, 100)
    }

    func testPromotionKeepsAnExistingManualGoalAndDoesNotRepeat() throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let map = AtlasPathMap(store: AtlasPathConnectionStore(file: file))
        map.setBase(room: "provisional", floors: [.init(left: 0, right: 100, y: 20)], fallbackHeight: 20)
        map.setBase(room: "registered", floors: [.init(left: 100, right: 200, y: 70)], fallbackHeight: 20)
        XCTAssertTrue(map.selectObjective(room: "provisional", point: CGPoint(x: 70, y: 40), tolerance: 2))
        XCTAssertTrue(map.selectObjective(room: "registered", point: CGPoint(x: 120, y: 90), tolerance: 2))
        map.promote(from: "provisional", into: "registered", translation: PathPoint(100, 50))
        var lines = map.lines(room: "registered", visible: true).filter { $0.kind == .atlasPathObjective }
        XCTAssertEqual((lines[0].start.x + lines[0].end.x) / 2, 120)
        XCTAssertTrue(map.selectObjective(room: "registered", point: CGPoint(x: 140, y: 90), tolerance: 2))
        map.promote(from: "provisional", into: "registered", translation: PathPoint(100, 50))
        lines = map.lines(room: "registered", visible: true).filter { $0.kind == .atlasPathObjective }
        XCTAssertEqual((lines[0].start.x + lines[0].end.x) / 2, 140)
    }
}
