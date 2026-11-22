import CoreGraphics
import Foundation

/// A horizontal passage proves equal floor elevation, never horizontal spacing.
/// Endpoints are immutable image-local observations; world connectors are derived.
struct LiveDoorwayFloorConstraint: Codable, Equatable, Identifiable {
    let id: Int
    let departureObservationID: Int
    let arrivalObservationID: Int
    let departureFloor: CGPoint
    let arrivalFloor: CGPoint
    let direction: Int
    let departureSupport: [Int]
    let arrivalSupport: [Int]

    func isValid(in snapshot: LiveWorldSnapshot, requiringAlignment: Bool) -> Bool {
        let observations = Dictionary(uniqueKeysWithValues: snapshot.observations.map { ($0.id, $0) })
        let owners = Dictionary(uniqueKeysWithValues: snapshot.trackingIslands.flatMap { island in
            island.members.map { ($0.observationID, (island, $0)) }
        })
        guard id >= 0, direction == -1 || direction == 1,
              let source = observations[departureObservationID],
              let target = observations[arrivalObservationID],
              source.roomID != target.roomID, source.solveWidth == target.solveWidth,
              let sourceOwner = owners[source.id], let targetOwner = owners[target.id],
              sourceOwner.0.placement != .legacyUnknown, targetOwner.0.placement != .legacyUnknown,
              sourceOwner.1.measured, targetOwner.1.measured,
              onGround(departureFloor, observation: source), onGround(arrivalFloor, observation: target)
        else { return false }
        let unit = source.solveWidth / 640
        let sourceWorldY = source.optimizedPose.y + departureFloor.y
        let targetWorldY = target.optimizedPose.y + arrivalFloor.y
        // Exit and entrance evidence must be on their respective image sides.
        guard direction == 1
            ? departureFloor.x >= source.solveWidth * 0.5 && arrivalFloor.x <= target.solveWidth * 0.5
            : departureFloor.x <= source.solveWidth * 0.5 && arrivalFloor.x >= target.solveWidth * 0.5
        else { return false }
        func stableSupport(_ ids: [Int], endpoint: LiveWorldObservation,
                           owner: LiveTrackingIsland, worldY: CGFloat) -> Bool {
            guard (3...16).contains(ids.count), Set(ids).count == ids.count,
                  ids.contains(endpoint.id) else { return false }
            let frames = ids.compactMap { observations[$0] }
            guard frames.count == ids.count,
                  frames.allSatisfy({ frame in
                      owners[frame.id]?.0.id == owner.id && owners[frame.id]?.1.measured == true
                          && frame.roomID == endpoint.roomID && frame.solveWidth == endpoint.solveWidth
                          && frame.groundGeometry?.lines.contains(where: {
                              abs(frame.optimizedPose.y + $0.start.y - worldY) <= 2 * unit
                          }) == true
                  }) else { return false }
            let times = frames.map(\.timestamp).sorted()
            let ys = frames.map(\.optimizedPose.y)
            guard times.last == endpoint.timestamp,
                  times.last! - times.first! >= 0.25,
                  times.last! - times.first! <= 2,
                  ys.max()! - ys.min()! <= 4 * unit,
                  zip(times, times.dropFirst()).allSatisfy({ $1 > $0 && $1 - $0 <= 0.35 })
            else { return false }
            return true
        }
        guard stableSupport(departureSupport, endpoint: source, owner: sourceOwner.0, worldY: sourceWorldY),
              stableSupport(arrivalSupport, endpoint: target, owner: targetOwner.0, worldY: targetWorldY),
              source.timestamp < arrivalSupport.compactMap({ observations[$0]?.timestamp }).min()!
        else { return false }
        return !requiringAlignment || abs(sourceWorldY - targetWorldY) <= 4 * unit
    }

    private func onGround(_ point: CGPoint, observation: LiveWorldObservation) -> Bool {
        guard point.x.isFinite, point.y.isFinite else { return false }
        return observation.groundGeometry?.lines.contains {
            abs($0.start.y - point.y) <= 0.000001
                && point.x >= $0.start.x && point.x <= $0.end.x
        } == true
    }
}

extension LiveWorldSnapshot {
    /// Solve before appending the constraint: the old estimated room layout is
    /// allowed to disagree; the newly accepted persisted graph must align floors.
    func addingDoorwayFloorConstraint(_ constraint: LiveDoorwayFloorConstraint) throws -> Self {
        guard !doorwayFloorConstraints.contains(where: { $0.id == constraint.id }),
              constraint.isValid(in: self, requiringAlignment: false) else {
            throw LiveWorldModelError.invalidDoorwayFloorConstraint
        }
        let observations = Dictionary(uniqueKeysWithValues: self.observations.map { ($0.id, $0) })
        let owners = Dictionary(uniqueKeysWithValues: trackingIslands.flatMap { island in
            island.members.map { ($0.observationID, island.id) }
        })
        let keyframes = Dictionary(uniqueKeysWithValues: self.keyframes.map { ($0.id, $0) })
        let islands = Dictionary(uniqueKeysWithValues: trackingIslands.map { ($0.id, $0) })
        var links = loopClosureEdges.compactMap { edge -> LiveIslandAxisSolver.Link? in
            guard let from = keyframes[edge.fromKeyframeID].flatMap({ owners[$0.observationID] }),
                  let to = keyframes[edge.toKeyframeID].flatMap({ owners[$0.observationID] }),
                  islands[from]?.placement != .legacyUnknown,
                  islands[to]?.placement != .legacyUnknown else { return nil }
            return .init(from: from, to: to)
        }
        links += doorwayFloorConstraints.map {
            .init(from: owners[$0.departureObservationID]!, to: owners[$0.arrivalObservationID]!, horizontal: false)
        }
        let from = observations[constraint.departureObservationID]!
        let to = observations[constraint.arrivalObservationID]!
        let correction = CGVector(dx: 0,
            dy: from.optimizedPose.y + constraint.departureFloor.y
                - to.optimizedPose.y - constraint.arrivalFloor.y)
        let solution = try LiveIslandAxisSolver.solve(islandIDs: Set(trackingIslands.map(\.id)),
            anchored: Set(trackingIslands.filter(\.isGloballyConnected).map(\.id)), links: links,
            newLink: .init(from: owners[from.id]!, to: owners[to.id]!, horizontal: false),
            correction: correction, tolerance: from.solveWidth / 640 * 4)
        let moved = try translatingIslands(solution.shifts, baseRevision: mapRevision)
        return try Self(mapRevision: moved.mapRevision, anchoredObservationID: moved.anchoredObservationID,
            observations: moved.observations, keyframes: moved.keyframes, landmarks: moved.landmarks,
            relativeMotionEdges: moved.relativeMotionEdges, loopClosureEdges: moved.loopClosureEdges,
            trackingIslands: moved.trackingIslands,
            doorwayFloorConstraints: moved.doorwayFloorConstraints + [constraint])
    }
}

/// The connector's floor elevation is measured. Its horizontal span describes
/// estimated display layout, not physical passage length.
struct LiveWorldDoorwayConnector: Equatable, Sendable {
    let id: Int
    let departureObservationID: Int
    let arrivalObservationID: Int
    let start: CGPoint
    let end: CGPoint
}

extension LiveWorldSnapshot {
    var ownedDoorwayConnectors: [LiveWorldDoorwayConnector] {
        let byID = Dictionary(uniqueKeysWithValues: observations.map { ($0.id, $0) })
        return doorwayFloorConstraints.map { constraint in
            let from = byID[constraint.departureObservationID]!
            let to = byID[constraint.arrivalObservationID]!
            return LiveWorldDoorwayConnector(id: constraint.id,
                departureObservationID: from.id, arrivalObservationID: to.id,
                start: CGPoint(x: from.optimizedPose.x + constraint.departureFloor.x,
                               y: from.optimizedPose.y + constraint.departureFloor.y),
                end: CGPoint(x: to.optimizedPose.x + constraint.arrivalFloor.x,
                             y: to.optimizedPose.y + constraint.arrivalFloor.y))
        }
    }
}
