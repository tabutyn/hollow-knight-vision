import AppKit
import XCTest
@testable import HollowKnightVision

final class BotDemonstrationTests: XCTestCase {
    func testOneRecordingIdentityLinksReplayAndLearningData() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let id = UUID(), pathRecorder = InputPathRecorder()
        let learning = BotDemonstrationRecorder(rootURL: root)
        XCTAssertTrue(pathRecorder.start(at: 100, id: id))
        let directory = try learning.start(gameMode: "modded", detectorVersion: "receiver-actors-v1", id: id, at: 100)
        pathRecorder.recordInput(button: .right, isPressed: true, at: 100.1)
        learning.recordInput(button: "right", isPressed: true, at: 100.1)
        pathRecorder.recordInput(button: .right, isPressed: false, at: 100.3)
        learning.recordInput(button: "right", isPressed: false, at: 100.3)
        var sample = observation(time: 100.1)
        let wall = DetectedWall(x: 332, top: 125, bottom: 225, solidOnRight: true)
        let evidence = BotWallEvidence.features(walls: [wall], knightX: sample.features[0],
            knightY: sample.features[1], width: 640, height: 360)
        sample.features.replaceSubrange(BotObservationContract.legacyFeatureCount..., with: evidence)
        learning.record(sample, image: nil)
        _ = try learning.stop(reason: "user-stop", at: 100.5)
        let path = try XCTUnwrap(pathRecorder.stop(at: 100.5))
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        let manifest = try decoder.decode(BotDemonstrationManifest.self,
            from: Data(contentsOf: directory.appendingPathComponent("manifest.json")))
        XCTAssertEqual(path.id, manifest.id)
        XCTAssertEqual(path.events.count, manifest.inputEventCount)
        XCTAssertEqual(manifest.status, "completed")
        XCTAssertEqual(manifest.observationCount, 1)
        XCTAssertEqual(manifest.featureNames, BotObservationContract.featureNames)
        let loaded = try XCTUnwrap(BotSnippet.load(directory).observations.first)
        var history = BotObservationHistory()
        let input = try XCTUnwrap(history.input(loaded, previousAction: .neutral, now: 100.13))
        XCTAssertEqual(Array(input.suffix(evidence.count)), evidence.map(Float.init))
        XCTAssertEqual(input.last, 1)
    }
    private func batch(time: Double = 100, generation: UInt64 = 1,
                       includeKnight: Bool = true) -> LiveObjectDetectionBatch {
        var detections = [LiveObjectDetection(classIdentifier: "enemies.custom-enemy",
            normalizedRect: CGRect(x: 0.7, y: 0.4, width: 0.1, height: 0.1), confidence: 0.8,
            sourceFrameIdentifier: 5, modelVersion: .init(major: 2, minor: 0))]
        if includeKnight {
            detections.append(LiveObjectDetection(classIdentifier: "game.playable-knight",
                normalizedRect: CGRect(x: 0.4, y: 0.4, width: 0.1, height: 0.2), confidence: 0.9,
                sourceFrameIdentifier: 5, modelVersion: .init(major: 2, minor: 0)))
        }
        return LiveObjectDetectionBatch(detections: detections, sourceFrameIdentifier: 5,
            sourceTimestamp: time, captureGeneration: generation, inferenceDuration: 0.01)
    }

    private func observation(time: Double = 100.1, batch: LiveObjectDetectionBatch? = nil,
                             terrain: [BotTerrainSegment] = []) -> BotObservation {
        BotObservationBuilder.make(captureTimestamp: time, capturedAt: time,
            availableAt: time + 0.02, generation: 1, roomID: 1, roomRevision: 1,
            frameSize: CGSize(width: 640, height: 360), detections: batch ?? self.batch(time: time - 0.05),
            terrain: terrain, terrainValid: true, transitioning: false)
    }

    func testCoordinatesUseOneScaleAndEnemyCatalogNamespace() {
        let sample = observation(terrain: [.init(minimumX: 200, maximumX: 400, y: 100, confidence: 1)])
        XCTAssertTrue(sample.isUsable)
        XCTAssertEqual(sample.features.count, BotObservationContract.featureCount)
        XCTAssertEqual(BotObservationContract.featureNames.count, BotObservationContract.featureCount)
        XCTAssertEqual(Set(BotObservationContract.featureNames).count, BotObservationContract.featureCount)
        XCTAssertEqual(sample.features[0], 288.0 / 640, accuracy: 0.0001)
        XCTAssertEqual(sample.features[1], 180.0 / 640, accuracy: 0.0001)
        XCTAssertEqual(sample.features[16], -88.0 / 640, accuracy: 0.0001)
        XCTAssertEqual(sample.features[18], -80.0 / 640, accuracy: 0.0001)
        XCTAssertEqual(sample.features[20], 1)
        XCTAssertEqual(sample.features[96], 192.0 / 640, accuracy: 0.0001)
        XCTAssertEqual(sample.features[101], 1)
        XCTAssertEqual(sample.features[102...], Array(repeating: 0, count: BotObservationContract.featureCount - 102)[...])
    }

    func testStaleFutureAndWrongGenerationDetectionsAreRejected() {
        for input in [batch(time: 99), batch(time: 101), batch(generation: 2)] {
            let sample = observation(batch: input)
            XCTAssertFalse(sample.isUsable)
            XCTAssertTrue(sample.issues.contains("stale-or-missing-detection"))
            XCTAssertTrue(sample.features.allSatisfy(\.isFinite))
        }
        XCTAssertTrue(observation(batch: batch(includeKnight: false)).issues.contains("missing-knight"))
    }

    func testAirborneObservationDoesNotRequireAFloor() {
        let sample = observation()
        XCTAssertTrue(sample.isUsable)
        XCTAssertEqual(sample.features[6], 1)
        XCTAssertTrue(sample.features[16..<96].allSatisfy { $0 == 0 })
    }

    func testTransitionAndSlowCaptureAreExcluded() {
        let sample = BotObservationBuilder.make(captureTimestamp: 100, capturedAt: 100,
            availableAt: 101, generation: 1, roomID: 1, roomRevision: 1,
            frameSize: CGSize(width: 640, height: 360), detections: batch(),
            terrain: [], terrainValid: false, transitioning: true)
        XCTAssertEqual(Set(sample.issues), ["room-transition", "stale-capture", "missing-terrain-analysis"])
    }

    func testRecorderStreamsTapsImagesAndFinalizesManifest() throws {
        let external = ProcessInfo.processInfo.environment["HKV_BOT_TEST_EXPORT"]
        let root = external.map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { if external == nil { try? FileManager.default.removeItem(at: root) } }
        let recorder = BotDemonstrationRecorder(rootURL: root)
        let url = try recorder.start(gameMode: "normal", detectorVersion: "synthetic-test-v2", at: 100)
        recorder.recordInput(button: "right", isPressed: true, at: 100.05)
        recorder.recordInput(button: "right", isPressed: true, at: 100.06) // repeat suppressed
        let image = CGContext(data: nil, width: 8, height: 8, bitsPerComponent: 8, bytesPerRow: 32,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!.makeImage()!
        for index in 0..<15 {
            let time = 100.1 + Double(index) * 0.1
            if index == 4 {
                recorder.recordInput(button: "z", isPressed: true, at: time + 0.03)
                recorder.recordInput(button: "z", isPressed: false, at: time + 0.05)
            }
            recorder.record(observation(time: time), image: image)
        }
        recorder.recordInput(button: "right", isPressed: false, at: 101.7)
        XCTAssertEqual(try recorder.stop(reason: "user-stop", at: 102), url)
        let manifest = try JSONSerialization.jsonObject(with: Data(contentsOf: url.appendingPathComponent("manifest.json"))) as! [String: Any]
        XCTAssertEqual(manifest["status"] as? String, "completed")
        XCTAssertEqual(manifest["observationCount"] as? Int, 15)
        XCTAssertEqual(manifest["inputEventCount"] as? Int, 4)
        XCTAssertEqual(manifest["imageCount"] as? Int, 2)
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.appendingPathComponent("frames/0.jpg").path))
        let rows = try String(contentsOf: url.appendingPathComponent("events.jsonl"), encoding: .utf8).split(separator: "\n")
        XCTAssertEqual(rows.count, 19)
        for row in rows { XCTAssertNoThrow(try JSONSerialization.jsonObject(with: Data(row.utf8))) }
        XCTAssertNil(try recorder.stop(reason: "user-stop", at: 103))
        let second = try recorder.start(gameMode: "modded", detectorVersion: "synthetic-test-v2", at: 110)
        XCTAssertNotEqual(second, url)
        recorder.record(observation(), image: nil) // previous session work cannot leak
        _ = try recorder.stop(reason: "focus-lost", at: 111)
        let interrupted = try JSONSerialization.jsonObject(with: Data(contentsOf: second.appendingPathComponent("manifest.json"))) as! [String: Any]
        XCTAssertEqual(interrupted["status"] as? String, "interrupted")
        XCTAssertEqual(interrupted["observationCount"] as? Int, 0)
    }

    func testBShortcutPreservesTextEntryModifiersAndSheets() {
        XCTAssertTrue(BotShortcutPolicy.handles(keyCode: 11, modifiers: [], isRepeat: false,
                                               editingText: false, hasSheet: false))
        for flags in [NSEvent.ModifierFlags.command, .control, .option, .shift] {
            XCTAssertFalse(BotShortcutPolicy.handles(keyCode: 11, modifiers: flags, isRepeat: false,
                                                    editingText: false, hasSheet: false))
        }
        XCTAssertFalse(BotShortcutPolicy.handles(keyCode: 11, modifiers: [], isRepeat: true, editingText: false, hasSheet: false))
        XCTAssertFalse(BotShortcutPolicy.handles(keyCode: 11, modifiers: [], isRepeat: false, editingText: true, hasSheet: false))
        XCTAssertFalse(BotShortcutPolicy.handles(keyCode: 11, modifiers: [], isRepeat: false, editingText: false, hasSheet: true))
        XCTAssertFalse(NormalGameKeyPolicy.supported.contains(11))
        XCTAssertFalse(GameControlForwarder.supportedKeyCodes.contains(11))
    }

    func testBotControlIsAvailableInGameplayAndHacker() {
        for workspace in DashboardWorkspace.allCases {
            XCTAssertEqual(BotControlPresentationPolicy.shows(in: workspace),
                           workspace == .gameplay || workspace == .hacker)
        }
    }

    func testMalformedFrameSizeCannotOverflowCoordinateConversion() {
        let sample = BotObservationBuilder.make(captureTimestamp: 100, capturedAt: 100,
            availableAt: 100.02, generation: 1, roomID: 1, roomRevision: 1,
            frameSize: CGSize(width: 1e30, height: 1e30), detections: batch(),
            terrain: [], terrainValid: true, transitioning: false)
        XCTAssertTrue(sample.issues.contains("invalid-frame-size"))
    }
}
