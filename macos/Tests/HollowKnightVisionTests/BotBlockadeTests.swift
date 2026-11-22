import XCTest
@testable import HollowKnightVision

final class BotBlockadeTests: XCTestCase {
    private func frame(_ t: Double, x: Double = 0.5, y: Double = 0.2, roomID: Int = 1, issues: [String] = []) -> BotObservation {
        var f = [Double](repeating: 0, count: BotObservationContract.featureCount)
        f.replaceSubrange(0..<7, with: [x, y, 0.03, 0.05, 1, 0, 1])
        return BotObservation(type: "observation", captureTimestamp: t, capturedAt: t, availableAt: t + 0.01,
                              generation: 1, roomID: roomID, roomRevision: 0, detectionTimestamp: t, detectionFrameID: nil,
                              modelVersion: nil, bestKnightConfidence: 1, features: f, issues: issues, knightSource: "visual")
    }

    func testCameraFollowDoesNotImplyStallAndBothDirectionsClearOnPassage() throws {
        for side in [-1, 1] {
            let s = Double(side)
            var tracker = BotBlockadeTracker(), map = BotBlockadeMap()
            func visit(_ tick: Int, _ camera: Double, _ knight: Double, _ input: Int) -> BotBlockadeEvidence? {
                let pose = BotBlockadePose(space: "room", x: camera, y: 0, width: 1)
                return tracker.observe(frame(Double(tick) / 10, x: knight), pose: pose, direction: input, map: &map)
            }
            for i in 0..<9 { XCTAssertNil(visit(i, s * Double(i) * 0.025, 0.5, side)) }
            // With no input, remaining stationary cannot establish a blockade.
            for i in 9..<15 { XCTAssertNil(visit(i, s * 0.2, 0.5, 0)) }
            var evidence: BotBlockadeEvidence?
            for i in 15..<23 {
                // The camera eases while the Knight stays in the same world position.
                let drift = s * Double(i - 15) * 0.003
                evidence = visit(i, s * 0.2 + drift, 0.5 - drift, side)
            }
            let block = try XCTUnwrap(evidence)
            XCTAssertEqual(block.mark.direction, side)
            XCTAssertNil(visit(23, s * 0.24, 0.5, side))
            XCTAssertEqual(map.marks.count, 1); XCTAssertTrue(map.marks[0].crossed)
            XCTAssertNil(visit(24, s * 0.25, 0.5, side))
        }
    }

    func testMissingPoseKnightOrRepeatedDetectorFrameCannotProveContact() {
        var tracker = BotBlockadeTracker(), map = BotBlockadeMap()
        let pose = BotBlockadePose(space: "room", x: 0, y: 0, width: 1)
        for i in 0..<15 {
            let t = Double(i) / 10
            var row = frame(t)
            if i % 2 == 0 { row.features[4] = 0 }
            XCTAssertNil(tracker.observe(row, pose: i % 3 == 0 ? nil : pose, direction: 1, map: &map))
        }
        let row = frame(2)
        for _ in 0..<15 { XCTAssertNil(tracker.observe(row, pose: pose, direction: 1, map: &map)) }
        XCTAssertTrue(map.marks.isEmpty)
    }

    func testSustainedPushReportsWallAfterTwoTenthsOfASecond() {
        for direction in [-1, 1] {
            var tracker = BotBlockadeTracker(), map = BotBlockadeMap()
            let pose = BotBlockadePose(space: "room", x: 0, y: 0, width: 1)
            XCTAssertNil(tracker.observe(frame(1), pose: pose, direction: direction, map: &map))
            XCTAssertNil(tracker.observe(frame(1.1), pose: pose, direction: direction, map: &map))
            XCTAssertEqual(tracker.observe(frame(1.2), pose: pose, direction: direction, map: &map)?.mark.direction, direction)
        }
    }

    func testAirborneKnockbackCannotRegisterAWallOrExtendAGroundedPush() {
        var visit = BotBlockadeVisit()
        let pose = BotBlockadePose(space: "hacker/Tutorial_01", x: 0, y: 0, width: 1)
        for (index, grounded) in [true, false, false, false, true, true, true].enumerated() {
            var row = frame(Double(index) / 10)
            row.grounded = grounded
            let evidence = visit.observe(row, pose: pose, direction: -1)
            if index < 6 {
                XCTAssertNil(evidence)
                XCTAssertTrue(visit.map.marks.isEmpty)
            } else {
                XCTAssertEqual(evidence?.mark.direction, -1)
                XCTAssertEqual(visit.map.marks.count, 1)
            }
        }
    }

    func testRoomReentryAndFreshStartRequireNewPushEvidence() {
        let pose = BotBlockadePose(space: "hacker/Tutorial_01", x: 0, y: 0, width: 1)
        var visit = BotBlockadeVisit()
        for t in [1.0, 1.1, 1.2] { _ = visit.observe(frame(t), pose: pose, direction: 1) }
        XCTAssertEqual(visit.map.marks.count, 1)
        let other = frame(1.3, roomID: 2)
        XCTAssertNil(visit.observe(other, pose: pose, direction: 0))
        XCTAssertNil(visit.observe(frame(1.4), pose: pose, direction: 0))
        XCTAssertTrue(visit.map.marks.isEmpty)
        for t in [1.5, 1.6, 1.7] { _ = visit.observe(frame(t), pose: pose, direction: 1) }
        visit = BotBlockadeVisit()
        XCTAssertNil(visit.observe(frame(1.8), pose: pose, direction: 1))
        XCTAssertTrue(visit.map.marks.isEmpty)
    }

    func testPinnedCameraAllowsWalkingAndPressAtDoorNeedsNoGroundLabels() {
        var tracker = BotBlockadeTracker(), map = BotBlockadeMap()
        let pose = BotBlockadePose(space: "room", x: 0, y: 0, width: 1)
        for i in 0...12 {
            // Slow continuous walking at the camera limit.
            XCTAssertNil(tracker.observe(frame(Double(i) / 10, x: 0.3 + Double(i) * 0.004),
                                         pose: pose, direction: 1, map: &map))
        }
        XCTAssertTrue(map.marks.isEmpty)
        for tick in 13...16 {
            let row = frame(Double(tick) / 10, x: 0.35, issues: ["missing-terrain-analysis"])
            _ = tracker.observe(row, pose: pose, direction: 1, map: &map)
        }
        XCTAssertEqual(map.marks.count, 1)
        XCTAssertEqual(map.marks.first?.direction, 1)
    }

    func testFreshContactOnRecordedFirstDoor() throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_FIRST_DOOR_CLIP"] else {
            throw XCTSkip("Supply a Hacker capture of the first rightward door approach in King's Pass.")
        }
        let clip = try BotSnippet.load(URL(fileURLWithPath: path))
        var visit = BotBlockadeVisit()
        var reachedDoor: Double?, detected: Double?, crossed = false
        for row in clip.observations {
            guard let pose = row.blockadePose else { continue }
            let knightX = pose.x + row.features[0] * pose.width
            let direction = row.driveDirection ?? 0
            let evidence = visit.observe(row, pose: pose, direction: direction)
            if reachedDoor == nil, abs(knightX - 58.75) < 0.02, direction == 1 {
                reachedDoor = row.capturedAt
            }
            if reachedDoor == nil {
                XCTAssertTrue(visit.map.marks.isEmpty, "A mark appeared on the unobstructed approach")
            } else if detected == nil, let evidence {
                XCTAssertEqual(direction, 1)
                XCTAssertEqual(evidence.mark.x, 59, accuracy: 0.6)
                detected = row.capturedAt
            }
            if knightX > 61 {
                XCTAssertNil(evidence)
                crossed = visit.map.marks.contains { $0.crossed && abs($0.x - 59) < 0.6 }
                break
            }
        }
        let delay = try XCTUnwrap(detected) - XCTUnwrap(reachedDoor)
        XCTAssertGreaterThanOrEqual(delay, 0)
        XCTAssertLessThanOrEqual(delay, 0.3)
        XCTAssertTrue(crossed)
        print(String(format: "Recorded first door marked %.3f s after contact, then cleared on passage.", delay))
    }

    func testVersionFourActorReceivesRegisteredTargetAndOlderHistoryKeepsItsOriginalInputs() throws {
        let names = BotObservationContract.blockadePolicyNames, size = names.count * 4
        var weights = [Float](repeating: 0, count: 10 * size)
        weights[9 * size + names.count * 3 + 15] = 2
        let actor = try BotNativePolicy(document: .init(schemaVersion: 1, observationSchemaVersion: 4,
            modelName: "Door test", featureNames: names, historyLength: 4, actionSizes: [3, 3, 2, 2],
            sourceCheckpointSHA256: "test", layers: [.init(inputSize: size, outputSize: 10, activation: "linear",
                weights: weights, biases: .init(repeating: 0, count: 10))]))
        let mark = BotBlockadeMark(space: "visual/1/2/3", x: 0.53, y: 0.2, direction: 1, viewWidth: 1, crossed: false)
        var row = frame(1), history = BotObservationHistory(), legacy = BotObservationHistory()
        row.blockade = .init(mark: mark, dx: 0.03, dy: 0)
        let obstacle = try XCTUnwrap(row.blockade)
        func enemy(_ dx: Double) -> BotCombat.Encounter {
            .engage(.init(dx: dx, dy: 0, width: 0.02, height: 0.04, knightWidth: 0.03, knightHeight: 0.05))
        }
        XCTAssertFalse(obstacle.blocksApproach(to: enemy(-0.04)))
        XCTAssertFalse(obstacle.blocksApproach(to: enemy(0.02)))
        XCTAssertTrue(obstacle.blocksApproach(to: enemy(0.1)))
        let current = try XCTUnwrap(history.input(row, previousAction: .neutral, now: 1.02, includeScreenTerrain: true, includeBlockade: true))
        let prior = try XCTUnwrap(legacy.input(row, previousAction: .neutral, now: 1.02, includeScreenTerrain: true))
        XCTAssertEqual(try actor.action(for: current).values[3], 1)
        XCTAssertEqual(try actor.action(for: prior).values[3], 0)
    }

    func testBDeliversAllFourModelControlsThroughoutBlockadeInBothDomains() throws {
        let names = BotObservationContract.blockadePolicyNames, size = 4 * names.count
        var weights = [Float](repeating: 0, count: 10 * size)
        let newest = 3 * names.count
        // This test actor alternates attack and release on the right and always releases on the left.
        weights[9 * size + newest + 15] = 1
        weights[9 * size + newest + 12] = -2
        let policy = try BotNativePolicy(document: .init(schemaVersion: 1, observationSchemaVersion: 4,
            modelName: "Blockade controls", featureNames: names, historyLength: 4, actionSizes: [3, 3, 2, 2],
            sourceCheckpointSHA256: "test", layers: [.init(inputSize: size, outputSize: 10, activation: "linear",
                weights: weights, biases: [0, 1, 0, 0, 0, 1, 0, 1, 0, 0])],
            pitPolicy: .init(schemaVersion: 1, checkpointSHA256: "test", layers: [
                .init(inputSize: 8, outputSize: 5, activation: "linear", weights: .init(repeating: 0, count: 40),
                      biases: [0, 0, 1, 0, 1])]),
            atlasPolicy: .init(schemaVersion: 1, checkpointSHA256: "test", layers: [
                .init(inputSize: 8, outputSize: 3, activation: "linear", weights: .init(repeating: 0, count: 24),
                      biases: [1, 0, 0])])))
        for domain in BotDomain.allCases {
            var time = 100.0, actions = [BotAction]()
            let bot = BotPlaybackController(policy: policy, domain: domain, now: { time }, hasFocus: { true })
            bot.configure(canContinue: { true }, begin: { UUID() },
                apply: { action, _, _ in actions.append(action); return true }, neutralize: { _ in }, end: { _ in })
            bot.start(domain: domain)
            defer { bot.stop() }
            for i in 0..<52 {
                var row = frame(100 + Double(i) / 10)
                let side = i < 26 ? -1 : 1
                row.terrainSource = domain == .gameplay ? "detected" : "human-authored"
                row.features.replaceSubrange(16..<31, with: [-0.5, -0.1, 0.07, 1, 1, 0.1, 0.5, 0.07, 1, 1,
                                                             -0.09, 0.09, -0.03, 1, 1])
                if domain == .gameplay && i == 0 {
                    var pit = BotPitPerception()
                    XCTAssertNotNil(pit.input(row, action: .neutral, requireDropEvidence: true))
                }
                row.atlasView = .init(left: 0, right: 1, camera: .zero, width: 640, coveredCells: 1,
                                      earnedCells: 0, revision: 0, health: 5)
                let dx = 0.05 * Double(side)
                row.blockade = .init(mark: .init(space: "room", x: 0.5 + dx, y: 0.2, direction: side,
                                                  viewWidth: 1, crossed: false), dx: dx, dy: 0)
                time = row.availableAt
                bot.observe(row)
                let delivered = expectation(description: "control delivered")
                DispatchQueue.main.async { delivered.fulfill() }
                wait(for: [delivered], timeout: 1)
                XCTAssertEqual(actions.count, i + 1)
                XCTAssertEqual(actions.last?.values, [1, 2, 1, side == 1 && i.isMultiple(of: 2) ? 1 : 0])
                XCTAssertEqual(bot.perceptionDiagnostic["actionSource"] as? String, "general-policy")
            }
        }
    }

    func testSelectedModelsOnRecordedDoorApproaches() throws {
        let env = ProcessInfo.processInfo.environment
        guard let root = env["HKV_BLOCKADE_LIBRARY"] else { throw XCTSkip("Supply the recorded door encounter to exercise the selected models.") }
        let library = URL(fileURLWithPath: root)
        for domain in BotDomain.allCases {
            let numbers = try JSONDecoder().decode([String: Int].self, from: Data(contentsOf:
                library.appendingPathComponent("clip-numbers-by-domain-\(domain.rawValue).json")))
            let prefix = "HKV_BLOCKADE_" + domain.rawValue.uppercased()
            let number = env[prefix + "_CLIP"].flatMap(Int.init) ?? 20
            let id = try XCTUnwrap(numbers.first { $0.value == number }?.key)
            let clip = try BotSnippet.load(library.appendingPathComponent("demonstrations/demo-" + id.lowercased()))
            let recovered = try BotBlockadeReplay.recover(clip)
            let firstSwing = try XCTUnwrap(clip.inputs.first { $0.button == "x" && $0.isPressed })
            if number == 20 { XCTAssertTrue(recovered.map.marks.contains { $0.crossed && $0.direction == 1 }) }
            let store = BotModelStore(root: library, domain: domain)
            let model = try env[prefix + "_MODEL"].map { URL(fileURLWithPath: $0) } ?? store.activeDirectory()
            let policy = try BotNativePolicy.load(from: model)
            var time = clip.manifest.startedAt
            let bot = BotPlaybackController(policy: policy, domain: domain, now: { time }, hasFocus: { true })
            bot.configure(canContinue: { true }, begin: { UUID() }, apply: { _, _, _ in true }, neutralize: { _ in }, end: { _ in })
            bot.start(domain: domain)
            var actorDecisions = 0, swings = [Double](), markedStatuses = [String: Int]()
            for var row in clip.observations {
                time = row.availableAt
                row.blockade = recovered.rows.first { $0.captureTimestamp == row.captureTimestamp }?.blockade
                let previous = bot.lastAction
                let decisions = bot.decisionCount
                bot.observe(row)
                let delivered = expectation(description: "controller decision")
                DispatchQueue.main.async { delivered.fulfill() }
                wait(for: [delivered], timeout: 1)
                if row.blockade != nil { markedStatuses[bot.status, default: 0] += 1 }
                if bot.decisionCount > decisions, row.blockade != nil {
                    XCTAssertNotNil(row.blockade)
                    actorDecisions += 1
                    if previous.values[3] == 0 && bot.lastAction.values[3] == 1 {
                        swings.append(time - clip.manifest.startedAt)
                    }
                }
            }
            bot.stop()
            XCTAssertGreaterThan(actorDecisions, 0, "\(markedStatuses)")
            if number == 20 { XCTAssertNil(recovered.rows.first { $0.captureTimestamp == clip.observations.last?.captureTimestamp }) }
            print("\(domain.title) clip \(number): \(actorDecisions) model decisions at blockade; model attack presses at \(swings)s, expert at \(firstSwing.timestamp - clip.manifest.startedAt)s.")
        }
    }
}
