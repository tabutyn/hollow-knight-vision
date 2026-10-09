import Foundation
import CoreGraphics

/// One immutable search at a time. Submission never waits for the worker and
/// obsolete generations cannot publish. The caller owns cadence and snapshots.
final class BoundedBackgroundSearch<Value> {
    private let queue = DispatchQueue(label: "com.ballroller.hkv.global-search", qos: .utility)
    private let lock = NSLock()
    private var epoch: UInt64 = 0
    private var busy = false
    private var completed: Value?

    @discardableResult
    func submit(_ operation: @escaping () -> Value) -> Bool {
        lock.lock()
        guard !busy else { lock.unlock(); return false }
        busy = true
        let submittedEpoch = epoch
        lock.unlock()
        queue.async { [self] in
            let value = autoreleasepool(invoking: operation)
            lock.lock()
            if epoch == submittedEpoch { completed = value }
            busy = false
            lock.unlock()
        }
        return true
    }

    func take() -> Value? {
        lock.lock()
        defer { lock.unlock() }
        let value = completed
        completed = nil
        return value
    }

    func invalidate() {
        lock.lock()
        epoch &+= 1
        completed = nil
        lock.unlock()
    }
}

/// Compare corrections, rather than absolute poses, while the camera moves.
/// Exact-frame Hacker audits showed that sub-two-pixel proposals are mostly
/// descriptor noise, while unconfirmed two-to-eight-pixel proposals often
/// create more drift than they remove. Ignore the noise floor and require two
/// independently timed observations for every material correction.
struct GroundCorrectionConfirmation {
    static let deadband: CGFloat = 2
    private var pending: (correction: CGVector, timestamp: Double)?
    var needsConfirmation: Bool { pending != nil }

    mutating func reset() { pending = nil }

    mutating func accepts(_ correction: CGVector, timestamp: Double) -> Bool {
        guard correction.dx.isFinite, correction.dy.isFinite, timestamp.isFinite else {
            pending = nil
            return false
        }
        let magnitude = hypot(correction.dx, correction.dy)
        if magnitude <= Self.deadband {
            pending = nil
            return false
        }
        defer { pending = (correction, timestamp) }
        guard let prior = pending else { return false }
        let elapsed = timestamp - prior.timestamp
        let priorMagnitude = hypot(prior.correction.dx, prior.correction.dy)
        let agreement = min(4, max(0.75, max(magnitude, priorMagnitude) * 0.25))
        return elapsed >= 0.15 && elapsed <= 1.5
            && hypot(correction.dx - prior.correction.dx,
                     correction.dy - prior.correction.dy) <= agreement
    }
}
