import CoreGraphics
import Foundation

struct BotHackerObservationFrame {
    let observation: BotObservation
    let image: CGImage
}

/// Hacker input comes directly from one rendered capture and its exact mod
/// sample. Visual actor inference and the ground solver never gate this path.
final class BotHackerObservationSource: @unchecked Sendable {
    private let lock = NSLock()
    private let ground: BotHumanGroundTerrainCache
    private var sceneIDs = [String: Int]()

    init(ground: BotHumanGroundTerrainCache) { self.ground = ground }
    func reset() { lock.lock(); sceneIDs = [:]; lock.unlock() }

    private var sampling = false
    private var sampleEpoch: UInt64?
    private var lastSample = -Double.infinity

    func beginSample(at timestamp: Double, epoch: UInt64) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !sampling, sampleEpoch != epoch || timestamp - lastSample >= BotObservationContract.sampleInterval - 0.005 else { return false }
        sampling = true
        lastSample = timestamp
        sampleEpoch = epoch
        return true
    }

    func endSample() { lock.lock(); sampling = false; lock.unlock() }

    func makeFrame(_ match: HackerMatchedFrame, now: @autoclosure () -> Double) -> BotHackerObservationFrame? {
        let capture = match.capture, sample = match.sample
        guard capture.unityFrame == HackerFrameSynchronizer.key(for: sample.unityFrame),
              let observation = makeObservation(sample, size: CGSize(width: capture.image.width, height: capture.image.height),
                  timestamp: capture.capturedAt, observedAt: match.sampleObservedAt, generation: capture.captureGeneration,
                  drive: capture.horizontalInput, availableAt: now()) else { return nil }
        return BotHackerObservationFrame(observation: observation, image: capture.image)
    }

    func makeObservation(_ sample: ReceiverGroundTruthSample, size: CGSize, timestamp: Double, observedAt: Double,
                         generation: UInt64, drive: Int, availableAt: @autoclosure () -> Double) -> BotObservation? {
        guard sample.hasFiniteCoordinates,
              let transform = GroundTruthCameraTransform(sample: sample, observedAt: observedAt, frameSize: size) else { return nil }
        let geometry = ground.geometry(transform: transform, frameSize: size,
            timestamp: timestamp, generation: generation)
        lock.lock()
        if sceneIDs[transform.sceneName] == nil { sceneIDs[transform.sceneName] = 1_000_000 + sceneIDs.count }
        let roomID = sceneIDs[transform.sceneName]!
        lock.unlock()
        var actorInput = BotModActorInput(frameID: UInt64(HackerFrameSynchronizer.key(for: sample.unityFrame)), knight: nil, enemies: nil)
        if let actors = sample.actors, actors.version == 1,
           let projectionWidth = sample.projectionPixelWidth, let projectionHeight = sample.projectionPixelHeight,
           projectionWidth > 0, projectionHeight > 0 {
            let scaleX = Double(size.width) / Double(projectionWidth)
            let scaleY = Double(size.height) / Double(projectionHeight)
            func project(_ actor: ReceiverActorState) -> BotActorBox {
                .init(centerX: actor.screenX * scaleX, centerY: actor.screenY * scaleY,
                    width: actor.width * scaleX, height: actor.height * scaleY)
            }
            let valid = actors.enemies.count <= 64 && actors.enemies.allSatisfy(\.isValid)
                && Set(actors.enemies.map(\.id)).count == actors.enemies.count
                && (actors.hero?.isValid ?? true)
            if valid {
                actorInput = BotModActorInput(frameID: UInt64(HackerFrameSynchronizer.key(for: sample.unityFrame)),
                    knight: sample.heroAvailable ? actors.hero.map(project) : nil,
                    enemies: actors.enemies.map(project),
                    geoCaches: actors.geoCaches?.filter(\.isValid).map(project),
                    lifebloodCocoons: actors.lifebloodCocoons?.filter(\.isValid).map(project))
            } else { actorInput.issues = ["invalid-hacker-actors"] }
        } else { actorInput.issues = ["missing-hacker-actor-telemetry"] }
        var observation = BotObservationBuilder.make(captureTimestamp: timestamp,
            capturedAt: timestamp, availableAt: availableAt(), generation: generation,
            roomID: roomID, roomRevision: 0, frameSize: size, detections: nil,
            terrain: geometry.floors, terrainValid: !geometry.floors.isEmpty || !geometry.walls.isEmpty, transitioning: false,
            terrainSource: "human-authored", humanTerrainCount: geometry.floors.count, modActors: actorInput)
        if actorInput.issues.isEmpty { observation.enemyOutcomes = sample.actors?.enemyOutcomes }
        observation.blueHealthOutcomes = sample.blueHealthOutcomes
        observation.authoredTerrain = geometry
        observation.blockadePose = BotBlockadePose.hacker(sample)
        observation.driveDirection = drive
        observation.grounded = sample.heroAvailable ? sample.grounded : nil
        let wallFeatures = BotWallEvidence.features(segments: observation.features[4] > 0 ? geometry.walls : nil,
            knightX: observation.features[0], knightY: observation.features[1], width: geometry.width)
        observation.features.replaceSubrange(BotObservationContract.legacyFeatureCount..., with: wallFeatures)
        return observation
    }
}

/// A delayed solve cannot replace newer input or clear its temporal history.
struct BotObservationPublicationGate {
    private var generation: UInt64?
    private var timestamp = -Double.infinity
    mutating func accepts(_ observation: BotObservation, currentGeneration: UInt64) -> Bool {
        guard observation.generation == currentGeneration, observation.captureTimestamp.isFinite else { return false }
        if generation != currentGeneration { generation = currentGeneration; timestamp = -Double.infinity }
        guard observation.captureTimestamp - timestamp >= BotObservationContract.sampleInterval - 0.005 else { return false }
        timestamp = observation.captureTimestamp
        return true
    }
}
