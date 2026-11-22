import CoreGraphics
import Foundation

enum LiveIslandCorrectionError: Error, Equatable {
    case missingClosure
    case inconsistentKnownComponent
    case conflictingAnchors
}

/// Accepted links define rigid connected groups. A new loop moves the complete
/// unanchored group; unrelated components retain their estimated placement.
/// Estimated spacing and old capture epochs never become constraints here.
enum LiveIslandCorrection {
    static func applying(_ closureID: Int, to snapshot: LiveWorldSnapshot,
                         tolerance: CGFloat = 4) throws -> LiveWorldSnapshot {
        guard let closure = snapshot.loopClosureEdges.first(where: { $0.id == closureID }) else {
            throw LiveIslandCorrectionError.missingClosure
        }
        let observationByID = Dictionary(uniqueKeysWithValues: snapshot.observations.map { ($0.id, $0) })
        let keyframeByID = Dictionary(uniqueKeysWithValues: snapshot.keyframes.map { ($0.id, $0) })
        let islandByID = Dictionary(uniqueKeysWithValues: snapshot.trackingIslands.map { ($0.id, $0) })
        let owner = Dictionary(uniqueKeysWithValues: snapshot.trackingIslands.flatMap { island in
            island.members.map { ($0.observationID, island.id) }
        })
        func endpoints(_ edge: LiveWorldLoopClosureEdge) -> (from: Int, to: Int)? {
            guard let a = keyframeByID[edge.fromKeyframeID], let b = keyframeByID[edge.toKeyframeID],
                  let from = owner[a.observationID], let to = owner[b.observationID] else { return nil }
            return (from, to)
        }
        guard let ends = endpoints(closure),
              let fromKey = keyframeByID[closure.fromKeyframeID],
              let toKey = keyframeByID[closure.toKeyframeID],
              let fromObservation = observationByID[fromKey.observationID],
              let toObservation = observationByID[toKey.observationID] else {
            throw LiveIslandCorrectionError.missingClosure
        }
        var links = snapshot.loopClosureEdges.compactMap { edge -> LiveIslandAxisSolver.Link? in
            guard edge.id != closureID, let pair = endpoints(edge), pair.from != pair.to,
                  islandByID[pair.from]?.placement != .legacyUnknown,
                  islandByID[pair.to]?.placement != .legacyUnknown else { return nil }
            return LiveIslandAxisSolver.Link(from: pair.from, to: pair.to)
        }
        links += snapshot.doorwayFloorConstraints.map {
            .init(from: owner[$0.departureObservationID]!, to: owner[$0.arrivalObservationID]!, horizontal: false)
        }
        let correction = CGVector(
            dx: closure.deltaX - (toObservation.optimizedPose.x - fromObservation.optimizedPose.x),
            dy: closure.deltaY - (toObservation.optimizedPose.y - fromObservation.optimizedPose.y))
        let anchored = Set(snapshot.trackingIslands.filter(\.isGloballyConnected).map(\.id))
        let solution: LiveIslandAxisSolver.Solution
        do {
            solution = try LiveIslandAxisSolver.solve(islandIDs: Set(islandByID.keys),
                anchored: anchored, links: links,
                newLink: .init(from: ends.from, to: ends.to), correction: correction,
                tolerance: tolerance)
        } catch LiveIslandAxisSolver.Failure.conflictingAnchors {
            throw LiveIslandCorrectionError.conflictingAnchors
        } catch {
            throw LiveIslandCorrectionError.inconsistentKnownComponent
        }
        let shifts = solution.shifts
        let moved = try snapshot.translatingIslands(shifts, baseRevision: snapshot.mapRevision)
        let connected = solution.horizontalComponent.intersection(solution.verticalComponent)
        let globallyConnected = !connected.isDisjoint(with: anchored)
        // Error estimates propagate along actual measured links only. The
        // one-pixel floor prevents a zero-residual descriptor fit from claiming
        // perfect localization. These are estimates, not calibrated intervals.
        var uncertaintyX = [Int: Double](), uncertaintyY = [Int: Double]()
        for id in connected {
            guard islandByID[id]?.isGloballyConnected == true else { continue }
            uncertaintyX[id] = islandByID[id]?.uncertainty.x
            uncertaintyY[id] = islandByID[id]?.uncertainty.y
        }
        let orderedEdges = snapshot.loopClosureEdges.sorted { $0.id < $1.id }
        for _ in 0..<max(1, connected.count) {
            var changed = false
            for edge in orderedEdges {
                guard let pair = endpoints(edge), connected.contains(pair.from), connected.contains(pair.to) else { continue }
                let cost = max(1, edge.residualPixels ?? 4)
                for (a, b) in [(pair.from, pair.to), (pair.to, pair.from)] {
                    if let x = uncertaintyX[a], x + cost < (uncertaintyX[b] ?? .infinity) {
                        uncertaintyX[b] = x + cost; changed = true
                    }
                    if let y = uncertaintyY[a], y + cost < (uncertaintyY[b] ?? .infinity) {
                        uncertaintyY[b] = y + cost; changed = true
                    }
                }
            }
            if !changed { break }
        }
        let islands = moved.trackingIslands.map { island -> LiveTrackingIsland in
            guard connected.contains(island.id) else { return island }
            let placement: LiveIslandPlacement = island.placement == .reference ? .reference
                : (globallyConnected ? .connected : .estimated)
            return island.replacingTranslation(island.worldFromLocal, placement: placement,
                uncertainty: globallyConnected ? LiveIslandUncertainty(
                    x: uncertaintyX[island.id], y: uncertaintyY[island.id]) : .unknown)
        }
        return try LiveWorldSnapshot(mapRevision: moved.mapRevision,
            anchoredObservationID: moved.anchoredObservationID, observations: moved.observations,
            keyframes: moved.keyframes, landmarks: moved.landmarks,
            relativeMotionEdges: moved.relativeMotionEdges, loopClosureEdges: moved.loopClosureEdges,
            trackingIslands: islands, doorwayFloorConstraints: moved.doorwayFloorConstraints)
    }
}
