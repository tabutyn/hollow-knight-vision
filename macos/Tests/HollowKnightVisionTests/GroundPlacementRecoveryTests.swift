import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundPlacementRecoveryTests: XCTestCase {
    private func proposal(_ x: CGFloat, _ y: CGFloat, support: Int = 14,
                          error: CGFloat = 6.7, distinct: Bool = true,
                          global: Bool = false, established: Bool = true) -> GroundPlacementRecovery.Proposal {
        .init(position: CGPoint(x: x, y: y), textureSupport: support,
              textureError: error, inlierCount: 3, globalMatchCount: global ? 4 : 0,
              hasGlobalCorrection: global, distinctFrame: distinct,
              hasEstablishedGround: established)
    }

    private func trusted() -> GroundPlacementRecovery {
        var gate = GroundPlacementRecovery()
        XCTAssertTrue(gate.accepts(proposal(1454, 508.837),
            current: CGPoint(x: 1454, y: 508.837), solveWidth: 640,
            timestamp: 51.99, captureElapsed: 1 / 60))
        return gate
    }

    func testLandingCorrectionSurvivesFallbackAndRequiresAnotherMeasurement() {
        var gate = trusted()
        gate.missingObservation()
        // Captured run 3: correct 35px rebound was rejected by the 32px gate.
        XCTAssertFalse(gate.accepts(proposal(1463, 473.837, support: 10, error: 17.4494),
            current: CGPoint(x: 1454, y: 508.837), solveWidth: 640,
            timestamp: 52.0694, captureElapsed: 0.0165))
        XCTAssertTrue(gate.retainsCandidate(at: 52.0694))
        XCTAssertFalse(gate.allowsAtlasWrite)
        // A second solve against the preserved reference confirms the origin.
        XCTAssertTrue(gate.accepts(proposal(1466, 467.837),
            current: CGPoint(x: 1454, y: 511.837), solveWidth: 640,
            timestamp: 52.0854, captureElapsed: 0.016))
        XCTAssertTrue(gate.allowsAtlasWrite)
        XCTAssertFalse(gate.retainsCandidate(at: 52.0854))
    }

    func testOrdinaryMeasuredFallbackKeepsExistingContinuityPolicy() {
        var gate = trusted()
        gate.missingObservation()
        XCTAssertTrue(gate.accepts(proposal(1457, 505.837),
            current: CGPoint(x: 1454, y: 511.837), solveWidth: 640,
            timestamp: 52.0854, captureElapsed: 0.016))
        XCTAssertTrue(gate.allowsAtlasWrite)
    }

    func testRejectsDuplicateStaleWeakAndIncoherentVotes() {
        for mode in ["duplicate", "stale", "weak", "error", "opposite", "oversized", "missing"] {
            var gate = trusted()
            gate.missingObservation()
            _ = gate.accepts(proposal(1463, 473.837, support: 10, error: 17.4494),
                current: CGPoint(x: 1454, y: 508.837), solveWidth: 640,
                timestamp: 52.0694, captureElapsed: 0.0165)
            if mode == "missing" { gate.missingObservation() }
            XCTAssertFalse(gate.accepts(proposal(1466,
                mode == "opposite" ? 550 : mode == "oversized" ? 400 : 467.837,
                support: mode == "weak" ? 2 : 14,
                error: mode == "error" ? 15 : 6.7, distinct: mode != "duplicate"),
                current: CGPoint(x: 1454, y: 511.837), solveWidth: 640,
                timestamp: mode == "stale" ? 52.21 : 52.0854, captureElapsed: 0.016), mode)
            XCTAssertFalse(gate.allowsAtlasWrite, mode)
        }
    }

    func testGlobalVerificationResolvesLossWithoutLocalVoting() {
        var gate = trusted()
        gate.missingObservation()
        XCTAssertTrue(gate.accepts(proposal(900, 100, support: 0, global: true),
            current: CGPoint(x: 1454, y: 508), solveWidth: 640,
            timestamp: 52.1, captureElapsed: 0.016))
        XCTAssertTrue(gate.allowsAtlasWrite)
    }

    func testProvisionalBootstrapDoesNotLockAnUnestablishedWorldOrigin() {
        var gate = GroundPlacementRecovery()
        XCTAssertTrue(gate.accepts(proposal(0, 0, support: 0, established: false),
            current: .zero, solveWidth: 640, timestamp: 1, captureElapsed: nil))
        gate.missingObservation()
        // The checkpoint restore displaced the provisional seed before any
        // floor was confirmed. Ordinary bootstrap must be free to reseed it.
        XCTAssertTrue(gate.accepts(proposal(100, 0, support: 0, established: false),
            current: .zero, solveWidth: 640, timestamp: 1.02, captureElapsed: 0.02))
        XCTAssertTrue(gate.accepts(proposal(100, 0), current: CGPoint(x: 100, y: 0),
            solveWidth: 640, timestamp: 1.04, captureElapsed: 0.02))
    }

    func testNormalMotionAndResetDoNotRequireRecovery() {
        var gate = trusted()
        XCTAssertTrue(gate.accepts(proposal(1458, 510), current: CGPoint(x: 1454, y: 508),
            solveWidth: 640, timestamp: 52.01, captureElapsed: 0.02))
        gate.missingObservation()
        gate.reset()
        gate.missingObservation()
        XCTAssertTrue(gate.accepts(proposal(0, 0, support: 0), current: .zero,
            solveWidth: 640, timestamp: 1, captureElapsed: nil))
    }
}
