import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class InputPathRecorderTests: XCTestCase {
    func testRecordsDeduplicatedTransitionsAndClosesHeldButtons() throws {
        let recorder = InputPathRecorder()
        let identifier = UUID(uuidString: "2B87556A-6D7A-4183-81A1-F93784E4290E")!
        let createdAt = Date(timeIntervalSince1970: 1_700_000_000)

        let checkpoint = makeCheckpoint()
        XCTAssertTrue(recorder.start(
            at: 100,
            createdAt: createdAt,
            id: identifier,
            startCheckpoint: checkpoint
        ))
        XCTAssertFalse(recorder.start(at: 101))
        recorder.recordInput(button: .right, isPressed: true, at: 100.1)
        recorder.recordInput(button: .right, isPressed: true, at: 100.2)
        recorder.recordInput(button: .up, isPressed: true, at: 100.4)
        recorder.recordInput(button: .right, isPressed: false, at: 101)

        var tracking = GroundHypothesisTrackingResult.empty
        tracking.cameraPosition = CGPoint(x: 42, y: -7)
        tracking.poseVerified = true
        tracking.hasConfirmedGround = true
        recorder.recordTracking(
            tracking,
            captureTimestamp: 7_777,
            groundTrackingMilliseconds: 4.5,
            floorlessCoarsePosition: CGPoint(x: 50, y: -8),
            floorlessMaskedPosition: CGPoint(x: 48, y: -9),
            floorlessExpectedDirection: .right,
            floorlessSelectionSource: "maskedRegistration",
            observedAt: 101.25
        )
        let truth = makeGroundTruth(sequence: 7)
        recorder.recordGroundTruth(truth, observedAt: 101.3)
        recorder.recordCoarseMotion(
            captureTimestamp: 7_778,
            renderedGameFrame: 88,
            presentedCameraPosition: CGPoint(x: 44, y: -6),
            coarseCameraPosition: CGPoint(x: 43.5, y: -6.5),
            estimate: LowResolutionRoomMotionEstimate(
                direction: .right, confidence: 0.8, screenShift: -1
            ),
            placeMatch: LowResolutionPlaceMatch(
                keyframeID: 4,
                cameraPosition: CGPoint(x: 43.5, y: -6.5),
                score: 0.12,
                margin: 0.19
            ),
            motionGrid: LowResolutionMotionGrid(
                width: 2,
                height: 2,
                luma: [1, 2, 3, 4]
            ),
            isControlling: true,
            isTransitioning: false,
            roomID: 2,
            roomRevision: 3,
            coarsePoseRevision: 42,
            signalMeanPeak: 12,
            signalVisibleFraction: 0.2,
            observedAt: 101.35
        )

        let path = try XCTUnwrap(recorder.stop(at: 102))
        XCTAssertFalse(recorder.isRecording)
        XCTAssertEqual(path.id, identifier)
        XCTAssertEqual(path.createdAt, createdAt)
        XCTAssertEqual(path.duration, 2, accuracy: 0.000_001)
        XCTAssertEqual(path.startCheckpoint, checkpoint)
        XCTAssertEqual(
            path.runtimeMetadata?["lowResolutionFrameTrace"],
            "64x36-20hz-measured-ground-reliability-v3"
        )
        XCTAssertEqual(
            path.runtimeMetadata?["lowResolutionMotionBridge"],
            "tentative-transition-handoff-v14"
        )
        XCTAssertEqual(
            path.runtimeMetadata?["pathPlaybackPreparation"],
            "purge-single-restore-settle-repurge-v2"
        )
        XCTAssertEqual(
            path.runtimeMetadata?["groundRelocalization"],
            "floorless-background-search-continuity-v1"
        )
        XCTAssertEqual(path.events.map(\.button), [.right, .up, .right, .up])
        XCTAssertEqual(
            path.events.map(\.transition),
            [.pressed, .pressed, .released, .released]
        )
        XCTAssertEqual(path.events[0].offset, 0.1, accuracy: 0.000_001)
        XCTAssertEqual(path.events[1].offset, 0.4, accuracy: 0.000_001)
        XCTAssertEqual(path.events[2].offset, 1, accuracy: 0.000_001)
        XCTAssertEqual(path.events[3].offset, 2, accuracy: 0.000_001)
        let sample = try XCTUnwrap(path.trackingSamples.first)
        XCTAssertEqual(sample.offset, 1.25, accuracy: 0.000_001)
        XCTAssertEqual(sample.captureTimestamp, 7_777)
        XCTAssertEqual(sample.cameraX, 42)
        XCTAssertEqual(sample.cameraY, -7)
        XCTAssertTrue(sample.poseVerified)
        XCTAssertTrue(sample.hasConfirmedGround)
        XCTAssertEqual(sample.groundTrackingMilliseconds, 4.5)
        XCTAssertEqual(sample.floorlessCoarseX, 50)
        XCTAssertEqual(sample.floorlessCoarseY, -8)
        XCTAssertEqual(sample.floorlessMaskedX, 48)
        XCTAssertEqual(sample.floorlessMaskedY, -9)
        XCTAssertEqual(
            sample.floorlessExpectedDirection,
            VisualRoomDirection.right.rawValue
        )
        XCTAssertEqual(sample.floorlessSelectionSource, "maskedRegistration")
        XCTAssertEqual(path.unverifiedSampleCount, 0)
        XCTAssertEqual(path.globalCorrectionCount, 0)
        let recordedTruth = try XCTUnwrap(path.groundTruthTrace.first)
        XCTAssertEqual(recordedTruth.offset, 1.3, accuracy: 0.000_001)
        XCTAssertEqual(recordedTruth.receivedTimestamp, 101.3)
        XCTAssertEqual(recordedTruth.sample, truth)
        let coarse = try XCTUnwrap(path.coarseMotionTrace.first)
        XCTAssertEqual(coarse.offset, 1.35, accuracy: 0.000_001)
        XCTAssertEqual(coarse.renderedGameFrame, 88)
        XCTAssertEqual(coarse.presentedCameraX, 44)
        XCTAssertEqual(coarse.coarseCameraX, 43.5)
        XCTAssertEqual(coarse.direction, VisualRoomDirection.right.rawValue)
        XCTAssertEqual(coarse.screenShift, -1)
        XCTAssertEqual(coarse.placeMatchKeyframeID, 4)
        XCTAssertEqual(coarse.placeMatchScore, 0.12)
        XCTAssertEqual(coarse.placeMatchMargin, 0.19)
        XCTAssertTrue(coarse.isControlling)
        XCTAssertEqual(coarse.roomID, 2)
        XCTAssertEqual(coarse.coarsePoseRevision, 42)
        let lowResolution = try XCTUnwrap(path.lowResolutionFrameTrace.first)
        XCTAssertEqual(lowResolution.offset, 1.35, accuracy: 0.000_001)
        XCTAssertEqual(lowResolution.renderedGameFrame, 88)
        XCTAssertEqual(lowResolution.roomID, 2)
        XCTAssertEqual(lowResolution.width, 2)
        XCTAssertEqual(lowResolution.height, 2)
        XCTAssertEqual(Array(lowResolution.luma), [1, 2, 3, 4])
        XCTAssertFalse(lowResolution.groundTrackingReliable)
        let decodedPath = try JSONDecoder().decode(
            RecordedInputPath.self,
            from: JSONEncoder().encode(path)
        )
        let decodedLowResolution = try XCTUnwrap(
            decodedPath.lowResolutionFrameTrace.first
        )
        XCTAssertEqual(decodedLowResolution, lowResolution)
        XCTAssertNil(recorder.stop(at: 103))
    }

    func testPathWithoutCaptureDiagnosticsRemainsReadable() throws {
        let path = RecordedInputPath(
            id: UUID(), createdAt: Date(), duration: 1,
            events: [], trackingSamples: []
        )
        let encoded = try JSONEncoder().encode(path)
        var legacy = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        legacy.removeValue(forKey: "coarseMotionSamples")
        legacy.removeValue(forKey: "lowResolutionFrames")
        let decoded = try JSONDecoder().decode(
            RecordedInputPath.self,
            from: JSONSerialization.data(withJSONObject: legacy)
        )
        XCTAssertTrue(decoded.coarseMotionTrace.isEmpty)
        XCTAssertTrue(decoded.lowResolutionFrameTrace.isEmpty)
    }

    func testLowResolutionFrameTraceIsCappedAtTwentyHertz() throws {
        let recorder = InputPathRecorder()
        XCTAssertTrue(recorder.start(at: 100))
        let grid = LowResolutionMotionGrid(
            width: 2,
            height: 2,
            luma: [1, 2, 3, 4]
        )
        func record(at timestamp: TimeInterval) {
            recorder.recordCoarseMotion(
                captureTimestamp: timestamp,
                renderedGameFrame: nil,
                presentedCameraPosition: .zero,
                coarseCameraPosition: nil,
                estimate: nil,
                motionGrid: grid,
                isControlling: false,
                isTransitioning: false,
                roomID: 0,
                roomRevision: 0,
                signalMeanPeak: 12,
                signalVisibleFraction: 0.2,
                observedAt: timestamp
            )
        }
        record(at: 100)
        record(at: 100.02)
        record(at: 100.051)
        let path = try XCTUnwrap(recorder.stop(at: 101))
        XCTAssertEqual(path.coarseMotionTrace.count, 3)
        XCTAssertEqual(path.lowResolutionFrameTrace.count, 2)
        XCTAssertEqual(path.lowResolutionFrameTrace[0].offset, 0, accuracy: 0.000_001)
        XCTAssertEqual(
            path.lowResolutionFrameTrace[1].offset,
            0.051,
            accuracy: 0.000_001
        )
    }

    func testLowResolutionReliabilityUsesOnlyExactCompletedGroundSolve() throws {
        let recorder = InputPathRecorder()
        XCTAssertTrue(recorder.start(at: 100))
        let grid = LowResolutionMotionGrid(
            width: 2,
            height: 2,
            luma: [1, 2, 3, 4]
        )
        func recordFrame(captureTimestamp: TimeInterval) {
            recorder.recordCoarseMotion(
                captureTimestamp: captureTimestamp,
                renderedGameFrame: nil,
                presentedCameraPosition: .zero,
                coarseCameraPosition: nil,
                estimate: nil,
                motionGrid: grid,
                isControlling: false,
                isTransitioning: false,
                roomID: 0,
                roomRevision: 0,
                signalMeanPeak: 12,
                signalVisibleFraction: 0.2,
                observedAt: captureTimestamp
            )
        }

        recordFrame(captureTimestamp: 100)
        var reliable = GroundHypothesisTrackingResult.empty
        reliable.poseVerified = true
        reliable.hasConfirmedGround = true
        reliable.localTextureSupport = 6
        recorder.recordTracking(
            reliable,
            captureTimestamp: 100,
            observedAt: 100.01
        )
        recordFrame(captureTimestamp: 100.051)
        recorder.recordTracking(
            .empty,
            captureTimestamp: 100.051,
            observedAt: 100.06
        )
        // A successful unsampled solve cannot promote a neighboring frame.
        recorder.recordTracking(
            reliable,
            captureTimestamp: 100.07,
            observedAt: 100.08
        )

        let path = try XCTUnwrap(recorder.stop(at: 101))
        XCTAssertEqual(
            path.lowResolutionFrameTrace.map(\.groundTrackingReliable),
            [true, false]
        )
    }

    func testCoarseMotionWithoutPlaceMatchFieldsRemainsReadable() throws {
        let recorder = InputPathRecorder()
        XCTAssertTrue(recorder.start(at: 20))
        recorder.recordCoarseMotion(
            captureTimestamp: 20.1,
            renderedGameFrame: nil,
            presentedCameraPosition: .zero,
            coarseCameraPosition: nil,
            estimate: nil,
            isControlling: false,
            isTransitioning: false,
            roomID: 0,
            roomRevision: 0,
            signalMeanPeak: 12,
            signalVisibleFraction: 0.2,
            observedAt: 20.2
        )
        let path = try XCTUnwrap(recorder.stop(at: 21))
        let encoded = try JSONEncoder().encode(path.coarseMotionTrace[0])
        var legacy = try XCTUnwrap(
            JSONSerialization.jsonObject(with: encoded) as? [String: Any]
        )
        legacy.removeValue(forKey: "placeMatchKeyframeID")
        legacy.removeValue(forKey: "placeMatchScore")
        legacy.removeValue(forKey: "placeMatchMargin")
        legacy.removeValue(forKey: "coarsePoseRevision")
        let decoded = try JSONDecoder().decode(
            RecordedCoarseMotionSample.self,
            from: JSONSerialization.data(withJSONObject: legacy)
        )
        XCTAssertNil(decoded.placeMatchKeyframeID)
        XCTAssertNil(decoded.placeMatchScore)
        XCTAssertNil(decoded.placeMatchMargin)
        XCTAssertNil(decoded.coarsePoseRevision)
    }

    func testRecordsUnverifiedTrackingForLaterFailureAnalysis() throws {
        let recorder = InputPathRecorder()
        XCTAssertTrue(recorder.start(at: 20))
        recorder.recordTracking(.empty, captureTimestamp: 19.8, observedAt: 20.5)
        let path = try XCTUnwrap(recorder.stop(at: 21))

        XCTAssertEqual(path.trackingSamples.count, 1)
        XCTAssertEqual(path.unverifiedSampleCount, 1)
        XCTAssertFalse(path.trackingSamples[0].poseVerified)
        XCTAssertNil(path.trackingSamples[0].cameraX)
    }

    func testAtlasAdmissionBelongsToExactCaptureAndOlderSamplesRemainReadable() throws {
        let recorder = InputPathRecorder()
        XCTAssertTrue(recorder.start(at: 20))
        recorder.recordTracking(.empty, captureTimestamp: 20.1,
            placementRecoveryState: "verifying", observedAt: 20.2)
        recorder.recordAtlasAdmission(captureTimestamp: 20.1, allowed: false)
        recorder.recordAtlasAdmission(captureTimestamp: 20.0, allowed: true)
        let path = try XCTUnwrap(recorder.stop(at: 21))
        XCTAssertEqual(path.trackingSamples[0].placementRecoveryState, "verifying")
        XCTAssertEqual(path.trackingSamples[0].atlasWriteAllowed, false)
        let encoded = try JSONEncoder().encode(path.trackingSamples[0])
        var legacy = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        legacy.removeValue(forKey: "placementRecoveryState")
        legacy.removeValue(forKey: "atlasWriteAllowed")
        let decoded = try JSONDecoder().decode(RecordedTrackingSample.self,
            from: JSONSerialization.data(withJSONObject: legacy))
        XCTAssertNil(decoded.placementRecoveryState)
        XCTAssertNil(decoded.atlasWriteAllowed)
    }

    func testStoreRoundTripsReplayReadyPath() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = InputPathStore(rootURL: root)
        let original = RecordedInputPath(
            id: UUID(),
            createdAt: Date(timeIntervalSince1970: 1_700_123_456),
            duration: 3.5,
            startCheckpoint: makeCheckpoint(),
            events: [
                RecordedInputPathEvent(offset: 0, button: .left, transition: .pressed),
                RecordedInputPathEvent(offset: 3.5, button: .left, transition: .released),
            ],
            trackingSamples: []
        )

        let url = try store.save(original)
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
        XCTAssertEqual(try store.load(from: url), original)
    }

    func testRecordedButtonsRoundTripBridgeBits() {
        for button in RecordedGameButton.allCases {
            XCTAssertEqual(RecordedGameButton.from(button.bridgeButton), button)
        }
        XCTAssertNil(RecordedGameButton.from([.left, .right]))
    }

    func testPlaybackValidationAllowsOverlappingBalancedKeys() {
        let path = RecordedInputPath(
            id: UUID(),
            createdAt: Date(),
            duration: 2,
            events: [
                RecordedInputPathEvent(offset: 0.1, button: .right, transition: .pressed),
                RecordedInputPathEvent(offset: 0.4, button: .up, transition: .pressed),
                RecordedInputPathEvent(offset: 1.2, button: .right, transition: .released),
                RecordedInputPathEvent(offset: 1.5, button: .up, transition: .released),
            ],
            trackingSamples: []
        )
        XCTAssertTrue(GameControlForwarder.isValidPlaybackPath(path))

        let duplicatePress = RecordedInputPath(
            id: UUID(),
            createdAt: Date(),
            duration: 2,
            events: [
                RecordedInputPathEvent(offset: 0.1, button: .right, transition: .pressed),
                RecordedInputPathEvent(offset: 0.2, button: .right, transition: .pressed),
                RecordedInputPathEvent(offset: 1, button: .right, transition: .released),
            ],
            trackingSamples: []
        )
        XCTAssertFalse(GameControlForwarder.isValidPlaybackPath(duplicatePress))
    }

    func testPlaybackValidationAcceptsRecordedWorldPoseTrace() {
        let truth = makeGroundTruth(sequence: 1)
        let path = RecordedInputPath(
            id: UUID(),
            createdAt: Date(),
            duration: 1,
            events: [],
            trackingSamples: [],
            groundTruthSamples: [
                RecordedGroundTruthSample(
                    offset: 0.25,
                    receivedTimestamp: 100.25,
                    sample: truth
                ),
                RecordedGroundTruthSample(
                    offset: 0.75,
                    receivedTimestamp: 100.75,
                    sample: makeGroundTruth(sequence: 2)
                ),
            ]
        )
        XCTAssertTrue(GameControlForwarder.isValidPlaybackPath(path))

        let outOfOrder = RecordedInputPath(
            id: UUID(),
            createdAt: Date(),
            duration: 1,
            events: [],
            trackingSamples: [],
            groundTruthSamples: Array(path.groundTruthTrace.reversed())
        )
        XCTAssertFalse(GameControlForwarder.isValidPlaybackPath(outOfOrder))
    }

    func testPlaybackValidationAcceptsPlayableRoomsAcrossHeroLessCutscene() {
        let path = RecordedInputPath(
            id: UUID(),
            createdAt: Date(),
            duration: 2,
            startCheckpoint: makeCheckpoint(),
            events: [],
            trackingSamples: [],
            groundTruthSamples: [
                RecordedGroundTruthSample(
                    offset: 0.25,
                    receivedTimestamp: 100.25,
                    sample: makeGroundTruth(
                        sequence: 1,
                        sceneName: "Tutorial_01"
                    )
                ),
                RecordedGroundTruthSample(
                    offset: 1,
                    receivedTimestamp: 101,
                    sample: makeGroundTruth(
                        sequence: 2,
                        sceneName: "Opening_Cutscene",
                        heroAvailable: false
                    )
                ),
                RecordedGroundTruthSample(
                    offset: 1.75,
                    receivedTimestamp: 101.75,
                    sample: makeGroundTruth(
                        sequence: 3,
                        sceneName: "Tutorial_02"
                    )
                ),
            ]
        )

        XCTAssertTrue(GameControlForwarder.isValidPlaybackPath(path))
    }

    func testPlaybackCommandAcceptsOnlySavedFileNamesAndBoundedIteration() {
        let name = "path-20260919-204216-test.json"
        XCTAssertEqual(
            GamePathPlaybackCommand(serialized: "replay-path:\(name):5"),
            GamePathPlaybackCommand(fileName: name, iteration: 5)
        )
        XCTAssertNil(GamePathPlaybackCommand(serialized: "replay-path:../secret.json:1"))
        XCTAssertNil(GamePathPlaybackCommand(serialized: "replay-path:\(name):0"))
        XCTAssertEqual(
            GamePathPlaybackCommand(serialized: "replay-path:\(name):101"),
            GamePathPlaybackCommand(fileName: name, iteration: 101)
        )
        XCTAssertNil(GamePathPlaybackCommand(serialized: "replay-path:\(name):10001"))
    }

    private func makeCheckpoint() -> RecordedGameCheckpoint {
        RecordedGameCheckpoint(
            sceneName: "Crossroads_01",
            heroX: 10, heroY: 4, heroZ: 0,
            velocityX: 0, velocityY: 0,
            facingRight: true, grounded: true,
            cameraX: 11, cameraY: 6, cameraZ: -38.1,
            cameraTargetX: 11, cameraTargetY: 6, cameraTargetZ: 0
        )
    }

    private func makeGroundTruth(
        sequence: UInt64,
        sceneName: String = "Tutorial_01",
        heroAvailable: Bool = true
    ) -> ReceiverGroundTruthSample {
        ReceiverGroundTruthSample(
            version: 2, type: "groundTruth", sessionID: UUID(), sequence: sequence,
            unityFrame: 99, unityRealtime: 12.5, sceneName: sceneName,
            heroAvailable: heroAvailable, heroX: 36.25, heroY: 11.5, heroZ: 0.004,
            velocityX: -2, velocityY: 0, facingRight: false, grounded: true,
            cameraAvailable: true, cameraX: 37.25, cameraY: 14.1, cameraZ: -38.1,
            cameraTargetX: 36.25, cameraTargetY: 14.1, cameraTargetZ: 0.004,
            orthographicSize: 14.0625,
            pixelsPerWorldUnitX: nil, pixelsPerWorldUnitY: nil,
            heroScreenX: nil, heroScreenY: nil,
            projectionPixelWidth: nil, projectionPixelHeight: nil,
            screenWidth: 1920, screenHeight: 1080
        )
    }
}
