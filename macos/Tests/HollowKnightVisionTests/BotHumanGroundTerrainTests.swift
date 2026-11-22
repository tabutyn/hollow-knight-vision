import XCTest
@testable import HollowKnightVision

final class BotHumanGroundTerrainTests: XCTestCase {
    private let size = CGSize(width: 100, height: 80)
    private var transform: GroundTruthCameraTransform {
        .init(sceneName: "Tutorial_01", cameraWorld: CGPoint(x: 5, y: 5),
              pixelsPerWorldUnitX: 10, pixelsPerWorldUnitY: 20, frameSize: size)
    }
    private func line(scene: String = "Tutorial_01", left: Double = 1, right: Double = 9,
                      y: Double = 4, kind: GroundTruthLineKind = .positive) -> GroundTruthWorldLine {
        .init(sceneName: scene, minimumWorldX: left, maximumWorldX: right, worldY: y,
              kind: kind, seededFromDetector: false)
    }

    func testProjectionUsesCameraCoordinatesAndOnlyVisiblePositiveLinesFromThisScene() {
        let lines = [line(), line(scene: "Tutorial_02"), line(kind: .negative), line(y: 100), line(y: .nan),
                     line(left: -20, right: 20, y: 5)]
        let result = BotHumanGroundProjection.segments(lines: lines, transform: transform, frameSize: size)
        XCTAssertEqual(result, [.init(minimumX: 10, maximumX: 90, y: 20, confidence: 1),
                                .init(minimumX: 0, maximumX: 100, y: 40, confidence: 1)])
        let scaled = BotHumanGroundProjection.segments(lines: [line()], transform: transform,
            frameSize: CGSize(width: 200, height: 160))
        XCTAssertEqual(scaled, [.init(minimumX: 20, maximumX: 180, y: 40, confidence: 1)])
    }

    func testCameraMustMatchCaptureFrameAndGeneration() {
        let cache = BotHumanGroundTerrainCache()
        var document = GroundTruthLineDocument(); document.lines = [line()]
        cache.update(document: document)
        cache.record(transform: transform, timestamp: 100, generation: 1)
        XCTAssertEqual(cache.lineCount, 1)
        XCTAssertEqual(cache.segments(timestamp: 100, generation: 1, frameSize: size).count, 1)
        XCTAssertTrue(cache.segments(timestamp: 100.01, generation: 1, frameSize: size).isEmpty)
        XCTAssertTrue(cache.segments(timestamp: 99.9, generation: 1, frameSize: size).isEmpty)
        XCTAssertTrue(cache.segments(timestamp: 100, generation: 2, frameSize: size).isEmpty)
        cache.clearCameras()
        XCTAssertTrue(cache.segments(timestamp: 100, generation: 1, frameSize: size).isEmpty)
        XCTAssertEqual(cache.lineCount, 1)
        document.lines = []
        cache.update(document: document)
        XCTAssertEqual(cache.lineCount, 0)
    }

    func testSpikesReachFrameMatchedGeometryAlongsideWallsAndDoors() {
        let cache = BotHumanGroundTerrainCache()
        var document = GroundTruthLineDocument()
        document.lines = [line()]
        document.spikes = [line(kind: .spike)]
        cache.update(document: document)
        cache.record(transform: transform, timestamp: 100, generation: 1)
        let geometry = cache.geometry(transform: transform, frameSize: size, timestamp: 100, generation: 1)
        XCTAssertEqual(geometry.spikes, [.init(x1: 10, y1: 20, x2: 90, y2: 20)])
        XCTAssertEqual(geometry.doors, [])
    }

    func testAuthoredGroundReachesTheExistingPolicySlotsWithVisualKnightPosition() throws {
        let version = LabelingModelSemanticVersion(major: 1, minor: 0)
        let knight = LiveObjectDetection(classIdentifier: "game.playable-knight",
            normalizedRect: CGRect(x: 0.4, y: 0.4, width: 0.1, height: 0.2), confidence: 0.9,
            sourceFrameIdentifier: 1, modelVersion: version)
        let batch = LiveObjectDetectionBatch(detections: [knight], sourceFrameIdentifier: 1,
            sourceTimestamp: 100, captureGeneration: 1, inferenceDuration: 0.01)
        let segments = BotHumanGroundProjection.segments(lines: [line()], transform: transform, frameSize: size)
        let observation = BotObservationBuilder.make(captureTimestamp: 100, capturedAt: 100, availableAt: 100.02,
            generation: 1, roomID: 1, roomRevision: 1, frameSize: size, detections: batch,
            terrain: segments, terrainValid: true, transitioning: false,
            terrainSource: "human-authored", humanTerrainCount: segments.count)
        XCTAssertTrue(observation.isUsable)
        XCTAssertEqual(observation.features[16], -0.35, accuracy: 0.0001)
        XCTAssertEqual(observation.features[17], 0.45, accuracy: 0.0001)
        XCTAssertEqual(observation.features[18], -0.2, accuracy: 0.0001)
        XCTAssertEqual(Array(observation.features[19...20]), [1, 1])
        XCTAssertEqual(observation.terrainSource, "human-authored")
        XCTAssertEqual(observation.humanTerrainCount, 1)
        XCTAssertEqual(try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(observation)), observation)
    }
}
