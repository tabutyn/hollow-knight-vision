import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LiveIslandProjectionTests: XCTestCase {
    func testCorrectionMovesWorldPoseWithoutBecomingImageMotion() throws {
        var coordinator = LiveTrackingIslandCoordinator()
        let first = coordinator.observe(timestamp: 1, roomID: 0, worldPose: CGPoint(x: 100, y: 20),
            measured: true, referenceTimestamp: 0.95)
        let world = try snapshot(first)
        let shifted = try world.translatingIslands([first.id: CGVector(dx: -40, dy: 7)], baseRevision: 0)
        XCTAssertTrue(coordinator.applyWorldSnapshot(shifted))
        let next = coordinator.observe(timestamp: 1.05, roomID: 0,
            worldPose: CGPoint(x: 105, y: 22), measured: true, referenceTimestamp: 1)
        XCTAssertEqual(next.id, first.id)
        XCTAssertEqual(next.localPose, CGPoint(x: 5, y: 2))
        XCTAssertEqual(next.worldPose, CGPoint(x: 65, y: 29))
        XCTAssertFalse(coordinator.applyWorldSnapshot(world), "Stale world cannot undo correction")
        XCTAssertFalse(coordinator.applyWorldSnapshot(shifted))
        let repeatedReference = coordinator.observe(timestamp: 1.10, roomID: 0,
            worldPose: CGPoint(x: 106, y: 22), measured: true, referenceTimestamp: 0.95)
        XCTAssertEqual(repeatedReference.worldPose, CGPoint(x: 66, y: 29))
    }

    func testPredictionGapKeepsEstimatedSpacingAndOldReferenceRecoversItsOwnIsland() throws {
        var coordinator = LiveTrackingIslandCoordinator()
        let first = coordinator.observe(timestamp: 1, roomID: 0, worldPose: CGPoint(x: 100, y: 20),
            measured: true, referenceTimestamp: 0.95)
        let moved = try snapshot(first).translatingIslands([first.id: CGVector(dx: -40, dy: 7)], baseRevision: 0)
        XCTAssertTrue(coordinator.applyWorldSnapshot(moved))
        let predicted = coordinator.observe(timestamp: 1.05, roomID: 0,
            worldPose: CGPoint(x: 110, y: 20), measured: false, referenceTimestamp: nil)
        XCTAssertNotEqual(predicted.id, first.id)
        XCTAssertEqual(predicted.worldPose, CGPoint(x: 70, y: 27))
        XCTAssertEqual(predicted.placement, .estimated)
        XCTAssertEqual(predicted.uncertainty, .unknown)
        let recovered = coordinator.observe(timestamp: 1.10, roomID: 0,
            worldPose: CGPoint(x: 112, y: 20), measured: true, referenceTimestamp: 1)
        XCTAssertEqual(recovered.id, first.id)
        XCTAssertEqual(recovered.localPose, CGPoint(x: 12, y: 0))
        XCTAssertEqual(recovered.worldPose, CGPoint(x: 72, y: 27))
    }

    func testResetDoesNotApplyPreviousWorldTranslationToNewSession() throws {
        var coordinator = LiveTrackingIslandCoordinator()
        let first = coordinator.observe(timestamp: 1, roomID: 0, worldPose: .zero,
            measured: true, referenceTimestamp: 0.95)
        XCTAssertTrue(coordinator.applyWorldSnapshot(try snapshot(first)
            .translatingIslands([first.id: CGVector(dx: 100, dy: 200)], baseRevision: 0)))
        coordinator.reset(nextID: 1)
        let fresh = coordinator.observe(timestamp: 2, roomID: 0, worldPose: CGPoint(x: 3, y: 5),
            measured: true, referenceTimestamp: 1.95)
        XCTAssertEqual(fresh.worldPose, CGPoint(x: 3, y: 5))
        XCTAssertEqual(fresh.placement, .estimated)
    }

    func testPublishedProjectionCorrectsQueuedCaptureAndRejectsReusedIdentity() throws {
        let capture = LiveIslandCapture(id: 0, roomID: 0, startedAt: 1,
            worldFromLocal: CGPoint(x: 100, y: 20), localPose: .zero,
            placement: .reference, uncertainty: .reference, breakReason: "start", measured: true)
        let old = try snapshot(capture)
        let moved = try old.translatingIslands([0: CGVector(dx: -40, dy: 7)], baseRevision: 0)
        let projection = LiveIslandWorldProjection(snapshot: moved)
        let queued = capture.advancing(by: CGVector(dx: 5, dy: 2))
        XCTAssertEqual(projection.worldPose(for: queued), CGPoint(x: 65, y: 29))
        XCTAssertEqual(LiveIslandWorldProjection(snapshot: old).worldPose(for: queued), CGPoint(x: 105, y: 22))
        let reused = LiveIslandCapture(id: 0, roomID: 1, startedAt: 2,
            worldFromLocal: .zero, localPose: .zero, placement: .estimated,
            uncertainty: .unknown, breakReason: "different-session", measured: false)
        XCTAssertNil(projection.worldPose(for: reused))
        let unknown = LiveIslandCapture(id: 9, roomID: 1, startedAt: 2,
            worldFromLocal: CGPoint(x: 5, y: 10), localPose: CGPoint(x: 2, y: 3),
            placement: .estimated, uncertainty: .unknown, breakReason: "gap", measured: false)
        XCTAssertEqual(projection.worldPose(for: unknown), CGPoint(x: 7, y: 13))
    }

    private func snapshot(_ capture: LiveIslandCapture) throws -> LiveWorldSnapshot {
        let observation = LiveWorldObservation(id: 0, sourceObservationID: 0, timestamp: 1,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
            excludedRects: [], rawPose: capture.worldPose, optimizedPose: capture.worldPose)
        return try LiveWorldSnapshot(anchoredObservationID: 0, observations: [observation],
            trackingIslands: [capture.island(observationID: 0)])
    }
}
