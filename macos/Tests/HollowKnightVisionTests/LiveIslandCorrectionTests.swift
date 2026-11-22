import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LiveIslandCorrectionTests: XCTestCase {
    func testLoopMovesEntireConnectedGroupAndLeavesDisconnectedIslandAlone() throws {
        let original = try fixture(closure: edge(id: 1, from: 0, to: 2, dx: 40))
        let moved = try LiveIslandCorrection.applying(1, to: original)
        XCTAssertEqual(moved.observations.map { $0.optimizedPose.x }, [0, 10, 40, 50, 90, 100, 300])
        XCTAssertEqual(moved.observations.map(\.rawPose), original.observations.map(\.rawPose))
        XCTAssertEqual(moved.trackingIslands.map(\.members), original.trackingIslands.map(\.members))
        XCTAssertEqual(moved.trackingIslands[1].placement, .connected)
        XCTAssertEqual(moved.trackingIslands[2].placement, .connected)
        XCTAssertEqual(moved.trackingIslands[3], original.trackingIslands[3])
        XCTAssertEqual(moved.trackingIslands[1].uncertainty.x, 2)
        XCTAssertEqual(moved.trackingIslands[2].uncertainty.x, 4)
        XCTAssertEqual(moved.landmarks.first { $0.sourceObservationID == 4 }?.worldPoint.x, 100)
        XCTAssertEqual(moved.ownedGroundLines.count, original.observations.count)
        for line in moved.ownedGroundLines {
            let observation = try XCTUnwrap(moved.observations.first { $0.id == line.observationID })
            XCTAssertEqual(line.start.x, observation.optimizedPose.x + 5)
            XCTAssertEqual(line.end.x, observation.optimizedPose.x + 55)
        }
        XCTAssertEqual(moved.observations.map(\.groundGeometry), original.observations.map(\.groundGeometry))
        let reopened = try JSONDecoder().decode(LiveWorldSnapshot.self, from: JSONEncoder().encode(moved))
        XCTAssertEqual(reopened.ownedGroundLines, moved.ownedGroundLines)
    }

    func testReverseDirectedLoopMovesSameWholeGroup() throws {
        let original = try fixture(closure: edge(id: 1, from: 2, to: 0, dx: -40))
        let moved = try LiveIslandCorrection.applying(1, to: original)
        XCTAssertEqual(moved.observations.map { $0.optimizedPose.x }, [0, 10, 40, 50, 90, 100, 300])
    }

    func testContradictoryLoopCannotBendAlreadyConnectedGroup() throws {
        let first = try LiveIslandCorrection.applying(1,
            to: fixture(closure: edge(id: 1, from: 0, to: 2, dx: 40)))
        let contradictory = try copy(first, edges: first.loopClosureEdges + [edge(id: 2, from: 0, to: 4, dx: 70)])
        XCTAssertThrowsError(try LiveIslandCorrection.applying(2, to: contradictory)) {
            XCTAssertEqual($0 as? LiveIslandCorrectionError, .inconsistentKnownComponent)
        }
        XCTAssertEqual(first.observations.map { $0.optimizedPose.x }, [0, 10, 40, 50, 90, 100, 300])
    }

    func testDisconnectedEstimatedGroupsMayConnectWithoutBecomingGloballyLocated() throws {
        let original = try fixture(closure: edge(id: 1, from: 2, to: 6, dx: 120))
        let moved = try LiveIslandCorrection.applying(1, to: original)
        XCTAssertEqual(moved.observations.last?.optimizedPose.x, 220)
        XCTAssertEqual(moved.trackingIslands[3].placement, .estimated)
        XCTAssertEqual(moved.trackingIslands[3].uncertainty, .unknown)
        XCTAssertEqual(moved.trackingIslands[0], original.trackingIslands[0])
    }

    private func edge(id: Int, from: Int, to: Int, dx: Double) -> LiveWorldLoopClosureEdge {
        LiveWorldLoopClosureEdge(id: id, fromKeyframeID: from, toKeyframeID: to,
            landmarkIDs: [from * 10, from * 10 + 1, from * 10 + 2], deltaX: dx, deltaY: 0,
            support: 3, ambiguity: 0, residualPixels: 2)
    }

    private func copy(_ original: LiveWorldSnapshot, edges: [LiveWorldLoopClosureEdge]) throws -> LiveWorldSnapshot {
        try LiveWorldSnapshot(mapRevision: original.mapRevision,
            anchoredObservationID: original.anchoredObservationID, observations: original.observations,
            keyframes: original.keyframes, landmarks: original.landmarks,
            relativeMotionEdges: original.relativeMotionEdges, loopClosureEdges: edges,
            trackingIslands: original.trackingIslands)
    }

    private func fixture(closure: LiveWorldLoopClosureEdge) throws -> LiveWorldSnapshot {
        let positions: [CGFloat] = [0, 10, 100, 110, 150, 160, 300]
        let observations = positions.enumerated().map { id, x in
            LiveWorldObservation(id: id, sourceObservationID: id, timestamp: Double(id),
                sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
                excludedRects: [], rawPose: CGPoint(x: x, y: 0), optimizedPose: CGPoint(x: x, y: 0),
                groundGeometry: LiveWorldGroundGeometry(lines: [LiveGroundLineEvidence(segmentID: id,
                    start: CGPoint(x: 5, y: 20), end: CGPoint(x: 55, y: 20))]))
        }
        let groups = [[0, 1], [2, 3], [4, 5], [6]]
        let islands = groups.enumerated().map { id, group in
            LiveTrackingIsland(id: id, roomID: 0, startedAt: Double(group[0]),
                worldFromLocal: CGPoint(x: positions[group[0]], y: 0),
                placement: id == 0 ? .reference : .estimated,
                uncertainty: id == 0 ? .reference : .unknown, breakReason: "test-gap",
                members: group.map { index in LiveIslandMember(observationID: index,
                    localPose: CGPoint(x: positions[index] - positions[group[0]], y: 0), measured: true) })
        }
        let keyframes = observations.map { LiveWorldKeyframe(id: $0.id, observationID: $0.id, sourceObservationID: $0.id) }
        let landmarks = observations.flatMap { observation in
            (0..<3).map { index in LiveWorldLandmark(id: observation.id * 10 + index,
                keyframeID: observation.id, sourceObservationID: observation.id,
                samplePoint: CGPoint(x: 10 + index * 20, y: 10),
                worldPoint: CGPoint(x: observation.rawPose.x + CGFloat(10 + index * 20), y: 10),
                descriptor: [0, 1, 2], depth: 1, depthConfidence: 1) }
        }
        return try LiveWorldSnapshot(mapRevision: 5, anchoredObservationID: 0, observations: observations,
            keyframes: keyframes, landmarks: landmarks,
            loopClosureEdges: [edge(id: 0, from: 2, to: 4, dx: 50), closure], trackingIslands: islands)
    }
}
