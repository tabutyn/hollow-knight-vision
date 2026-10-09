import CoreGraphics
import Foundation

/// Keeps a translation-only mapping from a short-lived local tracker into the
/// persistent world. All reads and transitions are serialized by one lock so
/// delayed matcher results cannot partially apply an obsolete basis.
final class WorldBasisController {
    enum TrackingState: Equatable {
        case tracking
        case recovering
    }

    struct ObservationTicket: Equatable {
        let captureGeneration: UInt64
        let localEpoch: UInt64
        let basisRevision: UInt64
        let captureTimestamp: TimeInterval
        let localPose: CGPoint
    }

    struct Snapshot: Equatable {
        let trackingState: TrackingState
        let captureGeneration: UInt64
        let localEpoch: UInt64
        let basisRevision: UInt64
        let worldFromLocal: CGPoint
        let latestCaptureTimestamp: TimeInterval?
        let latestLocalPose: CGPoint?
        let latestWorldPose: CGPoint?
    }

    struct ResolvedObservation: Equatable {
        let ticket: ObservationTicket
        let worldPose: CGPoint
    }

    private let lock = NSLock()
    private var state: TrackingState = .recovering
    private var generation: UInt64 = 0
    private var epoch: UInt64 = 0
    private var revision: UInt64 = 0
    private var basis: CGPoint = .zero
    private var latestLocal: (pose: CGPoint, timestamp: TimeInterval)?
    private var lastPlacementTimestamp: TimeInterval?

    /// Begins a fresh capture generation. A reset intentionally drops the old
    /// basis; only a local-tracking loss retains it for recovery.
    @discardableResult
    func beginCapture(worldFromLocal: CGPoint = .zero) -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        generation &+= 1
        epoch = 0
        revision &+= 1
        basis = worldFromLocal
        state = .tracking
        latestLocal = nil
        lastPlacementTimestamp = nil
        return generation
    }

    @discardableResult
    func resetCapture(worldFromLocal: CGPoint = .zero) -> UInt64 {
        beginCapture(worldFromLocal: worldFromLocal)
    }

    /// Records a local observation without changing recovery state. Recovery
    /// ends only after a confirmed placement against the committed world.
    func observe(localPose: CGPoint, captureTimestamp: TimeInterval) -> ObservationTicket? {
        guard Self.isFinite(localPose), captureTimestamp.isFinite else { return nil }
        lock.lock()
        defer { lock.unlock() }
        let ticket = ObservationTicket(
            captureGeneration: generation,
            localEpoch: epoch,
            basisRevision: revision,
            captureTimestamp: captureTimestamp,
            localPose: localPose
        )
        if latestLocal == nil || captureTimestamp >= latestLocal!.timestamp {
            latestLocal = (localPose, captureTimestamp)
        }
        return ticket
    }

    /// Invalidates local coordinates while retaining the last known world basis.
    func loseLocalTracking() {
        lock.lock()
        defer { lock.unlock() }
        guard state == .tracking else { return }
        state = .recovering
        epoch &+= 1
        latestLocal = nil
    }

    /// Starts a new room-local coordinate epoch without invalidating the
    /// capture generation. Outstanding tickets from the prior room become
    /// unusable, while the caller can continue displaying the persistent atlas.
    func beginLocalEpoch(worldFromLocal: CGPoint = .zero) {
        guard Self.isFinite(worldFromLocal) else { return }
        lock.lock()
        defer { lock.unlock() }
        epoch &+= 1
        revision &+= 1
        basis = worldFromLocal
        state = .tracking
        latestLocal = nil
        lastPlacementTimestamp = nil
    }

    /// Applies a placement only when its complete capture identity is current.
    /// The ticket's local pose is the historical pose used to derive the new
    /// basis; the newest local pose is then remapped through that basis.
    @discardableResult
    func confirmPlacement(for ticket: ObservationTicket, matchedWorldPose: CGPoint) -> Bool {
        guard Self.isFinite(matchedWorldPose) else { return false }
        lock.lock()
        defer { lock.unlock() }
        guard ticket.captureGeneration == generation,
              ticket.localEpoch == epoch,
              ticket.basisRevision == revision,
              ticket.captureTimestamp.isFinite,
              lastPlacementTimestamp.map({ ticket.captureTimestamp >= $0 }) ?? true else {
            return false
        }
        basis = CGPoint(
            x: matchedWorldPose.x - ticket.localPose.x,
            y: matchedWorldPose.y - ticket.localPose.y
        )
        revision &+= 1
        lastPlacementTimestamp = ticket.captureTimestamp
        state = .tracking
        return true
    }

    /// Maps queued work through the latest basis when it remains in this local
    /// coordinate epoch. A basis revision mismatch is deliberately rebaseable.
    func rebasedWorldPose(for ticket: ObservationTicket) -> CGPoint? {
        rebasedObservation(for: ticket)?.worldPose
    }

    /// Refreshes a queued ticket to the authoritative basis revision in the
    /// same atomic read that maps its local pose.
    func rebasedObservation(for ticket: ObservationTicket) -> ResolvedObservation? {
        lock.lock()
        defer { lock.unlock() }
        guard ticket.captureGeneration == generation, ticket.localEpoch == epoch else { return nil }
        let refreshed = ObservationTicket(
            captureGeneration: generation,
            localEpoch: epoch,
            basisRevision: revision,
            captureTimestamp: ticket.captureTimestamp,
            localPose: ticket.localPose
        )
        return ResolvedObservation(ticket: refreshed, worldPose: Self.add(ticket.localPose, basis))
    }

    var snapshot: Snapshot {
        lock.lock()
        defer { lock.unlock() }
        return Snapshot(
            trackingState: state,
            captureGeneration: generation,
            localEpoch: epoch,
            basisRevision: revision,
            worldFromLocal: basis,
            latestCaptureTimestamp: latestLocal?.timestamp,
            latestLocalPose: latestLocal?.pose,
            latestWorldPose: latestLocal.map { Self.add($0.pose, basis) }
        )
    }

    var trackingState: TrackingState { snapshot.trackingState }
    var latestWorldPose: CGPoint? { snapshot.latestWorldPose }

    private static func add(_ lhs: CGPoint, _ rhs: CGPoint) -> CGPoint {
        CGPoint(x: lhs.x + rhs.x, y: lhs.y + rhs.y)
    }

    private static func isFinite(_ point: CGPoint) -> Bool {
        point.x.isFinite && point.y.isFinite
    }
}
