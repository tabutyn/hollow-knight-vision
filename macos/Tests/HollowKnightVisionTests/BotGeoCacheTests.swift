import XCTest
@testable import HollowKnightVision

final class BotGeoCacheTests: XCTestCase {
    private func frame(_ t: Double = 10, age: Double = 0) -> BotObservation {
        func detection(_ label: String, _ x: Double, confidence: Double = 0.95) -> LiveObjectDetection {
            .init(classIdentifier: label, normalizedRect: CGRect(x: x, y: 0.25, width: 0.1, height: 0.1),
                  confidence: confidence, sourceFrameIdentifier: 1, modelVersion: .init(major: 1, minor: 0))
        }
        let batch = LiveObjectDetectionBatch(detections: [
            detection("game.playable-knight", 0.45), detection("world.geo-deposit", 0.7),
            detection("world.geo-deposit", 0.1), detection("game.geo", 0.4),
            detection("world.geo-deposit", 0.3, confidence: 0.25)],
            sourceFrameIdentifier: 1, sourceTimestamp: t - age, captureGeneration: 1, inferenceDuration: 0.01)
        return BotObservationBuilder.make(captureTimestamp: t, capturedAt: t, availableAt: t + 0.01,
            generation: 1, roomID: 1, roomRevision: 0, frameSize: CGSize(width: 200, height: 100),
            detections: batch, terrain: [], terrainValid: true, transitioning: false)
    }

    func testVisualCaptureAndNativeHistoryUseDepositBounds() throws {
        let row = frame(), cache = try XCTUnwrap(row.nearestGeoCache)
        XCTAssertEqual(row.geoCaches?.count, 2)
        XCTAssertEqual(cache.x, 0.75, accuracy: 0.0001)
        XCTAssertEqual(cache.y, 0.35, accuracy: 0.0001)
        XCTAssertEqual(try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(row)), row)
        var history = BotObservationHistory()
        let result = try XCTUnwrap(history.input(row, previousAction: .neutral, now: 10.02,
            includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true))
        XCTAssertEqual(result.count, BotObservationContract.geoPolicyNames.count * 4)
        XCTAssertEqual(Array(result.suffix(3)), [0.25, 0, 1])
        XCTAssertNil(frame(age: 1).geoCaches)
        var old = row
        old.geoCaches = nil
        XCTAssertNil(try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(old)).geoCaches)
    }

    func testActiveDetectorFeedsLabeledGeoDepositsIntoGamePlayCapture() throws {
        guard ProcessInfo.processInfo.environment["HKV_REPLAY_GEO_LABELS"] == "1" else {
            throw XCTSkip("Set HKV_REPLAY_GEO_LABELS=1 to replay local Geo deposit examples.")
        }
        let detector = LiveObjectDetector()
        try detector.load(LabelingModelVersionStore().activeModelArtifacts())
        let store = LabelingExampleStore(), decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        var labeled = 0, matched = 0, reported = 0
        for directory in try FileManager.default.contentsOfDirectory(at: store.rootURL, includingPropertiesForKeys: nil) {
            let file = directory.appendingPathComponent(LabelingExampleStore.manifestFilename)
            guard FileManager.default.fileExists(atPath: file.path) else { continue }
            let manifest = try decoder.decode(LabelingExampleManifest.self, from: Data(contentsOf: file))
            let boxes = manifest.annotations.filter { $0.classIdentifier == "world.geo-deposit" && !$0.isHardNegative }
            guard !boxes.isEmpty else { continue }
            let image = try store.loadImage(for: SavedLabelingExample(directoryURL: directory, manifest: manifest))
            let detections = try detector.detect(in: image, sourceFrameIdentifier: 1)
            let batch = LiveObjectDetectionBatch(detections: detections, sourceFrameIdentifier: 1, sourceTimestamp: 1,
                                                 captureGeneration: 1, inferenceDuration: 0.01)
            let row = BotObservationBuilder.make(captureTimestamp: 1, capturedAt: 1, availableAt: 1.01,
                generation: 1, roomID: 1, roomRevision: 0, frameSize: CGSize(width: image.width, height: image.height),
                detections: batch, terrain: [], terrainValid: true, transitioning: false)
            let aspect = Double(image.width) / Double(image.height)
            let detectionsTopLeft = (row.geoCaches ?? []).map {
                CGRect(x: $0.x - $0.width / 2, y: 1 - ($0.y + $0.height / 2) * aspect,
                       width: $0.width, height: $0.height * aspect)
            }
            labeled += boxes.count
            reported += detectionsTopLeft.count
            matched += boxes.filter { label in
                detectionsTopLeft.contains { RectangleOverlap.intersectionOverUnion($0, label.normalizedRect) >= 0.5 }
            }.count
        }
        print("Geo capture matched \(matched) of \(labeled) labeled deposits, \(reported) reported.")
        XCTAssertGreaterThan(matched, 0)
    }

    func testOffscreenTargetReturnsAfterCombatAndClearsWhenRoomChangesOrRockIsGone() {
        var driver = BotGeoPursuit()
        var row = frame()
        row.blockadePose = .init(space: "room-1", x: 0, y: 0, width: 1)
        XCTAssertNotNil(driver.target(row))
        row.geoCaches = []
        row.blockadePose = .init(space: "room-1", x: 1, y: 0, width: 1)
        XCTAssertLessThan(driver.target(row)?.dx ?? 0, 0)
        row.blockadePose = .init(space: "room-1", x: 0, y: 0, width: 1)
        XCTAssertNil(driver.target(row))
        row.blockadePose = .init(space: "room-2", x: 1, y: 0, width: 1)
        XCTAssertNil(driver.target(row))
    }
}
