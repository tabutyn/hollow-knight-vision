import CoreGraphics
import Foundation

/// Tracks global landmark corrections independently from registration motion.
/// The vision queue owns camera mutation; the landmark worker only reads this
/// locked ledger before it starts a queued job.
final class FeatureCorrectionPolicy: @unchecked Sendable {
    struct Snapshot: Equatable {
        let generation: UInt64
        let cumulativeOffset: CGVector
    }

    private let lock = NSLock()
    private var generation: UInt64
    private var cumulativeOffset = CGVector.zero
    private var lastAppliedTimestamp: Double?

    init(generation: UInt64 = 0) {
        self.generation = generation
    }

    /// Begins a new capture generation. Its first registration already
    /// contains every correction that preceded the reset, so the per-generation
    /// offset starts from zero.
    func beginGeneration(_ newGeneration: UInt64) {
        lock.lock()
        defer { lock.unlock() }
        guard newGeneration >= generation else { return }
        generation = newGeneration
        cumulativeOffset = .zero
        lastAppliedTimestamp = nil
    }

    func captureSnapshot(for request: FeatureRefinementRequest) -> Snapshot? {
        lock.lock()
        defer { lock.unlock() }
        guard request.generation == generation else { return nil }
        return Snapshot(generation: generation, cumulativeOffset: cumulativeOffset)
    }

    /// Rebase a pose captured before an earlier slow refinement completed.
    /// This keeps the persistent tracker in the same world coordinates as the
    /// camera accumulator without reapplying corrections already in the pose.
    func rebasedPosition(
        _ capturedPosition: CGPoint,
        from snapshot: Snapshot
    ) -> CGPoint {
        lock.lock()
        defer { lock.unlock() }
        guard snapshot.generation == generation else { return capturedPosition }
        return CGPoint(
            x: capturedPosition.x + cumulativeOffset.dx - snapshot.cumulativeOffset.dx,
            y: capturedPosition.y + cumulativeOffset.dy - snapshot.cumulativeOffset.dy
        )
    }

    /// Returns true exactly once for a finite correction from the active
    /// generation whose observation is newer than the last applied correction.
    func apply(
        _ correction: CGVector,
        for request: FeatureRefinementRequest
    ) -> Bool {
        apply(correction, for: request, onAcceptance: {})
    }

    /// Runs `onAcceptance` while the generation lock is held. The vision queue
    /// uses this to move the accumulator and tracking state as one atomic
    /// operation with `beginGeneration`, so an old worker cannot leak a
    /// correction into a newly started capture generation.
    func apply(
        _ correction: CGVector,
        for request: FeatureRefinementRequest,
        onAcceptance: () -> Void
    ) -> Bool {
        guard correction.dx.isFinite, correction.dy.isFinite else { return false }
        lock.lock()
        guard request.generation == generation,
              lastAppliedTimestamp.map({ request.timestamp > $0 }) ?? true else {
            lock.unlock()
            return false
        }
        cumulativeOffset.dx += correction.dx
        cumulativeOffset.dy += correction.dy
        lastAppliedTimestamp = request.timestamp
        onAcceptance()
        lock.unlock()
        return true
    }
}
