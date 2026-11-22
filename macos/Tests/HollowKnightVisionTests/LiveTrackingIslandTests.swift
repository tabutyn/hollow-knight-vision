import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class LiveTrackingIslandTests: XCTestCase {
    func testMeasuredFallbackContinuesIslandWithoutGround() {
        var tracker = LiveTrackingIslandCoordinator()
        let first = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: nil)
        let fall = tracker.observe(timestamp: 0.05, roomID: 0, worldPose: CGPoint(x: 2, y: -12),
                                   measured: true, referenceTimestamp: 0)
        XCTAssertEqual(first.id, fall.id)
        XCTAssertEqual(fall.localPose, CGPoint(x: 2, y: -12))
        XCTAssertEqual(fall.placement, .reference)
    }

    func testPredictionCannotBecomeTrustedGeometryButVisualBridgeCanResumeIsland() {
        var tracker = LiveTrackingIslandCoordinator()
        let first = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: nil)
        let predicted = tracker.observe(timestamp: 0.02, roomID: 0, worldPose: CGPoint(x: 4, y: 0),
                                        measured: false, referenceTimestamp: nil)
        let measured = tracker.observe(timestamp: 0.05, roomID: 0, worldPose: CGPoint(x: 6, y: 0),
                                       measured: true, referenceTimestamp: 0)
        XCTAssertNotEqual(predicted.id, first.id)
        XCTAssertFalse(predicted.measured)
        XCTAssertEqual(predicted.placement, .estimated)
        XCTAssertNil(predicted.uncertainty.x)
        XCTAssertEqual(measured.id, first.id)
    }

    func testReacquisitionAfterUnsupportedGapKeepsUnknownGlobalPlacement() {
        var tracker = LiveTrackingIslandCoordinator()
        let first = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: nil)
        _ = tracker.observe(timestamp: 0.3, roomID: 0, worldPose: CGPoint(x: 40, y: -30),
                            measured: false, referenceTimestamp: nil)
        let next = tracker.observe(timestamp: 0.35, roomID: 0, worldPose: CGPoint(x: 45, y: -35),
                                   measured: true, referenceTimestamp: 0.3)
        XCTAssertNotEqual(first.id, next.id)
        XCTAssertEqual(first.roomID, next.roomID)
        XCTAssertEqual(next.placement, .estimated)
        XCTAssertEqual(next.uncertainty, .unknown)
        let continuing = tracker.observe(timestamp: 0.4, roomID: 0, worldPose: CGPoint(x: 50, y: -40),
                                         measured: true, referenceTimestamp: 0.35)
        XCTAssertEqual(continuing.id, next.id)
        XCTAssertEqual(continuing.placement, .estimated)
    }

    func testRepeatedMatchesToBootstrapImageShareOneNewIslandWithoutPromotingOldCapture() {
        var tracker = LiveTrackingIslandCoordinator()
        let provisional = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
            measured: false, referenceTimestamp: nil)
        let first = tracker.observe(timestamp: 0.2, roomID: 0, worldPose: CGPoint(x: 4, y: 0),
            measured: true, referenceTimestamp: 0)
        let second = tracker.observe(timestamp: 0.22, roomID: 0, worldPose: CGPoint(x: 6, y: 0),
            measured: true, referenceTimestamp: 0)
        XCTAssertEqual(first.id, second.id)
        XCTAssertEqual(second.placement, .reference)
        XCTAssertNotEqual(provisional.id, second.id)
        XCTAssertFalse(provisional.measured)
        XCTAssertEqual(provisional.placement, .estimated)
    }

    func testReferenceInsideEarlierPredictionGapCannotInheritConfidence() {
        var tracker = LiveTrackingIslandCoordinator()
        let first = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: nil)
        _ = tracker.observe(timestamp: 0.02, roomID: 0, worldPose: CGPoint(x: 4, y: 0),
                            measured: false, referenceTimestamp: nil)
        let resumed = tracker.observe(timestamp: 0.05, roomID: 0, worldPose: CGPoint(x: 6, y: 0),
                                      measured: true, referenceTimestamp: 0)
        XCTAssertEqual(resumed.id, first.id)
        let unsupported = tracker.observe(timestamp: 0.1, roomID: 0, worldPose: CGPoint(x: 14, y: 0),
                                          measured: true, referenceTimestamp: 0.02)
        XCTAssertNotEqual(unsupported.id, first.id)
        XCTAssertEqual(unsupported.placement, .estimated)
        let oldAnchor = tracker.observe(timestamp: 0.15, roomID: 0, worldPose: CGPoint(x: 8, y: 0),
                                        measured: true, referenceTimestamp: 0.05)
        XCTAssertEqual(oldAnchor.id, first.id)
    }

    func testMissingReferenceDoesNotInventContinuityFromRecentTiming() {
        var tracker = LiveTrackingIslandCoordinator()
        let first = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: nil)
        let unsupported = tracker.observe(timestamp: 0.02, roomID: 0, worldPose: CGPoint(x: 12, y: 0),
                                          measured: true, referenceTimestamp: nil)
        XCTAssertNotEqual(unsupported.id, first.id)
        XCTAssertEqual(unsupported.placement, .estimated)
    }

    func testKnownSpatialAnchorCanBridgeCaptureGap() {
        var tracker = LiveTrackingIslandCoordinator()
        let first = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: nil)
        let next = tracker.observe(timestamp: 0.8, roomID: 0, worldPose: CGPoint(x: 35, y: 2),
                                   measured: true, referenceTimestamp: 0)
        XCTAssertEqual(first.id, next.id)
    }

    func testRoomChangeAndGlobalAdjustmentBreakOwnership() {
        var tracker = LiveTrackingIslandCoordinator()
        let first = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: nil)
        let other = tracker.observe(timestamp: 0.05, roomID: 1, worldPose: CGPoint(x: 800, y: 10),
                                    measured: true, referenceTimestamp: 0)
        XCTAssertNotEqual(first.id, other.id)
        XCTAssertEqual(other.placement, .estimated)
        let corrected = tracker.observe(timestamp: 0.1, roomID: 1, worldPose: CGPoint(x: 900, y: 10),
                                        measured: true, referenceTimestamp: 0.05,
                                        discontinuity: "ground-global-adjustment")
        XCTAssertNotEqual(other.id, corrected.id)
        XCTAssertEqual(corrected.placement, .estimated)
    }

    func testBootstrapWaitDoesNotLoseFirstReferenceAndIDsNeverReuse() {
        var tracker = LiveTrackingIslandCoordinator()
        _ = tracker.observe(timestamp: 0, roomID: 0, worldPose: .zero,
                            measured: false, referenceTimestamp: nil)
        let first = tracker.observe(timestamp: 0.02, roomID: 0, worldPose: .zero,
                                    measured: true, referenceTimestamp: 0)
        XCTAssertEqual(first.placement, .reference)
        tracker.reset(nextID: 1)
        let reopened = tracker.observe(timestamp: 1, roomID: 0, worldPose: .zero,
                                       measured: true, referenceTimestamp: nil)
        XCTAssertGreaterThan(reopened.id, first.id)
        XCTAssertEqual(reopened.placement, .estimated)
    }

    func testRigidCorrectionMovesWholeIslandAndOwnedLandmarks() throws {
        let world = try fixture()
        let moved = try world.translatingIslands([0: CGVector(dx: 8, dy: -3)], baseRevision: 4)
        XCTAssertEqual(moved.mapRevision, 5)
        XCTAssertEqual(moved.observations.map(\.optimizedPose),
                       [CGPoint(x: 8, y: -3), CGPoint(x: 18, y: -3), CGPoint(x: 80, y: 4)])
        XCTAssertEqual(moved.observations.map(\.rawPose), world.observations.map(\.rawPose))
        XCTAssertEqual(moved.trackingIslands.map(\.members), world.trackingIslands.map(\.members))
        XCTAssertEqual(moved.landmarks[0].worldPoint, CGPoint(x: 28, y: 7))
        XCTAssertEqual(moved.trackingIslands[1], world.trackingIslands[1])
        XCTAssertEqual(try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: JSONEncoder().encode(moved)), moved)
    }

    func testPartialIslandBendingAndStaleCorrectionAreRejected() throws {
        let world = try fixture()
        XCTAssertThrowsError(try world.replacingOptimizedPoses([
            10: CGPoint(x: 8, y: 0), 11: CGPoint(x: 10, y: 0), 12: CGPoint(x: 80, y: 4)
        ], baseRevision: 4)) { XCTAssertEqual($0 as? LiveWorldModelError, .nonRigidIslandCorrection(0)) }
        XCTAssertThrowsError(try world.translatingIslands([0: CGVector(dx: 2, dy: 0)], baseRevision: 3)) {
            XCTAssertEqual($0 as? LiveWorldModelError, .staleRevision(expected: 4, actual: 3))
        }
    }

    func testLegacyMigrationPreservesPlacementsWithoutInventingConnections() throws {
        let original = try fixture()
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(original)) as? [String: Any])
        object["schemaVersion"] = 1
        object.removeValue(forKey: "trackingIslands")
        let decoded = try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: JSONSerialization.data(withJSONObject: object))
        XCTAssertEqual(decoded.schemaVersion, LiveWorldSnapshot.currentSchemaVersion)
        XCTAssertEqual(decoded.observations, original.observations)
        XCTAssertEqual(decoded.trackingIslands.count, 3)
        XCTAssertTrue(decoded.trackingIslands.allSatisfy { $0.placement == .legacyUnknown && $0.uncertainty == .unknown })
        XCTAssertTrue(decoded.trackingIslands.allSatisfy { $0.members.count == 1 && !$0.members[0].measured })
    }

    func testQueuedCaptureUsesCorrectedIslandTranslationAndCannotCreateTrustedPredictedFeatures() throws {
        let tracker = LiveWorldTracker()
        let frame = try image()
        let first = capture(id: 0, pose: .zero)
        let initial = try tracker.ingest(observationID: 0, timestamp: 0, frame: frame,
            proposedCameraPose: .zero, solveWidth: 64, searchForClosure: false, islandCapture: first)
        let moved = try initial.snapshot.translatingIslands([0: CGVector(dx: 100, dy: 20)], baseRevision: initial.revision)
        let resumed = try LiveWorldTracker(snapshot: moved)
        let next = try resumed.ingest(observationID: 1, timestamp: 0.05, frame: frame,
            proposedCameraPose: CGPoint(x: 10, y: 0), solveWidth: 64, searchForClosure: false,
            islandCapture: capture(id: 0, pose: CGPoint(x: 10, y: 0)))
        XCTAssertEqual(next.snapshot.observations.last?.optimizedPose, CGPoint(x: 110, y: 20))
        let predicted = LiveIslandCapture(id: 2, roomID: 0, startedAt: 0.1,
            worldFromLocal: CGPoint(x: 120, y: 20), localPose: .zero, placement: .estimated,
            uncertainty: .unknown, breakReason: "prediction", measured: false)
        let untrusted = try resumed.ingest(observationID: 2, timestamp: 0.1, frame: frame,
            proposedCameraPose: CGPoint(x: 120, y: 20), solveWidth: 64, islandCapture: predicted)
        XCTAssertEqual(untrusted.snapshot.observations.count, 3)
        XCTAssertFalse(untrusted.snapshot.keyframes.contains { $0.observationID == 2 })
        XCTAssertFalse(untrusted.snapshot.relativeMotionEdges.contains { $0.toObservationID == 2 })
    }

    private func capture(id: Int, pose: CGPoint) -> LiveIslandCapture {
        LiveIslandCapture(id: id, roomID: 0, startedAt: 0, worldFromLocal: .zero,
            localPose: pose, placement: .reference, uncertainty: .reference,
            breakReason: "capture-start", measured: true)
    }

    private func fixture() throws -> LiveWorldSnapshot {
        let poses = [CGPoint.zero, CGPoint(x: 10, y: 0), CGPoint(x: 80, y: 4)]
        let observations = poses.enumerated().map { i, p in
            LiveWorldObservation(id: i + 10, sourceObservationID: i + 10, timestamp: Double(i),
                sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
                excludedRects: [], rawPose: p, optimizedPose: p)
        }
        let first = capture(id: 0, pose: .zero).island(observationID: 10)
            .adding(LiveIslandMember(observationID: 11, localPose: poses[1], measured: true))
        let second = LiveTrackingIsland(id: 1, roomID: 0, startedAt: 2, worldFromLocal: poses[2],
            placement: .estimated, uncertainty: .unknown, breakReason: "gap",
            members: [LiveIslandMember(observationID: 12, localPose: .zero, measured: true)])
        return try LiveWorldSnapshot(mapRevision: 4, anchoredObservationID: 10, observations: observations,
            keyframes: [LiveWorldKeyframe(id: 10, observationID: 10, sourceObservationID: 10)],
            landmarks: [LiveWorldLandmark(id: 1, keyframeID: 10, sourceObservationID: 10,
                samplePoint: CGPoint(x: 20, y: 10), worldPoint: CGPoint(x: 20, y: 10),
                descriptor: [1, 2, 3], depth: 1, depthConfidence: 1)], trackingIslands: [first, second])
    }

    private func image() throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: 64, height: 36, bitsPerComponent: 8,
            bytesPerRow: 64 * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(gray: 0.4, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 64, height: 36))
        return try XCTUnwrap(context.makeImage())
    }
}
