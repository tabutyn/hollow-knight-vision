import CoreGraphics
import Foundation

/// Capture-matched visual camera coordinates, in frame-width units. This is
/// visual odometry, never the mod's privileged camera or actor telemetry.
struct BotVisualTrackingContext: Codable, Equatable {
    let frameWidth: Double
    let frameHeight: Double
    let cameraX: Double?
    let cameraY: Double?
    var camera: CGPoint? {
        guard let cameraX, let cameraY, cameraX.isFinite, cameraY.isFinite else { return nil }
        return CGPoint(x: cameraX, y: cameraY)
    }
}

struct BotKnightPrediction: Codable, Equatable {
    let lastSeenAt: Double
    let age: Double
    let uncertainty: Double
    let cameraCompensated: Bool
    let observedIssues: [String]
    var usedWeakDetection: Bool? = nil
    var maximumAge: Double { cameraCompensated ? BotVisualKnightMemory.maximumPredictionAge : 0.35 }
}

/// A short-lived belief about the visible Knight. Predictions never refresh
/// their own age, and a fresh capture/terrain analysis is still mandatory.
struct BotVisualKnightMemory {
    static let maximumPredictionAge = 0.6
    private struct Track {
        let observation: BotObservation
        let seenAt: Double
        let camera: CGPoint?
        let position: CGPoint
        let velocity: CGVector
        let accelerationY: Double
        let velocityError: Double
        let hasVelocity: Bool
        let action: BotAction?
    }
    private var track: Track?
    private var latestTimestamp: Double?

    mutating func reset() { track = nil; latestTimestamp = nil }

    mutating func resolve(_ raw: BotObservation, previousAction: BotAction? = nil,
                          terrain: [BotTerrainSegment]? = nil,
                          detections: LiveObjectDetectionBatch? = nil) -> BotObservation {
        guard raw.terrainSource != "human-authored", raw.knightSource != "hacker-frame" else {
            reset(); return raw
        }
        guard raw.features.count == BotObservationContract.featureCount,
              raw.features.allSatisfy(\.isFinite),
              raw.captureTimestamp.isFinite, raw.capturedAt.isFinite, raw.availableAt.isFinite,
              (0...BotObservationContract.maximumCaptureAge).contains(raw.availableAt - raw.capturedAt),
              raw.visualContext.map({ $0.frameWidth.isFinite && $0.frameWidth > 0
                  && $0.frameHeight.isFinite && $0.frameHeight > 0 }) == true else {
            reset(); return raw
        }
        if let latestTimestamp, raw.captureTimestamp <= latestTimestamp { return raw }
        latestTimestamp = raw.captureTimestamp
        if let track, track.observation.generation != raw.generation || track.observation.roomID != raw.roomID
            || track.observation.visualContext?.frameHeight != raw.visualContext?.frameHeight
            || track.observation.visualContext?.frameWidth != raw.visualContext?.frameWidth {
            self.track = nil
        }
        let bridgeable = Set(["missing-knight", "stale-or-missing-detection"])
        guard raw.issues.allSatisfy(bridgeable.contains) else { track = nil; return raw }
        let measured = raw.knightSource == "visual" && !raw.issues.contains("missing-knight")
            && !raw.issues.contains("stale-or-missing-detection") && raw.features[4] >= 0.5
            && (0..<1).contains(raw.features[2]) && raw.features[2] > 0
            && (0..<raw.visualContext!.frameHeight).contains(raw.features[3]) && raw.features[3] > 0
        if measured {
            remember(raw, action: previousAction)
            return raw
        }
        guard raw.issues.contains("missing-knight"), let track else { return raw }
        let age = raw.captureTimestamp - track.seenAt
        let elapsed = raw.captureTimestamp - track.observation.captureTimestamp
        let camera = raw.visualContext?.camera
        let compensated = camera != nil && track.camera != nil
        let horizon = compensated ? Self.maximumPredictionAge : 0.35
        guard age > 0, age + raw.availableAt - raw.capturedAt < horizon, elapsed > 0 else { return raw }

        // A control change is an uncertainty source, not a fabricated physics
        // measurement. Continue recent motion only while that belief is useful.
        let changedControl = previousAction.map { action in
            track.action.map { action.values[0] != $0.values[0] || action.values[2] != $0.values[2] } ?? false
        } ?? false
        let uncertainty = 0.003 + (compensated ? 0.025 : 0.07) * age + 0.045 * age * age
            + track.velocityError * elapsed + (track.hasVelocity ? 0 : 0.035 * age)
            + (changedControl ? 0.018 : 0)
        guard uncertainty <= 0.06 else { return raw }
        var x = track.position.x + track.velocity.dx * elapsed
        var y = track.position.y + track.velocity.dy * elapsed + 0.5 * track.accelerationY * elapsed * elapsed
        if compensated, let camera { x -= camera.x; y -= camera.y }
        else if let oldCamera = track.camera { x -= oldCamera.x; y -= oldCamera.y }
        let halfWidth = track.observation.features[2] / 2
        let halfHeight = track.observation.features[3] / 2
        let frameHeight = raw.visualContext!.frameHeight
        guard (halfWidth...(1 - halfWidth)).contains(x),
              (halfHeight...(frameHeight - halfHeight)).contains(y) else { return raw }

        let lines: [BotTerrainSegment]
        if let terrain {
            let width = raw.visualContext!.frameWidth
            lines = terrain.filter { [$0.minimumX, $0.maximumX, $0.y, $0.confidence].allSatisfy(\.isFinite)
                && $0.maximumX > $0.minimumX && (0...1).contains($0.confidence) }.map {
                    BotTerrainSegment(minimumX: $0.minimumX / width, maximumX: $0.maximumX / width,
                        y: $0.y / width, confidence: $0.confidence)
                }
        } else {
            lines = (0..<BotObservationContract.terrainCapacity).compactMap { i in
                let base = 16 + i * 5
                guard raw.features[base + 4] > 0 else { return nil }
                return BotTerrainSegment(minimumX: raw.features[0] + raw.features[base],
                    maximumX: raw.features[0] + raw.features[base + 1],
                    y: raw.features[1] + raw.features[base + 2], confidence: raw.features[base + 3])
            }
        }
        // Prevent a falling estimate from passing through a currently observed
        // floor. No floor is invented across a pit or under an ascending jump.
        let verticalVelocity = track.velocity.dy + track.accelerationY * elapsed
        let oldY = track.position.y - (compensated ? (camera?.y ?? 0) : (track.camera?.y ?? 0))
        if verticalVelocity <= 0 {
            let floors = lines.compactMap { line -> Double? in
                guard line.confidence >= 0.65, x + halfWidth > line.minimumX, x - halfWidth < line.maximumX,
                      oldY - halfHeight >= line.y - 0.006, y - halfHeight <= line.y else { return nil }
                return line.y
            }
            if let floor = floors.max() { y = floor + halfHeight }
        }
        // Motion alone can keep the estimate on the floor during a real jump.
        // A fresh, size-compatible image cue can locate this predicted frame
        // without refreshing the timestamp or confidence of the last sighting.
        let cue = weakKnightCenter(in: detections, for: raw, track: track, estimate: CGPoint(x: x, y: y))
        if let cue { x = cue.x; y = cue.y }
        var features = raw.features
        features[0] = x; features[1] = y
        features[2] = track.observation.features[2]; features[3] = track.observation.features[3]
        features[4] = max(0.2, track.observation.features[4] * (1 - 0.75 * age / horizon))
        features[5] = min(1, age / BotObservationContract.maximumDetectionAge)
        // Re-select current floors around the estimate, rather than the missing
        // raw center (0,0). Current enemies must also be rebased before inference.
        features.replaceSubrange(16..<96, with: repeatElement(0, count: 80))
        let nearest = lines.sorted {
            let a = hypot(($0.minimumX + $0.maximumX) / 2 - x, $0.y - y)
            let b = hypot(($1.minimumX + $1.maximumX) / 2 - x, $1.y - y)
            if a != b { return a < b }
            if $0.y != $1.y { return $0.y < $1.y }
            return $0.minimumX < $1.minimumX
        }
        for (i, line) in nearest.prefix(BotObservationContract.terrainCapacity).enumerated() {
            let base = 16 + i * 5
            features.replaceSubrange(base..<(base + 5), with: [line.minimumX - x, line.maximumX - x,
                line.y - y, line.confidence, 1])
        }
        for i in 0..<BotObservationContract.enemyCapacity {
            let base = 96 + i * 6
            guard features[base + 5] > 0 else { continue }
            features[base] += raw.features[0] - x; features[base + 1] += raw.features[1] - y
        }
        var resolved = BotObservation(type: raw.type, captureTimestamp: raw.captureTimestamp,
            capturedAt: raw.capturedAt, availableAt: raw.availableAt, generation: raw.generation,
            roomID: raw.roomID, roomRevision: raw.roomRevision, detectionTimestamp: raw.detectionTimestamp,
            detectionFrameID: raw.detectionFrameID, modelVersion: raw.modelVersion,
            bestKnightConfidence: raw.bestKnightConfidence,
            features: features.map { min(4, max(-4, $0)) }, issues: [], imagePath: raw.imagePath,
            terrainSource: raw.terrainSource, humanTerrainCount: raw.humanTerrainCount,
            knightSource: "visual-predicted", enemySource: raw.issues.contains("stale-or-missing-detection") ? "visual-unavailable" : raw.enemySource)
        resolved.geoCaches = raw.geoCaches
        resolved.visualContext = raw.visualContext
        resolved.knightPrediction = BotKnightPrediction(lastSeenAt: track.seenAt, age: age,
            uncertainty: uncertainty, cameraCompensated: compensated, observedIssues: raw.issues,
            usedWeakDetection: cue == nil ? nil : true)
        return resolved
    }

    private func weakKnightCenter(in batch: LiveObjectDetectionBatch?, for raw: BotObservation,
                                  track: Track, estimate: CGPoint) -> CGPoint? {
        guard let batch, let context = raw.visualContext, batch.captureGeneration == raw.generation,
              batch.sourceFrameIdentifier == raw.detectionFrameID, batch.sourceTimestamp > track.seenAt,
              (0...0.05).contains(raw.captureTimestamp - batch.sourceTimestamp) else { return nil }
        let width = track.observation.features[2], height = track.observation.features[3]
        var best: (center: CGPoint, confidence: Double)?
        for candidate in batch.detections where (0.16..<0.5).contains(candidate.confidence)
            && candidate.sourceFrameIdentifier == batch.sourceFrameIdentifier
            && candidate.modelVersion.displayName == track.observation.modelVersion
            && LabelingClassIdentity.matches(candidate.classIdentifier, "game.playable-knight") {
            let rect = candidate.normalizedRect
            let center = CGPoint(x: rect.midX, y: (1 - rect.midY) * context.frameHeight)
            guard (0.42...2.25).contains(rect.width / width),
                  (0.42...2.25).contains(rect.height * context.frameHeight / height),
                  abs(center.x - estimate.x) <= 2 * height, abs(center.y - estimate.y) <= 4 * height,
                  (width / 2...1 - width / 2).contains(center.x),
                  (height / 2...context.frameHeight - height / 2).contains(center.y),
                  candidate.confidence > (best?.confidence ?? 0) else { continue }
            best = (center, candidate.confidence)
        }
        return best?.center
    }

    private mutating func remember(_ observation: BotObservation, action: BotAction?) {
        // A reused detection batch is not a new sighting. In particular, it
        // cannot extend the prediction deadline or manufacture zero velocity.
        guard let seenAt = observation.detectionTimestamp, seenAt.isFinite,
              (0...BotObservationContract.maximumDetectionAge).contains(observation.captureTimestamp - seenAt)
        else { track = nil; return }
        if track?.observation.modelVersion != observation.modelVersion { track = nil }
        if let track, seenAt <= track.seenAt { return }
        let camera = observation.visualContext?.camera
        let position = CGPoint(x: observation.features[0] + (camera?.x ?? 0),
                               y: observation.features[1] + (camera?.y ?? 0))
        var velocity = CGVector.zero, accelerationY = 0.0, error = 0.0, hasVelocity = false
        if let old = track {
            let dt = seenAt - old.seenAt
            let sameBasis = (camera == nil) == (old.camera == nil)
            if sameBasis, dt >= 0.025, dt <= 0.35 {
                let vx = (position.x - old.position.x) / dt
                let vy = (position.y - old.position.y) / dt
                // Reacquisition/teleport outliers seed a new track, rather than
                // becoming extreme velocity or a long-lived predicted actor.
                if abs(vx) <= 0.8, abs(vy) <= 1.5 {
                    velocity = CGVector(dx: vx, dy: vy); hasVelocity = true
                    if old.hasVelocity {
                        accelerationY = min(0, max(-4, (vy - old.velocity.dy) / dt))
                        error = min(0.15, hypot(vx - old.velocity.dx, vy - old.velocity.dy) * 0.1)
                    }
                }
            }
        }
        track = Track(observation: observation, seenAt: seenAt, camera: camera, position: position,
            velocity: velocity, accelerationY: accelerationY, velocityError: error, hasVelocity: hasVelocity, action: action)
    }
}
