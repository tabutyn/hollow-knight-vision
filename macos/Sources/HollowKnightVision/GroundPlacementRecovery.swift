import CoreGraphics
import Foundation

/// Defers a discontinuous ground correction before it can teach the map or
/// replace its own references. Ordinary fallback keeps its existing policy.
struct GroundPlacementRecovery {
    struct Proposal {
        let position: CGPoint
        let textureSupport: Int
        let textureError: CGFloat?
        let inlierCount: Int
        let globalMatchCount: Int
        let hasGlobalCorrection: Bool
        let distinctFrame: Bool
        let hasEstablishedGround: Bool
    }

    private struct Pending {
        let proposal: Proposal
        let current: CGPoint
        let timestamp: Double
    }
    private var pending: Pending?
    private(set) var needsVerification = false
    private var sparse = SparseGroundRecoveryGate()

    var state: String { !needsVerification ? "trusted" : pending == nil ? "unlocated" : "verifying" }
    var allowsAtlasWrite: Bool { !needsVerification }

    mutating func reset() { self = Self() }

    mutating func missingObservation() {
        // No viable ground correction remains to verify. Let the existing
        // fallback keep acquiring fresh visual evidence instead of freezing
        // an out-of-view reference indefinitely.
        needsVerification = false
        pending = nil
        sparse.reset()
    }

    func retainsCandidate(at timestamp: Double) -> Bool {
        guard let pending else { return false }
        return timestamp >= pending.timestamp && timestamp - pending.timestamp <= 0.12
    }

    mutating func accepts(_ proposal: Proposal, current: CGPoint, solveWidth: CGFloat,
                         timestamp: Double, captureElapsed: Double?) -> Bool {
        let position = proposal.position
        guard solveWidth.isFinite, solveWidth > 0, timestamp.isFinite,
              position.x.isFinite, position.y.isFinite,
              current.x.isFinite, current.y.isFinite else {
            missingObservation()
            return false
        }
        // An initial strip is provisional until ground has been established.
        // Protecting it as a world anchor can deadlock bootstrap after a
        // checkpoint restore. The separate atlas bootstrap gate still waits
        // for confirmed floor and stable placement before the first write.
        if !proposal.hasEstablishedGround {
            needsVerification = false
            pending = nil
            sparse.reset()
            return true
        }
        let continuous = GroundPoseContinuityGate.accepts(candidate: position, current: current,
            solveWidth: solveWidth, globalMatchCount: proposal.globalMatchCount,
            hasGlobalCorrection: proposal.hasGlobalCorrection,
            localInlierCount: proposal.inlierCount, localTextureSupport: proposal.textureSupport,
            localTextureError: proposal.textureError, captureElapsed: captureElapsed)
        if (proposal.hasGlobalCorrection && proposal.globalMatchCount >= 4)
            || (!needsVerification && continuous) {
            confirm()
            return true
        }
        needsVerification = true
        let scale = solveWidth / 640
        guard proposal.textureSupport >= 3, let error = proposal.textureError,
              error.isFinite, error >= 0, error <= 18,
              abs(position.x - current.x) <= 64 * scale,
              abs(position.y - current.y) <= 96 * scale else {
            pending = nil
            sparse.reset()
            return false
        }
        // A repeated capture must not supply a second independent vote, nor
        // extend the lifetime of a pending correction indefinitely.
        guard proposal.distinctFrame else {
            if !retainsCandidate(at: timestamp) { pending = nil; sparse.reset() }
            return false
        }
        let prior = pending
        pending = Pending(proposal: proposal, current: current, timestamp: timestamp)
        let sparseAccepted = sparse.accepts(candidate: position, current: current,
            solveWidth: solveWidth, timestamp: timestamp,
            textureSupport: proposal.textureSupport, textureError: error)
        if let prior {
            let dt = timestamp - prior.timestamp
            let priorCorrection = CGVector(dx: prior.proposal.position.x - prior.current.x,
                                           dy: prior.proposal.position.y - prior.current.y)
            let correction = CGVector(dx: position.x - current.x, dy: position.y - current.y)
            let coherentCorrection = hypot(correction.dx - priorCorrection.dx,
                                           correction.dy - priorCorrection.dy) <= 24 * scale
            let motionLimit = min(48, max(16, CGFloat(dt) * 960)) * scale
            let coherentMotion = hypot(position.x - prior.proposal.position.x,
                                       position.y - prior.proposal.position.y) <= motionLimit
            let strong = continuous
                ? proposal.textureSupport >= 3 && error <= 12
                : proposal.textureSupport >= max(6, min(12, prior.proposal.textureSupport))
                    && prior.proposal.textureSupport >= 6 && error <= 10
                    && abs(correction.dy) <= 64 * scale
            if dt >= 0.008, dt <= 0.12, coherentCorrection, coherentMotion,
               strong || sparseAccepted {
                confirm()
                return true
            }
        }
        return false
    }

    private mutating func confirm() {
        needsVerification = false
        pending = nil
        sparse.reset()
    }
}
