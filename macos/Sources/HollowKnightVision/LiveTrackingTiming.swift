import CoreGraphics
import Foundation
import Metal
import OSLog

enum PresentationPosePrediction {
    static func velocity(from old: CGPoint, at oldTime: Double?,
                         to new: CGPoint, at time: Double, continuous: Bool) -> CGVector {
        guard continuous, let oldTime, time > oldTime, time - oldTime <= 0.1 else { return .zero }
        let dx = new.x - old.x, dy = new.y - old.y
        guard hypot(dx, dy) <= 24 else { return .zero }
        let dt = time - oldTime
        let speed = hypot(dx, dy) / dt
        guard speed <= 1_000 else { return .zero }
        return CGVector(dx: dx / dt, dy: dy / dt)
    }

    static func position(_ measured: CGPoint, velocity: CGVector,
                         measuredAt: Double?, presentedAt: Double) -> CGPoint {
        guard let measuredAt, presentedAt >= measuredAt,
              presentedAt - measuredAt <= 0.1 else { return measured }
        let dt = min(0.05, presentedAt - measuredAt)
        let dx = velocity.dx * dt, dy = velocity.dy * dt
        let scale = min(1, 8 / max(0.001, hypot(dx, dy)))
        return CGPoint(x: measured.x + dx * scale, y: measured.y + dy * scale)
    }
}

/// A trusted coordinate is not necessarily a camera measurement. The first
/// frame after a local solver reset adopts the current coordinate with zero
/// support; treating that seed as measured ground interrupts floorless motion
/// exactly when the image is fading or the camera is moving fastest.
enum GroundMotionEvidence {
    static func isMeasured(
        poseVerified: Bool,
        hasConfirmedGround: Bool,
        localTextureSupport: Int,
        inlierCount: Int,
        globalMatchCount: Int
    ) -> Bool {
        poseVerified && hasConfirmedGround
            && (localTextureSupport > 0 || inlierCount > 0 || globalMatchCount > 0)
    }

    static func isMeasured(_ result: GroundHypothesisTrackingResult) -> Bool {
        isMeasured(
            poseVerified: result.poseVerified,
            hasConfirmedGround: result.hasConfirmedGround,
            localTextureSupport: result.localTextureSupport,
            inlierCount: result.inlierCount,
            globalMatchCount: result.globalMatchCount
        )
    }

    static func isMeasured(_ sample: RecordedTrackingSample) -> Bool {
        isMeasured(
            poseVerified: sample.poseVerified,
            hasConfirmedGround: sample.hasConfirmedGround,
            localTextureSupport: sample.localTextureSupport ?? 0,
            inlierCount: sample.inlierCount,
            globalMatchCount: sample.globalMatchCount
        )
    }
}

/// Capture-rate room tracking must not treat an old asynchronous ground solve
/// as evidence for a newly dark or floorless frame. The cutoff matches the
/// largest baseline the low-resolution bridge can register directly.
enum CaptureGroundReliability {
    static let maximumAge: TimeInterval = 0.12

    static func isReliable(
        groundAnchored: Bool?,
        poseTimestamp: TimeInterval?,
        frameTimestamp: TimeInterval,
        frameAdmitsTracking: Bool
    ) -> Bool {
        guard groundAnchored == true,
              frameAdmitsTracking,
              let poseTimestamp,
              poseTimestamp.isFinite,
              frameTimestamp.isFinite else { return false }
        let age = frameTimestamp - poseTimestamp
        return age >= 0 && age <= maximumAge
    }
}

/// A dark edge can announce a possible room transition before any image
/// motion has measured a replacement pose. Publishing that tentative pose as
/// tracking state erases the timestamp needed to carry the last verified
/// ground velocity into floorless odometry. Keep the verified handoff origin
/// until room ownership changes or coarse image motion actually takes over.
enum TransitionPosePublication {
    static func replacesTrackingState(
        roomOwnershipChanged: Bool,
        coarseMotionIsControlling: Bool
    ) -> Bool {
        roomOwnershipChanged || coarseMotionIsControlling
    }
}

/// Carries the last independently measured ground velocity across the short
/// delay between a floor disappearing in capture and the asynchronous Vision
/// worker publishing that loss. The capture-rate room tracker otherwise
/// starts its floorless trajectory from an already stale camera pose.
enum FloorlessMotionHandoff {
    static let maximumSeedAge: TimeInterval = 0.48
    static let maximumPoseAge: TimeInterval = 0.25
    static let maximumPredictedTravelFraction: CGFloat = 0.18

    static func seed(
        velocity: CGVector?,
        measuredAt: TimeInterval?,
        presentedAt: TimeInterval
    ) -> LowResolutionCameraVelocitySeed? {
        guard let velocity, let measuredAt,
              velocity.dx.isFinite, velocity.dy.isFinite,
              measuredAt.isFinite, presentedAt.isFinite else { return nil }
        let age = presentedAt - measuredAt
        guard age >= 0, age <= maximumSeedAge else { return nil }
        return LowResolutionCameraVelocitySeed(
            velocity: velocity,
            timestamp: measuredAt
        )
    }

    static func position(
        _ measured: CGPoint,
        measuredAt: TimeInterval?,
        seed: LowResolutionCameraVelocitySeed?,
        presentedAt: TimeInterval,
        solveWidth: CGFloat
    ) -> CGPoint {
        guard let measuredAt, let seed,
              measured.x.isFinite, measured.y.isFinite,
              measuredAt.isFinite, presentedAt.isFinite,
              solveWidth.isFinite, solveWidth > 0 else { return measured }
        let age = presentedAt - measuredAt
        guard age >= 0, age <= maximumPoseAge else { return measured }
        var dx = seed.velocity.dx * age
        var dy = seed.velocity.dy * age
        let distance = hypot(dx, dy)
        let maximumTravel = max(8, solveWidth * maximumPredictedTravelFraction)
        if distance > maximumTravel {
            let scale = maximumTravel / distance
            dx *= scale
            dy *= scale
        }
        return CGPoint(x: measured.x + dx, y: measured.y + dy)
    }
}

struct FloorlessPoseCandidate: Equatable {
    enum Source: Equatable {
        case coarseCaptureRate
        case maskedRegistration
    }

    let position: CGPoint
    let source: Source
}

/// Selects between two independently measured floorless trajectories without
/// letting either become unconditional authority. The capture-rate path sees
/// skipped fast motion; masked registration is more stable through a stop or
/// reversal. Horizontal input supplies only a sign constraint, never distance.
enum FloorlessPoseCandidateSelector {
    private static let stationaryTolerance: CGFloat = 1
    private static let verticalPreferenceThreshold: CGFloat = 2

    static func select(
        current: CGPoint,
        coarse: CGPoint?,
        masked: CGPoint?,
        expectedDirection: VisualRoomDirection?,
        coarseIsPlaceMatch: Bool
    ) -> FloorlessPoseCandidate? {
        guard current.x.isFinite, current.y.isFinite else { return nil }
        let coarseCandidate = candidate(
            coarse,
            source: .coarseCaptureRate,
            current: current
        )
        let maskedCandidate = candidate(
            masked,
            source: .maskedRegistration,
            current: current
        )
        if coarseIsPlaceMatch, let coarseCandidate { return coarseCandidate }

        guard let expectedDirection else {
            if let coarseCandidate {
                let step = step(from: current, to: coarseCandidate.position)
                if abs(step.dy) >= verticalPreferenceThreshold,
                   abs(step.dy) > abs(step.dx) {
                    return coarseCandidate
                }
            }
            return maskedCandidate ?? coarseCandidate
        }

        let directional = [coarseCandidate, maskedCandidate]
            .compactMap { $0 }
            .filter {
                agrees(
                    step(from: current, to: $0.position).dx,
                    with: expectedDirection
                )
            }
        if let strongest = directional.max(by: {
            abs(step(from: current, to: $0.position).dx)
                < abs(step(from: current, to: $1.position).dx)
        }) {
            return strongest
        }

        // When both trackers say the camera is stationary, retaining the more
        // detailed masked result is safe. A candidate moving against explicit
        // input is held until new image evidence agrees with the reversal.
        if let maskedCandidate,
           abs(step(from: current, to: maskedCandidate.position).dx)
            <= stationaryTolerance {
            return maskedCandidate
        }
        if let coarseCandidate,
           abs(step(from: current, to: coarseCandidate.position).dx)
            <= stationaryTolerance {
            return coarseCandidate
        }
        return nil
    }

    private static func candidate(
        _ position: CGPoint?,
        source: FloorlessPoseCandidate.Source,
        current: CGPoint
    ) -> FloorlessPoseCandidate? {
        guard let position,
              position.x.isFinite, position.y.isFinite,
              hypot(position.x - current.x, position.y - current.y).isFinite
        else { return nil }
        return FloorlessPoseCandidate(position: position, source: source)
    }

    private static func step(from current: CGPoint, to candidate: CGPoint) -> CGVector {
        CGVector(dx: candidate.x - current.x, dy: candidate.y - current.y)
    }

    private static func agrees(
        _ step: CGFloat,
        with direction: VisualRoomDirection
    ) -> Bool {
        direction == .left ? step < -stationaryTolerance
            : step > stationaryTolerance
    }
}

/// Bounded image provenance for actual drawable presentation. Offline atlas
/// tiles have no entry, and repeated draws do not count as new captured frames.
final class LivePresentationTiming {
    static let shared = LivePresentationTiming()
    private struct Frame {
        let image: CGImage
        let capturedAt: Double
    }
    private let lock = NSLock()
    private var frames = [Frame]()
    private var lastPresented: Double?
    private var lastSubmitted: Double?
    private var lastLog: Double = 0
    private let telemetry = FramePipelineTelemetry()
    private let log = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "presentation-latency")

    func register(_ image: CGImage, capturedAt: Double) {
        telemetry.record(.captureToPublish, seconds: ProcessInfo.processInfo.systemUptime - capturedAt)
        lock.lock()
        frames.append(Frame(image: image, capturedAt: capturedAt))
        if frames.count > 8 { frames.removeFirst(frames.count - 8) }
        lock.unlock()
    }

    func timestamp(for image: CGImage) -> Double? {
        lock.lock()
        defer { lock.unlock() }
        return frames.last(where: { $0.image === image })?.capturedAt
    }

    func submitted(capturedAt: Double, at submittedAt: Double) {
        lock.lock()
        guard lastSubmitted.map({ capturedAt > $0 }) ?? true else { lock.unlock(); return }
        lastSubmitted = capturedAt
        lock.unlock()
        telemetry.record(.captureToDraw, seconds: submittedAt - capturedAt)
    }

    func completed(submittedAt: Double, gpuStart: Double, gpuEnd: Double) {
        guard gpuStart > 0, gpuEnd >= gpuStart else { return }
        telemetry.record(.gpuQueueWait, seconds: gpuStart - submittedAt)
        telemetry.record(.gpuExecution, seconds: gpuEnd - gpuStart)
    }

    func presented(capturedAt: Double, submittedAt: Double, at presentedAt: Double) {
        guard presentedAt >= capturedAt else { return }
        lock.lock()
        guard lastPresented.map({ capturedAt > $0 }) ?? true else { lock.unlock(); return }
        lastPresented = capturedAt
        let shouldLog = presentedAt - lastLog >= 1
        if shouldLog { lastLog = presentedAt }
        lock.unlock()
        telemetry.record(.captureToPresent, seconds: presentedAt - capturedAt)
        telemetry.record(.drawToPresent, seconds: presentedAt - submittedAt)
        if shouldLog, let summary = telemetry.summary(for: .captureToPresent) {
            log.notice("captureToVisible p50=\(summary.p50Milliseconds, privacy: .public)ms p95=\(summary.p95Milliseconds, privacy: .public)ms n=\(summary.count, privacy: .public)")
            let stages: [FramePipelineTelemetry.Stage] = [.captureToPublish, .captureToDraw, .gpuQueueWait, .gpuExecution, .drawToPresent]
            let detail = stages.compactMap { stage -> String? in
                guard let value = telemetry.summary(for: stage) else { return nil }
                return "\(stage) p50=\(String(format: "%.2f", value.p50Milliseconds))ms p95=\(String(format: "%.2f", value.p95Milliseconds))ms"
            }.joined(separator: " ")
            log.notice("presentationStages \(detail, privacy: .public)")
        }
    }
}
