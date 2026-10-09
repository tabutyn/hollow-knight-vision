import XCTest
@testable import HollowKnightVision

final class LiveTrackingTimingTests: XCTestCase {
    func testFloorlessSelectorRejectsStaleCoarsePoseAfterDirectionReversal() {
        XCTAssertEqual(
            FloorlessPoseCandidateSelector.select(
                current: .zero,
                coarse: CGPoint(x: -20, y: 0),
                masked: CGPoint(x: 8, y: 0),
                expectedDirection: .right,
                coarseIsPlaceMatch: false
            ),
            FloorlessPoseCandidate(
                position: CGPoint(x: 8, y: 0),
                source: .maskedRegistration
            )
        )
    }

    func testFloorlessSelectorUsesLargerAgreeingCaptureRateStep() {
        XCTAssertEqual(
            FloorlessPoseCandidateSelector.select(
                current: .zero,
                coarse: CGPoint(x: 18, y: 1),
                masked: CGPoint(x: 5, y: 0),
                expectedDirection: .right,
                coarseIsPlaceMatch: false
            ),
            FloorlessPoseCandidate(
                position: CGPoint(x: 18, y: 1),
                source: .coarseCaptureRate
            )
        )
    }

    func testFloorlessSelectorKeepsMaskedHorizontalStopAndCoarseVerticalTravel() {
        XCTAssertEqual(
            FloorlessPoseCandidateSelector.select(
                current: .zero,
                coarse: CGPoint(x: -20, y: 0),
                masked: CGPoint(x: -4, y: 0),
                expectedDirection: nil,
                coarseIsPlaceMatch: false
            )?.source,
            .maskedRegistration
        )
        XCTAssertEqual(
            FloorlessPoseCandidateSelector.select(
                current: .zero,
                coarse: CGPoint(x: 1, y: 15),
                masked: CGPoint(x: 0, y: 1),
                expectedDirection: nil,
                coarseIsPlaceMatch: false
            )?.source,
            .coarseCaptureRate
        )
    }

    func testFloorlessSelectorAlwaysAcceptsAbsolutePlaceMatch() {
        XCTAssertEqual(
            FloorlessPoseCandidateSelector.select(
                current: .zero,
                coarse: CGPoint(x: -30, y: 4),
                masked: CGPoint(x: 8, y: 0),
                expectedDirection: .right,
                coarseIsPlaceMatch: true
            )?.source,
            .coarseCaptureRate
        )
    }

    func testGroundMotionEvidenceRejectsCoordinateSeedWithoutPixelMeasurement() {
        XCTAssertFalse(GroundMotionEvidence.isMeasured(
            poseVerified: true,
            hasConfirmedGround: true,
            localTextureSupport: 0,
            inlierCount: 0,
            globalMatchCount: 0
        ))
        XCTAssertTrue(GroundMotionEvidence.isMeasured(
            poseVerified: true,
            hasConfirmedGround: true,
            localTextureSupport: 1,
            inlierCount: 0,
            globalMatchCount: 0
        ))
        XCTAssertTrue(GroundMotionEvidence.isMeasured(
            poseVerified: true,
            hasConfirmedGround: true,
            localTextureSupport: 0,
            inlierCount: 0,
            globalMatchCount: 4
        ))
    }

    func testPredictionAdvancesCurrentFrameButIsBoundedAndExpires() {
        let speed = PresentationPosePrediction.velocity(from: .zero, at: 1,
            to: CGPoint(x: 8, y: 0), at: 1.02, continuous: true)
        XCTAssertEqual(speed.dx, 400, accuracy: 0.001)
        let predicted = PresentationPosePrediction.position(CGPoint(x: 8, y: 0),
            velocity: speed, measuredAt: 1.02, presentedAt: 1.04)
        XCTAssertEqual(predicted.x, 16, accuracy: 0.001)
        let capped = PresentationPosePrediction.position(.zero, velocity: speed,
            measuredAt: 1, presentedAt: 1.08)
        XCTAssertEqual(capped.x, 8, accuracy: 0.001)
        XCTAssertEqual(PresentationPosePrediction.position(.zero, velocity: speed,
            measuredAt: 1, presentedAt: 1.2), .zero)
    }

    func testCorrectionAndLossNeverBecomePresentationVelocity() {
        XCTAssertEqual(PresentationPosePrediction.velocity(from: .zero, at: 1,
            to: CGPoint(x: 80, y: 0), at: 1.02, continuous: true), .zero)
        XCTAssertEqual(PresentationPosePrediction.velocity(from: .zero, at: 1,
            to: CGPoint(x: 4, y: 0), at: 1.02, continuous: false), .zero)
    }

    func testCaptureGroundReliabilityRequiresFreshExactTrackingState() {
        XCTAssertTrue(CaptureGroundReliability.isReliable(
            groundAnchored: true,
            poseTimestamp: 10,
            frameTimestamp: 10.12,
            frameAdmitsTracking: true
        ))
        XCTAssertFalse(CaptureGroundReliability.isReliable(
            groundAnchored: true,
            poseTimestamp: 10,
            frameTimestamp: 10.121,
            frameAdmitsTracking: true
        ))
        XCTAssertFalse(CaptureGroundReliability.isReliable(
            groundAnchored: true,
            poseTimestamp: 10,
            frameTimestamp: 10.05,
            frameAdmitsTracking: false
        ))
        XCTAssertFalse(CaptureGroundReliability.isReliable(
            groundAnchored: nil,
            poseTimestamp: nil,
            frameTimestamp: 10,
            frameAdmitsTracking: true
        ))
        XCTAssertFalse(CaptureGroundReliability.isReliable(
            groundAnchored: false,
            poseTimestamp: 10,
            frameTimestamp: 10.01,
            frameAdmitsTracking: true
        ))
    }

    func testTentativeTransitionPreservesVerifiedHandoffOrigin() {
        XCTAssertFalse(TransitionPosePublication.replacesTrackingState(
            roomOwnershipChanged: false,
            coarseMotionIsControlling: false
        ))
        XCTAssertTrue(TransitionPosePublication.replacesTrackingState(
            roomOwnershipChanged: false,
            coarseMotionIsControlling: true
        ))
        XCTAssertTrue(TransitionPosePublication.replacesTrackingState(
            roomOwnershipChanged: true,
            coarseMotionIsControlling: false
        ))
    }

    func testFloorlessHandoffAdvancesDelayedPoseFromRetainedGroundVelocity() throws {
        let seed = try XCTUnwrap(FloorlessMotionHandoff.seed(
            velocity: CGVector(dx: -400, dy: 0),
            measuredAt: 5.211,
            presentedAt: 5.386
        ))
        let predicted = FloorlessMotionHandoff.position(
            CGPoint(x: -192, y: 10),
            measuredAt: 5.229,
            seed: seed,
            presentedAt: 5.386,
            solveWidth: 640
        )
        XCTAssertEqual(predicted.x, -254.8, accuracy: 0.001)
        XCTAssertEqual(predicted.y, 10, accuracy: 0.001)
    }

    func testFloorlessHandoffExpiresAndCapsTravel() throws {
        XCTAssertNil(FloorlessMotionHandoff.seed(
            velocity: CGVector(dx: -400, dy: 0),
            measuredAt: 1,
            presentedAt: 1.481
        ))
        let seed = try XCTUnwrap(FloorlessMotionHandoff.seed(
            velocity: CGVector(dx: 1_000, dy: 0),
            measuredAt: 1,
            presentedAt: 1.1
        ))
        XCTAssertEqual(FloorlessMotionHandoff.position(
            .zero,
            measuredAt: 1,
            seed: seed,
            presentedAt: 1.2,
            solveWidth: 640
        ).x, 115.2, accuracy: 0.001)
        XCTAssertEqual(FloorlessMotionHandoff.position(
            .zero,
            measuredAt: 1,
            seed: seed,
            presentedAt: 1.251,
            solveWidth: 640
        ), .zero)
    }
}
