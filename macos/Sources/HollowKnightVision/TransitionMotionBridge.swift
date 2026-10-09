import CoreGraphics
import CoreImage
import Foundation
import Vision

struct TransitionFrameDecision: Equatable {
    let admitsTracking: Bool
    let resumedAfterTransition: Bool
}

/// Rejects only frames that contain no usable image signal. Ordinary darkening
/// remains trackable so background parallax can carry camera motion through a
/// floorless passage. After a true blackout, a stable dark room may become the
/// new baseline without assigning it an invented world offset.
struct TransitionFrameGate {
    static let resumeStableFrames = 3

    private var previousMean: Double?
    private var suspended = false
    private var stableFrames = 0

    mutating func reset() {
        previousMean = nil
        suspended = false
        stableFrames = 0
    }

    mutating func observe(_ profile: FrameSignalProfile) -> TransitionFrameDecision {
        let hasRawSignal = profile.hasGameplaySignal

        guard hasRawSignal else {
            suspended = true
            stableFrames = 0
            previousMean = profile.meanPeak
            return TransitionFrameDecision(
                admitsTracking: false,
                resumedAfterTransition: false
            )
        }

        if suspended {
            let stable = previousMean.map {
                abs(profile.meanPeak - $0) <= max(2.5, max(profile.meanPeak, $0) * 0.20)
            } ?? false
            stableFrames = stable ? stableFrames + 1 : 0
            previousMean = profile.meanPeak
            guard stableFrames >= Self.resumeStableFrames else {
                return TransitionFrameDecision(
                    admitsTracking: false,
                    resumedAfterTransition: false
                )
            }
            suspended = false
            stableFrames = 0
            return TransitionFrameDecision(
                admitsTracking: true,
                resumedAfterTransition: true
            )
        }

        previousMean = profile.meanPeak
        return TransitionFrameDecision(
            admitsTracking: true,
            resumedAfterTransition: false
        )
    }
}

struct TransitionMotionBridgeResult: Equatable {
    enum Source: String, Equatable {
        case maskedRegistration
    }

    let cameraPosition: CGPoint
    let cameraStep: CGVector
    let confidence: Float
    let source: Source
}

/// A secondary odometry source used only while confirmed ground is absent.
/// Normal gameplay merely seeds its last frame. Masked image registration
/// therefore runs during the exceptional no-floor interval, never as the
/// primary camera solve.
final class TransitionMotionBridge {
    static let minimumConfidence: Float = 0.2

    private let imageContext = CIContext(options: [.cacheIntermediates: false])
    private var previousFrame: CGImage?
    private var previousExclusions = [CGRect]()
    private var previousMovingForegroundRect: CGRect?
    private var smallHorizontalMotion = TransitionSmallMotionAccumulator()
    private var position: CGPoint?
    private var awaitingSceneResume = false

    func reset() {
        previousFrame = nil
        previousExclusions = []
        previousMovingForegroundRect = nil
        smallHorizontalMotion.reset()
        position = nil
        awaitingSceneResume = false
    }

    func anchor(
        frame: CGImage,
        excluding: [CGRect],
        cameraPosition: CGPoint,
        movingForegroundRect: CGRect? = nil
    ) {
        position = cameraPosition
        awaitingSceneResume = false
        seedTranslation(frame, excluding: excluding)
        previousMovingForegroundRect = movingForegroundRect
        smallHorizontalMotion.reset()
    }

    func suspendForTransition() {
        guard !awaitingSceneResume else { return }
        awaitingSceneResume = true
        previousFrame = nil
        previousExclusions = []
        previousMovingForegroundRect = nil
        smallHorizontalMotion.reset()
    }

    /// Restarts image odometry after a true no-signal interval while retaining
    /// the last measured pose. The first visible frame is reference-only: it
    /// cannot authorize an atlas write because no image evidence spans the
    /// blackout.
    @discardableResult
    func resumeAfterTransition(
        frame: CGImage,
        excluding: [CGRect],
        movingForegroundRect: CGRect? = nil
    ) -> CGPoint? {
        guard awaitingSceneResume else { return nil }
        awaitingSceneResume = false
        seedTranslation(frame, excluding: excluding)
        previousMovingForegroundRect = movingForegroundRect
        smallHorizontalMotion.reset()
        return position
    }

    func track(
        frame: CGImage,
        excluding: [CGRect],
        solveWidth: CGFloat,
        lockVertical: Bool = false,
        movingForegroundRect: CGRect? = nil
    ) -> TransitionMotionBridgeResult? {
        guard !awaitingSceneResume, var next = position,
              let referenceFrame = previousFrame,
              solveWidth.isFinite, solveWidth > 0 else {
            seedTranslation(frame, excluding: excluding)
            return nil
        }
        // Mask the union in both images. Masking each frame only at its own
        // Knight position creates a moving black rectangle that registration
        // can itself mistake for camera motion.
        let comparisonExclusions = previousExclusions + excluding
        let reference = masked(referenceFrame, excluding: comparisonExclusions)
        let current = masked(frame, excluding: comparisonExclusions)
        let priorMovingForegroundRect = previousMovingForegroundRect
        previousFrame = frame
        previousExclusions = excluding
        previousMovingForegroundRect = movingForegroundRect
        do {
            let registration = VNTranslationalImageRegistrationRequest(
                targetedCGImage: current,
                orientation: .up,
                options: [:]
            )
            registration.regionOfInterest = Self.registrationRegionOfInterest
            try VNImageRequestHandler(cgImage: reference, orientation: .up)
                .perform([registration])
            guard let observation = registration.results?.first,
                  observation.confidence >= Self.minimumConfidence else {
                return nil
            }
            let scale = solveWidth / CGFloat(frame.width)
            let transform = observation.alignmentTransform
            let measuredStep = CGVector(
                dx: transform.tx * scale,
                dy: lockVertical ? 0 : transform.ty * scale
            )
            var step = TransitionForegroundResidualGate.adjusted(
                measuredStep,
                previousForeground: priorMovingForegroundRect,
                currentForeground: movingForegroundRect,
                frameWidth: CGFloat(frame.width),
                solveWidth: solveWidth
            )
            step.dx = smallHorizontalMotion.ingest(step.dx)
            guard isPlausible(step, solveWidth: solveWidth) else { return nil }
            next.x += step.dx
            next.y += step.dy
            position = next
            return TransitionMotionBridgeResult(
                cameraPosition: next,
                cameraStep: step,
                confidence: observation.confidence,
                source: .maskedRegistration
            )
        } catch {
            return nil
        }
    }

    private func seedTranslation(_ frame: CGImage, excluding: [CGRect]) {
        previousFrame = frame
        previousExclusions = excluding
    }

    private func isPlausible(_ step: CGVector, solveWidth: CGFloat) -> Bool {
        step.dx.isFinite && step.dy.isFinite
            && abs(step.dx) <= max(8, solveWidth * 0.08)
            && abs(step.dy) <= max(6, solveWidth * 0.05)
    }

    private func masked(_ frame: CGImage, excluding: [CGRect]) -> CGImage {
        let source = CIImage(cgImage: frame)
        let masked = RegistrationFrameMask.applying(
            to: source,
            foregroundRects: excluding,
            presentationSize: CGSize(width: frame.width, height: frame.height)
        )
        return imageContext.createCGImage(masked, from: source.extent) ?? frame
    }

    private static let registrationRegionOfInterest =
        // Exclude most HUD and ground animation while retaining broad scene
        // structure. This is an exceptional fallback only; confirmed ground
        // remains the primary camera solver.
        CGRect(x: 0.06, y: 0.12, width: 0.88, height: 0.66)
}

struct TransitionSmallMotionAccumulator {
    private var pending: CGFloat = 0
    private var idleFrames = 0

    mutating func reset() {
        pending = 0
        idleFrames = 0
    }

    mutating func ingest(_ motion: CGFloat) -> CGFloat {
        guard motion.isFinite else {
            reset()
            return motion
        }
        if abs(motion) > 3.25 {
            reset()
            return motion
        }
        if abs(motion) < 0.25 {
            idleFrames += 1
            if idleFrames >= 3 { reset() }
            return 0
        }
        idleFrames = 0
        if pending != 0, (pending < 0) != (motion < 0) {
            pending = 0
        }
        pending += motion
        guard abs(pending) >= 4 else { return 0 }
        let accepted = pending
        reset()
        return accepted
    }
}

enum TransitionForegroundResidualGate {
    static func adjusted(
        _ step: CGVector,
        previousForeground: CGRect?,
        currentForeground: CGRect?,
        frameWidth: CGFloat,
        solveWidth: CGFloat
    ) -> CGVector {
        guard let previousForeground, let currentForeground,
              !previousForeground.isNull, !previousForeground.isEmpty,
              !currentForeground.isNull, !currentForeground.isEmpty,
              frameWidth.isFinite, frameWidth > 0,
              solveWidth.isFinite, solveWidth > 0 else { return step }
        let foregroundMotion = abs(currentForeground.midX - previousForeground.midX)
            * solveWidth / frameWidth
        guard foregroundMotion >= 1.25, abs(step.dx) <= 3.25 else { return step }
        return CGVector(dx: 0, dy: step.dy)
    }
}

enum GroundPoseContinuityGate {
    // GroundGlobalPoseRANSAC requires four ordered observations and the
    // tracker then verifies the proposed pose against a wider pixel strip.
    // Do not demand a second, stricter vote count here: that discarded valid
    // occluded loop closures after the expensive verifier accepted them.
    static let minimumGlobalMatchSupport = 4

    static func accepts(
        candidate: CGPoint,
        current: CGPoint,
        solveWidth: CGFloat,
        globalMatchCount: Int,
        hasGlobalCorrection: Bool,
        localInlierCount: Int = 0,
        localTextureSupport: Int = 0,
        localTextureError: CGFloat? = nil,
        captureElapsed: Double? = nil
    ) -> Bool {
        guard candidate.x.isFinite, candidate.y.isFinite,
              current.x.isFinite, current.y.isFinite,
              solveWidth.isFinite, solveWidth > 0 else { return false }
        if hasGlobalCorrection, globalMatchCount >= minimumGlobalMatchSupport {
            return true
        }
        let maximumHorizontalStep = max(8, min(16, solveWidth * 0.025))
        let maximumVerticalStep = max(6, min(12, solveWidth * 0.019))
        let deltaX = abs(candidate.x - current.x)
        let deltaY = abs(candidate.y - current.y)
        if deltaX <= maximumHorizontalStep && deltaY <= maximumVerticalStep {
            return true
        }
        // A real capture gap can span more than the ordinary per-frame
        // limit. Require broad direct texture evidence and bound both time
        // and displacement; elapsed time alone never authorizes a jump.
        if let elapsed = captureElapsed, elapsed.isFinite,
           elapsed >= 0.08, elapsed <= 0.25,
           localTextureSupport >= 8,
           localTextureError.map({ $0.isFinite && $0 <= 18 }) == true {
            let scale = solveWidth / 640
            if deltaX <= min(96, 16 * elapsed * 60) * scale,
               deltaY <= min(96, 12 * elapsed * 60) * scale { return true }
        }
        // A dropped vision frame can make the next valid local measurement a
        // 30-40 pixel catch-up. Permit that only when several persistent,
        // ordered ground tiles independently agree with the line-first solve.
        // False row proposals in captured failures had zero such inliers.
        // A newly visible platform has no persistent IDs yet. Its independent
        // wide-strip texture solve can still measure a fast camera descent.
        // Dropping that measured Y and reseeding at a vertically locked bridge
        // accumulated hundreds of pixels of error in the recorded route.
        let supportedNewGround = localTextureSupport >= 6
            && localTextureError.map { $0.isFinite && $0 <= 18 } == true
        guard localInlierCount >= minimumGlobalMatchSupport || supportedNewGround else { return false }
        let supportedHorizontalStep = max(32, min(64, solveWidth * 0.10))
        let supportedVerticalStep = max(16, min(32, solveWidth * 0.05))
        return deltaX <= supportedHorizontalStep
            && deltaY <= supportedVerticalStep
    }
}
