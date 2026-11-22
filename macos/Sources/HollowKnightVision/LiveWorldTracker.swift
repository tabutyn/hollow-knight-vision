import CoreGraphics
import Foundation

enum LiveWorldTrackerError: Error, Equatable {
    case duplicateObservationID(Int)
    case invalidScan
    case invalidSnapshot
}

enum LiveWorldTrackerRejection: Equatable {
    case noEligibleReference
    case matcherRejected
    case recentOrOverlappingReference
    case confirmationRequired
    case inconsistentConfirmation
    case inconsistentIslandGeometry
    case groundPlaneVerificationRejected
}

struct LiveWorldTrackerReviewPoint: Equatable {
    let observationID: Int
    let rawPose: CGPoint
    let optimizedPose: CGPoint
    let isKeyframe: Bool
}

struct LiveWorldTrackerMatchLine: Equatable {
    let fromObservationID: Int
    let toObservationID: Int
    let fromPose: CGPoint
    let toPose: CGPoint
}

struct LiveWorldTrackerReview: Equatable {
    let points: [LiveWorldTrackerReviewPoint]
    let matchLines: [LiveWorldTrackerMatchLine]
    let rejections: [LiveWorldTrackerRejection]
}

struct LiveWorldTrackerUpdate: Equatable {
    /// Apply this once to the incoming live pose when `acceptedClosure` is non-nil.
    let correction: CGPoint
    let acceptedClosure: LiveWorldLoopClosureEdge?
    let snapshot: LiveWorldSnapshot
    let revision: Int
    let review: LiveWorldTrackerReview
}

/// A confirmed placement of a live frame against the committed world. Recovery
/// evidence is intentionally transient: it never becomes an observation,
/// keyframe, landmark, or pose-graph edge.
struct LiveWorldRecoveryPlacement: Equatable {
    let cameraPose: CGPoint
    let correction: CGPoint
    let confirmationReferenceKeyframeIDs: [Int]
}

struct LiveWorldRecoveryUpdate: Equatable {
    /// Non-nil only after two geometrically consistent frames in one recovery
    /// epoch. Apply `correction` to the incoming local pose.
    let confirmedPlacement: LiveWorldRecoveryPlacement?
    let matchedReferenceKeyframeID: Int?
    let snapshot: LiveWorldSnapshot
    let revision: Int
    let review: LiveWorldTrackerReview
}

enum LiveWorldRecoveryConfirmation {
    static func referenceIDs(
        firstReferenceID: Int,
        firstCorrection: CGPoint,
        secondReferenceID: Int,
        secondCorrection: CGPoint,
        tolerance: CGFloat
    ) -> [Int]? {
        guard tolerance.isFinite, tolerance >= 0,
              hypot(
                firstCorrection.x - secondCorrection.x,
                firstCorrection.y - secondCorrection.y
              ) <= tolerance else { return nil }
        return [firstReferenceID, secondReferenceID]
    }
}

/// In-memory coordinator for one room's sparse global camera graph. Images are
/// used only while accepting a scan; the durable snapshot retains feature
/// descriptors and pose evidence, never image bytes.
final class LiveWorldTracker {
    private struct LocalEpochKey: Hashable {
        let captureGeneration: UInt64
        let localEpoch: UInt64
    }
    private struct CachedReference {
        let keyframeID: Int
        let observationID: Int
        let roomID: Int
        let reference: GlobalFeatureReference
    }

    private struct PendingClosure {
        let timestamp: Double
        let referenceKeyframeID: Int
        let islandID: Int?
        /// Global drift inferred independently by two observations. Comparing
        /// this correction allows the camera to keep moving between scans.
        let correction: CGPoint
    }

    private struct PendingRecovery {
        let epoch: Int
        let referenceKeyframeID: Int
        let correction: CGPoint
        let missedFrames: Int
    }

    private let matcher = GlobalFeatureMatcher()
    private let visitID = UUID()
    private let keyframeTravelFraction: CGFloat = 0.25
    private let minimumClosureTravelFraction: CGFloat = 0.75
    // Sparse appearance matches need not occur on adjacent recovery samples.
    // Keep one tentative placement only across a brief gap; a second positive,
    // geometrically consistent match is still required before any world write.
    private let maximumMissedRecoveryFrames = 4
    private var references = [Int: CachedReference]()
    private var pendingClosure: PendingClosure?
    private var pendingRecovery: PendingRecovery?
    private var recoveryEpoch = 0
    private var cumulativeTravel = [Int: CGFloat]()
    private var closedReferenceKeyframeIDs = Set<Int>()
    private var lastAcceptedClosureTravel: CGFloat?
    private var lastAcceptedClosureIslandID: Int?
    /// Observations made since the current capture connection began. Saved
    /// observations are immediately searchable after a reopen, while motion
    /// edges are created only inside this visit.
    private var currentVisitObservationIDs = Set<Int>()
    private var lastObservationID: Int?

    private(set) var snapshot: LiveWorldSnapshot

    init(snapshot: LiveWorldSnapshot) throws {
        self.snapshot = snapshot
        try restoreRuntimeState()
    }

    convenience init() {
        try! self.init(snapshot: try! LiveWorldSnapshot())
    }

    func layingOutNewRoom(_ transition: LiveDoorwayTransition,
                          following previous: LiveWorldTrackerUpdate) throws -> LiveWorldTrackerUpdate? {
        guard previous.snapshot == snapshot else { throw LiveWorldTrackerError.invalidSnapshot }
        guard let placed = try LiveEstimatedRoomLayout.placingNewRoom(for: transition, in: snapshot) else { return nil }
        snapshot = placed
        pendingClosure = nil
        pendingRecovery = nil
        refreshReferences()
        return update(correction: previous.correction, closure: previous.acceptedClosure,
                      rejections: previous.review.rejections)
    }

    /// Keep the ingestion and measured doorway alignment in one pipeline commit.
    /// References follow optimized placements; raw local motion remains unchanged.
    func connectingDoorwayFloor(_ constraint: LiveDoorwayFloorConstraint,
                                following previous: LiveWorldTrackerUpdate) throws -> LiveWorldTrackerUpdate {
        guard previous.snapshot == snapshot else { throw LiveWorldTrackerError.invalidSnapshot }
        snapshot = try snapshot.addingDoorwayFloorConstraint(constraint)
        pendingClosure = nil
        pendingRecovery = nil
        refreshReferences()
        return update(correction: previous.correction, closure: previous.acceptedClosure,
                      rejections: previous.review.rejections)
    }

    func beginVisit() {
        pendingClosure = nil
        beginRecoveryEpoch()
        currentVisitObservationIDs.removeAll(keepingCapacity: true)
        closedReferenceKeyframeIDs.removeAll(keepingCapacity: true)
        lastAcceptedClosureTravel = nil
        lastAcceptedClosureIslandID = nil
        lastObservationID = nil
    }

    /// Starts a fresh two-frame relocalization attempt. A recovery epoch is
    /// separate from pose-graph edge cooldowns, so it can always search every
    /// committed reference, including a keyframe used by an earlier closure.
    func beginRecoveryEpoch() {
        recoveryEpoch = recoveryEpoch < Int.max ? recoveryEpoch + 1 : 0
        pendingRecovery = nil
    }

    /// Searches committed keyframes for a transient world placement. Unlike
    /// `ingest`, this does not retain the frame or add a graph edge.
    func recover(
        frame: CGImage,
        proposedCameraPose: CGPoint,
        solveWidth: Double,
        exclusions: [CGRect] = [],
        roomID: Int = 0
    ) throws -> LiveWorldRecoveryUpdate {
        guard frame.width > 0, frame.height > 0,
              proposedCameraPose.x.isFinite, proposedCameraPose.y.isFinite,
              solveWidth.isFinite, solveWidth > 0,
              exclusions.allSatisfy(Self.isValid) else {
            throw LiveWorldTrackerError.invalidScan
        }
        guard !references.isEmpty else {
            pendingRecovery = nil
            return recoveryUpdate(reference: nil, placement: nil, rejections: [.noEligibleReference])
        }
        guard roomID >= 0 else { throw LiveWorldTrackerError.invalidScan }
        guard let candidate = recoveryMatch(
            frame: frame, solveWidth: solveWidth, exclusions: exclusions,
            roomID: roomID
        ) else {
            if let pending = pendingRecovery, pending.epoch == recoveryEpoch,
               pending.missedFrames < maximumMissedRecoveryFrames {
                pendingRecovery = PendingRecovery(
                    epoch: pending.epoch,
                    referenceKeyframeID: pending.referenceKeyframeID,
                    correction: pending.correction,
                    missedFrames: pending.missedFrames + 1
                )
            } else {
                pendingRecovery = nil
            }
            return recoveryUpdate(reference: nil, placement: nil, rejections: [.matcherRejected])
        }

        let (reference, match) = candidate
        let correction = CGPoint(
            x: match.proposedCameraPose.x - proposedCameraPose.x,
            y: match.proposedCameraPose.y - proposedCameraPose.y
        )
        let consistency = max(CGFloat(solveWidth) * 0.0125, 4)
        guard let pending = pendingRecovery, pending.epoch == recoveryEpoch else {
            pendingRecovery = PendingRecovery(
                epoch: recoveryEpoch, referenceKeyframeID: reference.keyframeID,
                correction: correction, missedFrames: 0
            )
            return recoveryUpdate(reference: reference, placement: nil, rejections: [.confirmationRequired])
        }
        guard let confirmationReferenceIDs = LiveWorldRecoveryConfirmation.referenceIDs(
            firstReferenceID: pending.referenceKeyframeID,
            firstCorrection: pending.correction,
            secondReferenceID: reference.keyframeID,
            secondCorrection: correction,
            tolerance: consistency
        ) else {
            pendingRecovery = PendingRecovery(
                epoch: recoveryEpoch, referenceKeyframeID: reference.keyframeID,
                correction: correction, missedFrames: 0
            )
            return recoveryUpdate(reference: reference, placement: nil, rejections: [.inconsistentConfirmation])
        }

        pendingRecovery = nil
        let placement = LiveWorldRecoveryPlacement(
            cameraPose: match.proposedCameraPose,
            correction: correction,
            confirmationReferenceKeyframeIDs: confirmationReferenceIDs
        )
        return recoveryUpdate(reference: reference, placement: placement, rejections: [])
    }

    var hasPendingClosure: Bool { pendingClosure != nil }

    func ingest(
        observationID: Int,
        sourceObservationID: Int? = nil,
        timestamp: Double,
        captureTimestamp: Double? = nil,
        frame: CGImage,
        proposedCameraPose: CGPoint,
        solveWidth: Double,
        exclusions: [CGRect] = [],
        searchForClosure: Bool = true,
        isClosureConfirmationProbe: Bool = false,
        localPose: CGPoint? = nil,
        captureGeneration: UInt64 = 0,
        localEpoch: UInt64 = 0,
        basisRevision: UInt64 = 0,
        roomID: Int = 0,
        islandCapture: LiveIslandCapture? = nil,
        groundGeometry: LiveWorldGroundGeometry? = nil,
        observedFloorCandidates: LiveObservedFloorCandidates? = nil,
        observedPlayerBounds: CGRect? = nil,
        verifyClosure: ((LiveWorldObservation, CGPoint) -> CGPoint?)? = nil,
        proposePatchClosure: (([LiveWorldPatchReference]) -> GlobalFeatureMatch?)? = nil
    ) throws -> LiveWorldTrackerUpdate {
        guard observationID >= 0, timestamp.isFinite, captureTimestamp?.isFinite ?? true,
              frame.width > 0, frame.height > 0,
              proposedCameraPose.x.isFinite, proposedCameraPose.y.isFinite,
              solveWidth.isFinite, solveWidth > 0,
              roomID >= 0, exclusions.allSatisfy(Self.isValid) else {
            throw LiveWorldTrackerError.invalidScan
        }
        guard !snapshot.observations.contains(where: { $0.id == observationID }) else {
            throw LiveWorldTrackerError.duplicateObservationID(observationID)
        }
        let sourceID = sourceObservationID ?? observationID
        guard sourceID >= 0 else { throw LiveWorldTrackerError.invalidScan }

        var islands = snapshot.trackingIslands
        var optimizedPose = proposedCameraPose
        if let capture = islandCapture {
            guard capture.roomID == roomID else { throw LiveWorldTrackerError.invalidScan }
            if let index = islands.firstIndex(where: { $0.id == capture.id }) {
                guard islands[index].roomID == roomID,
                      islands[index].startedAt == capture.startedAt else { throw LiveWorldTrackerError.invalidScan }
                // A queued frame retains local geometry even if its island
                // moved after capture and before atlas integration.
                optimizedPose = CGPoint(x: islands[index].worldFromLocal.x + capture.localPose.x,
                                        y: islands[index].worldFromLocal.y + capture.localPose.y)
                islands[index] = islands[index].adding(capture.member(observationID: observationID))
            } else {
                islands.append(capture.island(observationID: observationID))
                optimizedPose = CGPoint(x: capture.worldFromLocal.x + capture.localPose.x,
                                        y: capture.worldFromLocal.y + capture.localPose.y)
            }
        }
        let travel = nextCumulativeTravel(for: proposedCameraPose, roomID: roomID)
        let observation = LiveWorldObservation(
            id: observationID, sourceObservationID: sourceID, timestamp: timestamp,
            captureTimestamp: captureTimestamp,
            sourceWidth: frame.width, sourceHeight: frame.height, solveWidth: solveWidth,
            sampleWidth: 160, excludedRects: exclusions,
            localPose: localPose ?? proposedCameraPose,
            captureGeneration: captureGeneration,
            localEpoch: localEpoch,
            basisRevision: basisRevision,
            roomID: roomID,
            rawPose: proposedCameraPose,
            optimizedPose: optimizedPose,
            groundGeometry: islandCapture?.measured == true ? groundGeometry : nil,
            observedFloorCandidates: islandCapture?.measured == true ? observedFloorCandidates : nil,
            observedPlayerBounds: observedPlayerBounds
        )
        if islandCapture == nil {
            // Legacy callers have no proof of continuity; keep each as an
            // explicit unknown component rather than inventing a rigid group.
            let nextIslandID = (islands.map(\.id).max() ?? -1) + 1
            let legacy = LiveTrackingIsland.legacy(observation)
            islands.append(LiveTrackingIsland(id: nextIslandID, roomID: legacy.roomID,
                startedAt: legacy.startedAt, worldFromLocal: legacy.worldFromLocal,
                placement: legacy.placement, uncertainty: legacy.uncertainty,
                breakReason: legacy.breakReason, members: legacy.members))
        }
        try replaceSnapshot(observations: snapshot.observations + [observation], islands: islands)
        currentVisitObservationIDs.insert(observationID)
        cumulativeTravel[observationID] = travel
        lastObservationID = observationID

        if islandCapture?.measured == false {
            // Retain pixels for provisional display, never as landmark/loop or
            // trusted motion evidence. Local tracking may later reconnect a
            // different measured island without promoting these captures.
            pendingClosure = nil
            return update(correction: .zero, closure: nil, rejections: [])
        }

        if snapshot.keyframes.isEmpty {
            _ = try addKeyframe(observation, frame: frame)
            pendingClosure = nil
            return update(correction: .zero, closure: nil, rejections: [.noEligibleReference])
        }

        if !searchForClosure {
            if shouldCreateKeyframe(for: observation, cumulativeTravel: travel) {
                _ = try addKeyframe(observation, frame: frame)
            }
            return update(correction: .zero, closure: nil, rejections: [])
        }

        // An extra probe between scheduled scans may be inconclusive because
        // the floor is briefly occluded. Keep the original vote only within
        // this short measured island; it still cannot accept any edge alone.
        // Regular failed scans, stale probes, and ownership breaks clear it.
        let retainVoteAfterInconclusiveProbe = isClosureConfirmationProbe
            && islandCapture?.measured == true
            && pendingClosure?.islandID == islandCapture?.id
            && pendingClosure.map { timestamp > $0.timestamp && timestamp - $0.timestamp < 0.5 } == true
        var rejections = [LiveWorldTrackerRejection]()
        let eligible = eligibleReferences(for: observation, cumulativeTravel: travel)
        let allOlder = references.values.filter {
            $0.observationID != observationID && $0.roomID == observation.roomID
        }
        if eligible.isEmpty {
            rejections.append(allOlder.isEmpty ? .noEligibleReference : .recentOrOverlappingReference)
            if shouldCreateKeyframe(for: observation, cumulativeTravel: travel) {
                _ = try addKeyframe(observation, frame: frame)
            }
            pendingClosure = nil
            return update(correction: .zero, closure: nil, rejections: rejections)
        }

        let preferredReferenceID = pendingClosure.map {
            Self.referenceID(for: $0.referenceKeyframeID)
        }
        let featureMatch = matcher.match(frame: frame, references: eligible.map(\.reference),
            preferredReferenceID: preferredReferenceID, currentSolveWidth: solveWidth, excluding: exclusions)
        let patchMatch: GlobalFeatureMatch? = {
            guard featureMatch == nil, verifyClosure != nil, !matcher.lastSearchWasAmbiguous,
                  let proposePatchClosure else { return nil }
            let searched = Set(matcher.lastSearchedReferenceIDs)
            let selected = eligible.filter { searched.contains($0.reference.id) }
            let permitted = Set(matcher.permittingPatchProposals(selected.map(\.reference)).map(\.id))
            let observations = Dictionary(uniqueKeysWithValues: snapshot.observations.map { ($0.id, $0) })
            let inputs = selected.compactMap { entry -> LiveWorldPatchReference? in
                guard permitted.contains(entry.reference.id), let source = observations[entry.observationID],
                      source.groundGeometry?.lines.isEmpty == false else { return nil }
                return LiveWorldPatchReference(observation: source, reference: entry.reference)
            }
            return proposePatchClosure(inputs)
        }()
        guard let match = featureMatch ?? patchMatch,
              let reference = eligible.first(where: { $0.reference.id == match.referenceID }) else {
            rejections.append(.matcherRejected)
            if shouldCreateKeyframe(for: observation, cumulativeTravel: travel) {
                _ = try addKeyframe(observation, frame: frame)
            }
            if !retainVoteAfterInconclusiveProbe { pendingClosure = nil }
            return update(correction: .zero, closure: nil, rejections: rejections)
        }

        // Scenery features propose displacement. Production callers can require
        // independent camera-plane evidence before either confirmation vote.
        // A failed scheduled verification clears the pending vote. An optional
        // short probe may leave it pending, but never counts as a second vote.
        let closurePose: CGPoint
        if let verifyClosure {
            guard let source = snapshot.observations.first(where: { $0.id == reference.observationID }),
                  let verified = verifyClosure(source, match.proposedCameraPose),
                  verified.x.isFinite, verified.y.isFinite else {
                if !retainVoteAfterInconclusiveProbe { pendingClosure = nil }
                if shouldCreateKeyframe(for: observation, cumulativeTravel: travel) {
                    _ = try addKeyframe(observation, frame: frame)
                }
                return update(correction: .zero, closure: nil,
                    rejections: [.groundPlaneVerificationRejected], match: (reference, match))
            }
            closurePose = verified
        } else { closurePose = match.proposedCameraPose }
        let proposedCorrection = CGPoint(
            x: closurePose.x - observation.rawPose.x,
            y: closurePose.y - observation.rawPose.y
        )
        let consistency = max(CGFloat(solveWidth) * 0.0125, 4)
        guard let pending = pendingClosure else {
            pendingClosure = PendingClosure(
                timestamp: timestamp,
                referenceKeyframeID: reference.keyframeID,
                islandID: islandCapture?.id,
                correction: proposedCorrection
            )
            rejections.append(.confirmationRequired)
            if shouldCreateKeyframe(for: observation, cumulativeTravel: travel) {
                _ = try addKeyframe(observation, frame: frame)
            }
            return update(correction: .zero, closure: nil, rejections: rejections, match: (reference, match))
        }
        guard (pending.referenceKeyframeID == reference.keyframeID
                || (verifyClosure != nil && referenceKeyframesShareRigidIsland(
                    pending.referenceKeyframeID, reference.keyframeID))),
              pending.islandID == islandCapture?.id,
              Self.distance(pending.correction, proposedCorrection) <= consistency else {
            pendingClosure = PendingClosure(
                timestamp: timestamp,
                referenceKeyframeID: reference.keyframeID,
                islandID: islandCapture?.id,
                correction: proposedCorrection
            )
            rejections.append(.inconsistentConfirmation)
            if shouldCreateKeyframe(for: observation, cumulativeTravel: travel) {
                _ = try addKeyframe(observation, frame: frame)
            }
            return update(correction: .zero, closure: nil, rejections: rejections, match: (reference, match))
        }

        pendingClosure = nil
        let currentKeyframeID = try addKeyframe(observation, frame: frame)
        guard snapshot.observations.contains(where: { $0.id == reference.observationID }),
              match.inlierReferenceFeatureIDs.count >= 3 else {
            throw LiveWorldTrackerError.invalidSnapshot
        }
        let closure = LiveWorldLoopClosureEdge(
            id: nextID(in: snapshot.loopClosureEdges.map(\.id)),
            fromKeyframeID: reference.keyframeID,
            toKeyframeID: currentKeyframeID,
            landmarkIDs: match.inlierReferenceFeatureIDs,
            deltaX: Double(closurePose.x - reference.reference.cameraPose.x),
            deltaY: Double(closurePose.y - reference.reference.cameraPose.y),
            support: match.inlierReferenceFeatureIDs.count,
            ambiguity: match.ambiguityRatio,
            residualPixels: match.residual * solveWidth / Double(reference.reference.sampleWidth)
        )
        let beforeClosure = snapshot
        let correction: CGPoint
        do {
            try replaceSnapshot(loopEdges: snapshot.loopClosureEdges + [closure])
            correction = try optimize(afterAdding: observationID, closureID: closure.id)
        } catch is LiveIslandCorrectionError {
            // A plausible repeated match can still contradict known geometry.
            // Reject just that constraint; retain the new capture/keyframe and
            // leave the accepted graph, poses and closure cooldown untouched.
            snapshot = beforeClosure
            refreshReferences()
            return update(correction: .zero, closure: nil,
                          rejections: [.inconsistentIslandGeometry], match: (reference, match))
        }
        closedReferenceKeyframeIDs.insert(reference.keyframeID)
        lastAcceptedClosureTravel = travel
        lastAcceptedClosureIslandID = island(containing: observationID)?.id
        return update(correction: correction, closure: closure, rejections: [], match: (reference, match))
    }

    /// Distinct views of the same proven rigid island can independently confirm
    /// a moving revisit. Estimated spacing between different islands cannot.
    private func referenceKeyframesShareRigidIsland(_ first: Int, _ second: Int) -> Bool {
        guard let a = snapshot.keyframes.first(where: { $0.id == first }),
              let b = snapshot.keyframes.first(where: { $0.id == second }),
              let owner = island(containing: a.observationID),
              owner.placement != .legacyUnknown,
              island(containing: b.observationID)?.id == owner.id else { return false }
        return true
    }

    private func eligibleReferences(for observation: LiveWorldObservation, cumulativeTravel travelDistance: CGFloat) -> [CachedReference] {
        let minimumTravel = max(CGFloat(observation.solveWidth) * minimumClosureTravelFraction, 1)
        let minimumTravelAfterClosure = max(CGFloat(observation.solveWidth) * keyframeTravelFraction, 1)
        let currentIsland = island(containing: observation.id)
        if let lastAcceptedClosureTravel,
           (currentIsland?.placement == .legacyUnknown || currentIsland?.id == lastAcceptedClosureIslandID),
           travelDistance - lastAcceptedClosureTravel < minimumTravelAfterClosure {
            return []
        }
        let keyframes = Dictionary(uniqueKeysWithValues: snapshot.keyframes.map { ($0.id, $0) })
        return references.values.filter { candidate in
            guard let referenceObservation = snapshot.observations.first(where: { $0.id == candidate.observationID }),
                  let keyframe = keyframes[candidate.keyframeID], keyframe.observationID == candidate.observationID,
                  referenceObservation.id < observation.id,
                  let referenceTravel = cumulativeTravel[candidate.observationID] else { return false }
            let referenceIsland = island(containing: candidate.observationID)
            let differentIsland = currentIsland?.placement != .legacyUnknown
                && currentIsland?.id != referenceIsland?.id
            return candidate.roomID == observation.roomID
                && (differentIsland || !closedReferenceKeyframeIDs.contains(candidate.keyframeID))
                && (differentIsland || !currentVisitObservationIDs.contains(candidate.observationID)
                    || travelDistance - referenceTravel >= minimumTravel)
        }.sorted { $0.keyframeID < $1.keyframeID }
    }

    private func shouldCreateKeyframe(for observation: LiveWorldObservation, cumulativeTravel: CGFloat) -> Bool {
        if let owner = island(containing: observation.id), owner.placement != .legacyUnknown,
           !snapshot.keyframes.contains(where: { keyframe in
               keyframe.observationID != observation.id
                   && owner.members.contains { $0.observationID == keyframe.observationID }
           }) { return true }
        let roomObservationIDs = Set(snapshot.observations.lazy
            .filter { $0.roomID == observation.roomID }
            .map(\.id))
        guard let last = snapshot.keyframes.last(where: {
                  currentVisitObservationIDs.contains($0.observationID)
                      && $0.observationID != observation.id
                      && roomObservationIDs.contains($0.observationID)
              }),
              let lastTravel = cumulativeTravelForObservation(last.observationID) else { return true }
        return cumulativeTravel - lastTravel >= CGFloat(observation.solveWidth) * keyframeTravelFraction
    }

    @discardableResult
    private func addKeyframe(_ observation: LiveWorldObservation, frame: CGImage) throws -> Int {
        if let existing = snapshot.keyframes.first(where: { $0.observationID == observation.id }) { return existing.id }
        let roomObservationIDs = Set(snapshot.observations.lazy
            .filter { $0.roomID == observation.roomID }
            .map(\.id))
        let keyframeID = observation.id
        let prior = snapshot.keyframes.last {
            currentVisitObservationIDs.contains($0.observationID)
                && $0.observationID != observation.id
                && roomObservationIDs.contains($0.observationID)
        }
        let keyframe = LiveWorldKeyframe(id: keyframeID, observationID: observation.id, sourceObservationID: observation.sourceObservationID)
        var motionEdges = snapshot.relativeMotionEdges
        if let prior,
           let priorObservation = snapshot.observations.first(where: { $0.id == prior.observationID }),
           priorObservation.roomID == observation.roomID,
           priorObservation.captureGeneration == observation.captureGeneration,
           priorObservation.localEpoch == observation.localEpoch,
           reliableMotionCanConnect(priorObservation.id, observation.id) {
            motionEdges.append(LiveWorldRelativeMotionEdge(
                id: nextID(in: motionEdges.map(\.id)), fromObservationID: priorObservation.id,
                toObservationID: observation.id,
                deltaX: Double(observation.localPose.x - priorObservation.localPose.x),
                deltaY: Double(observation.localPose.y - priorObservation.localPose.y), support: 1
            ))
        }

        var landmarks = snapshot.landmarks
        if let reference = matcher.makeReference(
            id: Self.referenceID(for: keyframeID), frame: frame, cameraPose: observation.optimizedPose,
            solveWidth: observation.solveWidth, excluding: observation.excludedRects
        ) {
            for feature in reference.features {
                let landmarkID = nextID(in: landmarks.map(\.id))
                let sampleScale = CGFloat(observation.solveWidth) / CGFloat(reference.sampleWidth)
                landmarks.append(LiveWorldLandmark(
                    id: landmarkID, keyframeID: keyframeID, sourceObservationID: observation.sourceObservationID,
                    samplePoint: feature.samplePoint,
                    worldPoint: CGPoint(
                        x: observation.optimizedPose.x + feature.samplePoint.x * sampleScale,
                        y: observation.optimizedPose.y
                            + (CGFloat(reference.sampleHeight) - feature.samplePoint.y) * sampleScale
                    ),
                    descriptor: feature.descriptor.map(Float.init), depth: feature.depth,
                    depthConfidence: feature.depthConfidence
                ))
            }
        }
        try replaceSnapshot(keyframes: snapshot.keyframes + [keyframe], landmarks: landmarks, relativeEdges: motionEdges)
        if let rebuilt = reference(for: keyframe) { references[keyframe.id] = rebuilt }
        return keyframeID
    }

    private func island(containing observationID: Int) -> LiveTrackingIsland? {
        snapshot.trackingIslands.first { $0.members.contains { $0.observationID == observationID } }
    }

    private func reliableMotionCanConnect(_ from: Int, _ to: Int) -> Bool {
        let first = snapshot.trackingIslands.first { $0.members.contains { $0.observationID == from } }
        let second = snapshot.trackingIslands.first { $0.members.contains { $0.observationID == to } }
        if first?.placement == .legacyUnknown && second?.placement == .legacyUnknown { return true }
        return first?.id == second?.id
            && first?.members.first(where: { $0.observationID == from })?.measured == true
            && second?.members.first(where: { $0.observationID == to })?.measured == true
    }

    private func optimize(afterAdding observationID: Int, closureID: Int) throws -> CGPoint {
        if snapshot.trackingIslands.contains(where: { $0.placement != .legacyUnknown }) {
            guard let before = snapshot.observations.first(where: { $0.id == observationID }) else {
                throw LiveWorldTrackerError.invalidSnapshot
            }
            snapshot = try LiveIslandCorrection.applying(closureID, to: snapshot)
            refreshReferences()
            guard let after = snapshot.observations.first(where: { $0.id == observationID }) else {
                throw LiveWorldTrackerError.invalidSnapshot
            }
            return CGPoint(x: after.optimizedPose.x - before.rawPose.x,
                           y: after.optimizedPose.y - before.rawPose.y)
        }
        guard let currentObservation = snapshot.observations.first(where: { $0.id == observationID })
        else { throw LiveWorldTrackerError.invalidSnapshot }
        let roomID = currentObservation.roomID
        let roomObservationIDs = Set(snapshot.observations.lazy
            .filter { $0.roomID == roomID }
            .map(\.id))
        let roomKeyframes = snapshot.keyframes.filter {
            roomObservationIDs.contains($0.observationID)
        }
        guard let anchor = roomKeyframes.first?.observationID else {
            throw LiveWorldTrackerError.invalidSnapshot
        }
        let keyframeObservationIDs = Set(roomKeyframes.map(\.observationID))
        let nodes = roomKeyframes.compactMap { keyframe -> TranslationPoseNode? in
            guard let observation = snapshot.observations.first(where: { $0.id == keyframe.observationID }) else { return nil }
            return TranslationPoseNode(
                id: observation.id, raw: observation.rawPose, initial: observation.optimizedPose
            )
        }
        guard nodes.count == roomKeyframes.count else { throw LiveWorldTrackerError.invalidSnapshot }
        var constraints = [TranslationPoseConstraint]()
        for (index, edge) in snapshot.relativeMotionEdges.sorted(by: { $0.id < $1.id }).enumerated()
            where keyframeObservationIDs.contains(edge.fromObservationID) && keyframeObservationIDs.contains(edge.toObservationID) {
            constraints.append(TranslationPoseConstraint(
                id: index, from: edge.fromObservationID, to: edge.toObservationID,
                measuredDelta: CGPoint(x: edge.deltaX, y: edge.deltaY), weight: Double(edge.support), kind: .motion
            ))
        }
        let keyframes = Dictionary(uniqueKeysWithValues: snapshot.keyframes.map { ($0.id, $0) })
        for edge in snapshot.loopClosureEdges.sorted(by: { $0.id < $1.id }) {
            guard let from = keyframes[edge.fromKeyframeID], let to = keyframes[edge.toKeyframeID] else {
                throw LiveWorldTrackerError.invalidSnapshot
            }
            guard keyframeObservationIDs.contains(from.observationID),
                  keyframeObservationIDs.contains(to.observationID) else { continue }
            constraints.append(TranslationPoseConstraint(
                id: constraints.count, from: from.observationID, to: to.observationID,
                measuredDelta: CGPoint(x: edge.deltaX, y: edge.deltaY),
                weight: max(1, Double(edge.support) * 8), kind: .loopClosure
            ))
        }
        let solution = try TranslationPoseGraph(
            nodes: nodes, constraints: constraints, anchorID: anchor, baseRevision: snapshot.mapRevision
        ).solve()
        let roomObservations = snapshot.observations.filter { $0.roomID == roomID }
        let epochIDs = Dictionary(uniqueKeysWithValues: Set(roomObservations.map {
            LocalEpochKey(captureGeneration: $0.captureGeneration, localEpoch: $0.localEpoch)
        }).map { ($0, UUID()) })
        let samples = roomKeyframes.compactMap { keyframe -> PoseCorrectionSample? in
            guard let observation = snapshot.observations.first(where: { $0.id == keyframe.observationID }),
                  let position = solution.positions[observation.id],
                  let epochID = epochIDs[LocalEpochKey(
                    captureGeneration: observation.captureGeneration,
                    localEpoch: observation.localEpoch
                  )] else { return nil }
            return PoseCorrectionSample(
                observationID: observation.id, visitID: epochID, timestamp: observation.timestamp,
                correction: CGPoint(x: position.x - observation.rawPose.x, y: position.y - observation.rawPose.y)
            )
        }
        var optimized = Dictionary(uniqueKeysWithValues: snapshot.observations.map {
            ($0.id, $0.optimizedPose)
        })
        for observation in roomObservations {
            let epochID = epochIDs[LocalEpochKey(
                captureGeneration: observation.captureGeneration,
                localEpoch: observation.localEpoch
            )] ?? visitID
            optimized[observation.id] = PoseCorrectionInterpolator.refinedPose(
                raw: observation.rawPose, forTimestamp: observation.timestamp, visitID: epochID, samples: samples
            )
        }
        snapshot = try snapshot.replacingOptimizedPoses(optimized, baseRevision: snapshot.mapRevision)
        refreshReferences()
        guard let current = snapshot.observations.first(where: { $0.id == observationID }) else {
            throw LiveWorldTrackerError.invalidSnapshot
        }
        return CGPoint(x: current.optimizedPose.x - current.rawPose.x, y: current.optimizedPose.y - current.rawPose.y)
    }

    private func update(
        correction: CGPoint,
        closure: LiveWorldLoopClosureEdge?,
        rejections: [LiveWorldTrackerRejection],
        match: (CachedReference, GlobalFeatureMatch)? = nil
    ) -> LiveWorldTrackerUpdate {
        let keyframeIDs = Set(snapshot.keyframes.map(\.observationID))
        let points = snapshot.observations.map {
            LiveWorldTrackerReviewPoint(
                observationID: $0.id, rawPose: $0.rawPose, optimizedPose: $0.optimizedPose,
                isKeyframe: keyframeIDs.contains($0.id)
            )
        }
        var lines = snapshot.loopClosureEdges.compactMap { edge -> LiveWorldTrackerMatchLine? in
            guard let from = snapshot.keyframes.first(where: { $0.id == edge.fromKeyframeID }),
                  let to = snapshot.keyframes.first(where: { $0.id == edge.toKeyframeID }),
                  let fromObservation = snapshot.observations.first(where: { $0.id == from.observationID }),
                  let toObservation = snapshot.observations.first(where: { $0.id == to.observationID }) else { return nil }
            return LiveWorldTrackerMatchLine(
                fromObservationID: from.observationID, toObservationID: to.observationID,
                fromPose: fromObservation.optimizedPose, toPose: toObservation.optimizedPose
            )
        }
        if let match, let current = snapshot.observations.last {
            lines.append(LiveWorldTrackerMatchLine(
                fromObservationID: match.0.observationID, toObservationID: current.id,
                fromPose: match.0.reference.cameraPose, toPose: match.1.proposedCameraPose
            ))
        }
        return LiveWorldTrackerUpdate(
            correction: correction, acceptedClosure: closure, snapshot: snapshot,
            revision: snapshot.mapRevision, review: LiveWorldTrackerReview(points: points, matchLines: lines, rejections: rejections)
        )
    }

    private func recoveryMatch(
        frame: CGImage, solveWidth: Double, exclusions: [CGRect], roomID: Int
    ) -> (CachedReference, GlobalFeatureMatch)? {
        let eligible = references.values.filter { $0.roomID == roomID }
            .sorted { $0.keyframeID < $1.keyframeID }
        guard let match = matcher.match(
            frame: frame,
            references: eligible.map(\.reference),
            currentSolveWidth: solveWidth,
            excluding: exclusions
        ), let reference = eligible.first(where: { $0.reference.id == match.referenceID }) else {
            return nil
        }
        return (reference, match)
    }

    private func recoveryUpdate(
        reference: CachedReference?, placement: LiveWorldRecoveryPlacement?,
        rejections: [LiveWorldTrackerRejection]
    ) -> LiveWorldRecoveryUpdate {
        LiveWorldRecoveryUpdate(
            confirmedPlacement: placement, matchedReferenceKeyframeID: reference?.keyframeID,
            snapshot: snapshot, revision: snapshot.mapRevision,
            review: update(correction: .zero, closure: nil, rejections: rejections).review
        )
    }

    private func replaceSnapshot(
        observations: [LiveWorldObservation]? = nil,
        keyframes: [LiveWorldKeyframe]? = nil,
        landmarks: [LiveWorldLandmark]? = nil,
        relativeEdges: [LiveWorldRelativeMotionEdge]? = nil,
        loopEdges: [LiveWorldLoopClosureEdge]? = nil,
        islands: [LiveTrackingIsland]? = nil
    ) throws {
        snapshot = try LiveWorldSnapshot(
            schemaVersion: snapshot.schemaVersion, mapRevision: snapshot.mapRevision + 1,
            anchoredObservationID: snapshot.anchoredObservationID ?? observations?.first?.id,
            observations: observations ?? snapshot.observations,
            keyframes: keyframes ?? snapshot.keyframes, landmarks: landmarks ?? snapshot.landmarks,
            relativeMotionEdges: relativeEdges ?? snapshot.relativeMotionEdges,
            loopClosureEdges: loopEdges ?? snapshot.loopClosureEdges,
            trackingIslands: islands ?? snapshot.trackingIslands,
            doorwayFloorConstraints: snapshot.doorwayFloorConstraints
        )
    }

    private func restoreRuntimeState() throws {
        cumulativeTravel.removeAll(keepingCapacity: true)
        var previous: LiveWorldObservation?
        var travel: CGFloat = 0
        for observation in snapshot.observations {
            if let previous, previous.roomID == observation.roomID {
                travel += Self.distance(observation.rawPose, previous.rawPose)
            } else if previous != nil {
                travel = 0
            }
            cumulativeTravel[observation.id] = travel
            previous = observation
        }
        // Loading a snapshot begins a new visit. A later visual closure joins
        // it to the saved graph; raw camera motion must never bridge launches.
        lastObservationID = nil
        refreshReferences()
    }

    private func refreshReferences() {
        references.removeAll(keepingCapacity: true)
        for keyframe in snapshot.keyframes {
            if let reference = reference(for: keyframe) { references[keyframe.id] = reference }
        }
    }

    private func reference(for keyframe: LiveWorldKeyframe) -> CachedReference? {
        guard let observation = snapshot.observations.first(where: { $0.id == keyframe.observationID }) else { return nil }
        let landmarks = snapshot.landmarks.filter { $0.keyframeID == keyframe.id }.sorted { $0.id < $1.id }
        guard landmarks.count >= 6 else { return nil }
        let sampleHeight = max(1, Int((Double(observation.sourceHeight) * Double(observation.sampleWidth) / Double(observation.sourceWidth)).rounded()))
        let features = landmarks.map { landmark in
            GlobalFeatureReference.Feature(
                id: landmark.id, samplePoint: landmark.samplePoint,
                descriptor: landmark.descriptor.map { UInt8(clamping: Int($0.rounded())) },
                strength: 255, depth: landmark.depth, depthConfidence: landmark.depthConfidence
            )
        }
        guard features.allSatisfy({ $0.descriptor.count == 25 }) else { return nil }
        return CachedReference(
            keyframeID: keyframe.id, observationID: observation.id,
            roomID: observation.roomID,
            reference: GlobalFeatureReference(
                id: Self.referenceID(for: keyframe.id), cameraPose: observation.optimizedPose,
                solveWidth: observation.solveWidth, sourceWidth: observation.sourceWidth,
                sourceHeight: observation.sourceHeight, sampleWidth: observation.sampleWidth,
                sampleHeight: sampleHeight, features: features
            )
        )
    }

    private func nextCumulativeTravel(for pose: CGPoint, roomID: Int) -> CGFloat {
        guard let lastObservationID,
              let last = snapshot.observations.first(where: { $0.id == lastObservationID }),
              last.roomID == roomID,
              let previousTravel = cumulativeTravel[lastObservationID] else { return 0 }
        return previousTravel + Self.distance(pose, last.rawPose)
    }

    private func cumulativeTravelForObservation(_ observationID: Int) -> CGFloat? {
        cumulativeTravel[observationID]
    }

    private func nextID(in ids: [Int]) -> Int {
        guard let maximum = ids.max() else { return 0 }
        return maximum < Int.max ? maximum + 1 : 0
    }

    private static func referenceID(for keyframeID: Int) -> UUID {
        let value = UInt64(keyframeID)
        return UUID(uuid: (
            0, 0, 0, 0, 0, 0, 0, 0,
            UInt8((value >> 56) & 0xff), UInt8((value >> 48) & 0xff),
            UInt8((value >> 40) & 0xff), UInt8((value >> 32) & 0xff),
            UInt8((value >> 24) & 0xff), UInt8((value >> 16) & 0xff),
            UInt8((value >> 8) & 0xff), UInt8(value & 0xff)
        ))
    }

    private static func distance(_ lhs: CGPoint, _ rhs: CGPoint) -> CGFloat {
        hypot(lhs.x - rhs.x, lhs.y - rhs.y)
    }

    private static func isValid(_ rect: CGRect) -> Bool {
        rect.origin.x.isFinite && rect.origin.y.isFinite && rect.width.isFinite && rect.height.isFinite
            && rect.width >= 0 && rect.height >= 0
    }
}
