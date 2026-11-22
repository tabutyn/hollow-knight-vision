import CoreGraphics
import Foundation

/// An image-confirmed loading cut. Timestamps use the capture clock until the
/// atlas request explicitly converts both to its durable observation clock.
struct LiveDoorwayTransition: Equatable, Sendable {
    let sourceRoomID: Int
    let targetRoomID: Int
    let departureTimestamp: TimeInterval
    let arrivalTimestamp: TimeInterval
    let direction: VisualRoomDirection

    func offsettingTimestamps(by offset: TimeInterval) -> Self {
        Self(sourceRoomID: sourceRoomID, targetRoomID: targetRoomID,
             departureTimestamp: departureTimestamp + offset,
             arrivalTimestamp: arrivalTimestamp + offset, direction: direction)
    }
}

/// A cut alone proves no floor relationship. Require repeated, recent measured
/// ground on the exit/entry sides, with stable camera height on both sides.
/// All evidence is retained source-image geometry, never telemetry or prediction.
enum LiveDoorwayFloorEvidence {
    private struct Surface {
        let point: CGPoint
        let worldY: CGFloat
    }

    static func proposal(for transition: LiveDoorwayTransition,
                         in snapshot: LiveWorldSnapshot) -> LiveDoorwayFloorConstraint? {
        guard transition.sourceRoomID != transition.targetRoomID,
              transition.departureTimestamp.isFinite, transition.arrivalTimestamp.isFinite,
              transition.arrivalTimestamp > transition.departureTimestamp,
              let newest = snapshot.observations.last,
              newest.roomID == transition.targetRoomID,
              newest.timestamp >= transition.arrivalTimestamp,
              newest.timestamp <= transition.arrivalTimestamp + 3 else { return nil }
        let owners = Dictionary(uniqueKeysWithValues: snapshot.trackingIslands.flatMap { island in
            island.members.map { ($0.observationID, (island, $0)) }
        })
        let byID = Dictionary(uniqueKeysWithValues: snapshot.observations.map { ($0.id, $0) })
        guard !snapshot.doorwayFloorConstraints.contains(where: { edge in
            guard let from = byID[edge.departureObservationID],
                  let to = byID[edge.arrivalObservationID] else { return false }
            return from.roomID == transition.sourceRoomID && to.roomID == transition.targetRoomID
                && abs(from.timestamp - transition.departureTimestamp) <= 0.75
                && to.timestamp >= transition.arrivalTimestamp
        }) else { return nil }

        // Bound candidate work to the two short windows around this cut. Missing
        // evidence at a doorway cannot be replaced by a later floor elsewhere.
        let departure = snapshot.observations.filter {
            $0.roomID == transition.sourceRoomID
                && $0.timestamp <= transition.departureTimestamp + 0.01
                && $0.timestamp >= transition.departureTimestamp - 2
        }.suffix(16)
        let arrival = snapshot.observations.filter {
            $0.roomID == transition.targetRoomID
                && $0.timestamp >= transition.arrivalTimestamp
                && $0.timestamp <= transition.arrivalTimestamp + 3
        }.suffix(16)
        let exitRight = transition.direction == .right
        guard let source = departure.last(where: {
            guard let floor = surface($0, rightSide: exitRight),
                  let player = $0.observedPlayerBounds,
                  exitRight ? player.midX >= $0.solveWidth * 0.82 : player.midX <= $0.solveWidth * 0.18
            else { return false }
            return playerContact($0, floor: floor) != nil
        }),
              transition.departureTimestamp - source.timestamp <= 0.75,
              let target = arrival.last(where: {
                  guard let floor = surface($0, rightSide: !exitRight), let player = $0.observedPlayerBounds,
                        exitRight ? player.midX <= $0.solveWidth * 0.5 : player.midX >= $0.solveWidth * 0.5
                  else { return false }
                  return playerContact($0, floor: floor) != nil
              }),
              let sourceSurface = surface(source, rightSide: exitRight),
              let targetSurface = surface(target, rightSide: !exitRight) else { return nil }

        func support(_ frames: ArraySlice<LiveWorldObservation>, endpoint: LiveWorldObservation,
                     floor: Surface, rightSide: Bool) -> [Int] {
            guard let owner = owners[endpoint.id], owner.0.placement != .legacyUnknown,
                  owner.1.measured else { return [] }
            let unit = endpoint.solveWidth / 640
            var result = [Int]()
            var previousTime = endpoint.timestamp
            for frame in frames.reversed() where frame.timestamp <= endpoint.timestamp {
                guard endpoint.timestamp - frame.timestamp <= 2,
                      previousTime - frame.timestamp <= 0.35,
                      owners[frame.id]?.0.id == owner.0.id,
                      owners[frame.id]?.1.measured == true,
                      frame.solveWidth == endpoint.solveWidth,
                      abs(frame.optimizedPose.y - endpoint.optimizedPose.y) <= 4 * unit,
                      let measured = surface(frame, rightSide: rightSide),
                      abs(measured.worldY - floor.worldY) <= 2 * unit else { break }
                result.append(frame.id)
                previousTime = frame.timestamp
                if result.count >= 3 && endpoint.timestamp - frame.timestamp >= 0.25 { break }
            }
            return result.reversed()
        }
        let candidate = LiveDoorwayFloorConstraint(
            id: (snapshot.doorwayFloorConstraints.map(\.id).max() ?? -1) + 1,
            departureObservationID: source.id, arrivalObservationID: target.id,
            departureFloor: sourceSurface.point, arrivalFloor: targetSurface.point,
            direction: transition.direction.rawValue,
            departureSupport: support(departure, endpoint: source, floor: sourceSurface, rightSide: exitRight),
            arrivalSupport: support(arrival, endpoint: target, floor: targetSurface, rightSide: !exitRight))
        // Stable scenery is not proof that the player walked through this
        // doorway. A jump/fall can expose stable floors at unrelated elevations.
        // Require image-derived foot contact on both sides; missing optional
        // player detections leave the inter-room elevation estimated.
        guard groundedPlayer(candidate.departureSupport.compactMap { byID[$0] },
                             endpoint: source, floor: sourceSurface),
              groundedPlayer(candidate.arrivalSupport.compactMap { byID[$0] },
                             endpoint: target, floor: targetSurface) else { return nil }
        return candidate.isValid(in: snapshot, requiringAlignment: false) ? candidate : nil
    }

    private static func groundedPlayer(_ observations: [LiveWorldObservation],
                                       endpoint: LiveWorldObservation, floor: Surface) -> Bool {
        let unit = endpoint.solveWidth / 640
        let contacts = observations.compactMap { observation -> (Double, CGFloat)? in
            playerContact(observation, floor: floor).map { (observation.timestamp, $0) }
        }
        guard contacts.count >= 2,
              let earliest = contacts.map(\.0).min(), let latest = contacts.map(\.0).max(),
              latest - earliest >= 0.15, latest == endpoint.timestamp,
              let low = contacts.map(\.1).min(), let high = contacts.map(\.1).max(),
              high - low <= 6 * unit else { return false }
        return true
    }

    private static func playerContact(_ observation: LiveWorldObservation, floor: Surface) -> CGFloat? {
        let unit = observation.solveWidth / 640
        let height = CGFloat(observation.sourceHeight) / CGFloat(observation.sourceWidth) * observation.solveWidth
        guard let player = observation.observedPlayerBounds,
              player.width >= 3 * unit, player.width <= observation.solveWidth * 0.3,
              player.height >= 8 * unit, player.height <= height * 0.5,
              abs(observation.optimizedPose.y + player.minY - floor.worldY) <= 6 * unit,
              observation.groundGeometry?.lines.contains(where: {
                  abs(observation.optimizedPose.y + $0.start.y - floor.worldY) <= 2 * unit
                      && player.midX >= $0.start.x - 4 * unit
                      && player.midX <= $0.end.x + 4 * unit
              }) == true else { return nil }
        return observation.optimizedPose.y + player.minY
    }

    private static func surface(_ observation: LiveWorldObservation, rightSide: Bool) -> Surface? {
        guard let geometry = observation.groundGeometry else { return nil }
        let unit = observation.solveWidth / 640
        let halfWidth = observation.solveWidth / 2
        let height = CGFloat(observation.sourceHeight) / CGFloat(observation.sourceWidth) * observation.solveWidth
        // Keep distinct elevations separate. Two credible floors on the entry
        // side are ambiguous, even when one is longer or nearer the prediction.
        let groups = Dictionary(grouping: geometry.lines.filter {
            $0.start.y < height * 0.5
                && (rightSide ? $0.end.x >= halfWidth : $0.start.x <= halfWidth)
        }, by: { $0.start.y })
        let candidates = groups.compactMap { y, lines -> Surface? in
            let ordered = lines.sorted { $0.start.x < $1.start.x }
            guard let first = ordered.first else { return nil }
            var end = first.start.x, length: CGFloat = 0
            for line in ordered {
                length += max(0, line.end.x - max(end, line.start.x))
                end = max(end, line.end.x)
            }
            guard length >= 48 * unit else { return nil }
            let x = rightSide ? ordered.map(\.end.x).max()! : first.start.x
            return Surface(point: CGPoint(x: x, y: y), worldY: observation.optimizedPose.y + y)
        }
        return candidates.count == 1 ? candidates[0] : nil
    }
}

/// New, visually distinct rooms need a readable provisional layout. This is a
/// display translation only: no X constraint, loop edge or confidence is added.
/// Apply once, before the target room has any older retained observations.
enum LiveEstimatedRoomLayout {
    static func placingNewRoom(for transition: LiveDoorwayTransition,
                               in snapshot: LiveWorldSnapshot) throws -> LiveWorldSnapshot? {
        guard transition.sourceRoomID != transition.targetRoomID,
              transition.departureTimestamp.isFinite, transition.arrivalTimestamp.isFinite,
              transition.arrivalTimestamp > transition.departureTimestamp,
              let latest = snapshot.observations.last,
              latest.roomID == transition.targetRoomID,
              latest.timestamp >= transition.arrivalTimestamp,
              latest.timestamp <= transition.arrivalTimestamp + 3,
              snapshot.observations.count(where: { $0.roomID == latest.roomID }) == 1,
              let island = snapshot.trackingIslands.first(where: { $0.members.contains { $0.observationID == latest.id } }),
              island.placement == .estimated, island.members.count == 1 else { return nil }
        let source = snapshot.observations.filter { $0.roomID == transition.sourceRoomID }
        guard !source.isEmpty else { return nil }
        let gap = latest.solveWidth * 0.10
        let origin: CGFloat
        if transition.direction == .right {
            origin = source.map { $0.optimizedPose.x + $0.solveWidth }.max()! + gap
        } else {
            origin = source.map { $0.optimizedPose.x }.min()! - gap - latest.solveWidth
        }
        return try snapshot.translatingIslands([island.id: CGVector(dx: origin - latest.optimizedPose.x, dy: 0)],
                                              baseRevision: snapshot.mapRevision)
    }
}
