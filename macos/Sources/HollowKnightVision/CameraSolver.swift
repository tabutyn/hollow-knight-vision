import CoreGraphics
import Foundation

struct CameraSample: Identifiable, Equatable {
    let id: Int
    let position: CGPoint
    let confidence: Float
}

enum CameraUpdateState: Equatable {
    case accepted
    case heldLowConfidence
    case heldSceneChange
    case invalid
}

struct CameraUpdate: Equatable {
    let state: CameraUpdateState
    let rawStep: CGVector
    let acceptedStep: CGVector
    let position: CGPoint
}

enum LiveRegistrationAdmission {
    static func requiresRecovery(_ update: CameraUpdate?) -> Bool {
        update?.state != .accepted
    }

    static func canWriteAtlas(
        _ update: CameraUpdate?,
        worldState: WorldBasisController.TrackingState
    ) -> Bool {
        update?.state == .accepted && worldState == .tracking
    }
}

/// An isolated weak Vision result pauses atlas writes for that frame, but
/// does not discard a previously verified world placement. A hard scene jump
/// still enters recovery immediately.
struct LiveRegistrationContinuity {
    static let softFailureLimit = 3
    private(set) var consecutiveSoftFailures = 0

    mutating func reset() {
        consecutiveSoftFailures = 0
    }

    mutating func shouldEnterRecovery(
        after update: CameraUpdate?, hasGameplaySignal: Bool
    ) -> Bool {
        guard hasGameplaySignal else {
            consecutiveSoftFailures += 1
            return consecutiveSoftFailures >= Self.softFailureLimit
        }
        switch update?.state {
        case .accepted:
            consecutiveSoftFailures = 0
            return false
        case .heldSceneChange, .invalid:
            consecutiveSoftFailures = 0
            return true
        case .heldLowConfidence, .none:
            consecutiveSoftFailures += 1
            return consecutiveSoftFailures >= Self.softFailureLimit
        }
    }
}

/// A new atlas has no committed geometry capable of exposing a bad initial
/// pose. Hold its first write until the ground solve has remained stationary
/// for a rolling window. Continuity alone is insufficient: a slowly drifting
/// startup solve can otherwise become permanent evidence before it settles.
/// A relocalization jump or accumulated drift starts a fresh window at the new
/// pose before any frame can enter the atlas.
struct FreshAtlasBootstrapGate {
    static let requiredStableSamples = 45
    static let maximumPoseStep: CGFloat = 12
    static let maximumStepChange: CGFloat = 8
    static let maximumBootstrapDisplacement: CGFloat = 6

    private(set) var stableSampleCount = 0
    private(set) var isReady = false
    private var stabilityOrigin: CGPoint?
    private var previousPosition: CGPoint?
    private var previousStep: CGVector?

    mutating func reset() {
        stableSampleCount = 0
        isReady = false
        stabilityOrigin = nil
        previousPosition = nil
        previousStep = nil
    }

    mutating func allowsFirstAtlasWrite(
        position: CGPoint?,
        registrationAccepted: Bool,
        groundVerified: Bool,
        hasCommittedEvidence: Bool
    ) -> Bool {
        guard !hasCommittedEvidence else {
            isReady = true
            return true
        }
        guard registrationAccepted, groundVerified, let position,
              position.x.isFinite, position.y.isFinite
        else {
            reset()
            return false
        }
        if isReady { return true }

        guard let previousPosition else {
            beginWindow(at: position)
            return false
        }
        let step = CGVector(
            dx: position.x - previousPosition.x,
            dy: position.y - previousPosition.y
        )
        self.previousPosition = position
        let continuousPose = hypot(step.dx, step.dy) <= Self.maximumPoseStep
        let continuousMotion = previousStep.map {
            hypot(step.dx - $0.dx, step.dy - $0.dy) <= Self.maximumStepChange
        } ?? true
        let stayedNearOrigin = stabilityOrigin.map {
            hypot(position.x - $0.x, position.y - $0.y)
                <= Self.maximumBootstrapDisplacement
        } ?? false
        guard continuousPose, continuousMotion, stayedNearOrigin else {
            beginWindow(at: position)
            return false
        }
        previousStep = step
        stableSampleCount += 1
        isReady = stableSampleCount >= Self.requiredStableSamples
        return isReady
    }

    private mutating func beginWindow(at position: CGPoint) {
        stableSampleCount = 1
        stabilityOrigin = position
        previousPosition = position
        previousStep = nil
    }
}

struct CameraAccumulator {
    private(set) var position = CGPoint.zero
    private(set) var samples = [CameraSample(id: 0, position: .zero, confidence: 1)]
    private(set) var lastStep = CGVector.zero
    private var nextID = 1

    var minimumConfidence: Float = 0.10
    /// Depth work can skip source frames, so continuous hallway pans may span
    /// one sixth of the solve width between registrations. The separate
    /// vertical cap still rejects recorded foreground-driven jumps.
    var maximumStepFraction: CGFloat = 0.18
    var maximumVerticalStepFraction: CGFloat = 0.06
    var smoothing: CGFloat = 0.65
    var invertMotion = false
    var sampleLimit = 2_400

    mutating func ingest(
        alignment: CGAffineTransform,
        confidence: Float,
        frameWidth: CGFloat
    ) -> CameraUpdate {
        let direction: CGFloat = invertMotion ? -1 : 1
        let raw = CGVector(dx: alignment.tx * direction, dy: alignment.ty * direction)
        guard raw.dx.isFinite, raw.dy.isFinite, confidence.isFinite else {
            return CameraUpdate(state: .invalid, rawStep: raw, acceptedStep: .zero, position: position)
        }
        guard confidence >= minimumConfidence else {
            lastStep = .zero
            return CameraUpdate(state: .heldLowConfidence, rawStep: raw, acceptedStep: .zero, position: position)
        }
        guard abs(raw.dx) <= max(8, frameWidth * maximumStepFraction),
              abs(raw.dy) <= max(8, frameWidth * maximumVerticalStepFraction) else {
            lastStep = .zero
            return CameraUpdate(state: .heldSceneChange, rawStep: raw, acceptedStep: .zero, position: position)
        }
        let accepted = CGVector(
            dx: raw.dx * smoothing + lastStep.dx * (1 - smoothing),
            dy: raw.dy * smoothing + lastStep.dy * (1 - smoothing)
        )
        lastStep = accepted
        if hypot(accepted.dx, accepted.dy) >= 0.05 {
            position.x += accepted.dx
            position.y += accepted.dy
            samples.append(CameraSample(id: nextID, position: position, confidence: confidence))
            nextID += 1
            if samples.count > sampleLimit {
                samples.removeFirst(samples.count - sampleLimit)
            }
        }
        return CameraUpdate(state: .accepted, rawStep: raw, acceptedStep: accepted, position: position)
    }

    mutating func reset() {
        position = .zero
        samples = [CameraSample(id: 0, position: .zero, confidence: 1)]
        lastStep = .zero
        nextID = 1
    }

    mutating func applyGlobalCorrection(to correctedPosition: CGPoint) {
        guard correctedPosition.x.isFinite, correctedPosition.y.isFinite else { return }
        let correction = hypot(correctedPosition.x - position.x, correctedPosition.y - position.y)
        guard correction >= 0.05 else { return }
        position = correctedPosition
        if correction > 4 { lastStep = .zero }
        if correction >= 0.25 {
            samples.append(CameraSample(id: nextID, position: position, confidence: 1))
            nextID += 1
            if samples.count > sampleLimit {
                samples.removeFirst(samples.count - sampleLimit)
            }
        }
    }

    /// Applies a correction measured for an older pose without discarding
    /// camera motion accumulated after that pose was queued for reconstruction.
    mutating func applyGlobalOffset(_ correction: CGVector) {
        guard correction.dx.isFinite, correction.dy.isFinite else { return }
        applyGlobalCorrection(to: CGPoint(
            x: position.x + correction.dx,
            y: position.y + correction.dy
        ))
    }
}
