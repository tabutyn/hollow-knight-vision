import XCTest
@testable import HollowKnightVision

final class BotPlaybackTests: XCTestCase {
    private func document(checkpointHash: String = "test") -> BotRuntimePolicyDocument {
        let weights = [Float](repeating: 0, count: 576 * 10)
        return BotRuntimePolicyDocument(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: "Synthetic policy", featureNames: BotObservationContract.legacyFeatureNames,
            historyLength: 4, actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: checkpointHash,
            layers: [BotRuntimeLayer(inputSize: 576, outputSize: 10, activation: "linear",
                weights: weights, biases: [0, 2, 1, 0, 1, 2, 0, 1, 0, 3])])
    }

    private func observation(at time: Double, room: Int = 1, issues: [String] = []) -> BotObservation {
        var values = [Double](repeating: 0, count: BotObservationContract.featureCount)
        values[4] = 0.9; values[6] = 1
        return BotObservation(type: "observation", captureTimestamp: time, capturedAt: time,
            availableAt: time + 0.01, generation: 1, roomID: room, roomRevision: 1,
            detectionTimestamp: time, detectionFrameID: 1, modelVersion: "v1", bestKnightConfidence: 0.9,
            features: values, issues: issues)
    }

    func testRoadInputPreservesOlderPolicyHistoryAndIsRecordedWithTheFrame() throws {
        var row = observation(at: 13)
        row.roadHint = .init(dx: -1, dy: 0, clearance: 0.1, distance: 0.25, frontier: false)
        let restored = try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(row))
        var current = BotObservationHistory(), older = BotObservationHistory()
        let vector = try XCTUnwrap(current.input(restored, previousAction: .neutral, now: 13.02,
            includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true,
            includeAtlasMap: true, includeRoad: true))
        let legacy = try XCTUnwrap(older.input(restored, previousAction: .neutral, now: 13.02,
            includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true, includeAtlasMap: true))
        XCTAssertEqual(vector.count, BotObservationContract.roadNames.count*4)
        XCTAssertEqual(legacy.count, BotObservationContract.atlasMapNames.count*4)
        XCTAssertEqual(Array(vector.suffix(6)), [1, -1, 0, 0.1, 0.25, 0])
        XCTAssertEqual(Array(vector.dropLast(6).suffix(BotObservationContract.atlasMapNames.count)),
                       Array(legacy.suffix(BotObservationContract.atlasMapNames.count)))
    }

    func testNativeActionsMapEachHeadAndNeverUseConflictingButtons() throws {
        let policy = try BotNativePolicy(document: document())
        let action = try policy.action(for: Array(repeating: 0, count: 576))
        XCTAssertEqual(action.values, [1, 2, 1, 1])
        XCTAssertEqual(action.buttons, [.left, .down, .actionZ, .actionX])
        XCTAssertEqual(try BotNativePolicy.action(from: Array(repeating: 0, count: 10)), .neutral)
        XCTAssertThrowsError(try BotAction(values: [3, 0, 0, 0]))
        XCTAssertThrowsError(try policy.action(for: [0]))
        var invalid = [Float](repeating: 0, count: 576); invalid[4] = .nan
        XCTAssertThrowsError(try policy.action(for: invalid))
    }

    func testHistoryPadsAndResetsAtInvalidOrDiscontinuousObservations() throws {
        var history = BotObservationHistory()
        let action = try BotAction(values: [2, 1, 1, 0])
        let first = try XCTUnwrap(history.input(observation(at: 100), previousAction: action, now: 100.02))
        XCTAssertTrue(first.prefix((3 * BotObservationContract.featureCount)).allSatisfy { $0 == 0 })
        XCTAssertEqual(first[(3 * BotObservationContract.featureCount) + 8], 1)
        XCTAssertEqual(Array(first[((3 * BotObservationContract.featureCount) + 9)..<((3 * BotObservationContract.featureCount) + 13)]), [0, 0, 0, 0])
        let second = try XCTUnwrap(history.input(observation(at: 100.1), previousAction: action, now: 100.12))
        XCTAssertEqual(Array(second[((3 * BotObservationContract.featureCount) + 9)..<((3 * BotObservationContract.featureCount) + 13)]), [1, 1, 1, 0])
        XCTAssertEqual(second[(3 * BotObservationContract.featureCount) + 8], 0)
        XCTAssertNil(history.input(observation(at: 100.2, issues: ["missing-knight"]), previousAction: action, now: 100.22))
        let restored = try XCTUnwrap(history.input(observation(at: 100.3), previousAction: action, now: 100.32))
        XCTAssertTrue(restored.prefix((3 * BotObservationContract.featureCount)).allSatisfy { $0 == 0 })
        let room = try XCTUnwrap(history.input(observation(at: 100.4, room: 2), previousAction: action, now: 100.42))
        XCTAssertTrue(room.prefix((3 * BotObservationContract.featureCount)).allSatisfy { $0 == 0 })
        XCTAssertNil(history.input(observation(at: 100.5), previousAction: action, now: 101))
    }

    func testTransportLeaseExpiresAndOldTokensCannotRenewANewSession() {
        var lease = BotInputLease()
        let first = lease.begin()
        XCTAssertFalse(lease.hasFreshDecision(at: 100))
        XCTAssertTrue(lease.renew(sessionID: first, until: 100.2, now: 100))
        XCTAssertTrue(lease.hasFreshDecision(at: 100.19))
        XCTAssertFalse(lease.hasFreshDecision(at: 100.21))
        XCTAssertEqual(lease.sessionID, first)
        XCTAssertFalse(lease.renew(sessionID: first, until: 110, now: 100))
        lease.end()
        let second = lease.begin()
        XCTAssertFalse(lease.renew(sessionID: first, until: 101.2, now: 101))
        XCTAssertTrue(lease.renew(sessionID: second, until: 101.2, now: 101))
        lease.neutralize(sessionID: first)
        XCTAssertTrue(lease.hasFreshDecision(at: 101.1))
        lease.neutralize(sessionID: second)
        XCTAssertFalse(lease.hasFreshDecision(at: 101.1))
    }

    func testRejectsMalformedModelParameters() throws {
        let original = document()
        let bad = BotRuntimePolicyDocument(schemaVersion: 2, observationSchemaVersion: 1,
            modelName: original.modelName, featureNames: original.featureNames, historyLength: 4,
            actionSizes: original.actionSizes, sourceCheckpointSHA256: "test", layers: original.layers)
        XCTAssertThrowsError(try BotNativePolicy(document: bad))
    }

    func testRuntimeLoaderVerifiesActorAndSourceCheckpointHashes() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let checkpoint = Data("synthetic-checkpoint".utf8)
        let digest = BotNativePolicy.sha256(checkpoint)
        let policy = try JSONEncoder().encode(document(checkpointHash: digest))
        try checkpoint.write(to: root.appendingPathComponent("model.zip"))
        try policy.write(to: root.appendingPathComponent("runtime-policy.json"))
        let manifest: [String: Any] = ["schemaVersion": 1, "policyFile": "runtime-policy.json",
            "policySHA256": BotNativePolicy.sha256(policy), "sourceCheckpointSHA256": digest]
        try JSONSerialization.data(withJSONObject: manifest).write(to: root.appendingPathComponent("runtime-manifest.json"))
        XCTAssertNoThrow(try BotNativePolicy.load(from: root))
        try Data("modified".utf8).write(to: root.appendingPathComponent("model.zip"))
        XCTAssertThrowsError(try BotNativePolicy.load(from: root))
        try checkpoint.write(to: root.appendingPathComponent("model.zip"))
        try Data("modified".utf8).write(to: root.appendingPathComponent("runtime-policy.json"))
        XCTAssertThrowsError(try BotNativePolicy.load(from: root))
    }

    func testNativeRuntimeMatchesTheTrainedSB3Checkpoint() throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_BOT_RUNTIME_FIXTURE"] else {
            throw XCTSkip("Set HKV_BOT_RUNTIME_FIXTURE to the private native export directory for SB3 parity")
        }
        struct Fixture: Decodable { let inputs: [[Float]]; let logits: [[Float]]; let actions: [[Int]] }
        let root = URL(fileURLWithPath: path)
        let policy = try BotNativePolicy(document: JSONDecoder().decode(BotRuntimePolicyDocument.self,
            from: Data(contentsOf: root.appendingPathComponent("runtime-policy.json"))))
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: root.appendingPathComponent("parity.json")))
        for index in fixture.inputs.indices {
            let logits = try policy.logits(for: fixture.inputs[index])
            for (actual, expected) in zip(logits, fixture.logits[index]) {
                XCTAssertEqual(actual, expected, accuracy: 0.0001)
            }
            XCTAssertEqual(try policy.action(for: fixture.inputs[index]).values, fixture.actions[index])
        }
    }
}
