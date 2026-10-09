import CoreGraphics
import Foundation

struct ConnectedGroundTruthCameraSample: Equatable, Sendable {
    let frameKey: Int
    let sceneName: String
    let cameraPosition: CGPoint
    let observedAt: TimeInterval
}

/// Converts scene-local mod camera coordinates into the same connected pixel
/// space as Hacker Atlas. Scene joins use the Knight's projected doorway point;
/// returning to a known scene reuses its original anchor.
struct ConnectedGroundTruthCameraTracker {
    private struct PlayableReference {
        let cameraPosition: CGPoint
        let heroScreenPoint: CGPoint?
        let velocity: CGVector
        let facingRight: Bool
    }

    private var anchors = [String: CGPoint]()
    private var lastPlayableReference: PlayableReference?

    mutating func reset() {
        anchors.removeAll(keepingCapacity: true)
        lastPlayableReference = nil
    }

    mutating func observe(
        _ sample: ReceiverGroundTruthSample,
        observedAt: TimeInterval,
        frameSize: CGSize
    ) -> ConnectedGroundTruthCameraSample? {
        guard let transform = GroundTruthCameraTransform(
            sample: sample,
            observedAt: observedAt,
            frameSize: frameSize
        ) else { return nil }
        let rawPosition = HackerAtlasCameraPlacement.cameraPosition(transform)
        let heroPoint = sample.heroAvailable
            ? Self.heroScreenPoint(sample, frameSize: frameSize) : nil
        let suppliesProjection = sample.heroScreenX != nil
            && sample.heroScreenY != nil
            && (sample.projectionPixelWidth ?? 0) > 0
            && (sample.projectionPixelHeight ?? 0) > 0
        let canInitializeScene = sample.heroAvailable
            && (heroPoint != nil || !suppliesProjection)

        let anchor: CGPoint
        if let existing = anchors[sample.sceneName] {
            anchor = existing
        } else {
            guard canInitializeScene else { return nil }
            let origin = transitionOrigin(
                frameSize: frameSize,
                heroScreenPoint: heroPoint
            )
            anchor = CGPoint(
                x: rawPosition.x - origin.x,
                y: rawPosition.y - origin.y
            )
            anchors[sample.sceneName] = anchor
        }
        let connected = CGPoint(
            x: rawPosition.x - anchor.x,
            y: rawPosition.y - anchor.y
        )
        if canInitializeScene {
            lastPlayableReference = PlayableReference(
                cameraPosition: connected,
                heroScreenPoint: heroPoint,
                velocity: CGVector(dx: sample.velocityX, dy: sample.velocityY),
                facingRight: sample.facingRight
            )
        }
        return ConnectedGroundTruthCameraSample(
            frameKey: HackerFrameSynchronizer.key(for: sample.unityFrame),
            sceneName: sample.sceneName,
            cameraPosition: connected,
            observedAt: observedAt
        )
    }

    private func transitionOrigin(
        frameSize: CGSize,
        heroScreenPoint: CGPoint?
    ) -> CGPoint {
        guard let previous = lastPlayableReference else { return .zero }
        if let oldHero = previous.heroScreenPoint,
           let newHero = heroScreenPoint {
            return CGPoint(
                x: previous.cameraPosition.x + oldHero.x - newHero.x,
                y: previous.cameraPosition.y + oldHero.y - newHero.y
            )
        }
        let overlapX = min(32, (frameSize.width * 0.1).rounded())
        let overlapY = min(24, (frameSize.height * 0.1).rounded())
        if abs(previous.velocity.dy) > abs(previous.velocity.dx),
           abs(previous.velocity.dy) > 0.05 {
            return CGPoint(
                x: previous.cameraPosition.x,
                y: previous.cameraPosition.y + (previous.velocity.dy > 0
                    ? frameSize.height - overlapY
                    : -frameSize.height + overlapY)
            )
        }
        let exitsRight = abs(previous.velocity.dx) > 0.05
            ? previous.velocity.dx > 0 : previous.facingRight
        return CGPoint(
            x: previous.cameraPosition.x + (exitsRight
                ? frameSize.width - overlapX
                : -frameSize.width + overlapX),
            y: previous.cameraPosition.y
        )
    }

    private static func heroScreenPoint(
        _ sample: ReceiverGroundTruthSample,
        frameSize: CGSize
    ) -> CGPoint? {
        guard let x = sample.heroScreenX,
              let y = sample.heroScreenY,
              let width = sample.projectionPixelWidth,
              let height = sample.projectionPixelHeight,
              width > 0, height > 0 else { return nil }
        let point = CGPoint(
            x: x * frameSize.width / CGFloat(width),
            y: y * frameSize.height / CGFloat(height)
        )
        guard point.x >= 0, point.x <= frameSize.width,
              point.y >= 0, point.y <= frameSize.height else { return nil }
        return point
    }
}

struct PlaybackDivergenceDecision: Equatable, Sendable {
    enum Cause: String, Equatable, Sendable {
        case poseError
        case visionStall
    }

    let cause: Cause
    let errorPixels: CGFloat
    let thresholdPixels: CGFloat
    let sceneName: String
    let consecutiveSamples: Int
    let silenceSeconds: TimeInterval?
}

/// Exact-frame watchdog used only by controlled replay evaluation. It never
/// feeds truth into the visual solver; it releases replay controls after a
/// sustained quarter-screen disagreement or a moving interval with no exact
/// Vision pose so a failed trial cannot keep painting a known-bad atlas.
final class PlaybackDivergenceMonitor: @unchecked Sendable {
    struct VisionSample {
        let cameraPosition: CGPoint
        let observedAt: TimeInterval
        let isTransitioning: Bool
    }

    static let thresholdPixels: CGFloat = 160
    static let requiredConsecutiveSamples = 12
    static let startupGrace: TimeInterval = 5
    static let sceneChangeGrace: TimeInterval = 0.25
    /// Exact-frame pose checks cannot fire while Vision publishes no frame.
    /// Hacker truth therefore also stops a moving replay after a bounded blind
    /// interval. Stationary scenes remain exempt.
    static let maximumVisionSilence: TimeInterval = 0.75
    static let minimumTruthTravelDuringSilence: CGFloat = 48
    private static let capacity = 240

    private let lock = NSLock()
    private var connector = ConnectedGroundTruthCameraTracker()
    private var truth = [Int: ConnectedGroundTruthCameraSample]()
    private var vision = [Int: VisionSample]()
    private var truthOrder = [Int]()
    private var visionOrder = [Int]()
    private var accepting = false
    private var evaluating = false
    private var startedAt: TimeInterval = 0
    private var baselineOffset: CGPoint?
    private var lastSceneName: String?
    private var sceneGraceUntil: TimeInterval = 0
    private var consecutiveDivergentSamples = 0
    private var watchdogTruthPosition: CGPoint?
    private var watchdogObservedAt: TimeInterval?
    private var watchdogSceneName: String?
    private var unmatchedTruthSamples = 0
    private var triggered = false

    var isAcceptingSamples: Bool {
        lock.withLock { accepting }
    }

    func prepare() {
        lock.withLock {
            connector.reset()
            clearPairs()
            accepting = true
            evaluating = false
            startedAt = 0
            baselineOffset = nil
            lastSceneName = nil
            sceneGraceUntil = 0
            consecutiveDivergentSamples = 0
            clearVisionWatchdog()
            triggered = false
        }
    }

    func beginEvaluation(at timestamp: TimeInterval) {
        lock.withLock {
            guard accepting else { return }
            // Preparation can begin while the game is still at the previous
            // trial's endpoint. Discard those scene anchors after the exact
            // checkpoint has been verified so the one-way run always starts
            // with its recorded first room as connected-space origin.
            connector.reset()
            clearPairs()
            evaluating = true
            startedAt = timestamp
            baselineOffset = nil
            lastSceneName = nil
            sceneGraceUntil = timestamp + Self.startupGrace
            consecutiveDivergentSamples = 0
            clearVisionWatchdog()
            triggered = false
        }
    }

    func stop() {
        lock.withLock {
            connector.reset()
            clearPairs()
            accepting = false
            evaluating = false
            baselineOffset = nil
            lastSceneName = nil
            consecutiveDivergentSamples = 0
            clearVisionWatchdog()
            triggered = false
        }
    }

    func appendGroundTruth(
        _ sample: ReceiverGroundTruthSample,
        observedAt: TimeInterval,
        frameSize: CGSize
    ) -> PlaybackDivergenceDecision? {
        lock.withLock {
            guard accepting,
                  let connected = connector.observe(
                    sample, observedAt: observedAt, frameSize: frameSize
                  ) else { return nil }
            let key = connected.frameKey
            insert(connected, key: key, values: &truth, order: &truthOrder)
            guard let captured = vision.removeValue(forKey: key) else {
                return evaluateVisionSilence(connected)
            }
            visionOrder.removeAll { $0 == key }
            truth.removeValue(forKey: key)
            truthOrder.removeAll { $0 == key }
            return evaluate(connected, captured)
        }
    }

    func appendVision(
        frameKey: Int,
        cameraPosition: CGPoint,
        observedAt: TimeInterval,
        isTransitioning: Bool
    ) -> PlaybackDivergenceDecision? {
        lock.withLock {
            guard accepting else { return nil }
            let captured = VisionSample(
                cameraPosition: cameraPosition,
                observedAt: observedAt,
                isTransitioning: isTransitioning
            )
            insert(captured, key: frameKey, values: &vision, order: &visionOrder)
            guard let connected = truth.removeValue(forKey: frameKey) else { return nil }
            truthOrder.removeAll { $0 == frameKey }
            vision.removeValue(forKey: frameKey)
            visionOrder.removeAll { $0 == frameKey }
            return evaluate(connected, captured)
        }
    }

    private func evaluate(
        _ groundTruth: ConnectedGroundTruthCameraSample,
        _ visual: VisionSample
    ) -> PlaybackDivergenceDecision? {
        guard evaluating, !triggered else { return nil }
        watchdogTruthPosition = groundTruth.cameraPosition
        watchdogObservedAt = max(groundTruth.observedAt, visual.observedAt)
        watchdogSceneName = groundTruth.sceneName
        unmatchedTruthSamples = 0
        if baselineOffset == nil {
            baselineOffset = CGPoint(
                x: visual.cameraPosition.x - groundTruth.cameraPosition.x,
                y: visual.cameraPosition.y - groundTruth.cameraPosition.y
            )
            lastSceneName = groundTruth.sceneName
            return nil
        }
        if groundTruth.sceneName != lastSceneName {
            lastSceneName = groundTruth.sceneName
            sceneGraceUntil = visual.observedAt + Self.sceneChangeGrace
            consecutiveDivergentSamples = 0
            return nil
        }
        guard visual.observedAt >= startedAt + Self.startupGrace,
              visual.observedAt >= sceneGraceUntil,
              !visual.isTransitioning,
              let baselineOffset else {
            consecutiveDivergentSamples = 0
            return nil
        }
        let error = hypot(
            visual.cameraPosition.x - baselineOffset.x
                - groundTruth.cameraPosition.x,
            visual.cameraPosition.y - baselineOffset.y
                - groundTruth.cameraPosition.y
        )
        if error > Self.thresholdPixels {
            consecutiveDivergentSamples += 1
        } else {
            consecutiveDivergentSamples = 0
        }
        guard consecutiveDivergentSamples >= Self.requiredConsecutiveSamples else {
            return nil
        }
        triggered = true
        return PlaybackDivergenceDecision(
            cause: .poseError,
            errorPixels: error,
            thresholdPixels: Self.thresholdPixels,
            sceneName: groundTruth.sceneName,
            consecutiveSamples: consecutiveDivergentSamples,
            silenceSeconds: nil
        )
    }

    private func evaluateVisionSilence(
        _ groundTruth: ConnectedGroundTruthCameraSample
    ) -> PlaybackDivergenceDecision? {
        guard evaluating, !triggered else { return nil }
        unmatchedTruthSamples += 1
        guard watchdogSceneName == groundTruth.sceneName,
              let referencePosition = watchdogTruthPosition,
              let referenceObservedAt = watchdogObservedAt else {
            watchdogTruthPosition = groundTruth.cameraPosition
            watchdogObservedAt = groundTruth.observedAt
            watchdogSceneName = groundTruth.sceneName
            unmatchedTruthSamples = 1
            return nil
        }
        let silence = groundTruth.observedAt - referenceObservedAt
        guard groundTruth.observedAt >= startedAt + Self.startupGrace,
              silence >= Self.maximumVisionSilence else { return nil }
        let travel = hypot(
            groundTruth.cameraPosition.x - referencePosition.x,
            groundTruth.cameraPosition.y - referencePosition.y
        )
        guard travel >= Self.minimumTruthTravelDuringSilence else { return nil }
        triggered = true
        return PlaybackDivergenceDecision(
            cause: .visionStall,
            errorPixels: travel,
            thresholdPixels: Self.minimumTruthTravelDuringSilence,
            sceneName: groundTruth.sceneName,
            consecutiveSamples: unmatchedTruthSamples,
            silenceSeconds: silence
        )
    }

    private func insert<Value>(
        _ value: Value,
        key: Int,
        values: inout [Int: Value],
        order: inout [Int]
    ) {
        if values[key] == nil { order.append(key) }
        values[key] = value
        while order.count > Self.capacity {
            values.removeValue(forKey: order.removeFirst())
        }
    }

    private func clearPairs() {
        truth.removeAll(keepingCapacity: true)
        vision.removeAll(keepingCapacity: true)
        truthOrder.removeAll(keepingCapacity: true)
        visionOrder.removeAll(keepingCapacity: true)
    }

    private func clearVisionWatchdog() {
        watchdogTruthPosition = nil
        watchdogObservedAt = nil
        watchdogSceneName = nil
        unmatchedTruthSamples = 0
    }
}
