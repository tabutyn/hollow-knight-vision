import CoreGraphics
import XCTest

@testable import HollowKnightVision

final class LiveWorldTrackerTests: XCTestCase {
    func testExtraProbePreservesOnlyFreshMeasuredVoteWithoutCountingIt() throws {
        for failure in ["floor", "appearance", "scheduled", "expired", "unmeasured", "different-island", "contradictory"] {
            let tracker = LiveWorldTracker()
            let frame = anchorTexture()
            func capture(_ island: Int, measured: Bool = true) -> LiveIslandCapture {
                LiveIslandCapture(id: island, roomID: 0, startedAt: Double(island),
                    worldFromLocal: CGPoint(x: island == 0 ? 0 : 8, y: 0), localPose: .zero,
                    placement: island == 0 ? .reference : .estimated,
                    uncertainty: island == 0 ? .reference : .unknown,
                    breakReason: "gap", measured: measured)
            }
            _ = try tracker.ingest(observationID: 0, timestamp: 0, frame: frame,
                proposedCameraPose: .zero, solveWidth: 320, islandCapture: capture(0))
            let first = try tracker.ingest(observationID: 1, timestamp: 1, frame: frame,
                proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320,
                islandCapture: capture(1), verifyClosure: { _, pose in pose })
            XCTAssertNil(first.acceptedClosure, failure)
            XCTAssertTrue(tracker.hasPendingClosure, failure)
            let probeTime = failure == "expired" ? 1.6 : 1.1
            let missed = try tracker.ingest(observationID: 2, timestamp: probeTime,
                frame: failure == "appearance" ? unrelatedTexture(999) : frame,
                proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320,
                isClosureConfirmationProbe: failure != "scheduled",
                islandCapture: capture(failure == "different-island" ? 2 : 1,
                    measured: failure != "unmeasured"),
                verifyClosure: { _, _ in failure == "contradictory" ? CGPoint(x: 100, y: 0) : nil })
            XCTAssertNil(missed.acceptedClosure, failure)
            XCTAssertTrue(missed.snapshot.loopClosureEdges.isEmpty, failure)
            let last = try tracker.ingest(observationID: 3, timestamp: probeTime + 0.1,
                frame: frame, proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320,
                islandCapture: capture(1), verifyClosure: { _, pose in pose })
            XCTAssertEqual(last.acceptedClosure != nil, ["floor", "appearance"].contains(failure), failure)
            XCTAssertEqual(last.snapshot.observations.count, 4, failure)
        }
    }

    func testExactCaptureTimeSurvivesCorrectionAndLegacyDecode() throws {
        let tracker = LiveWorldTracker()
        let update = try tracker.ingest(observationID: 0, timestamp: 1_000,
            captureTimestamp: 123.456, frame: anchorTexture(),
            proposedCameraPose: .zero, solveWidth: 320)
        let moved = try update.snapshot.translatingIslands([0: CGVector(dx: 12, dy: 8)],
            baseRevision: update.snapshot.mapRevision)
        let data = try JSONEncoder().encode(moved)
        let reopened = try JSONDecoder().decode(LiveWorldSnapshot.self, from: data)
        XCTAssertEqual(reopened.observations.first?.captureTimestamp, 123.456)
        XCTAssertEqual(reopened.observations.first?.timestamp, 1_000)
        var legacy = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        var observations = try XCTUnwrap(legacy["observations"] as? [[String: Any]])
        observations[0].removeValue(forKey: "captureTimestamp")
        legacy["observations"] = observations
        let old = try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: JSONSerialization.data(withJSONObject: legacy))
        XCTAssertNil(old.observations.first?.captureTimestamp)
        XCTAssertEqual(old.observations.first?.optimizedPose, CGPoint(x: 12, y: 8))
        XCTAssertThrowsError(try tracker.ingest(observationID: 1, timestamp: 1_001,
            captureTimestamp: .nan, frame: anchorTexture(), proposedCameraPose: .zero, solveWidth: 320))
        XCTAssertEqual(tracker.snapshot.observations.count, 1)
    }

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

    func testFloorVerificationRejectsEachVoteAndUsesRefinedDisplacement() throws {
        let tracker = try loopReadyTracker()
        var calls = 0
        var allowed = true
        let verifier: (LiveWorldObservation, CGPoint) -> CGPoint? = { source, proposal in
            calls += 1
            XCTAssertEqual(source.id, 0)
            XCTAssertEqual(proposal.x, 0, accuracy: 0.01)
            return allowed ? CGPoint(x: 2, y: 1) : nil
        }
        func ingest(_ id: Int) throws -> LiveWorldTrackerUpdate {
            try tracker.ingest(observationID: id, timestamp: Double(id), frame: anchorTexture(),
                proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320, verifyClosure: verifier)
        }
        XCTAssertTrue(try ingest(4).review.rejections.contains(.confirmationRequired))
        allowed = false
        let rejected = try ingest(5)
        XCTAssertNil(rejected.acceptedClosure)
        XCTAssertTrue(rejected.review.rejections.contains(.groundPlaneVerificationRejected))
        allowed = true
        XCTAssertTrue(try ingest(6).review.rejections.contains(.confirmationRequired))
        let accepted = try XCTUnwrap(ingest(7).acceptedClosure)
        XCTAssertEqual(calls, 4)
        XCTAssertEqual(accepted.deltaX, 2, accuracy: 0.01)
        XCTAssertEqual(accepted.deltaY, 1, accuracy: 0.01)
    }

    func testFloorVotesMayUseDifferentViewsOnlyWithinSameRigidReferenceIsland() throws {
        func distinctTexture(_ seed: UInt64) -> CGImage {
            image { x, y in
                var value = UInt64(x / 4 + 1000) &* 0x9e3779b185ebca87
                value ^= UInt64(y / 4 + 1000) &* 0xc2b2ae3d27d4eb4f
                value ^= seed &* 0x165667b19e3779f9
                value ^= value >> 33; value = value &* 0xff51afd7ed558ccd
                let byte = Int(UInt8(truncatingIfNeeded: value >> 24))
                return (byte, byte, byte, 255)
            }
        }
        for sharedReferenceIsland in [true, false] {
            let tracker = LiveWorldTracker()
            func capture(_ id: Int, _ pose: CGPoint, _ origin: CGPoint, reference: Bool = false) -> LiveIslandCapture {
                LiveIslandCapture(id: id, roomID: 0, startedAt: Double(id),
                    worldFromLocal: origin, localPose: CGPoint(x: pose.x - origin.x, y: pose.y - origin.y),
                    placement: reference ? .reference : .estimated,
                    uncertainty: reference ? .reference : .unknown, breakReason: "gap", measured: true)
            }
            _ = try tracker.ingest(observationID: 0, timestamp: 0, frame: distinctTexture(1),
                proposedCameraPose: .zero, solveWidth: 320, searchForClosure: false,
                islandCapture: capture(0, .zero, .zero, reference: true))
            let secondPose = CGPoint(x: 100, y: 0)
            _ = try tracker.ingest(observationID: 1, timestamp: 1, frame: distinctTexture(2),
                proposedCameraPose: secondPose, solveWidth: 320, searchForClosure: false,
                islandCapture: capture(sharedReferenceIsland ? 0 : 2, secondPose,
                    sharedReferenceIsland ? .zero : secondPose, reference: sharedReferenceIsland))
            let origin = CGPoint(x: 108, y: 0)
            let verifier: (LiveWorldObservation, CGPoint) -> CGPoint? = { _, pose in pose }
            let first = try tracker.ingest(observationID: 2, timestamp: 2, frame: distinctTexture(2),
                proposedCameraPose: origin, solveWidth: 320,
                islandCapture: capture(3, origin, origin), verifyClosure: verifier)
            XCTAssertTrue(first.review.rejections.contains(.confirmationRequired), "\(first.review.rejections)")
            let lastPose = CGPoint(x: 8, y: 0)
            let last = try tracker.ingest(observationID: 3, timestamp: 3, frame: distinctTexture(1),
                proposedCameraPose: lastPose, solveWidth: 320,
                islandCapture: capture(3, lastPose, origin), verifyClosure: verifier)
            if sharedReferenceIsland {
                XCTAssertNotNil(last.acceptedClosure)
                XCTAssertEqual(last.snapshot.observations[2].optimizedPose.x, 100, accuracy: 0.01)
                XCTAssertEqual(last.snapshot.observations[3].optimizedPose.x, 0, accuracy: 0.01)
            } else {
                XCTAssertNil(last.acceptedClosure)
                XCTAssertTrue(last.review.rejections.contains(.inconsistentConfirmation))
            }
        }
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

    func testConflictingLoopCannotBendAnIslandOrDropItsCapture() throws {
        let tracker = LiveWorldTracker()
        func capture(_ pose: CGPoint) -> LiveIslandCapture {
            LiveIslandCapture(id: 0, roomID: 0, startedAt: 0, worldFromLocal: .zero,
                localPose: pose, placement: .reference, uncertainty: .reference,
                breakReason: "capture-start", measured: true)
        }
        let poses = [CGPoint.zero, CGPoint(x: 100, y: 0), CGPoint(x: 100, y: 100),
                     CGPoint(x: 0, y: 100), CGPoint(x: 20, y: 0), CGPoint(x: 20, y: 0)]
        var last: LiveWorldTrackerUpdate?
        for (id, pose) in poses.enumerated() {
            last = try tracker.ingest(observationID: id, timestamp: Double(id),
                frame: id == 0 || id >= 4 ? anchorTexture() : unrelatedTexture(id),
                proposedCameraPose: pose, solveWidth: 320, islandCapture: capture(pose))
        }
        let result = try XCTUnwrap(last)
        XCTAssertNil(result.acceptedClosure)
        XCTAssertTrue(result.review.rejections.contains(.inconsistentIslandGeometry))
        XCTAssertTrue(result.snapshot.loopClosureEdges.isEmpty)
        XCTAssertEqual(result.snapshot.observations.map(\.optimizedPose), poses)
        XCTAssertEqual(result.snapshot.trackingIslands[0].members.count, poses.count)
    }

    func testLoopConfirmationCannotCrossUnrelatedIslands() throws {
        let tracker = LiveWorldTracker()
        func capture(_ id: Int) -> LiveIslandCapture {
            LiveIslandCapture(id: id, roomID: 0, startedAt: Double(id),
                worldFromLocal: CGPoint(x: CGFloat(id) * 100, y: 0), localPose: .zero,
                placement: id == 0 ? .reference : .estimated,
                uncertainty: id == 0 ? .reference : .unknown,
                breakReason: "gap", measured: true)
        }
        _ = try tracker.ingest(observationID: 0, timestamp: 0, frame: anchorTexture(),
            proposedCameraPose: .zero, solveWidth: 320, islandCapture: capture(0))
        let first = try tracker.ingest(observationID: 1, timestamp: 1, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 100, y: 0), solveWidth: 320, islandCapture: capture(1))
        XCTAssertNil(first.acceptedClosure)
        let second = try tracker.ingest(observationID: 2, timestamp: 2, frame: anchorTexture(),
            proposedCameraPose: CGPoint(x: 100, y: 0), solveWidth: 320,
            islandCapture: LiveIslandCapture(id: 2, roomID: 0, startedAt: 2,
                worldFromLocal: CGPoint(x: 100, y: 0), localPose: .zero, placement: .estimated,
                uncertainty: .unknown, breakReason: "gap", measured: true))
        XCTAssertNil(second.acceptedClosure)
        XCTAssertTrue(second.snapshot.loopClosureEdges.isEmpty)
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
