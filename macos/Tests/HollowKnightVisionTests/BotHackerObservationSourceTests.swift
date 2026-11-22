import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class BotHackerObservationSourceTests: XCTestCase {
    private func image() throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: 100, height: 80,
            bitsPerComponent: 8, bytesPerRow: 400, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        return try XCTUnwrap(context.makeImage())
    }
    private func ground() -> BotHumanGroundTerrainCache {
        let cache = BotHumanGroundTerrainCache()
        var document = GroundTruthLineDocument()
        document.lines = [.init(sceneName: "Tutorial_01", minimumWorldX: 1, maximumWorldX: 9,
            worldY: 4, kind: .positive, seededFromDetector: false)]
        cache.update(document: document)
        return cache
    }
    private func match(at timestamp: Double = 100, frame: Int = 10,
                       hero: Bool = true, actors: Bool = true, enemyX: Double = 70,
                       captureFrame: Int? = nil) throws -> HackerMatchedFrame {
        var sample = ReceiverGroundTruthSample(version: 2, type: "groundTruth", sessionID: UUID(),
            sequence: UInt64(frame), unityFrame: Int64(frame), unityRealtime: timestamp,
            sceneName: "Tutorial_01", heroAvailable: hero, heroX: 5, heroY: 5, heroZ: 0,
            velocityX: 0, velocityY: 0, facingRight: true, grounded: true,
            cameraAvailable: true, cameraX: 5, cameraY: 5, cameraZ: -38,
            cameraTargetX: 5, cameraTargetY: 5, cameraTargetZ: 0, orthographicSize: 2,
            pixelsPerWorldUnitX: 10, pixelsPerWorldUnitY: 20, heroScreenX: 40, heroScreenY: 40,
            projectionPixelWidth: 100, projectionPixelHeight: 80, screenWidth: 100, screenHeight: 80)
        if actors {
            sample.actors = .init(version: 1, hero: hero ? .init(id: 1, name: "Knight", screenX: 40,
                screenY: 40, width: 10, height: 20) : nil,
                enemies: [.init(id: 2, name: "Crawler", screenX: enemyX, screenY: 30, width: 12, height: 8)],
                geoCaches: [.init(id: 3, name: "Geo Rock", screenX: 25, screenY: 10, width: 10, height: 20)],
                lifebloodCocoons: [.init(id: 4, name: "Health Cocoon", screenX: 65, screenY: 15, width: 12, height: 16)])
        }
        let outcomes = BotEnemyOutcomes(epoch: sample.sessionID, damage: 10, kills: 1)
        sample.actors?.enemyOutcomes = outcomes
        return .init(capture: .init(unityFrame: captureFrame ?? frame, image: try image(),
            capturedAt: timestamp, integratesAtlas: false, captureGeneration: 1), sample: sample,
            sampleObservedAt: timestamp + 0.001)
    }

    func testExactModKnightAndEnemyPositionsReachPolicyWithoutAnyVisualDetections() throws {
        let source = BotHackerObservationSource(ground: ground())
        let frame = try XCTUnwrap(source.makeFrame(try match(), now: 100.02))
        let observation = frame.observation
        XCTAssertEqual(observation.enemyOutcomes?.kills, 1)
        XCTAssertEqual(observation.enemyOutcomes?.damage, 10)
        XCTAssertTrue(observation.isUsable, observation.issues.joined(separator: ", "))
        XCTAssertEqual(observation.knightSource, "hacker-frame")
        XCTAssertEqual(observation.enemySource, "hacker-frame")
        XCTAssertEqual(observation.terrainSource, "human-authored")
        XCTAssertEqual(Array(observation.features.prefix(5)), [0.4, 0.4, 0.1, 0.2, 1])
        XCTAssertEqual(observation.features[16], -0.3, accuracy: 0.0001)
        XCTAssertEqual(observation.features[18], -0.2, accuracy: 0.0001)
        XCTAssertEqual(Array(observation.features[96..<102]), [0.3, -0.1, 0.12, 0.08, 1, 1])
        XCTAssertEqual(observation.geoCaches, [.init(x: 0.25, y: 0.1, width: 0.1, height: 0.2)])
        XCTAssertEqual(observation.lifebloodCocoons, [.init(x: 0.65, y: 0.15, width: 0.12, height: 0.16)])
        let recorded = try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(observation))
        var history = BotObservationHistory()
        let input = try XCTUnwrap(history.input(recorded, previousAction: .neutral, now: 100.03,
            includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true,
            includeAtlasMap: true, includeRoad: true, includePickup: true, includeCocoons: true))
        XCTAssertEqual(input.count, BotObservationContract.cocoonNames.count * 4)
        XCTAssertEqual(Array(input.suffix(3)), [0.25, -0.25, 1])
        XCTAssertNil(observation.bestKnightConfidence)
        XCTAssertGreaterThanOrEqual(observation.roomID, 0)
        XCTAssertEqual(observation.detectionFrameID, 10)
        let decoded = try JSONDecoder().decode(ReceiverGroundTruthSample.self,
            from: JSONEncoder().encode(try match().sample))
        XCTAssertEqual(decoded.actors?.enemies.first?.screenX, 70)
    }

    func testAuthoredDoorsReachExactFrameObservation() throws {
        let cache = ground()
        var document = cache.snapshot
        document.doors = [
            .init(sceneName: "Tutorial_01", minimumWorldX: 1, maximumWorldX: 5, worldY: 4, kind: .door, seededFromDetector: false),
            .init(sceneName: "Tutorial_01", minimumWorldX: 8, maximumWorldX: 8, worldY: 3, kind: .doorVertical, seededFromDetector: false, endWorldY: 6),
            .init(sceneName: "other", minimumWorldX: 1, maximumWorldX: 5, worldY: 4, kind: .door, seededFromDetector: false)
        ]
        cache.update(document: document)
        let row = try XCTUnwrap(BotHackerObservationSource(ground: cache).makeFrame(try match(), now: 100.02)).observation
        let terrain = try XCTUnwrap(row.authoredTerrain)
        XCTAssertEqual(terrain.doors?.count, 2)
        XCTAssertGreaterThan(BotTerrainGrid.doorFeatures(terrain).reduce(0, +), 0)
        XCTAssertEqual(try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(row)), row)
    }

    func testAuthoredWallsProjectClipAndReachReviewAndPolicyOnBlankPixels() throws {
        let cache = ground()
        var document = cache.snapshot
        func wall(_ x: Double, scene: String = "Tutorial_01", seeded: Bool = false) -> GroundTruthWorldLine {
            .init(sceneName: scene, minimumWorldX: x, maximumWorldX: x, worldY: 2,
                  kind: .wall, seededFromDetector: seeded, endWorldY: 6)
        }
        document.walls = [wall(1), wall(3), wall(8), wall(11), wall(4, scene: "other"), wall(5, seeded: true)]
        cache.update(document: document)
        let source = BotHackerObservationSource(ground: cache)
        let observation = try XCTUnwrap(source.makeFrame(try match(), now: 100.02)).observation
        XCTAssertTrue(observation.isUsable)
        let geometry = try XCTUnwrap(observation.authoredTerrain)
        XCTAssertEqual(geometry.walls.map(\.x), [10, 30, 80])
        XCTAssertEqual(geometry.walls.map(\.bottomY), [0, 0, 0])
        XCTAssertEqual(geometry.walls.map(\.topY), [60, 60, 60])
        XCTAssertEqual(geometry.floors.count, 1)
        XCTAssertEqual(Array(observation.features[144...]), [1, -0.1, -0.4, 0.2, 0, 1, 0.4, -0.4, 0.2, 0, 1])
        XCTAssertEqual(try JSONDecoder().decode(BotObservation.self, from: JSONEncoder().encode(observation)), observation)
        document.walls = nil
        cache.update(document: document)
        let empty = try XCTUnwrap(source.makeFrame(try match(), now: 100.02)).observation
        XCTAssertEqual(empty.authoredTerrain?.walls, [])
        XCTAssertEqual(Array(empty.features[145...]), Array(repeating: 0, count: 10))
    }

    func testHackerClipStoresVisibleAndBrokenCocoonOnTheirOwnFrames() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = BotDemonstrationRecorder(rootURL: root)
        let clip = try recorder.start(gameMode: "modded", detectorVersion: "receiver-actors-v1", at: 100, domain: .hacker)
        let source = BotHackerObservationSource(ground: ground())
        for index in 0..<3 {
            let t = 100.1 + Double(index) * 0.12
            let frame = try match(at: t, frame: 10 + index)
            var sample = frame.sample
            if index == 1 { sample.actors?.lifebloodCocoons = [] }
            if index == 2 { sample.actors?.lifebloodCocoons = nil } // An older receiver.
            let matched = HackerMatchedFrame(capture: frame.capture, sample: sample, sampleObservedAt: frame.sampleObservedAt)
            let captured = try XCTUnwrap(source.makeFrame(matched, now: t + 0.02))
            recorder.record(captured.observation, image: captured.image)
        }
        XCTAssertEqual(try recorder.stop(reason: "user-stop", at: 101), clip)
        let review = try BotSnippet.load(clip)
        XCTAssertEqual(review.observations.count, 3)
        XCTAssertEqual(review.observations.map { $0.lifebloodCocoons?.count }, [1, 0, nil])
    }

    func testMissingMismatchedStaleOrInvalidModEvidenceCannotDriveTheBot() throws {
        let source = BotHackerObservationSource(ground: ground())
        XCTAssertNil(source.makeFrame(try match(captureFrame: 11), now: 100.02))
        let old = try XCTUnwrap(source.makeFrame(try match(actors: false), now: 100.02))
        XCTAssertTrue(old.observation.issues.contains("missing-hacker-actor-telemetry"))
        let missing = try XCTUnwrap(source.makeFrame(try match(hero: false), now: 100.02))
        XCTAssertTrue(missing.observation.issues.contains("missing-hacker-knight"))
        let invalid = try XCTUnwrap(source.makeFrame(try match(enemyX: .nan), now: 100.02))
        XCTAssertTrue(invalid.observation.issues.contains("invalid-hacker-actors"))
        let stale = try XCTUnwrap(source.makeFrame(try match(), now: 100.5))
        XCTAssertTrue(stale.observation.issues.contains("stale-capture"))
    }

    func testContinuousModActorAndGreenFloorDecisionsIgnoreSlowVisualSolves() throws {
        let source = BotHackerObservationSource(ground: ground())
        let policy = try BotNativePolicy(document: .init(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: "Synthetic continuous input", featureNames: BotObservationContract.legacyFeatureNames,
            historyLength: 4, actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "test",
            layers: [.init(inputSize: 576, outputSize: 10, activation: "linear",
                weights: .init(repeating: 0, count: 5760), biases: [0, 1, 0, 1, 0, 0, 0, 1, 1, 0])]))
        var now = 100.0, applied = 0, released = 0, gate = BotObservationPublicationGate()
        let controller = BotPlaybackController(policy: policy, now: { now }, hasFocus: { true })
        controller.configure(canContinue: { true }, begin: { UUID() }, apply: { _, deadline, _ in
            XCTAssertGreaterThan(deadline, now); applied += 1; return true
        }, neutralize: { _ in released += 1 }, end: { _ in })
        controller.start(); defer { controller.stop() }
        func drain() {
            let done = expectation(description: "Main-thread input")
            DispatchQueue.main.async { done.fulfill() }; wait(for: [done], timeout: 1)
        }
        for index in 0..<150 {
            let timestamp = 100 + Double(index) * 0.1; now = timestamp + 0.02
            let frame = try XCTUnwrap(source.makeFrame(try match(at: timestamp, frame: index), now: now))
            XCTAssertTrue(gate.accepts(frame.observation, currentGeneration: 1))
            controller.observe(frame.observation); drain()
            let delayed = try XCTUnwrap(source.makeFrame(try match(at: timestamp - 1, frame: index), now: now))
            XCTAssertFalse(gate.accepts(delayed.observation, currentGeneration: 1))
        }
        XCTAssertEqual(applied, 150); XCTAssertEqual(controller.humanTerrainDecisionCount, 150)
        XCTAssertEqual(controller.hackerKnightDecisionCount, 150); XCTAssertFalse(controller.isWaiting)
        now = 115.02
        controller.observe(try XCTUnwrap(source.makeFrame(try match(at: 115, frame: 150, hero: false), now: now)).observation)
        drain(); XCTAssertTrue(controller.isWaiting); XCTAssertEqual(released, 1)
        now = 115.12
        controller.observe(try XCTUnwrap(source.makeFrame(try match(at: 115.1, frame: 151), now: now)).observation)
        drain(); XCTAssertEqual(applied, 151); XCTAssertFalse(controller.isWaiting)
    }
}
