import XCTest
@testable import HollowKnightVision

final class BotWallEvidenceTests: XCTestCase {
    func testPitKeepsBothSidesAndRanksByDistanceToSegment() {
        let left = DetectedWall(x: 45, top: 10, bottom: 70, solidOnRight: false)
        let overhead = DetectedWall(x: 49, top: 0, bottom: 5, solidOnRight: true)
        let right = DetectedWall(x: 60, top: 15, bottom: 65, solidOnRight: true)
        func encode(_ walls: [DetectedWall]?) -> [Double] {
            BotWallEvidence.features(walls: walls, knightX: 0.5, knightY: 0.4, width: 100, height: 80)
        }
        XCTAssertEqual(encode([overhead, right, left]), [1, -0.05, -0.3, 0.3, -1, 1, 0.1, -0.25, 0.25, 1, 1])
        XCTAssertEqual(encode([]), [1] + Array(repeating: 0, count: 10))
        XCTAssertEqual(encode(nil), Array(repeating: 0, count: 11))
    }

    func testWallChangesNativeJumpAndLegacyProjectionKeepsEveryHistoryStep() throws {
        let width = BotObservationContract.featureCount, legacy = BotObservationContract.legacyFeatureCount
        func policy(version: Int, width: Int, jumpIndex: Int) throws -> BotNativePolicy {
            let size = width * 4
            var weights = [Float](repeating: 0, count: size * 10)
            weights[7 * size + jumpIndex] = 2
            return try BotNativePolicy(document: .init(schemaVersion: 1, observationSchemaVersion: version,
                modelName: "Fixture", featureNames: try XCTUnwrap(BotObservationContract.names(for: version)),
                historyLength: 4, actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "fixture",
                layers: [.init(inputSize: size, outputSize: 10, activation: "linear", weights: weights,
                               biases: Array(repeating: 0, count: 10))]))
        }
        let wallPolicy = try policy(version: 2, width: width, jumpIndex: 3 * width + 154)
        var stack = [Float](repeating: 0, count: 4 * width)
        XCTAssertEqual(try wallPolicy.action(for: stack), .neutral)
        stack[3 * width + 154] = 1
        XCTAssertEqual(try wallPolicy.action(for: stack).values, [0, 0, 1, 0])
        let oldPolicy = try policy(version: 1, width: legacy, jumpIndex: 2 * legacy + 6)
        XCTAssertEqual(try oldPolicy.action(for: stack), .neutral)
        stack[2 * width + 6] = 1
        var oldStack = [Float](repeating: 0, count: 4 * legacy)
        oldStack[2 * legacy + 6] = 1
        XCTAssertEqual(try oldPolicy.logits(for: stack), try oldPolicy.logits(for: oldStack))
        XCTAssertEqual(try oldPolicy.action(for: stack).values, [0, 0, 1, 0])
    }
}
