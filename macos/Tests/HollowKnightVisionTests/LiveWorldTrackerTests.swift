import CoreGraphics
import XCTest

@testable import HollowKnightVision

final class LiveWorldTrackerTests: XCTestCase {
    func testRoomBoundaryPreventsCrossRoomMotionAndAppearanceEdges() throws {
        let tracker = LiveWorldTracker()
        _ = try tracker.ingest(
            observationID: 0, timestamp: 0, frame: anchorTexture(),
            proposedCameraPose: .zero, solveWidth: 320,
            localPose: .zero, captureGeneration: 1, localEpoch: 0, roomID: 0
        )
        let entered = try tracker.ingest(
            observationID: 1, timestamp: 1, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: -700, y: 0), solveWidth: 320,
            localPose: CGPoint(x: -700, y: 0), captureGeneration: 1,
            localEpoch: 1, roomID: 1
        )

        XCTAssertEqual(entered.snapshot.observations.map(\.roomID), [0, 1])
        XCTAssertEqual(entered.snapshot.relativeMotionEdges.count, 0)
        XCTAssertEqual(entered.snapshot.loopClosureEdges.count, 0)
        XCTAssertTrue(entered.review.rejections.contains(.noEligibleReference))
    }

    func testOutwardLoopNeedsTwoConfirmationsThenClosesAndDistributesDrift() throws {
        let tracker = LiveWorldTracker()
        _ = try tracker.ingest(observationID: 0, timestamp: 0, frame: anchorTexture(), proposedCameraPose: .zero, solveWidth: 320)
        _ = try tracker.ingest(observationID: 1, timestamp: 1, frame: unrelatedTexture(1), proposedCameraPose: CGPoint(x: 100, y: 0), solveWidth: 320)
        _ = try tracker.ingest(observationID: 2, timestamp: 2, frame: unrelatedTexture(2), proposedCameraPose: CGPoint(x: 100, y: 100), solveWidth: 320)
        _ = try tracker.ingest(observationID: 3, timestamp: 3, frame: unrelatedTexture(3), proposedCameraPose: CGPoint(x: 0, y: 100), solveWidth: 320)

        let first = try tracker.ingest(
            observationID: 4, timestamp: 4, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertNil(first.acceptedClosure)
        XCTAssertTrue(first.review.rejections.contains(.confirmationRequired))

        let closed = try tracker.ingest(
            observationID: 5, timestamp: 5, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertNotNil(closed.acceptedClosure)
        let closedPose = try XCTUnwrap(closed.snapshot.observations.first(where: { $0.id == 5 }))
        XCTAssertLessThan(hypot(closedPose.optimizedPose.x, closedPose.optimizedPose.y), 4)
        XCTAssertGreaterThan(closed.revision, 1)
        XCTAssertEqual(closed.snapshot.loopClosureEdges.count, 1)
        XCTAssertTrue(closed.review.matchLines.contains { $0.fromObservationID == 0 && $0.toObservationID == 5 })
        let movedInterior = try XCTUnwrap(closed.snapshot.observations.first(where: { $0.id == 1 }))
        XCTAssertNotEqual(movedInterior.optimizedPose, movedInterior.rawPose)
    }

    func testOneFrameCandidateDoesNotCloseAndUnrelatedFramesAreRejected() throws {
        let tracker = try loopReadyTracker()
        let candidate = try tracker.ingest(
            observationID: 4, timestamp: 4, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertNil(candidate.acceptedClosure)
        XCTAssertTrue(candidate.review.rejections.contains(.confirmationRequired))

        let unrelated = try tracker.ingest(
            observationID: 5, timestamp: 5, frame: unrelatedTexture(9),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertNil(unrelated.acceptedClosure)
        XCTAssertTrue(unrelated.review.rejections.contains(.matcherRejected))
    }

    func testReloadedSnapshotRelocalizesAgainstSavedLandmarks() throws {
        let original = try loopReadyTracker()
        let reloaded = try LiveWorldTracker(snapshot: original.snapshot)
        let first = try reloaded.ingest(
            observationID: 4, timestamp: 4, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertTrue(first.review.rejections.contains(.confirmationRequired))
        let second = try reloaded.ingest(
            observationID: 5, timestamp: 5, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertNotNil(second.acceptedClosure)
    }

    func testReloadedVisitSearchesSavedFeaturesImmediatelyWithoutCrossVisitMotionEdge() throws {
        let original = try loopReadyTracker()
        let priorEdgeCount = original.snapshot.relativeMotionEdges.count
        let reloaded = try LiveWorldTracker(snapshot: original.snapshot)

        let first = try reloaded.ingest(
            observationID: 4, timestamp: 40, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )

        XCTAssertTrue(first.review.rejections.contains(.confirmationRequired))
        XCTAssertEqual(first.snapshot.relativeMotionEdges.count, priorEdgeCount)
    }

    func testConfirmationComparesDriftWhileCameraContinuesMoving() throws {
        let tracker = try loopReadyTracker()
        let first = try tracker.ingest(
            observationID: 4, timestamp: 4, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertTrue(first.review.rejections.contains(.confirmationRequired))

        let second = try tracker.ingest(
            observationID: 5, timestamp: 5, frame: shiftedAnchorTexture(dx: 12),
            proposedCameraPose: CGPoint(x: 20, y: 0), solveWidth: 320
        )

        XCTAssertNotNil(second.acceptedClosure)
    }

    func testRecentOverlapIsNotTreatedAsClosure() throws {
        let tracker = LiveWorldTracker()
        _ = try tracker.ingest(observationID: 0, timestamp: 0, frame: anchorTexture(), proposedCameraPose: .zero, solveWidth: 320)
        let update = try tracker.ingest(
            observationID: 1, timestamp: 1, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 10, y: 0), solveWidth: 320
        )
        XCTAssertNil(update.acceptedClosure)
        XCTAssertTrue(update.review.rejections.contains(.recentOrOverlappingReference))
        XCTAssertEqual(update.snapshot.loopClosureEdges.count, 0)
    }

    func testAcceptedClosureRequiresAnotherExcursionBeforeAddingMoreEdges() throws {
        let tracker = try loopReadyTracker()
        _ = try tracker.ingest(
            observationID: 4, timestamp: 4, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        let closed = try tracker.ingest(
            observationID: 5, timestamp: 5, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertNotNil(closed.acceptedClosure)

        let nearbyCandidate = try tracker.ingest(
            observationID: 6, timestamp: 6, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 12, y: 0), solveWidth: 320
        )
        let nearbyConfirmation = try tracker.ingest(
            observationID: 7, timestamp: 7, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 16, y: 0), solveWidth: 320
        )

        XCTAssertNil(nearbyCandidate.acceptedClosure)
        XCTAssertNil(nearbyConfirmation.acceptedClosure)
        XCTAssertEqual(nearbyConfirmation.snapshot.loopClosureEdges.count, 1)
        XCTAssertTrue(nearbyCandidate.review.rejections.contains(.recentOrOverlappingReference))
    }

    func testRepeatedAppearanceIsRejectedAsAClosureCandidate() throws {
        let tracker = LiveWorldTracker()
        _ = try tracker.ingest(observationID: 0, timestamp: 0, frame: tiledTexture(), proposedCameraPose: .zero, solveWidth: 320)
        _ = try tracker.ingest(observationID: 1, timestamp: 1, frame: unrelatedTexture(1), proposedCameraPose: CGPoint(x: 100, y: 0), solveWidth: 320)
        _ = try tracker.ingest(observationID: 2, timestamp: 2, frame: unrelatedTexture(2), proposedCameraPose: CGPoint(x: 100, y: 100), solveWidth: 320)
        _ = try tracker.ingest(observationID: 3, timestamp: 3, frame: unrelatedTexture(3), proposedCameraPose: CGPoint(x: 0, y: 100), solveWidth: 320)
        let update = try tracker.ingest(
            observationID: 4, timestamp: 4, frame: tiledTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertNil(update.acceptedClosure)
        XCTAssertTrue(update.review.rejections.contains(.matcherRejected))
    }

    func testRecoveryReusesClosedReferenceWithoutPersistingFrames() throws {
        let tracker = try loopReadyTracker()
        _ = try tracker.ingest(
            observationID: 4, timestamp: 4, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        let closed = try tracker.ingest(
            observationID: 5, timestamp: 5, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        XCTAssertEqual(closed.acceptedClosure?.fromKeyframeID, 0)
        let before = tracker.snapshot

        tracker.beginRecoveryEpoch()
        let first = try tracker.recover(
            frame: anchorTexture(), proposedCameraPose: CGPoint(x: 80, y: 0), solveWidth: 320
        )
        let confirmed = try tracker.recover(
            frame: anchorTexture(), proposedCameraPose: CGPoint(x: 80, y: 0), solveWidth: 320
        )

        XCTAssertEqual(first.matchedReferenceKeyframeID, 0)
        XCTAssertEqual(confirmed.matchedReferenceKeyframeID, 0)
        XCTAssertEqual(confirmed.confirmedPlacement?.cameraPose, .zero)
        XCTAssertEqual(confirmed.confirmedPlacement?.correction, CGPoint(x: -80, y: 0))
        XCTAssertEqual(tracker.snapshot, before)
        XCTAssertEqual(confirmed.snapshot.observations.count, before.observations.count)
        XCTAssertEqual(confirmed.snapshot.keyframes.count, before.keyframes.count)
    }

    func testRecoveryConfirmsAcrossDifferentReferenceKeyframes() throws {
        XCTAssertEqual(
            LiveWorldRecoveryConfirmation.referenceIDs(
                firstReferenceID: 0,
                firstCorrection: CGPoint(x: -8, y: 1),
                secondReferenceID: 3,
                secondCorrection: CGPoint(x: -7, y: 0),
                tolerance: 4
            ),
            [0, 3]
        )
        XCTAssertNil(LiveWorldRecoveryConfirmation.referenceIDs(
            firstReferenceID: 0,
            firstCorrection: CGPoint(x: -8, y: 1),
            secondReferenceID: 0,
            secondCorrection: CGPoint(x: 30, y: 0),
            tolerance: 4
        ))
    }

    func testRecoveryKeepsTentativeMatchAcrossOneUnmatchedFrame() throws {
        let tracker = try loopReadyTracker()
        let proposed = CGPoint(x: 80, y: 0)
        let first = try tracker.recover(
            frame: anchorTexture(), proposedCameraPose: proposed, solveWidth: 320
        )
        XCTAssertTrue(first.review.rejections.contains(.confirmationRequired))

        let gap = try tracker.recover(
            frame: unrelatedTexture(9), proposedCameraPose: proposed, solveWidth: 320
        )
        XCTAssertTrue(gap.review.rejections.contains(.matcherRejected))

        let second = try tracker.recover(
            frame: anchorTexture(), proposedCameraPose: proposed, solveWidth: 320
        )
        XCTAssertEqual(second.confirmedPlacement?.cameraPose, .zero)
    }

    func testRecoveryExpiresTentativeMatchAfterLongUnmatchedGap() throws {
        let tracker = try loopReadyTracker()
        let proposed = CGPoint(x: 80, y: 0)
        _ = try tracker.recover(
            frame: anchorTexture(), proposedCameraPose: proposed, solveWidth: 320
        )
        for index in 0..<5 {
            _ = try tracker.recover(
                frame: unrelatedTexture(20 + index), proposedCameraPose: proposed,
                solveWidth: 320
            )
        }
        let afterGap = try tracker.recover(
            frame: anchorTexture(), proposedCameraPose: proposed, solveWidth: 320
        )
        XCTAssertNil(afterGap.confirmedPlacement)
        XCTAssertTrue(afterGap.review.rejections.contains(.confirmationRequired))
    }

    func testMotionEdgesUseRawCoordinatesAfterPoseOptimization() throws {
        let tracker = try loopReadyTracker()
        _ = try tracker.ingest(
            observationID: 4, timestamp: 4, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        _ = try tracker.ingest(
            observationID: 5, timestamp: 5, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        let optimized = try XCTUnwrap(tracker.snapshot.observations.first(where: { $0.id == 5 }))
        XCTAssertNotEqual(optimized.rawPose, optimized.optimizedPose)

        let update = try tracker.ingest(
            observationID: 6, timestamp: 6, frame: unrelatedTexture(8),
            proposedCameraPose: CGPoint(x: 200, y: 0), solveWidth: 320, searchForClosure: false
        )
        let edge = try XCTUnwrap(update.snapshot.relativeMotionEdges.last)
        XCTAssertEqual(edge.fromObservationID, 5)
        XCTAssertEqual(edge.toObservationID, 6)
        XCTAssertEqual(edge.deltaX, 192)
        XCTAssertEqual(edge.deltaY, 0)
    }

    func testMotionEdgesNeverBridgeLocalTrackingEpochs() throws {
        let tracker = LiveWorldTracker()
        _ = try tracker.ingest(
            observationID: 0, timestamp: 0, frame: anchorTexture(),
            proposedCameraPose: .zero, solveWidth: 320, searchForClosure: false,
            localPose: .zero, captureGeneration: 9, localEpoch: 0
        )
        _ = try tracker.ingest(
            observationID: 1, timestamp: 1, frame: unrelatedTexture(1),
            proposedCameraPose: CGPoint(x: 100, y: 0), solveWidth: 320, searchForClosure: false,
            localPose: CGPoint(x: 100, y: 0), captureGeneration: 9, localEpoch: 1
        )
        let sameEpoch = try tracker.ingest(
            observationID: 2, timestamp: 2, frame: unrelatedTexture(2),
            proposedCameraPose: CGPoint(x: 200, y: 0), solveWidth: 320, searchForClosure: false,
            localPose: CGPoint(x: 200, y: 0), captureGeneration: 9, localEpoch: 1
        )

        XCTAssertEqual(sameEpoch.snapshot.relativeMotionEdges.count, 1)
        XCTAssertEqual(sameEpoch.snapshot.relativeMotionEdges[0].fromObservationID, 1)
        XCTAssertEqual(sameEpoch.snapshot.relativeMotionEdges[0].toObservationID, 2)
    }

    private func loopReadyTracker() throws -> LiveWorldTracker {
        let tracker = LiveWorldTracker()
        _ = try tracker.ingest(observationID: 0, timestamp: 0, frame: anchorTexture(), proposedCameraPose: .zero, solveWidth: 320)
        _ = try tracker.ingest(observationID: 1, timestamp: 1, frame: unrelatedTexture(1), proposedCameraPose: CGPoint(x: 100, y: 0), solveWidth: 320)
        _ = try tracker.ingest(observationID: 2, timestamp: 2, frame: unrelatedTexture(2), proposedCameraPose: CGPoint(x: 100, y: 100), solveWidth: 320)
        _ = try tracker.ingest(observationID: 3, timestamp: 3, frame: unrelatedTexture(3), proposedCameraPose: CGPoint(x: 0, y: 100), solveWidth: 320)
        return tracker
    }

    private func anchorTexture() -> CGImage {
        image { x, y in
            let value = (x * 17 + y * 31 + (x * y) % 89) & 255
            return (value, 255 - value, value / 3, 255)
        }
    }

    private func unrelatedTexture(_ seed: Int) -> CGImage {
        image { x, y in
            let value = ((x * (7 + seed) ^ y * (53 + seed * 3)) + seed * 41) & 255
            return (255 - value, value / 5, value, 255)
        }
    }

    private func tiledTexture() -> CGImage {
        image { x, y in
            let tx = ((x % 32) + 32) % 32
            let ty = ((y % 32) + 32) % 32
            let value = (tx * 23 + ty * 11 + tx * ty) & 255
            return (value, 255 - value, value / 2, 255)
        }
    }

    private func shiftedAnchorTexture(dx: Int) -> CGImage {
        image { x, y in
            let sourceX = x + dx
            let value = (sourceX * 17 + y * 31 + (sourceX * y) % 89) & 255
            return (value, 255 - value, value / 3, 255)
        }
    }

    private func image(_ color: (Int, Int) -> (Int, Int, Int, Int)) -> CGImage {
        let width = 320, height = 180
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let index = (y * width + x) * 4
                let value = color(x, y)
                bytes[index] = UInt8(value.0)
                bytes[index + 1] = UInt8(value.1)
                bytes[index + 2] = UInt8(value.2)
                bytes[index + 3] = UInt8(value.3)
            }
        }
        return CGContext(
            data: &bytes, width: width, height: height, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!.makeImage()!
    }
}
