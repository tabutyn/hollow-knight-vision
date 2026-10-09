import XCTest
@testable import HollowKnightVision

final class InputBridgeTests: XCTestCase {
    func testPlayerTestStateNormalizesBoundsAndRandomizesValidValues() {
        let normalized = PlayerTestState(
            maxHealth: 20, health: 0, lifebloodSeed: 40,
            mana: 300, extraManaSlots: 30, geo: -5
        ).normalized
        XCTAssertEqual(normalized.maxHealth, 9)
        XCTAssertEqual(normalized.health, 1)
        XCTAssertEqual(normalized.lifebloodSeed, 9)
        XCTAssertEqual(normalized.mana, 198)
        XCTAssertEqual(normalized.extraManaSlots, 3)
        XCTAssertEqual(normalized.geo, 0)
        XCTAssertTrue(normalized.invincible)

        var generator = SystemRandomNumberGenerator()
        for _ in 0..<100 {
            let state = PlayerTestState.randomized(
                invincible: false,
                using: &generator
            )
            XCTAssertEqual(state, state.normalized)
            XCTAssertTrue((0...3).contains(state.extraManaSlots))
            XCTAssertFalse(state.invincible)
        }
    }

    func testPlayerOpsWireRoundTrip() throws {
        let sessionID = UUID()
        let state = PlayerTestState(
            maxHealth: 8, health: 3, lifebloodSeed: 2,
            mana: 112, extraManaSlots: 1, geo: 987,
            invincible: false
        )
        let command = ReceiverPlayerOpsCommand.apply(sessionID: sessionID, state: state)
        let encoded = try InputBridgeWireCodec.encode(command)
        XCTAssertEqual(encoded.last, 0x0A)
        XCTAssertEqual(
            try JSONDecoder().decode(ReceiverPlayerOpsCommand.self, from: encoded.dropLast()),
            command
        )
        let acknowledgement = """
        {"version":2,"type":"playerOpsAck","sessionID":"\(sessionID.uuidString)","commandID":"\(command.commandID.uuidString)","accepted":true,"state":{"maxHealth":8,"health":3,"lifebloodSeed":2,"mana":112,"extraManaSlots":1,"geo":987,"invincible":false},"enemiesRestored":4}
        """.data(using: .utf8)!
        let decoded = try InputBridgeWireCodec.decodePlayerOpsAcknowledgement(
            line: acknowledgement
        )
        XCTAssertEqual(decoded.state, state)
        XCTAssertEqual(decoded.enemiesRestored, 4)
    }
    func testPhysicalReconciliationDoesNotCancelAVisibleLocalPressWhenGlobalPollingIsUnavailable() {
        var reconciler = PhysicalKeyReconciler()

        XCTAssertEqual(
            reconciler.releasedKeyCodes(from: [124], isPhysicallyHeld: { _ in false }),
            []
        )
        XCTAssertTrue(reconciler.globallyObservedKeyCodes.isEmpty)
    }

    func testPhysicalReconciliationReleasesOnlyAfterObservingTheHeldKey() {
        var reconciler = PhysicalKeyReconciler()

        XCTAssertEqual(
            reconciler.releasedKeyCodes(from: [124], isPhysicallyHeld: { _ in true }),
            []
        )
        XCTAssertEqual(reconciler.globallyObservedKeyCodes, [124])
        XCTAssertEqual(
            reconciler.releasedKeyCodes(from: [124], isPhysicallyHeld: { _ in false }),
            [124]
        )
    }

    func testSenderEmitsOrderedCompleteTransitionsAndNeutralRelease() throws {
        let sessionID = UUID()
        var sender = InputBridgeSenderState(sessionID: sessionID)

        let enabled = sender.setEnabled(true)
        let left = try XCTUnwrap(sender.setHeld(.left, isHeld: true))
        let jump = try XCTUnwrap(sender.setHeld(.actionZ, isHeld: true))
        let release = sender.releaseAll()

        XCTAssertEqual([enabled.sequence, left.sequence, jump.sequence, release.sequence], [0, 1, 2, 3])
        XCTAssertEqual(left.heldButtons, [.left])
        XCTAssertEqual(jump.heldButtons, [.left, .actionZ])
        XCTAssertFalse(release.enabled)
        XCTAssertEqual(release.heldButtons, [])
    }

    func testFreshPressEnablesAndHoldsInOneSnapshot() {
        let sessionID = UUID()
        var sender = InputBridgeSenderState(sessionID: sessionID)

        let firstPress = sender.beginFreshPress(.right)
        XCTAssertEqual(firstPress.sequence, 0)
        XCTAssertTrue(firstPress.enabled)
        XCTAssertEqual(firstPress.heldButtons, [.right])
    }

    func testWireCodecRoundTripsVersionedSnapshot() throws {
        let snapshot = InputBridgeSnapshot(
            sessionID: UUID(), sequence: 9, enabled: true,
            heldButtons: [.up, .actionZ, .inventory, .pauseMenu]
        )
        XCTAssertEqual(snapshot.heldButtons.rawValue, 424)
        let encoded = try InputBridgeWireCodec.encode(snapshot)
        XCTAssertEqual(encoded.last, 0x0A)
        XCTAssertEqual(try InputBridgeWireCodec.decodeSnapshot(line: encoded.dropLast()), snapshot)
    }

    func testPauseControlMessagesUseNegotiatedVersionTwoProtocol() throws {
        let sessionID = UUID()
        let hello = try InputBridgeWireCodec.encode(ReceiverCapabilityRequest(sessionID: sessionID))
        XCTAssertEqual(hello.last, 0x0A)
        XCTAssertEqual(try InputBridgeWireCodec.messageType(line: hello.dropLast()), "hello")

        let pause = ReceiverPauseCommand.pause(sessionID: sessionID, leaseMilliseconds: 2_000)
        let encodedPause = try InputBridgeWireCodec.encode(pause)
        XCTAssertEqual(try InputBridgeWireCodec.messageType(line: encodedPause.dropLast()), "pause")
        let decodedPause = try JSONDecoder().decode(ReceiverPauseCommand.self, from: encodedPause.dropLast())
        XCTAssertEqual(decodedPause, pause)
        XCTAssertEqual(decodedPause.version, 2)

        let resume = ReceiverPauseCommand.resume(sessionID: sessionID)
        let encodedResume = try InputBridgeWireCodec.encode(resume)
        XCTAssertEqual(try InputBridgeWireCodec.messageType(line: encodedResume.dropLast()), "resume")
        XCTAssertNil(try JSONDecoder().decode(ReceiverPauseCommand.self, from: encodedResume.dropLast()).leaseMilliseconds)
    }

    func testPauseAcknowledgementsDecodeByWireType() throws {
        let sessionID = UUID()
        let commandID = UUID()
        let capabilities = """
        {"version":2,"type":"capabilitiesAck","sessionID":"\(sessionID.uuidString)","capabilities":["input-state-v1","pause-lease-v1","menu-shortcuts-v1","player-checkpoint-v1"],"pauseLeaseMilliseconds":2000}
        """.data(using: .utf8)!
        let decodedCapabilities = try InputBridgeWireCodec.decodeCapabilitiesAcknowledgement(line: capabilities)
        XCTAssertEqual(decodedCapabilities.sessionID, sessionID)
        XCTAssertTrue(decodedCapabilities.capabilities.contains(ReceiverControlProtocol.pauseCapability))
        XCTAssertTrue(decodedCapabilities.capabilities.contains(ReceiverControlProtocol.menuShortcutsCapability))
        XCTAssertTrue(decodedCapabilities.capabilities.contains(ReceiverControlProtocol.playerCheckpointCapability))

        let pauseAck = """
        {"version":2,"type":"pauseAck","sessionID":"\(sessionID.uuidString)","commandID":"\(commandID.uuidString)","paused":true}
        """.data(using: .utf8)!
        let decodedPause = try InputBridgeWireCodec.decodePauseAcknowledgement(line: pauseAck)
        XCTAssertEqual(decodedPause.commandID, commandID)
        XCTAssertTrue(decodedPause.paused)
    }

    func testCheckpointControlMessagesRoundTrip() throws {
        let sessionID = UUID()
        let checkpoint = RecordedGameCheckpoint(
            sceneName: "Crossroads_01",
            heroX: 12.5, heroY: 7.25, heroZ: 0,
            velocityX: 0, velocityY: 0,
            facingRight: true, grounded: true,
            cameraX: 13, cameraY: 9, cameraZ: -38.1,
            cameraTargetX: 13, cameraTargetY: 9, cameraTargetZ: 0,
            isFirstGame: false,
            enteredTutorialFirstTime: false,
            visitedDirtmouth: false,
            visitedCrossroads: false,
            openedTown: false,
            openedCrossroads: false,
            scenesVisited: ["Tutorial_01"]
        )
        XCTAssertTrue(checkpoint.hasFiniteCoordinates)

        let capture = ReceiverCheckpointCommand.capture(sessionID: sessionID)
        let encodedCapture = try InputBridgeWireCodec.encode(capture)
        XCTAssertEqual(
            try InputBridgeWireCodec.messageType(line: encodedCapture.dropLast()),
            "captureCheckpoint"
        )
        XCTAssertNil(try JSONDecoder().decode(
            ReceiverCheckpointCommand.self,
            from: encodedCapture.dropLast()
        ).checkpoint)

        let restore = ReceiverCheckpointCommand.restore(
            sessionID: sessionID,
            checkpoint: checkpoint
        )
        let encodedRestore = try InputBridgeWireCodec.encode(restore)
        XCTAssertEqual(
            try JSONDecoder().decode(
                ReceiverCheckpointCommand.self,
                from: encodedRestore.dropLast()
            ),
            restore
        )

        let acknowledgement = """
        {"version":2,"type":"captureCheckpointAck","sessionID":"\(sessionID.uuidString)","commandID":"\(capture.commandID.uuidString)","accepted":true,"checkpoint":{"sceneName":"Crossroads_01","heroX":12.5,"heroY":7.25,"heroZ":0,"velocityX":0,"velocityY":0,"facingRight":true,"grounded":true,"cameraX":13,"cameraY":9,"cameraZ":-38.1,"cameraTargetX":13,"cameraTargetY":9,"cameraTargetZ":0,"isFirstGame":false,"enteredTutorialFirstTime":false,"visitedDirtmouth":false,"visitedCrossroads":false,"openedTown":false,"openedCrossroads":false,"scenesVisited":["Tutorial_01"]}}
        """.data(using: .utf8)!
        let decoded = try InputBridgeWireCodec.decodeCheckpointAcknowledgement(
            line: acknowledgement
        )
        XCTAssertTrue(decoded.accepted)
        XCTAssertEqual(decoded.checkpoint, checkpoint)
    }

    func testGroundTruthTelemetryDecodesAndProvidesProjectionScale() throws {
        let sessionID = UUID()
        let data = """
        {"version":2,"type":"groundTruth","sessionID":"\(sessionID.uuidString)","sequence":7,"unityFrame":99,"unityRealtime":12.5,"sceneName":"Tutorial_01","heroAvailable":true,"heroX":36.25,"heroY":11.5,"heroZ":0.004,"velocityX":-2,"velocityY":0,"facingRight":false,"grounded":true,"cameraAvailable":true,"cameraX":37.25,"cameraY":14.1,"cameraZ":-38.1,"cameraTargetX":36.25,"cameraTargetY":14.1,"cameraTargetZ":0.004,"orthographicSize":480,"pixelsPerWorldUnitX":38.4,"pixelsPerWorldUnitY":38.4,"heroScreenX":1000,"heroScreenY":420,"projectionPixelWidth":1920,"projectionPixelHeight":1080,"screenWidth":1920,"screenHeight":1080}
        """.data(using: .utf8)!

        let sample = try InputBridgeWireCodec.decodeGroundTruth(line: data)
        XCTAssertEqual(sample.sessionID, sessionID)
        XCTAssertEqual(sample.sceneName, "Tutorial_01")
        XCTAssertEqual(sample.cameraX, 37.25)
        XCTAssertTrue(sample.hasFiniteCoordinates)
        XCTAssertEqual(sample.pixelsPerWorldUnit(frameHeight: 360) ?? 0, 12.8, accuracy: 0.000_001)
    }

    func testPlayerPosePlaybackEncodesRecordedGroundTruth() throws {
        let sessionID = UUID()
        let sample = ReceiverGroundTruthSample(
            version: 2, type: "groundTruth", sessionID: UUID(), sequence: 19,
            unityFrame: 120, unityRealtime: 14.5, sceneName: "Tutorial_01",
            heroAvailable: true, heroX: 36.25, heroY: 11.5, heroZ: 0.004,
            velocityX: -2, velocityY: 0.25, facingRight: false, grounded: false,
            cameraAvailable: true, cameraX: 37.25, cameraY: 14.1, cameraZ: -38.1,
            cameraTargetX: 36.25, cameraTargetY: 14.1, cameraTargetZ: 0.004,
            orthographicSize: 14.0625,
            pixelsPerWorldUnitX: 38.4, pixelsPerWorldUnitY: 38.4,
            heroScreenX: 1000, heroScreenY: 420,
            projectionPixelWidth: 1920, projectionPixelHeight: 1080,
            screenWidth: 1920, screenHeight: 1080
        )
        let command = ReceiverPlayerPoseCommand(
            sessionID: sessionID,
            sequence: 7,
            sample: sample
        )
        let encoded = try InputBridgeWireCodec.encode(command)
        XCTAssertEqual(try InputBridgeWireCodec.messageType(line: encoded.dropLast()), "playerPose")
        let decoded = try JSONDecoder().decode(
            ReceiverPlayerPoseCommand.self,
            from: encoded.dropLast()
        )
        XCTAssertEqual(decoded, command)
        XCTAssertEqual(decoded.sessionID, sessionID)
        XCTAssertEqual(decoded.sequence, 7)
        XCTAssertEqual(decoded.heroX, sample.heroX)
        XCTAssertEqual(decoded.velocityY, sample.velocityY)
        XCTAssertEqual(decoded.sceneName, sample.sceneName)
    }

    func testAcknowledgementTrackerMeasuresOnlyMatchedStateChanges() {
        let sessionID = UUID()
        var tracker = InputAcknowledgementTracker()
        let first = InputBridgeSnapshot(sessionID: sessionID, sequence: 0, enabled: false, heldButtons: [])
        let heartbeat = InputBridgeSnapshot(sessionID: sessionID, sequence: 1, enabled: false, heldButtons: [])
        let press = InputBridgeSnapshot(sessionID: sessionID, sequence: 2, enabled: true, heldButtons: [.actionX])

        XCTAssertTrue(tracker.recordSent(first, at: 1.0))
        XCTAssertFalse(tracker.recordSent(heartbeat, at: 1.01))
        XCTAssertTrue(tracker.recordSent(press, at: 1.02))
        assertMatchedLatency(
            tracker.receive(InputBridgeAcknowledgement(sessionID: sessionID, sequence: 2, appliedButtons: [.actionX], enabled: true), at: 1.035),
            0.015
        )
        XCTAssertEqual(tracker.p95Latency ?? -1, 0.015, accuracy: 0.000_001)
    }

    func testAcknowledgementTrackerRejectsMismatchAndOldOrUnknownAcknowledgements() {
        let sessionID = UUID()
        var tracker = InputAcknowledgementTracker()
        let press = InputBridgeSnapshot(sessionID: sessionID, sequence: 3, enabled: true, heldButtons: [.left])
        XCTAssertTrue(tracker.recordSent(press, at: 1))

        XCTAssertEqual(
            tracker.receive(InputBridgeAcknowledgement(sessionID: sessionID, sequence: 3, appliedButtons: [.right], enabled: true), at: 1.02),
            .rejectedStateMismatch
        )
        XCTAssertEqual(
            tracker.receive(InputBridgeAcknowledgement(sessionID: UUID(), sequence: 3, appliedButtons: [.left], enabled: true), at: 1.03),
            .ignoredOldSession
        )
        XCTAssertEqual(
            tracker.receive(InputBridgeAcknowledgement(sessionID: sessionID, sequence: 2, appliedButtons: [.left], enabled: true), at: 1.04),
            .ignoredUnknownSequence
        )
        XCTAssertNil(tracker.p95Latency)
    }

    func testAcknowledgementTrackerBoundsPendingStatesAndCalculatesRollingP95() {
        let sessionID = UUID()
        var tracker = InputAcknowledgementTracker(maximumOutstanding: 2, maximumLatencySamples: 3)
        let first = InputBridgeSnapshot(sessionID: sessionID, sequence: 0, enabled: false, heldButtons: [])
        let press = InputBridgeSnapshot(sessionID: sessionID, sequence: 1, enabled: true, heldButtons: [.left])
        let release = InputBridgeSnapshot(sessionID: sessionID, sequence: 2, enabled: true, heldButtons: [])
        XCTAssertTrue(tracker.recordSent(first, at: 0))
        XCTAssertTrue(tracker.recordSent(press, at: 1))
        XCTAssertTrue(tracker.recordSent(release, at: 2))
        XCTAssertEqual(tracker.outstandingCount, 2)
        XCTAssertEqual(
            tracker.receive(InputBridgeAcknowledgement(sessionID: sessionID, sequence: 0, appliedButtons: [], enabled: false), at: 0.1),
            .ignoredUnknownSequence
        )
        assertMatchedLatency(tracker.receive(InputBridgeAcknowledgement(sessionID: sessionID, sequence: 1, appliedButtons: [.left], enabled: true), at: 1.010), 0.010)
        assertMatchedLatency(tracker.receive(InputBridgeAcknowledgement(sessionID: sessionID, sequence: 2, appliedButtons: [], enabled: true), at: 2.030), 0.030)
        XCTAssertEqual(tracker.p95Latency ?? -1, 0.030, accuracy: 0.000_001)
    }

    private func assertMatchedLatency(
        _ result: InputAcknowledgementTracker.Result,
        _ expected: TimeInterval,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        guard case let .matched(latency) = result else {
            return XCTFail("Expected matched acknowledgement, got \(result)", file: file, line: line)
        }
        XCTAssertEqual(latency, expected, accuracy: 0.000_001, file: file, line: line)
    }
}
