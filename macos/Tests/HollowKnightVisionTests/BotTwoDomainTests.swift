import AppKit
import XCTest
@testable import HollowKnightVision

final class BotTwoDomainTests: XCTestCase {
    private func policy() throws -> BotNativePolicy {
        try BotNativePolicy(document: BotRuntimePolicyDocument(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: "Audit test", featureNames: BotObservationContract.legacyFeatureNames, historyLength: 4,
            actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "test-hash",
            layers: [.init(inputSize: 576, outputSize: 10, activation: "linear",
                weights: .init(repeating: 0, count: 5760), biases: .init(repeating: 0, count: 10))]))
    }

    func testModelNamespacesAndSelectionsAreIndependent() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let gameplay = BotModelStore(root: root, domain: .gameplay), hacker = BotModelStore(root: root, domain: .hacker)
        XCTAssertNotEqual(gameplay.models, hacker.models)
        XCTAssertNotEqual(gameplay.selectionURL, hacker.selectionURL)
        try FileManager.default.createDirectory(at: gameplay.models, withIntermediateDirectories: true)
        try JSONEncoder().encode(BotModelStore.Selection(directory: "g-version", previousDirectory: nil)).write(to: gameplay.selectionURL)
        XCTAssertEqual(try gameplay.selection().directory, "g-version")
        XCTAssertEqual(try hacker.selection().directory, "v1")
        XCTAssertEqual(try gameplay.directory(named: "candidate").deletingLastPathComponent(), gameplay.models)
        XCTAssertEqual(try hacker.directory(named: "candidate").deletingLastPathComponent(), hacker.models)
    }

    func testAuditRequiresExactCameraAndFreezesTheAuthoredDocument() {
        let cache = BotHumanGroundTerrainCache(), size = CGSize(width: 100, height: 80)
        var document = GroundTruthLineDocument()
        document.lines = [.init(sceneName: "Tutorial_01", minimumWorldX: 1, maximumWorldX: 9,
            worldY: 4, kind: .positive, seededFromDetector: false)]
        cache.update(document: document)
        let snapshot = cache.snapshot
        let camera = GroundTruthCameraTransform(sceneName: "Tutorial_01", cameraWorld: CGPoint(x: 5, y: 5),
            pixelsPerWorldUnitX: 10, pixelsPerWorldUnitY: 20, frameSize: size)
        cache.record(transform: camera, timestamp: 100, generation: 1)
        let paired = cache.auditProjection(timestamp: 100, generation: 1, frameSize: size)
        XCTAssertTrue(paired.available); XCTAssertEqual(paired.scene, "Tutorial_01")
        XCTAssertEqual(paired.lines, [.init(minimumX: 10, maximumX: 90, y: 20, confidence: 1)])
        XCTAssertFalse(cache.auditProjection(timestamp: 100.01, generation: 1, frameSize: size).available)
        XCTAssertFalse(cache.auditProjection(timestamp: 100, generation: 2, frameSize: size).available)
        document.lines = []; cache.update(document: document)
        XCTAssertEqual(snapshot.lines.count, 1)
        XCTAssertFalse(cache.auditProjection(timestamp: 100, generation: 1, frameSize: size).available)
    }

    func testPermanentDeletionRemovesOnlyThisClipAndItsLinkedReplay() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let id = UUID(), recorder = BotDemonstrationRecorder(rootURL: root.appendingPathComponent("demonstrations"))
        let directory = try recorder.start(gameMode: "modded", detectorVersion: "test", id: id, at: 100, domain: .gameplay)
        let replay = root.appendingPathComponent("input-paths-v1")
        try FileManager.default.createDirectory(at: replay, withIntermediateDirectories: true)
        let linked = replay.appendingPathComponent("path-test-\(id.uuidString.lowercased()).json")
        try Data("replay".utf8).write(to: linked)
        let frames = replay.appendingPathComponent("frames-\(id.uuidString.lowercased())")
        try FileManager.default.createDirectory(at: frames, withIntermediateDirectories: true)
        let unrelated = replay.appendingPathComponent("path-test-other.json")
        try Data("keep".utf8).write(to: unrelated)
        XCTAssertThrowsError(try BotClipDeletion.delete(snippet: BotSnippet.load(directory), root: root, replayRoot: replay))
        _ = try recorder.stop(reason: "user-stop", at: 101)
        try BotClipDeletion.delete(snippet: BotSnippet.load(directory), root: root, replayRoot: replay)
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: linked.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: frames.path))
        XCTAssertEqual(try Data(contentsOf: unrelated), Data("keep".utf8))
    }

    func testBotEvaluationPreservesFramesControlsAndPairedAuditOutsideExpertData() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = BotEvaluationRecorder(root: root.appendingPathComponent("evaluations"))
        recorder.start(domain: .gameplay, model: try policy(), reference: GroundTruthLineDocument())
        XCTAssertTrue(recorder.isRecording)
        let directory = try XCTUnwrap(recorder.lastDirectory), now = ProcessInfo.processInfo.systemUptime
        let context = try XCTUnwrap(CGContext(data: nil, width: 16, height: 16, bitsPerComponent: 8,
            bytesPerRow: 64, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let image = try XCTUnwrap(context.makeImage())
        recorder.observe(.init(type: "observation", captureTimestamp: now, capturedAt: now, availableAt: now,
            generation: 1, roomID: 1, roomRevision: 0, detectionTimestamp: now, detectionFrameID: 42,
            modelVersion: "test", bestKnightConfidence: 1, features: .init(repeating: 0, count: BotObservationContract.featureCount),
            issues: [], terrainSource: "detected"), image: image)
        recorder.action(try BotAction(values: [2, 0, 1, 0]), at: now)
        recorder.action(.neutral, at: now)
        recorder.audit(.init(captureTimestamp: now, capturedAt: now, captureGeneration: 1, width: 16, height: 16,
            sceneName: "Tutorial_01", referenceAvailable: true, detected: [], authored: []), image: image, solveWidth: 16)
        recorder.stop()
        let clip = try BotSnippet.load(directory)
        XCTAssertEqual(clip.manifest.botDomain, .gameplay)
        XCTAssertEqual(clip.manifest.observationCount, 1)
        XCTAssertEqual(clip.manifest.inputEventCount, 4)
        XCTAssertEqual(clip.manifest.terrainPairedFrameCount, 1)
        XCTAssertEqual(directory.deletingLastPathComponent().lastPathComponent, "gameplay")
        XCTAssertFalse(directory.path.contains("demonstrations"))
        let archive = directory.appendingPathComponent("frames-\(clip.id.uuidString.lowercased())/manifest.json")
        let manifest = try JSONDecoder().decode(VisualReplayManifest.self, from: Data(contentsOf: archive))
        XCTAssertTrue(manifest.summary.complete); XCTAssertEqual(manifest.summary.savedFrameCount, 1)
        XCTAssertEqual(manifest.frames.first?.captureTimestamp, now)
        XCTAssertEqual(manifest.frames.first?.captureGeneration, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: directory.appendingPathComponent("authored-ground.json").path))
    }
}
