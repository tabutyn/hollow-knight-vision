import XCTest
@testable import HollowKnightVision

final class BotScreenTerrainTests: XCTestCase {
    func testVisualPortalProjectionReachesGameplayPolicyOnlyInItsRoom() throws {
        let portal = VisualRoomPortal(id: 1, leftRoomID: 1, rightRoomID: 2,
            leftDoorWorldX: 110, rightDoorWorldX: 115, leftDoorYRange: 60...100, rightDoorYRange: 70...110)
        let doors = BotVisualPortalProjection.doors([portal], roomID: 1, camera: CGPoint(x: 100, y: 50),
            solveWidth: 120, frameSize: CGSize(width: 240, height: 140))
        XCTAssertEqual(doors, [.init(x1: 20, y1: 20, x2: 20, y2: 100)])
        XCTAssertTrue(BotVisualPortalProjection.doors([portal], roomID: 3, camera: .zero,
            solveWidth: 120, frameSize: CGSize(width: 240, height: 140)).isEmpty)
        var terrain = BotScreenTerrain(width: 240, height: 140)
        terrain.doors = doors
        var row = BotObservation(type: "observation", captureTimestamp: 100, capturedAt: 100,
            availableAt: 100, generation: 1, roomID: 1, roomRevision: 0, detectionTimestamp: 100,
            detectionFrameID: 1, modelVersion: nil, bestKnightConfidence: nil,
            features: Array(repeating: 0, count: 155), issues: [], terrainSource: "detected")
        row.detectedTerrain = terrain
        var history = BotObservationHistory()
        let input = try XCTUnwrap(history.input(row, previousAction: .neutral, now: 100,
            includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true,
            includeAtlasMap: true, includeRoad: true, includePickup: true, includeCocoons: true, includeDoors: true))
        XCTAssertEqual(Array(input.suffix(BotTerrainGrid.cells)), BotTerrainGrid.doorFeatures(terrain))
        XCTAssertGreaterThan(input.suffix(BotTerrainGrid.cells).reduce(0, +), 0)
        row.terrainSource = "human-authored"
        history.reset()
        let hacker = try XCTUnwrap(history.input(row, previousAction: .neutral, now: 100,
            includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true,
            includeAtlasMap: true, includeRoad: true, includePickup: true, includeCocoons: true, includeDoors: true))
        XCTAssertEqual(hacker.suffix(BotTerrainGrid.cells).reduce(0, +), 0)
    }

    func testDoorChannelKeepsOldSchemaAndBothAxes() throws {
        var terrain = BotScreenTerrain(width: 240, height: 140)
        terrain.doors = [.init(x1: 20, y1: 30, x2: 50, y2: 30),
                         .init(x1: 100, y1: 40, x2: 100, y2: 70)]
        XCTAssertEqual(BotTerrainGrid.doorFeatures(terrain).filter { $0 == 1 }.count, 8)
        XCTAssertEqual(BotObservationContract.doorNames, BotObservationContract.cocoonNames + BotTerrainGrid.doorNames)
        let old = try JSONDecoder().decode(BotScreenTerrain.self, from: Data("{\"width\":240,\"height\":140,\"floors\":[],\"walls\":[]}".utf8))
        XCTAssertNil(old.doors)
        XCTAssertTrue(BotTerrainGrid.doorFeatures(old).allSatisfy { $0 == 0 })
    }

    func testPolicyCanUseDistantFloorAndWallOutsideLocalSlotsInEitherDomain() throws {
        var terrain = BotScreenTerrain(width: 240, height: 140)
        terrain.floors = (0..<20).map { i in
            let x = Double(i * 10)
            return .init(minimumX: x + 1, maximumX: x + 4, y: 115, confidence: 1)
        }
        terrain.walls = [5, 15, 25, 195].map { .init(x: $0, bottomY: 115, topY: 135, solidSide: 0) }
        let width = BotObservationContract.screenPolicyNames.count
        let inputSize = 4 * width
        let floorCell = 11 * 24 + 19, wallCell = 336 + 13 * 24 + 19
        let grid = BotTerrainGrid.features(terrain)
        XCTAssertEqual(grid.prefix(336).filter { $0 == 1 }.count, 20)
        XCTAssertEqual(grid.suffix(336).filter { $0 == 1 }.count, 12)
        var weights = [Float](repeating: 0, count: inputSize * 10)
        weights[7 * inputSize + 3 * width + 155 + floorCell] = 1
        weights[7 * inputSize + 3 * width + 155 + wallCell] = 1
        var bias = [Float](repeating: 0, count: 10)
        bias[6] = 1.5
        let policy = try BotNativePolicy(document: .init(schemaVersion: 1, observationSchemaVersion: 3,
            modelName: "Full screen", featureNames: BotObservationContract.screenPolicyNames, historyLength: 4,
            actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "test",
            layers: [.init(inputSize: inputSize, outputSize: 10, activation: "linear", weights: weights, biases: bias)]))
        for hacker in [true, false] {
            var observation = BotObservation(type: "observation", captureTimestamp: 100, capturedAt: 100,
                availableAt: 100, generation: 1, roomID: 1, roomRevision: 0, detectionTimestamp: 100,
                detectionFrameID: 1, modelVersion: nil, bestKnightConfidence: nil,
                features: Array(repeating: 0, count: 155), issues: [], terrainSource: hacker ? "human-authored" : "detected")
            var history = BotObservationHistory()
            // The other domain's terrain must never enter this policy.
            if hacker { observation.detectedTerrain = terrain } else { observation.authoredTerrain = terrain }
            func action() throws -> BotAction {
                try policy.action(for: XCTUnwrap(history.input(observation, previousAction: .neutral,
                    now: 100, includeScreenTerrain: true)))
            }
            XCTAssertEqual(try action(), .neutral)
            if hacker { observation.authoredTerrain = terrain } else { observation.detectedTerrain = terrain }
            XCTAssertTrue(try action().buttons.contains(.actionZ))
        }
    }
}
