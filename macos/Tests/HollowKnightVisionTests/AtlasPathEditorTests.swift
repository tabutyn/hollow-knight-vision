import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class AtlasPathEditorTests: XCTestCase {
    func testDefaultPicksFarRightOfTopTierAndRespectsClearingAndReplacement() {
        let paths = AtlasPathMap()
        let floors = [PathPlatform(left: 10, right: 100, y: 101), .init(left: 140, right: 260, y: 100),
                      .init(left: 280, right: 350, y: 50)]
        func objective() -> [Int]? {
            _ = paths.graph(room: "room")
            return paths.diagnostic(room: "room")["objective"] as? [Int]
        }
        paths.setBase(room: "room", floors: floors, fallbackHeight: 20)
        XCTAssertEqual(objective(), [260, 120])
        paths.setBase(room: "room", floors: floors + [.init(left: 280, right: 330, y: 100)], fallbackHeight: 20)
        XCTAssertEqual(objective(), [330, 120])
        XCTAssertTrue(paths.selectObjective(room: "room", point: .init(x: 330, y: 120), tolerance: 2))
        paths.setBase(room: "room", floors: floors, fallbackHeight: 20)
        XCTAssertNil(objective())
        XCTAssertTrue(paths.selectObjective(room: "room", point: .init(x: 50, y: 121), tolerance: 2))
        paths.setBase(room: "room", floors: floors + [.init(left: 380, right: 440, y: 105)], fallbackHeight: 20)
        XCTAssertEqual(objective(), [50, 121])
    }

    func testConfirmedFloorTilesStitchAcrossOneSurfaceAndKeepPitWallAndHigherLedge() {
        func part(_ left: Int, _ right: Int, _ y: Int) -> BotTerrainSegment {
            .init(minimumX: Double(left), maximumX: Double(right), y: Double(y), confidence: 1)
        }
        let samples = [part(0, 30, 60), part(39, 72, 61), part(110, 150, 60)]
        let surface = CleanFloorLine(row: 40, xRange: 0...149, evidenceRanges: [0...29, 39...71, 110...149])
        func stitch(_ floors: [BotTerrainSegment], walls: [DetectedWall] = [], proof: [CleanFloorLine] = []) -> [BotTerrainSegment] {
            BotScreenTerrain.stitchedFloors(floors, walls: walls, frameHeight: 100, knightHeight: 24, knightWidth: 12, surfaces: proof)
        }
        XCTAssertEqual(stitch(samples).count, 2)
        let result = stitch(samples, proof: [surface])
        XCTAssertEqual(result, [part(0, 150, 60)])
        XCTAssertEqual(stitch(samples + [part(80, 98, 35)], proof: [surface]).count, 3)
        XCTAssertEqual(stitch(samples, walls: [.init(x: 88, top: 28, bottom: 43, solidOnRight: true)], proof: [surface]).count, 2)
        XCTAssertEqual(stitch(samples + [part(30, 65, 78)], proof: [surface]).count, 2)
        let map = AtlasPathMap()
        map.setBase(room: "room", floors: result.map { .init(left: Int($0.minimumX), right: Int($0.maximumX), y: Int($0.y)) }, fallbackHeight: 24)
        XCTAssertEqual(map.graph(room: "room")?.strokes.map(\.kind), [.walk])
    }
    func testDropBranchesShareOneVisibleShaft() {
        let visible = AtlasPathMap.displayStrokes([
            .init(a: .init(8, 0), b: .init(8, 120), kind: .drop, forward: false),
            .init(a: .init(8, 60), b: .init(8, 120), kind: .drop, forward: false),
            .init(a: .init(8, 60), b: .init(30, 60), kind: .drop),
        ])
        XCTAssertEqual(visible.count, 2)
        XCTAssertEqual(visible.filter { !$0.horizontal }.map(\.a), [.init(8, 0)])
        XCTAssertEqual(visible.filter(\.horizontal).map(\.b), [.init(30, 60)])
    }

    func testClickBeyondVisitedCameraImmediatelyHighlightsWholeReachableRoute() throws {
        let paths = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        let calibration = AtlasPathCalibration(scene: "room", origin: .zero, scale: CGPoint(x: 10, y: 10))
        let ground = GroundTruthWorldLine(sceneName: "room", minimumWorldX: 0, maximumWorldX: 30,
            worldY: 0, kind: .positive, seededFromDetector: false)
        let ledge = GroundTruthWorldLine(sceneName: "room", minimumWorldX: 32, maximumWorldX: 42,
            worldY: 2, kind: .positive, seededFromDetector: false)
        paths.setAuthored(room: "room", calibration: calibration, document: .init(lines: [ground, ledge]))
        var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
        features[0] = 0.5; features[1] = 0.13; features[2] = 0.05; features[3] = 0.13; features[4] = 1
        var row = BotObservation(type: "observation", captureTimestamp: 1, capturedAt: 1, availableAt: 1,
            generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: 1, detectionFrameID: 1,
            modelVersion: nil, bestKnightConfidence: 1, features: features, issues: [])
        paths.observe(room: "room", camera: .zero, width: 100, row: &row, grounded: true)
        let offset = CGPoint(x: -250, y: 50)
        let target = CGPoint(x: 145, y: 83)
        paths.setBase(room: "overlap", floors: [.init(left: 0, right: 440, y: 25)], fallbackHeight: 13)
        let placements: [(key: String, offset: CGPoint)] = [("overlap", offset), ("room", offset)]
        XCTAssertEqual(paths.selectObjective(at: target, tolerance: 9, in: placements), "room")
        let route = paths.lines(room: "room", offset: offset, visible: true).filter { $0.kind == .atlasPathObjective }
        XCTAssertTrue(route.contains { $0.start.x == -200 && $0.start.y == 63 && $0.end.y == 63 })
        XCTAssertTrue(route.contains { $0.start.x == $0.end.x && $0.end.y > $0.start.y && $0.start.y == 63 })
        XCTAssertTrue(route.contains { $0.start.x < target.x && $0.start.y == target.y && $0.end == target })
        XCTAssertFalse(paths.hasUnroutedObjective(room: "room"))
        XCTAssertEqual(paths.selectObjective(at: target, tolerance: 9, in: placements), "room")
        XCTAssertTrue(paths.lines(room: "room", visible: true).allSatisfy { $0.kind != .atlasPathObjective })
    }

    func testWallGeometryConstrainsRoutesWithoutDrawingWallGuides() throws {
        let map = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        let calibration = AtlasPathCalibration(scene: "room", origin: .zero, scale: CGPoint(x: 10, y: 10))
        let baseline = GroundTruthWorldLine(sceneName: "room", minimumWorldX: 0, maximumWorldX: 20,
            worldY: 0, kind: .positive, seededFromDetector: false)
        var wall = GroundTruthWorldLine(sceneName: "room", minimumWorldX: 10, maximumWorldX: 10,
            worldY: 0, kind: .wall, seededFromDetector: false)
        wall.endWorldY = 12
        map.setAuthored(room: "room", calibration: calibration, document: .init(lines: [baseline], walls: [wall]))
        let graph = try XCTUnwrap(map.graph(room: "room"))
        XCTAssertEqual(graph.floors.count, 2)
        XCTAssertTrue(graph.strokes.contains { $0.kind == .wall })
        let roads = map.lines(room: "room", visible: true).filter { $0.kind != .atlasPathObjective }
        XCTAssertTrue(roads.allSatisfy { $0.start.y == $0.end.y })
    }

    func testAuthoredSpikesCutSafeGroundAndNeverCreateGround() throws {
        let paths = AtlasPathMap(store: .init(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)))
        let ground = GroundTruthWorldLine(sceneName: "room", minimumWorldX: 0, maximumWorldX: 100,
            worldY: 0, kind: .positive, seededFromDetector: false)
        let spike = GroundTruthWorldLine(sceneName: "room", minimumWorldX: 40, maximumWorldX: 60,
            worldY: 0, kind: .spike, seededFromDetector: false)
        let calibration = AtlasPathCalibration(scene: "room", origin: .zero, scale: CGPoint(x: 1, y: 1))
        paths.setAuthored(room: "room", calibration: calibration,
                          document: GroundTruthLineDocument(lines: [ground], spikes: [spike]))
        let floors = try XCTUnwrap(paths.graph(room: "room")).floors
        XCTAssertEqual(floors.count, 2)
        XCTAssertFalse(floors.contains { $0.contains(50) })
        paths.setAuthored(room: "room", calibration: calibration,
                          document: GroundTruthLineDocument(spikes: [spike]))
        XCTAssertTrue(try XCTUnwrap(paths.graph(room: "room")).floors.isEmpty)
    }
    func testDoorAtLipSuppressesOnlyThatExitFall() {
        let floors = [PathPlatform(left: 0, right: 100, y: 0), PathPlatform(left: 20, right: 80, y: 200)]
        let open = AtlasPlatformGraph(floors: floors, walls: [], height: 20, width: 8)
        let marked = AtlasPlatformGraph(floors: floors, walls: [], height: 20, width: 8,
                                        doors: [(.init(80, 200), .init(80, 225))])
        func rightLip(_ graph: AtlasPlatformGraph) -> Bool {
            graph.strokes.contains { $0.kind == .drop && ($0.a == .init(80, 220) || $0.b == .init(80, 220)) }
        }
        XCTAssertTrue(rightLip(open))
        XCTAssertFalse(rightLip(marked))
        XCTAssertTrue(marked.strokes.contains { $0.kind == .drop && ($0.a == .init(20, 220) || $0.b == .init(20, 220)) })
        XCTAssertEqual(open.floors, marked.floors)
    }

    func testDropStrokesShowDownwardArrowsAndRespectVisibility() throws {
        let store = AtlasPathConnectionStore(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString))
        let paths = AtlasPathMap(store: store)
        paths.setBase(room: "room", floors: [
            .init(left: 0, right: 100, y: 0), .init(left: 20, right: 80, y: 200),
        ], fallbackHeight: 20)
        let drops = paths.lines(room: "room", offset: CGPoint(x: 7, y: 9), visible: true)
            .filter { $0.kind == .atlasPathDrop }
        let arms = drops.filter { $0.start.x != $0.end.x && $0.start.y != $0.end.y }
        XCTAssertFalse(arms.isEmpty)
        XCTAssertTrue(arms.allSatisfy { $0.end.y > $0.start.y })
        XCTAssertTrue(arms.allSatisfy { arm in
            drops.contains { $0.start.x == arm.start.x && $0.end.x == arm.start.x
                && min($0.start.y, $0.end.y) < arm.start.y && max($0.start.y, $0.end.y) > arm.start.y }
        })
        XCTAssertTrue(paths.lines(room: "room", visible: false).isEmpty)
        XCTAssertEqual(paths.groundLines(room: "room").count, 2)
    }

    func testHidingAtlasPathsHidesTheSelectedObjectiveAndShowingThemRestoresIt() throws {
        let store = AtlasPathConnectionStore(file: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString))
        let paths = AtlasPathMap(store: store)
        paths.setBase(room: "room", floors: [.init(left: 0, right: 80, y: 20)], fallbackHeight: 20)
        let walk = try XCTUnwrap(paths.lines(room: "room", visible: true).first { $0.kind == .atlasRoad })
        let middle = CGPoint(x: (walk.start.x + walk.end.x) / 2, y: (walk.start.y + walk.end.y) / 2)
        XCTAssertTrue(paths.selectObjective(room: "room", point: middle, tolerance: 2))
        let objective = paths.lines(room: "room", visible: true).filter { $0.kind == .atlasPathObjective }
        XCTAssertFalse(objective.isEmpty)
        XCTAssertTrue(paths.lines(room: "room", visible: false).isEmpty)
        XCTAssertFalse(paths.groundLines(room: "room").isEmpty)
        let restored = paths.lines(room: "room", visible: true).filter { $0.kind == .atlasPathObjective }
        XCTAssertEqual(restored.map(\.start), objective.map(\.start))
        XCTAssertEqual(restored.map(\.end), objective.map(\.end))
    }

    func testConnectionsPersistReprojectAndDeleteAsAWhole() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("paths.json")
        let calibration = AtlasPathCalibration(scene: "room", origin: .zero, scale: CGPoint(x: 2, y: 2))
        let graph = AtlasPlatformGraph(floors: [.init(left: 0, right: 80, y: 20), .init(left: 100, right: 180, y: 80)],
                                       walls: [], height: 20, width: 10)
        let store = AtlasPathConnectionStore(file: file)
        XCTAssertNil(store.click(room: "first", point: CGPoint(x: 40, y: 40), tolerance: 4, graph: graph,
                                 tool: .doubleJump, calibration: calibration))
        XCTAssertEqual(store.selection?.point, PathPoint(40, 20))
        let saved = try XCTUnwrap(store.click(room: "first", point: CGPoint(x: 130, y: 80), tolerance: 4, graph: graph,
                                               tool: .doubleJump, calibration: calibration))
        XCTAssertEqual(saved.count, 1)
        XCTAssertTrue(saved[0].doubleJump)
        XCTAssertNil(store.selection)

        let restored = AtlasPathConnectionStore(file: file)
        XCTAssertEqual(restored.connections(room: "first"), saved)
        let moved = restored.connections(room: "another", scene: "room", origin: CGPoint(x: 100, y: -50),
                                         scale: CGPoint(x: 4, y: 4))
        XCTAssertEqual(moved.first?.first, PathPoint(180, -10))
        XCTAssertEqual(moved.first?.second, PathPoint(360, 110))
        XCTAssertEqual(moved.first?.id, saved[0].id)
        XCTAssertEqual(restored.connections(room: "first", scene: "room", origin: CGPoint(x: 100, y: -50),
                                             scale: CGPoint(x: 4, y: 4)).first?.first, PathPoint(180, -10))

        let withConnection = AtlasPlatformGraph(floors: graph.floors, walls: [], height: 20, width: 10, connections: saved)
        let deleted = try XCTUnwrap(restored.click(room: "first", point: CGPoint(x: 85, y: 105), tolerance: 5,
                                                    graph: withConnection, tool: .delete, calibration: calibration))
        XCTAssertTrue(deleted.isEmpty)
        let afterDelete = AtlasPathConnectionStore(file: file)
        XCTAssertTrue(afterDelete.connections(room: "first").isEmpty)
        XCTAssertTrue(afterDelete.connections(room: "another", scene: "room", origin: .zero,
                                              scale: CGPoint(x: 2, y: 2)).isEmpty)
    }

    func testInvalidSavedFileRemainsUntouched() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let file = directory.appendingPathComponent("paths.json")
        let original = Data("unreadable".utf8)
        try original.write(to: file)
        let store = AtlasPathConnectionStore(file: file)
        XCTAssertNotNil(store.issue)
        let graph = AtlasPlatformGraph(floors: [.init(left: 0, right: 30, y: 10), .init(left: 50, right: 80, y: 20)],
                                       walls: [], height: 10, width: 5)
        _ = store.click(room: "room", point: CGPoint(x: 10, y: 10), tolerance: 3, graph: graph, tool: .jump)
        XCTAssertNil(store.click(room: "room", point: CGPoint(x: 60, y: 20), tolerance: 3, graph: graph, tool: .jump))
        XCTAssertEqual(try Data(contentsOf: file), original)
    }
}
