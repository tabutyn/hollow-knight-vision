import XCTest
@testable import HollowKnightVision

final class GroundGlobalCorrectionPolicyTests: XCTestCase {
    func testFourWrongVotesCannotOverrideWideStrip() {
        let wrong = GroundGlobalCorrectionPolicy.Quality(tested: 24, inliers: 4, horizontalSpread: 80, error: 10)
        let local = GroundGlobalCorrectionPolicy.Quality(tested: 24, inliers: 23, horizontalSpread: 400, error: 1)
        XCTAssertFalse(GroundGlobalCorrectionPolicy.accepts(candidate: wrong, current: local,
            displacement: 80, hasTrustedPose: true))
    }

    func testSimilarRepeatedTextureDoesNotAuthorizePhaseJump() {
        let quality = GroundGlobalCorrectionPolicy.Quality(tested: 20, inliers: 18, horizontalSpread: 320, error: 4)
        XCTAssertFalse(GroundGlobalCorrectionPolicy.accepts(candidate: quality, current: quality,
            displacement: 80, hasTrustedPose: true))
        XCTAssertTrue(GroundGlobalCorrectionPolicy.accepts(candidate: quality, current: quality,
            displacement: 0, hasTrustedPose: true))
    }

    func testBetterVerifiedStripCanCorrectDriftAndRecoverLostAnchor() {
        let good = GroundGlobalCorrectionPolicy.Quality(tested: 20, inliers: 19, horizontalSpread: 320, error: 2)
        let drift = GroundGlobalCorrectionPolicy.Quality(tested: 20, inliers: 10, horizontalSpread: 160, error: 16)
        XCTAssertTrue(GroundGlobalCorrectionPolicy.accepts(candidate: good, current: drift,
            displacement: 5, hasTrustedPose: true))
        XCTAssertTrue(GroundGlobalCorrectionPolicy.accepts(candidate: good, current: nil,
            displacement: 64, hasTrustedPose: false))
    }

    func testSmallUnambiguousRefinementNeedsOnlyMeasurableImprovement() {
        let refined = GroundGlobalCorrectionPolicy.Quality(
            tested: 20, inliers: 18, horizontalSpread: 320, error: 4)
        let drifted = GroundGlobalCorrectionPolicy.Quality(
            tested: 20, inliers: 16, horizontalSpread: 320, error: 5.2)
        XCTAssertTrue(GroundGlobalCorrectionPolicy.accepts(candidate: refined, current: drifted,
            displacement: 5, hasTrustedPose: true))
        XCTAssertFalse(GroundGlobalCorrectionPolicy.accepts(candidate: refined, current: drifted,
            displacement: 9, hasTrustedPose: true))
    }

    func testSmallOccludedLoopClosureUsesWideInlierAdvantage() {
        let returning = GroundGlobalCorrectionPolicy.Quality(
            tested: 30, inliers: 10, horizontalSpread: 368, error: 19.2)
        let drifted = GroundGlobalCorrectionPolicy.Quality(
            tested: 28, inliers: 3, horizontalSpread: 96, error: 25.4)
        XCTAssertTrue(GroundGlobalCorrectionPolicy.accepts(candidate: returning, current: drifted,
            displacement: 8, hasTrustedPose: true))
        XCTAssertFalse(GroundGlobalCorrectionPolicy.accepts(candidate: returning, current: drifted,
            displacement: 13, hasTrustedPose: true))

        let notDecisive = GroundGlobalCorrectionPolicy.Quality(
            tested: 28, inliers: 6, horizontalSpread: 240, error: 21)
        XCTAssertFalse(GroundGlobalCorrectionPolicy.accepts(candidate: returning, current: notDecisive,
            displacement: 8, hasTrustedPose: true))
    }

    func testStrongGlobalStripClosesWhenDriftedLocalPoseHasNoSupport() {
        let origin = GroundGlobalCorrectionPolicy.Quality(
            tested: 13, inliers: 11, horizontalSpread: 224, error: 9.3)
        XCTAssertTrue(GroundGlobalCorrectionPolicy.accepts(
            candidate: origin, current: nil,
            displacement: 37, hasTrustedPose: true
        ))

        let sparse = GroundGlobalCorrectionPolicy.Quality(
            tested: 19, inliers: 10, horizontalSpread: 128, error: 9)
        XCTAssertFalse(GroundGlobalCorrectionPolicy.accepts(
            candidate: sparse, current: nil,
            displacement: 37, hasTrustedPose: true
        ))
        let noisy = GroundGlobalCorrectionPolicy.Quality(
            tested: 13, inliers: 11, horizontalSpread: 224, error: 10.5)
        XCTAssertFalse(GroundGlobalCorrectionPolicy.accepts(
            candidate: noisy, current: nil,
            displacement: 37, hasTrustedPose: true
        ))
    }
}
