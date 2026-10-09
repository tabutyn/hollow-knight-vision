import CoreGraphics
import XCTest

@testable import HollowKnightVision

final class WorldBasisControllerTests: XCTestCase {
    func testFastLossAdvancesEpochWithoutErasingWorldBasis() throws {
        let controller = WorldBasisController()
        controller.beginCapture(worldFromLocal: point(100, -5))
        let old = try XCTUnwrap(controller.observe(localPose: point(3, 4), captureTimestamp: 1))
        let beforeLoss = controller.snapshot

        controller.loseLocalTracking()
        let recovered = controller.snapshot

        XCTAssertEqual(recovered.trackingState, .recovering)
        XCTAssertEqual(recovered.localEpoch, beforeLoss.localEpoch + 1)
        XCTAssertEqual(recovered.worldFromLocal, beforeLoss.worldFromLocal)
        XCTAssertEqual(recovered.basisRevision, beforeLoss.basisRevision)
        XCTAssertNil(controller.rebasedWorldPose(for: old))
    }

    func testDelayedCorrectionIsRejectedAfterLocalEpochChanges() throws {
        let controller = WorldBasisController()
        controller.beginCapture()
        let delayed = try XCTUnwrap(controller.observe(localPose: point(4, 2), captureTimestamp: 1))

        controller.loseLocalTracking()
        _ = controller.observe(localPose: point(1, 1), captureTimestamp: 2)
        let beforeCorrection = controller.snapshot

        XCTAssertEqual(beforeCorrection.trackingState, .recovering)

        XCTAssertFalse(controller.confirmPlacement(for: delayed, matchedWorldPose: point(40, 20)))
        XCTAssertEqual(controller.snapshot, beforeCorrection)
    }

    func testQueuedSameEpochObservationRebasesThroughCorrectedBasis() throws {
        let controller = WorldBasisController()
        controller.beginCapture()
        let matched = try XCTUnwrap(controller.observe(localPose: point(10, 5), captureTimestamp: 10))
        let queued = try XCTUnwrap(controller.observe(localPose: point(12, 8), captureTimestamp: 11))

        XCTAssertTrue(controller.confirmPlacement(for: matched, matchedWorldPose: point(110, 55)))

        XCTAssertEqual(controller.trackingState, .tracking)
        let rebased = try XCTUnwrap(controller.rebasedObservation(for: queued))
        XCTAssertEqual(rebased.worldPose, point(112, 58))
        XCTAssertEqual(rebased.ticket.basisRevision, controller.snapshot.basisRevision)
        XCTAssertEqual(controller.latestWorldPose, point(112, 58))
    }

    func testGenerationResetRejectsOldTickets() throws {
        let controller = WorldBasisController()
        let firstGeneration = controller.beginCapture(worldFromLocal: point(9, 9))
        let old = try XCTUnwrap(controller.observe(localPose: point(1, 1), captureTimestamp: 1))

        let secondGeneration = controller.resetCapture(worldFromLocal: point(-2, 3))

        XCTAssertEqual(secondGeneration, firstGeneration + 1)
        XCTAssertNil(controller.rebasedWorldPose(for: old))
        XCTAssertFalse(controller.confirmPlacement(for: old, matchedWorldPose: point(1, 1)))
        XCTAssertEqual(controller.snapshot.worldFromLocal, point(-2, 3))
    }

    func testHistoricalCorrectionUsesMatchedPoseAndRemapsLatestPose() throws {
        let controller = WorldBasisController()
        controller.beginCapture(worldFromLocal: point(10, 0))
        let historical = try XCTUnwrap(controller.observe(localPose: point(5, 0), captureTimestamp: 100))
        _ = try XCTUnwrap(controller.observe(localPose: point(20, 0), captureTimestamp: 200))

        XCTAssertTrue(controller.confirmPlacement(for: historical, matchedWorldPose: point(105, 0)))

        XCTAssertEqual(controller.snapshot.worldFromLocal, point(100, 0))
        XCTAssertEqual(controller.rebasedWorldPose(for: historical), point(105, 0))
        XCTAssertEqual(controller.latestWorldPose, point(120, 0))
    }

    private func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
        CGPoint(x: x, y: y)
    }
}
