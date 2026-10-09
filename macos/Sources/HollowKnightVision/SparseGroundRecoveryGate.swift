import CoreGraphics
import Foundation
import os

/// A fallback may underestimate a fast vertical pan. Two independently
/// verified sparse ground observations may recover that short gap, provided
/// their measured vertical motion agrees with the direction of correction.
/// This does not authorize stationary relocalization or a room-sized jump.
struct SparseGroundRecoveryGate {
    private static let traceLog = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "ground-trace")
    private let traceEnabled = ProcessInfo.processInfo.arguments.contains("--trace-ground-tracking")
    private var pending: (position: CGPoint, timestamp: Double)?

    private var broadPending: (position: CGPoint, current: CGPoint, timestamp: Double)?

    mutating func reset() { pending = nil; broadPending = nil }

    mutating func accepts(candidate: CGPoint, current: CGPoint,
                          solveWidth: CGFloat, timestamp: Double,
                          textureSupport: Int, textureError: CGFloat?) -> Bool {
        guard solveWidth.isFinite, solveWidth > 0,
              candidate.x.isFinite, candidate.y.isFinite,
              current.x.isFinite, current.y.isFinite, timestamp.isFinite,
              textureSupport >= 3, let textureError,
              textureError.isFinite, textureError >= 0, textureError <= 18 else {
            reset()
            return false
        }
        let scale = solveWidth / 640
        guard abs(candidate.x - current.x) <= 64 * scale,
              abs(candidate.y - current.y) <= 96 * scale else {
            reset()
            return false
        }
        // A landing snap may settle or rebound before the next capture.
        // Its tiny instantaneous motion need not have the correction's sign.
        // Require two broad texture verifications, a very precise second
        // match, coherent positions and a bounded same-direction correction.
        let broadPrior = broadPending
        broadPending = textureSupport >= 12
            ? (candidate, current, timestamp) : nil
        if let broadPrior, textureSupport >= 12, textureError <= 6 {
            let dt = timestamp - broadPrior.timestamp
            let dx = abs(candidate.x - broadPrior.position.x)
            let dy = abs(candidate.y - broadPrior.position.y)
            let correctionY = candidate.y - current.y
            let priorCorrectionY = broadPrior.position.y - broadPrior.current.y
            if dt >= 0.008, dt <= 0.08,
               dx <= 16 * scale, dy <= 12 * scale,
               max(dx, dy) >= scale,
               abs(correctionY) > 32 * scale, abs(correctionY) <= 64 * scale,
               abs(priorCorrectionY) > 32 * scale, abs(priorCorrectionY) <= 64 * scale,
               correctionY * priorCorrectionY > 0 {
                if traceEnabled {
                    Self.traceLog.notice("broad-ground-recovery t=\(timestamp, privacy: .public) x=\(candidate.x, privacy: .public) y=\(candidate.y, privacy: .public) support=\(textureSupport, privacy: .public) error=\(textureError, privacy: .public)")
                }
                reset()
                return true
            }
        }
        guard textureError <= 12 else { pending = nil; return false }
        let prior = pending
        pending = (candidate, timestamp)
        guard let prior else { return false }
        let elapsed = timestamp - prior.timestamp
        let dx = candidate.x - prior.position.x
        let dy = candidate.y - prior.position.y
        guard elapsed >= 0.008, elapsed <= 0.08,
              abs(dx) <= min(48, 16 * max(1, elapsed * 60)) * scale,
              abs(dy) >= scale,
              abs(dy) <= min(64, 32 * max(1, elapsed * 60)) * scale,
              dy * (candidate.y - current.y) > 0 else { return false }
        reset()
        return true
    }
}
