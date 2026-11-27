import XCTest
@testable import HollowKnightVision

final class BotPlaybackControllerTests: XCTestCase {
    func testBForwardsTheActorsFourButtonsAtEnemiesGeoAndAtlasFrontiers() throws {
        var document = try policy().document
        document.atlasPolicy = BotAtlasDirectionPolicy(schemaVersion: 1, checkpointSHA256: "test",
            layers: [BotRuntimeLayer(inputSize: 8, outputSize: 3, activation: "linear",
                weights: .init(repeating: 0, count: 24), biases: [0, 2, 0])])
        let model = try BotNativePolicy(document: document)
        var time = 100.0
        let bot = BotPlaybackController(policy: model, domain: .gameplay, now: { time }, hasFocus: { true })
        bot.configure(canContinue: { true }, begin: { UUID() }, apply: { _, _, _ in true },
                      neutralize: { _ in }, end: { _ in })
        bot.start(domain: .gameplay)
        defer { bot.stop() }
        var tick = 0
        func frame(_ enemies: [(Double, Double)], room: Int = 1, detectionLag: Double = 0, geoX: Double? = nil) {
            let t = 100 + Double(tick) / 10
            time = t + 0.01; tick += 1
            var f = [Double](repeating: 0, count: BotObservationContract.featureCount)
            f.replaceSubrange(0..<7, with: [0.5, 0.3, 0.025, 0.045, 1, 0, 1])
            for (slot, enemy) in enemies.enumerated() {
                let base = 96 + slot * 6
                f.replaceSubrange(base..<(base + 6), with: [enemy.0, enemy.1, 0.03, 0.04, 1, 1])
            }
            var row = BotObservation(type: "observation", captureTimestamp: t, capturedAt: t,
                availableAt: time, generation: 1, roomID: room, roomRevision: 1, detectionTimestamp: t - detectionLag,
                detectionFrameID: nil, modelVersion: nil, bestKnightConfidence: 1, features: f, issues: [],
                terrainSource: "detected", knightSource: "visual")
            row.geoCaches = geoX.map { [.init(x: $0, y: 0.3, width: 0.03, height: 0.03)] }
            row.atlasView = BotAtlasView(left: 1, right: 0, camera: .zero, width: 640,
                coveredCells: 1, earnedCells: 0, revision: 0, health: tick > 5 ? 4 : 5)
            bot.observe(row)
            let done = expectation(description: "decision")
            DispatchQueue.main.async { done.fulfill() }
            wait(for: [done], timeout: 1)
            XCTAssertEqual(bot.lastAction.values, [1, 0, 1, 0])
        }
        frame([])
        frame([(0.16, 0), (-0.33, 0)])
        frame([(0.04, 0)])
        frame([(-0.04, 0)])
        frame([], geoX: 0.53)
        frame([(-0.3, 0)], room: 2, geoX: 0.8)
        frame([], room: 2)
        XCTAssertEqual(bot.decisionCount, tick)
    }

    func testCombatUsesDemonstratedDodgeAndAimsAboveOrBelowKnight() throws {
        var combat = BotCombat()
        let above = BotCombat.Target(dx: 0.005, dy: 0.05, width: 0.02, height: 0.03,
                                     knightWidth: 0.025, knightHeight: 0.045)
        let below = BotCombat.Target(dx: 0.005, dy: -0.05, width: 0.02, height: 0.03,
                                     knightWidth: 0.025, knightHeight: 0.045)
        let first = try combat.action(above, actor: .neutral, learnedCombat: false, previous: .neutral, now: 10)
        XCTAssertEqual(first.values, [0, 1, 1, 1])
        let dodge = try BotAction(values: [1, 0, 1, 0])
        let second = try combat.action(below, actor: dodge, learnedCombat: true, previous: first, now: 10.1)
        XCTAssertEqual(second.values, [1, 2, 1, 0])
        let swing = try BotAction(values: [0, 2, 0, 1])
        XCTAssertEqual(try combat.action(below, actor: swing, learnedCombat: true, previous: second,
                                         now: 10.3).values, [0, 2, 0, 1])
    }

    func testSelectedGamePlayPolicyOnRecordedEnemySightings() throws {
        let env = ProcessInfo.processInfo.environment
        guard let path = env["HKV_COMBAT_GAMEPLAY_MODEL"], let clips = env["HKV_COMBAT_CLIP_DIRECTORY"] else {
            throw XCTSkip("Supply a Gameplay checkpoint and demonstration directory with enemies in reach.")
        }
        let policy = try BotNativePolicy.load(from: URL(fileURLWithPath: path))
        let folders = try FileManager.default.contentsOfDirectory(at: URL(fileURLWithPath: clips),
                                                                  includingPropertiesForKeys: nil)
        var sightings = 0, attacks = 0
        for folder in folders where folder.lastPathComponent.hasPrefix("demo-") {
            let contents = try String(contentsOf: folder.appendingPathComponent("events.jsonl"), encoding: .utf8)
            let rows = contents.split(separator: "\n").compactMap {
                try? JSONDecoder().decode(BotObservation.self, from: Data($0.utf8))
            }
            guard let first = rows.first, first.terrainSource == "detected" else { continue }
            var time = first.capturedAt
            let bot = BotPlaybackController(policy: policy, domain: .gameplay, now: { time }, hasFocus: { true })
            bot.configure(canContinue: { true }, begin: { UUID() }, apply: { _, _, _ in true },
                          neutralize: { _ in }, end: { _ in })
            bot.start(domain: .gameplay)
            for row in rows {
                time = row.availableAt
                let decisions = bot.decisionCount
                bot.observe(row)
                let done = expectation(description: "replay")
                DispatchQueue.main.async { done.fulfill() }
                wait(for: [done], timeout: 1)
                if decisions < bot.decisionCount, row.features[101] == 1 {
                    sightings += 1
                    if bot.lastAction.buttons.contains(.actionX) { attacks += 1 }
                }
            }
            bot.stop()
        }
        print("Recorded enemy sightings: \(sightings) accepted frames, model chose attack on \(attacks).")
        XCTAssertGreaterThan(sightings, 0)
    }

    func testTrainedGameplayPolicyCanMoveThroughSustainedOcclusionWithFreshTerrain() throws {
        var document = try policy().document
        document.supportsMaskedKnight = true
        var time = 100.0
        let player = BotPlaybackController(policy: try BotNativePolicy(document: document), domain: .gameplay,
                                          now: { time }, hasFocus: { true })
        var actions = [BotAction]()
        player.configure(canContinue: { true }, begin: { UUID() }, apply: { action, _, _ in actions.append(action); return true },
                         neutralize: { _ in }, end: { _ in })
        player.start(domain: .gameplay)
        defer { player.stop() }
        func frame(_ t: Double, visible: Bool = false) {
            time = t + 0.01
            var values = [Double](repeating: 0, count: BotObservationContract.featureCount)
            values[4] = visible ? 0.9 : 0
            values[6] = 1; values[20] = 1
            player.observe(BotObservation(type: "observation", captureTimestamp: t, capturedAt: t,
                availableAt: time, generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: t,
                detectionFrameID: 1, modelVersion: nil, bestKnightConfidence: nil, features: values,
                issues: visible ? [] : ["missing-knight"], terrainSource: "detected", knightSource: visible ? "visual" : nil))
            let drained = expectation(description: "frame delivered")
            DispatchQueue.main.async { drained.fulfill() }
            wait(for: [drained], timeout: 1)
        }
        frame(100)
        XCTAssertEqual(actions.count, 1); XCTAssertTrue(player.hasFreshObservation)
        for i in 1...53 { frame(100 + Double(i) / 10) }
        XCTAssertEqual(actions.count, 54)
        XCTAssertTrue(actions.allSatisfy { $0.buttons.contains(.left) && $0.buttons.contains(.actionZ) })
        XCTAssertFalse(player.isWaiting)
        time += 1
        XCTAssertFalse(player.hasFreshObservation)
        frame(107, visible: true)
        XCTAssertEqual(actions.count, 55)
    }

    func testNeutralOccludedActorRetainsControlAtKnownFrontier() throws {
        let model: BotNativePolicy
        let rows: [BotObservation]
        do {
            var doc = BotRuntimePolicyDocument(schemaVersion: 1, observationSchemaVersion: 1,
                modelName: "Neutral", featureNames: BotObservationContract.legacyFeatureNames, historyLength: 4,
                actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "test",
                layers: [BotRuntimeLayer(inputSize: 576, outputSize: 10, activation: "linear",
                    weights: .init(repeating: 0, count: 5760), biases: [1,0,0,1,0,0,1,0,1,0])])
            doc.supportsMaskedKnight = true
            doc.atlasPolicy = BotAtlasDirectionPolicy(schemaVersion: 1, checkpointSHA256: "test",
                layers: [BotRuntimeLayer(inputSize: 8, outputSize: 3, activation: "linear",
                    weights: .init(repeating: 0, count: 24), biases: [0,0,1])])
            model = try BotNativePolicy(document: doc)
            rows = (0..<15).map { tick in
                let t = 100 + Double(tick) / 10
                var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
                features[6] = 1; features[20] = 1
                return BotObservation(type: "observation", captureTimestamp: t, capturedAt: t,
                    availableAt: t + 0.02, generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: t,
                    detectionFrameID: UInt64(tick), modelVersion: nil, bestKnightConfidence: nil, features: features,
                    issues: ["missing-knight"], terrainSource: "detected",
                    atlasView: .init(left: 0.13, right: 0.56, camera: .zero, width: 640,
                                     coveredCells: 10, earnedCells: 0, revision: 1, health: 5))
            }
        }
        var time = try XCTUnwrap(rows.first).capturedAt
        let bot = BotPlaybackController(policy: model, domain: .gameplay, now: { time }, hasFocus: { true })
        bot.configure(canContinue: { true }, begin: { UUID() }, apply: { _, _, _ in true },
                      neutralize: { _ in }, end: { _ in })
        bot.start(domain: .gameplay)
        defer { bot.stop() }
        for row in rows {
            time = row.availableAt
            bot.observe(row)
            let applied = expectation(description: "Applied masked-frame prediction")
            DispatchQueue.main.async { applied.fulfill() }
            wait(for: [applied], timeout: 1)
            XCTAssertEqual(bot.lastAction, .neutral)
        }
        XCTAssertEqual(bot.decisionCount, rows.count)
    }

    private func policy() throws -> BotNativePolicy {
        try BotNativePolicy(document: BotRuntimePolicyDocument(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: "Synthetic controller test", featureNames: BotObservationContract.legacyFeatureNames,
            historyLength: 4, actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "test",
            layers: [BotRuntimeLayer(inputSize: 576, outputSize: 10, activation: "linear",
                weights: .init(repeating: 0, count: 5760), biases: [0, 1, 0, 1, 0, 0, 0, 1, 1, 0])]))
    }

    private func observe(_ controller: BotPlaybackController, time: Double, issues: [String] = [], terrainSource: String? = nil) {
        controller.observe(BotObservation(type: "observation", captureTimestamp: time, capturedAt: time,
            availableAt: time + 0.01, generation: 1, roomID: 1, roomRevision: 1,
            detectionTimestamp: time, detectionFrameID: 1, modelVersion: "synthetic", bestKnightConfidence: 0.9,
            features: .init(repeating: 0, count: BotObservationContract.featureCount), issues: issues,
            terrainSource: terrainSource, humanTerrainCount: terrainSource == "human-authored" ? 2 : 0))
        let drained = expectation(description: "Observation delivered on main queue")
        DispatchQueue.main.async { drained.fulfill() }
        wait(for: [drained], timeout: 1)
    }

    func testActorGetsFreshHistoryAfterItsLastInputExpires() throws {
        let names = BotObservationContract.legacyFeatureNames
        let frames = BotObservationContract.historyLength
        let width = names.count * frames
        var weights = [Float](repeating: 0, count: width * 10)
        // The right-action logit reads the newest frame's sequence-start flag.
        weights[2 * width + (frames - 1) * names.count + 8] = 1
        let model = try BotNativePolicy(document: BotRuntimePolicyDocument(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: "Lease-sensitive actor", featureNames: names, historyLength: frames, actionSizes: [3, 3, 2, 2],
            sourceCheckpointSHA256: "test", layers: [.init(inputSize: width, outputSize: 10, activation: "linear",
                weights: weights, biases: [0.5, 0, 0, 1, 0, 0, 1, 0, 1, 0])]))
        var time = 100.0, actions = [BotAction](), deadline = 0.0
        let bot = BotPlaybackController(policy: model, domain: .gameplay, now: { time }, hasFocus: { true })
        bot.configure(canContinue: { true }, begin: { UUID() }, apply: { action, expires, _ in
            actions.append(action); deadline = expires; return true
        }, neutralize: { _ in }, end: { _ in })
        bot.start(domain: .gameplay)
        defer { bot.stop() }
        func frame(at capture: Double) {
            time = capture + 0.01
            observe(bot, time: capture, terrainSource: "detected")
        }
        frame(at: 100)
        frame(at: 100.1)
        frame(at: deadline + 0.02)
        XCTAssertEqual(actions.map { $0.values[0] }, [2, 0, 2])
    }

    func testStartsWaitingThenReleasesOnStalePerceptionAndStop() throws {
        var time = 100.0
        let session = UUID()
        var actions = [BotAction](), deadlines = [Double](), neutralized = [UUID](), ended = [UUID]()
        let controller = BotPlaybackController(policy: try policy(), now: { time }, hasFocus: { true })
        controller.configure(canContinue: { true }, begin: { session }, apply: { action, deadline, id in
            XCTAssertEqual(id, session); actions.append(action); deadlines.append(deadline); return true
        }, neutralize: { neutralized.append($0) }, end: { ended.append($0) })
        controller.start()
        XCTAssertTrue(controller.isActive); XCTAssertTrue(controller.isWaiting); XCTAssertTrue(actions.isEmpty)
        time = 100.02; observe(controller, time: 100, terrainSource: "human-authored")
        XCTAssertEqual(actions.first?.values, [1, 0, 1, 0]); XCTAssertFalse(controller.isWaiting)
        XCTAssertLessThanOrEqual(deadlines[0], time + 0.2)
        XCTAssertEqual(controller.lastDecisionTerrainSource, "human-authored")
        XCTAssertEqual(controller.lastDecisionHumanTerrainCount, 2)
        time = 100.12; observe(controller, time: 100.1, issues: ["missing-knight"])
        XCTAssertTrue(controller.isWaiting); XCTAssertEqual(controller.lastAction, .neutral)
        XCTAssertEqual(neutralized.last, session); XCTAssertEqual(actions.count, 1)
        XCTAssertEqual(controller.lastDecisionTerrainSource, "human-authored")
        time = 100.22; observe(controller, time: 100.2)
        XCTAssertEqual(actions.count, 2); XCTAssertFalse(controller.isWaiting)
        controller.stop()
        XCTAssertFalse(controller.isActive); XCTAssertEqual(ended, [session])
        controller.stop(); XCTAssertEqual(ended.count, 1)
    }

    func testOldSessionInterruptionCannotStopNewOwnerAndManualTakeoverStopsCurrent() throws {
        var session = UUID()
        var ended = [UUID]()
        let controller = BotPlaybackController(policy: try policy(), now: { 100 }, hasFocus: { true })
        controller.configure(canContinue: { true }, begin: { session }, apply: { _, _, _ in true },
            neutralize: { _ in }, end: { ended.append($0) })
        controller.start(); let old = session; controller.stop()
        session = UUID(); controller.start()
        controller.interrupted(sessionID: old, reason: "Delayed callback")
        XCTAssertTrue(controller.isActive)
        controller.interrupted(sessionID: session, reason: "Manual control")
        XCTAssertFalse(controller.isActive); XCTAssertEqual(controller.status, "Manual control")
        XCTAssertEqual(ended, [old, session])
    }

    func testFocusLossDisarmsWaitingBot() throws {
        var focused = true
        var releases = 0
        let released = expectation(description: "Focus watchdog releases the controls")
        let controller = BotPlaybackController(policy: try policy(), now: { 100 }, hasFocus: { focused })
        controller.configure(canContinue: { true }, begin: { UUID() }, apply: { _, _, _ in true },
            neutralize: { _ in }, end: { _ in
                releases += 1
                released.fulfill()
            })
        controller.start()
        XCTAssertTrue(controller.isWaiting)
        focused = false
        wait(for: [released], timeout: 2)
        XCTAssertFalse(controller.isActive)
        XCTAssertEqual(releases, 1)
    }

    func testDelayedObservationCannotReplaceFreshHumanGroundInput() throws {
        var time = 100.0
        let controller = BotPlaybackController(policy: try policy(), now: { time }, hasFocus: { true })
        var applications = 0, neutralizations = 0
        controller.configure(canContinue: { true }, begin: { UUID() }, apply: { _, _, _ in
            applications += 1; return true
        }, neutralize: { _ in neutralizations += 1 }, end: { _ in })
        controller.start()
        defer { controller.stop() }
        time = 100.02
        observe(controller, time: 100, terrainSource: "human-authored")
        time = 100.03
        observe(controller, time: 99, issues: ["stale-capture"])
        XCTAssertEqual(applications, 1); XCTAssertEqual(neutralizations, 0)
        XCTAssertFalse(controller.isWaiting)
        XCTAssertEqual(controller.humanTerrainDecisionCount, 1)
    }

    func testWaitingMessageIdentifiesTheActualMissingInput() {
        func observation(_ issues: [String]) -> BotObservation {
            .init(type: "observation", captureTimestamp: 100, capturedAt: 100, availableAt: 100.01,
                generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: nil,
                detectionFrameID: nil, modelVersion: nil, bestKnightConfidence: nil,
                features: .init(repeating: 0, count: BotObservationContract.featureCount), issues: issues)
        }
        XCTAssertEqual(BotPlaybackController.waitingStatus(for: observation(["missing-terrain-analysis"]), now: 100.02),
                       "Bot waiting for ground lines")
        XCTAssertEqual(BotPlaybackController.waitingStatus(for: observation(["missing-knight"]), now: 100.02),
                       "Bot waiting for Knight detection")
        XCTAssertEqual(BotPlaybackController.waitingStatus(for: observation(["stale-capture", "missing-knight"]), now: 100.02),
                       "Bot waiting for a fresh capture")
        XCTAssertEqual(BotPlaybackController.waitingStatus(for: observation([]), now: 101),
                       "Bot waiting for a fresh capture")
    }

    func testPlaybackCaptureFailureReleasesControlBeforeAnyAction() throws {
        let session = UUID()
        var ended = [UUID](), actions = 0
        let controller = BotPlaybackController(policy: try policy(), now: { 100 }, hasFocus: { true })
        controller.configure(canContinue: { true }, begin: { session }, apply: { _, _, _ in actions += 1; return true },
            neutralize: { _ in }, end: { ended.append($0) })
        controller.onStarted = { _, _ in false }
        controller.start()
        XCTAssertFalse(controller.isActive)
        XCTAssertEqual(controller.status, "Bot playback capture failed")
        XCTAssertEqual(ended, [session]); XCTAssertEqual(actions, 0)
    }

    func testGamePlayCannotSilentlyBorrowTheHackerPolicy() throws {
        var begins = 0
        let controller = BotPlaybackController(policy: try policy(), now: { 100 }, hasFocus: { true })
        controller.configure(canContinue: { true }, begin: { begins += 1; return UUID() }, apply: { _, _, _ in true },
            neutralize: { _ in }, end: { _ in })
        var startedWithMotor = true
        controller.onStarted = { domain, motor in
            XCTAssertEqual(domain, .gameplay)
            startedWithMotor = motor != nil
            return true
        }
        controller.start(domain: .gameplay)
        XCTAssertTrue(controller.isActive)
        XCTAssertEqual(begins, 1)
        XCTAssertFalse(startedWithMotor)
        controller.stop()
    }

    func testHackerRejectsDetectedTerrainFromTheOtherModelDomain() throws {
        var time = 100.0, actions = 0
        let controller = BotPlaybackController(policy: try policy(), now: { time }, hasFocus: { true })
        controller.configure(canContinue: { true }, begin: { UUID() }, apply: { _, _, _ in actions += 1; return true },
            neutralize: { _ in }, end: { _ in })
        controller.start()
        defer { controller.stop() }
        time = 100.02; observe(controller, time: 100, terrainSource: "human-authored")
        time = 100.12; observe(controller, time: 100.1, terrainSource: "detected")
        XCTAssertEqual(actions, 1); XCTAssertEqual(controller.lastDecisionTerrainSource, "human-authored")
    }
}
