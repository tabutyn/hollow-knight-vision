import CoreGraphics
import Foundation

/// Placement of the island origin, independent of current local tracking.
/// `.reference` fixes the arbitrary first origin; it does not assert a game
/// coordinate. An unsupported gap always leaves subsequent placement estimated.
enum LiveIslandPlacement: String, Codable, Equatable, Sendable {
    case reference
    case connected
    case estimated
    case legacyUnknown
}

struct LiveIslandUncertainty: Codable, Equatable, Sendable {
    /// Nil means unbounded/unknown, not zero. Units are camera solve pixels.
    let x: Double?
    let y: Double?
    static let unknown = Self(x: nil, y: nil)
    static let reference = Self(x: 0, y: 0)

    var isValid: Bool {
        [x, y].allSatisfy { $0.map { $0.isFinite && $0 >= 0 } ?? true }
    }
}

struct LiveIslandMember: Codable, Equatable, Sendable {
    let observationID: Int
    /// Immutable within-island geometry. World placement is applied separately.
    let localPose: CGPoint
    let measured: Bool
}

struct LiveTrackingIsland: Codable, Equatable, Identifiable, Sendable {
    let id: Int
    let roomID: Int
    let startedAt: Double
    let worldFromLocal: CGPoint
    let placement: LiveIslandPlacement
    let uncertainty: LiveIslandUncertainty
    let breakReason: String
    let members: [LiveIslandMember]

    var isGloballyConnected: Bool { placement == .reference || placement == .connected }
    var isProvisional: Bool { !isGloballyConnected }

    func adding(_ member: LiveIslandMember) -> Self {
        Self(id: id, roomID: roomID, startedAt: startedAt, worldFromLocal: worldFromLocal,
             placement: placement, uncertainty: uncertainty, breakReason: breakReason,
             members: members + [member])
    }

    func replacingTranslation(_ translation: CGPoint,
                              placement: LiveIslandPlacement? = nil,
                              uncertainty: LiveIslandUncertainty? = nil) -> Self {
        Self(id: id, roomID: roomID, startedAt: startedAt, worldFromLocal: translation,
             placement: placement ?? self.placement, uncertainty: uncertainty ?? self.uncertainty,
             breakReason: breakReason, members: members)
    }

    /// Old epochs do not prove reliable continuity. Each old observation keeps
    /// its exact placement as a separate unknown component until new evidence
    /// can support a connection. Its immutable source pixels are not modified.
    static func legacy(_ observation: LiveWorldObservation) -> Self {
        Self(id: observation.id, roomID: observation.roomID, startedAt: observation.timestamp,
             worldFromLocal: observation.optimizedPose, placement: .legacyUnknown,
             uncertainty: .unknown, breakReason: "legacy-unverified",
             members: [LiveIslandMember(observationID: observation.id, localPose: .zero, measured: false)])
    }
}

/// Capture-time island ownership travels with an atlas request, rather than
/// being read from mutable current tracking state by a delayed atlas worker.
struct LiveIslandCapture: Codable, Equatable, Sendable {
    let id: Int
    let roomID: Int
    let startedAt: Double
    let worldFromLocal: CGPoint
    let localPose: CGPoint
    let placement: LiveIslandPlacement
    let uncertainty: LiveIslandUncertainty
    let breakReason: String
    let measured: Bool

    var worldPose: CGPoint {
        CGPoint(x: worldFromLocal.x + localPose.x, y: worldFromLocal.y + localPose.y)
    }

    func island(observationID: Int) -> LiveTrackingIsland {
        LiveTrackingIsland(id: id, roomID: roomID, startedAt: startedAt,
            worldFromLocal: worldFromLocal, placement: placement, uncertainty: uncertainty,
            breakReason: breakReason, members: [member(observationID: observationID)])
    }

    func member(observationID: Int) -> LiveIslandMember {
        LiveIslandMember(observationID: observationID, localPose: localPose, measured: measured)
    }
}

/// A visual lineage ledger. Floor presence alone never decides island identity.
/// Prediction is visible but cannot extend trusted local geometry across a gap.
struct LiveTrackingIslandCoordinator {
    private struct State {
        let id: Int
        let roomID: Int
        let startedAt: Double
        /// Image-solver origin never changes when atlas placement is corrected.
        let origin: CGPoint
        let initialWorldOrigin: CGPoint
        let placement: LiveIslandPlacement
        let reason: String
        let measured: Bool
    }
    private struct Placement {
        let roomID: Int
        let startedAt: Double
        let translation: CGPoint
        let kind: LiveIslandPlacement
        let uncertainty: LiveIslandUncertainty
    }
    private var worldPlacements = [Int: Placement]()
    private var worldRevision = -1
    private var nextID = 0
    private var canEstablishReference = true
    private var current: State?
    // References must name an image supported by an actual measured pair.
    // A timestamp merely between two good captures can be a predicted/held
    // pose. An unknown seed may establish a new local coordinate system, but
    // never retroactively promotes its prediction-only screenshot placement.
    // Bound the ledger; evicted references conservatively start an unknown
    // component until a persistent visual closure establishes their identity.
    private var supportedReferences = [Int64: State]()
    private var referenceOrder = [Int64]()
    private let referenceCapacity = 16_384
    private var lastObservedAt: Double?
    private let maximumContinuityGap: Double = 0.12

    mutating func reset(nextID: Int) {
        self.nextID = max(self.nextID, max(0, nextID))
        canEstablishReference = nextID == 0
        current = nil
        supportedReferences.removeAll(keepingCapacity: true)
        referenceOrder.removeAll(keepingCapacity: true)
        lastObservedAt = nil
        worldPlacements.removeAll(keepingCapacity: true)
        worldRevision = -1
    }

    /// Runs on the owning vision queue. A corrected atlas changes only world
    /// translations; old image references keep their original local geometry.
    /// Duplicate or delayed revisions cannot roll placement backward.
    @discardableResult
    mutating func applyWorldSnapshot(_ snapshot: LiveWorldSnapshot) -> Bool {
        guard snapshot.mapRevision > worldRevision else { return false }
        worldPlacements = Dictionary(uniqueKeysWithValues: snapshot.trackingIslands.map {
            ($0.id, Placement(roomID: $0.roomID, startedAt: $0.startedAt,
                translation: $0.worldFromLocal, kind: $0.placement, uncertainty: $0.uncertainty))
        })
        worldRevision = snapshot.mapRevision
        return true
    }

    private func placement(for state: State) -> Placement {
        if let placed = worldPlacements[state.id],
           placed.roomID == state.roomID, placed.startedAt == state.startedAt { return placed }
        return Placement(roomID: state.roomID, startedAt: state.startedAt,
            translation: state.initialWorldOrigin, kind: state.placement,
            uncertainty: state.placement == .reference ? .reference : .unknown)
    }

    private mutating func recordReference(_ timestamp: Double, state: State) {
        let key = Int64((timestamp * 1_000_000).rounded())
        if supportedReferences.updateValue(state, forKey: key) == nil {
            referenceOrder.append(key)
            if referenceOrder.count > referenceCapacity {
                // Amortize compaction instead of shifting the queue per frame.
                let evicted = referenceCapacity / 4
                for old in referenceOrder.prefix(evicted) { supportedReferences.removeValue(forKey: old) }
                referenceOrder.removeFirst(evicted)
            }
        }
    }

    mutating func observe(timestamp: Double, roomID: Int, worldPose: CGPoint,
                          measured: Bool, referenceTimestamp: Double?,
                          discontinuity: String? = nil) -> LiveIslandCapture {
        precondition(timestamp.isFinite && worldPose.x.isFinite && worldPose.y.isFinite && roomID >= 0)
        let prior = current
        let known: State? = referenceTimestamp.flatMap { reference in
            guard reference.isFinite, reference <= timestamp, discontinuity == nil else { return nil }
            let key = Int64((reference * 1_000_000).rounded())
            return [key, key - 1, key + 1].compactMap { supportedReferences[$0] }
                .first { $0.roomID == roomID }
        }
        let gap = lastObservedAt.map { timestamp - $0 > maximumContinuityGap || timestamp < $0 } ?? false
        if measured, let known {
            // A measured step can bridge a brief provisional interval. Keep
            // those prediction-only screenshots separate, but resume the
            // original reliable island when the actual reference proves it.
            current = known
        } else {
            let reason: String?
            if prior == nil { reason = "capture-start" }
            else if let discontinuity { reason = discontinuity }
            else if prior?.roomID != roomID { reason = "room-change" }
            else if gap { reason = "capture-gap" }
            else if measured { reason = "visual-reacquisition" }
            else if prior?.measured == true { reason = "unmeasured-motion" }
            else { reason = nil }
            if let reason {
                let placement: LiveIslandPlacement = canEstablishReference && measured
                    ? .reference : .estimated
                if measured { canEstablishReference = false }
                // Carry only display spacing into an unsupported new island.
                // This offset is not a motion edge or a confidence claim.
                let priorOffset = prior.map { state -> CGVector in
                    let placed = self.placement(for: state)
                    return CGVector(dx: placed.translation.x - state.origin.x,
                                    dy: placed.translation.y - state.origin.y)
                } ?? .zero
                current = State(id: nextID, roomID: roomID, startedAt: timestamp,
                    origin: worldPose,
                    initialWorldOrigin: CGPoint(x: worldPose.x + priorOffset.dx,
                                                y: worldPose.y + priorOffset.dy),
                    placement: placement, reason: reason, measured: measured)
                nextID += 1
            }
        }
        if measured, let state = current {
            recordReference(timestamp, state: state)
            // Repeated matches to the same bootstrap image share one measured
            // local frame, even if that first image was captured before tracking
            // became ready. Its original provisional screenshot stays separate.
            if known == nil, discontinuity == nil, let referenceTimestamp,
               referenceTimestamp.isFinite, referenceTimestamp <= timestamp {
                let key = Int64((referenceTimestamp * 1_000_000).rounded())
                if supportedReferences[key] == nil { recordReference(referenceTimestamp, state: state) }
            }
        }
        lastObservedAt = timestamp
        let state = current!
        let placed = placement(for: state)
        return LiveIslandCapture(id: state.id, roomID: roomID, startedAt: state.startedAt,
            worldFromLocal: placed.translation,
            localPose: CGPoint(x: worldPose.x - state.origin.x, y: worldPose.y - state.origin.y),
            placement: placed.kind, uncertainty: placed.uncertainty,
            breakReason: state.reason, measured: measured)
    }
}
