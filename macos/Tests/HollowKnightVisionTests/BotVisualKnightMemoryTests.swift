import XCTest
@testable import HollowKnightVision

final class BotVisualKnightMemoryTests: XCTestCase {
    private func sample(_ time: Double, x: Double? = nil, y: Double = 0.3,
                        camera: Double? = 0, generation: UInt64 = 1, room: Int = 1,
                        issues: [String]? = nil, floor: ClosedRange<Double>? = nil,
                        seenAt: Double? = nil, processingAge: Double = 0.01, version: String = "test-v1") -> BotObservation {
        var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
        if let x { features[0] = x; features[1] = y; features[2] = 0.03; features[3] = 0.06; features[4] = 0.9 }
        features[6] = 1
        if let floor {
            features.replaceSubrange(16..<21, with: [floor.lowerBound - features[0], floor.upperBound - features[0],
                0.19 - features[1], 1, 1])
        }
        features.replaceSubrange(96..<102, with: [0.6 - features[0], 0.3 - features[1], 0.03, 0.04, 0.8, 1])
        var result = BotObservation(type: "observation", captureTimestamp: time, capturedAt: time,
            availableAt: time + processingAge, generation: generation, roomID: room, roomRevision: 1,
            detectionTimestamp: seenAt ?? time, detectionFrameID: UInt64(time * 100), modelVersion: version,
            bestKnightConfidence: x == nil ? nil : 0.9, features: features,
            issues: issues ?? (x == nil ? ["missing-knight"] : []), terrainSource: "detected",
            knightSource: x == nil ? nil : "visual", enemySource: "visual")
        result.visualContext = .init(frameWidth: 640, frameHeight: 0.5625, cameraX: camera, cameraY: camera.map { _ in 0 })
        return result
    }

    func testWeakKnightOnJumpGuidesOnlyAnExistingPredictionWithoutRefreshingIt() throws {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.30, version: "v8.0"))
        _ = memory.resolve(sample(100.1, x: 0.32, version: "v8.0"))
        func cue(_ time: Double, x: Double = 0.34, confidence: Double = 0.3,
                 frameTime: Double? = nil) -> LiveObjectDetectionBatch {
            let id = UInt64(time * 100)
            let rect = CGRect(x: x - 0.015, y: 1 - 0.41 / 0.5625, width: 0.03, height: 0.06 / 0.5625)
            let detection = LiveObjectDetection(classIdentifier: "game.playable-knight", normalizedRect: rect,
                confidence: confidence, sourceFrameIdentifier: id, modelVersion: .init(major: 8, minor: 0))
            return .init(detections: [detection], sourceFrameIdentifier: id, sourceTimestamp: frameTime ?? time,
                captureGeneration: 1, inferenceDuration: 0)
        }
        let corrected = memory.resolve(sample(100.2), detections: cue(100.2))
        XCTAssertEqual(corrected.features[1], 0.38, accuracy: 0.00001)
        XCTAssertEqual(corrected.features[97], 0.3 - 0.38, accuracy: 0.00001)
        XCTAssertEqual(corrected.knightSource, "visual-predicted")
        XCTAssertEqual(corrected.knightPrediction?.lastSeenAt, 100.1)
        XCTAssertEqual(corrected.knightPrediction?.usedWeakDetection, true)
        XCTAssertNil(memory.resolve(sample(100.3), detections: cue(100.3, x: 0.82)).knightPrediction?.usedWeakDetection)
        XCTAssertNil(memory.resolve(sample(100.4), detections: cue(100.4, frameTime: 100.2)).knightPrediction?.usedWeakDetection)
        XCTAssertNil(memory.resolve(sample(100.5), detections: cue(100.5, confidence: 0.1)).knightPrediction?.usedWeakDetection)
        XCTAssertFalse(memory.resolve(sample(100.8), detections: cue(100.8)).isUsable)
        var cold = BotVisualKnightMemory()
        XCTAssertFalse(cold.resolve(sample(100.2), detections: cue(100.2)).isUsable)
    }

    func testContinuesMeasuredMotionAndRebasesCurrentTerrainAndEnemies() throws {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3))
        _ = memory.resolve(sample(100.1, x: 0.32))
        let predicted = memory.resolve(sample(100.2, floor: 0.1...0.7))
        XCTAssertTrue(predicted.isUsable)
        XCTAssertEqual(predicted.knightSource, "visual-predicted")
        XCTAssertEqual(predicted.features[0], 0.34, accuracy: 0.0001)
        XCTAssertEqual(predicted.features[16], -0.24, accuracy: 0.0001)
        XCTAssertEqual(predicted.features[18], -0.11, accuracy: 0.0001)
        XCTAssertEqual(predicted.features[96], 0.26, accuracy: 0.0001)
        XCTAssertLessThan(predicted.features[4], 0.9)
        XCTAssertEqual(try XCTUnwrap(predicted.knightPrediction).observedIssues, ["missing-knight"])
        XCTAssertEqual(try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(predicted)), predicted)
    }

    func testFreshPredictionsNeverExtendTheLastRealSightingDeadline() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3))
        _ = memory.resolve(sample(100.1, x: 0.32))
        for time in [100.2, 100.3, 100.4, 100.5, 100.6] {
            XCTAssertTrue(memory.resolve(sample(time)).isUsable)
        }
        XCTAssertFalse(memory.resolve(sample(100.71)).isUsable)
        let real = sample(100.8, x: 0.45)
        XCTAssertEqual(memory.resolve(real), real)
        XCTAssertTrue(memory.resolve(sample(100.9)).isUsable)
    }

    func testPolicyRejectsPredictionsWhoseEvidenceCannotDescribeThisFrame() throws {
        var memory = BotVisualKnightMemory(), history = BotObservationHistory()
        _ = memory.resolve(sample(100, x: 0.3))
        let row = memory.resolve(sample(100.1))
        let evidence = try XCTUnwrap(row.knightPrediction)
        func accepts(_ change: (inout BotObservation) -> Void) -> Bool {
            var edited = row
            change(&edited)
            return history.input(edited, previousAction: .neutral, now: edited.availableAt) != nil
        }
        func evidenceWith(lastSeenAt: Double = 100, issues: [String] = ["missing-knight"]) -> BotKnightPrediction {
            .init(lastSeenAt: lastSeenAt, age: evidence.age, uncertainty: evidence.uncertainty,
                  cameraCompensated: evidence.cameraCompensated, observedIssues: issues)
        }
        XCTAssertTrue(accepts { _ in })
        XCTAssertFalse(accepts { $0.knightPrediction = evidenceWith(lastSeenAt: 100.05) })
        XCTAssertFalse(accepts { $0.knightPrediction = evidenceWith(issues: []) })
        XCTAssertFalse(accepts { $0.knightPrediction = evidenceWith(issues: ["missing-knight", "room-transition"]) })
        XCTAssertFalse(accepts { $0.terrainSource = "human-authored" })
        XCTAssertFalse(accepts { $0.visualContext = nil })
        XCTAssertFalse(accepts { $0.visualContext = .init(frameWidth: 0, frameHeight: 0.56, cameraX: 0, cameraY: 0) })
        XCTAssertFalse(accepts { $0.visualContext = .init(frameWidth: 640, frameHeight: 0.56, cameraX: nil, cameraY: nil) })
        XCTAssertTrue(accepts { $0.knightPrediction = evidenceWith(issues: ["missing-knight", "stale-or-missing-detection"]) })
    }

    func testReusedDetectionCannotRefreshTheAnchor() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3))
        _ = memory.resolve(sample(100.1, x: 0.32))
        _ = memory.resolve(sample(100.2, x: 0.32, seenAt: 100.1))
        XCTAssertFalse(memory.resolve(sample(100.71)).isUsable)
    }

    func testUsesVisualCameraMotionWithoutPrivilegedModPositions() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3, camera: 0))
        _ = memory.resolve(sample(100.1, x: 0.3, camera: 0.02))
        let predicted = memory.resolve(sample(100.2, camera: 0.04))
        XCTAssertEqual(predicted.features[0], 0.3, accuracy: 0.0001)
        XCTAssertEqual(predicted.knightPrediction?.cameraCompensated, true)
    }

    func testMissingCameraHasAShorterPredictionWindow() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3, camera: nil))
        _ = memory.resolve(sample(100.1, x: 0.32, camera: nil))
        XCTAssertTrue(memory.resolve(sample(100.4, camera: nil)).isUsable)
        XCTAssertFalse(memory.resolve(sample(100.5, camera: nil)).isUsable)
    }

    func testDescendingEstimateLandsOnObservedFloorButNeverInventsAPitFloor() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3, y: 0.3))
        _ = memory.resolve(sample(100.1, x: 0.3, y: 0.25))
        let landed = memory.resolve(sample(100.2, floor: 0.1...0.7))
        XCTAssertEqual(landed.features[1], 0.22, accuracy: 0.0001)
        var pitMemory = BotVisualKnightMemory()
        _ = pitMemory.resolve(sample(100, x: 0.3, y: 0.3))
        _ = pitMemory.resolve(sample(100.1, x: 0.3, y: 0.25))
        XCTAssertEqual(pitMemory.resolve(sample(100.2, floor: 0.05...0.2)).features[1], 0.2, accuracy: 0.0001)
    }

    func testTransitionsCaptureChangesAndOtherFailuresInvalidateMemory() {
        for failure in [sample(100.2, issues: ["room-transition", "missing-knight"]),
                        sample(100.2, issues: ["missing-terrain-analysis", "missing-knight"]),
                        sample(100.2, generation: 2), sample(100.2, room: 2)] {
            var memory = BotVisualKnightMemory()
            _ = memory.resolve(sample(100, x: 0.3))
            _ = memory.resolve(sample(100.1, x: 0.32))
            XCTAssertEqual(memory.resolve(failure), failure)
            XCTAssertFalse(memory.resolve(sample(100.3)).isUsable)
        }
    }

    func testStaleCaptureCannotRenewInputThroughPrediction() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3))
        var stale = sample(100.2)
        stale = BotObservation(type: stale.type, captureTimestamp: stale.captureTimestamp, capturedAt: 99,
            availableAt: stale.availableAt, generation: stale.generation, roomID: stale.roomID,
            roomRevision: stale.roomRevision, detectionTimestamp: nil, detectionFrameID: nil,
            modelVersion: nil, bestKnightConfidence: nil, features: stale.features, issues: ["missing-knight"])
        XCTAssertFalse(memory.resolve(stale).isUsable)
        XCTAssertFalse(memory.resolve(sample(100.3)).isUsable)
    }

    func testHackerTelemetryIsNeverReplacedByVisualMemory() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3))
        var hacker = sample(100.1, issues: ["missing-hacker-knight"])
        hacker.terrainSource = "human-authored"; hacker.knightSource = "hacker-frame"
        XCTAssertEqual(memory.resolve(hacker), hacker)
        XCTAssertFalse(memory.resolve(sample(100.2)).isUsable)
    }

    func testControlChangeRaisesUncertaintyWithoutInventingMeasuredMovement() throws {
        let right = try BotAction(values: [2, 0, 0, 0]), leftJump = try BotAction(values: [1, 0, 1, 0])
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3), previousAction: right)
        _ = memory.resolve(sample(100.1, x: 0.32), previousAction: right)
        let continued = memory.resolve(sample(100.2), previousAction: right)
        let changed = memory.resolve(sample(100.3), previousAction: leftJump)
        XCTAssertGreaterThan(try XCTUnwrap(changed.knightPrediction).uncertainty,
                             try XCTUnwrap(continued.knightPrediction).uncertainty)
    }

    func testSelectsFreshTerrainAroundTheEstimatedKnightInsteadOfTheMissingOrigin() {
        var memory = BotVisualKnightMemory()
        _ = memory.resolve(sample(100, x: 0.3))
        _ = memory.resolve(sample(100.1, x: 0.32))
        let predicted = memory.resolve(sample(100.2, floor: 0.01...0.1), terrain: [
            BotTerrainSegment(minimumX: 192, maximumX: 256, y: 128, confidence: 1)])
        XCTAssertEqual(predicted.features[16], -0.04, accuracy: 0.0001)
        XCTAssertEqual(predicted.features[17], 0.06, accuracy: 0.0001)
        XCTAssertEqual(predicted.features[18], -0.1, accuracy: 0.0001)
    }

    func testLateProcessingCannotExtendAnOtherwiseFreshPrediction() {
        var memory = BotVisualKnightMemory(), history = BotObservationHistory()
        _ = memory.resolve(sample(100, x: 0.3))
        _ = memory.resolve(sample(100.1, x: 0.32))
        let predicted = memory.resolve(sample(100.6))
        XCTAssertTrue(predicted.isUsable)
        XCTAssertEqual(predicted.knightPredictionDeadline, 100.7, accuracy: 0.0001)
        XCTAssertNotNil(history.input(predicted, previousAction: .neutral, now: 100.62))
        XCTAssertNil(history.input(predicted, previousAction: .neutral, now: 100.71))
        XCTAssertFalse(memory.resolve(sample(100.65, processingAge: 0.1)).isUsable)
    }

    func testGamePlayKeepsDrivingThroughOcclusionThenReleasesAndReacquires() throws {
        let policy = try BotNativePolicy(document: BotRuntimePolicyDocument(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: "Occlusion controller test", featureNames: BotObservationContract.legacyFeatureNames,
            historyLength: 4, actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "test",
            layers: [BotRuntimeLayer(inputSize: 576, outputSize: 10, activation: "linear",
                weights: .init(repeating: 0, count: 5760), biases: [0, 0, 1, 1, 0, 0, 1, 0, 1, 0])]))
        var now = 100.0, memory = BotVisualKnightMemory(), actions = [BotAction](), deadlines = [Double]()
        var releases = 0
        let controller = BotPlaybackController(policy: policy, domain: .gameplay, now: { now }, hasFocus: { true })
        controller.configure(canContinue: { true }, begin: { UUID() }, apply: { action, deadline, _ in
            actions.append(action); deadlines.append(deadline); return true
        }, neutralize: { _ in releases += 1 }, end: { _ in })
        controller.start(domain: .gameplay)
        defer { controller.stop() }
        func push(_ raw: BotObservation) {
            now = raw.availableAt + 0.01
            controller.observe(memory.resolve(raw, previousAction: controller.lastAction))
            let delivered = expectation(description: "Main queue delivered observation")
            DispatchQueue.main.async { delivered.fulfill() }
            wait(for: [delivered], timeout: 1)
        }
        push(sample(100, x: 0.3)); push(sample(100.1, x: 0.32)); push(sample(100.2))
        XCTAssertEqual(actions.count, 3); XCTAssertEqual(releases, 0)
        XCTAssertFalse(controller.isWaiting); XCTAssertTrue(controller.status.contains("estimating Knight"))
        XCTAssertEqual(controller.lastAction.values, [2, 0, 0, 0])
        XCTAssertEqual(controller.predictedKnightDecisionCount, 1)
        for time in [100.3, 100.4, 100.5, 100.6] { push(sample(time)) }
        XCTAssertEqual(deadlines.last ?? 0, 100.7, accuracy: 0.0001)
        push(sample(100.71))
        XCTAssertTrue(controller.isWaiting); XCTAssertEqual(controller.lastAction, .neutral)
        XCTAssertEqual(actions.count, 7); XCTAssertEqual(releases, 1)
        push(sample(100.8, x: 0.45))
        XCTAssertFalse(controller.isWaiting); XCTAssertEqual(actions.count, 8)
        XCTAssertFalse(controller.status.contains("estimating Knight"))
    }
}
