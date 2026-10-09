import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class BroadGroundRecoveryGateTests: XCTestCase {
    func testLandingReboundNeedsTwoBroadIndependentMeasurements() {
        var gate = SparseGroundRecoveryGate()
        XCTAssertFalse(gate.accepts(candidate: CGPoint(x: 2221, y: 2),
            current: CGPoint(x: 2226, y: 38), solveWidth: 640,
            timestamp: 1, textureSupport: 15, textureError: 16.188))
        XCTAssertTrue(gate.accepts(candidate: CGPoint(x: 2219, y: 5),
            current: CGPoint(x: 2226, y: 38), solveWidth: 640,
            timestamp: 1.0165, textureSupport: 15, textureError: 4.583))
    }

    func testBroadRecoveryRejectsWeakStaleDuplicateIncoherentOrExcessiveEvidence() {
        for mode in ["weak-first", "weak-second", "expensive-first", "expensive-second",
                     "stale", "duplicate", "horizontal", "vertical", "oversized",
                     "opposite", "reset", "invalid"] {
            var gate = SparseGroundRecoveryGate()
            _ = gate.accepts(candidate: CGPoint(x: 2, y: 2),
                current: CGPoint(x: 0, y: 38), solveWidth: 640, timestamp: 1,
                textureSupport: mode == "weak-first" ? 11 : 15,
                textureError: mode == "expensive-first" ? 19 : 16)
            if mode == "reset" { gate.reset() }
            let x: CGFloat = mode == "horizontal" ? 19 : mode == "duplicate" ? 2 : 0
            let y: CGFloat = mode == "vertical" ? 15 : mode == "duplicate" ? 2 : 5
            let currentY: CGFloat = mode == "oversized" ? 70 : mode == "opposite" ? -38 : 38
            XCTAssertFalse(gate.accepts(candidate: CGPoint(x: x, y: y),
                current: CGPoint(x: 0, y: currentY), solveWidth: 640,
                timestamp: mode == "stale" ? 1.2 : 1.017,
                textureSupport: mode == "weak-second" ? 11 : 15,
                textureError: mode == "invalid" ? .nan : mode == "expensive-second" ? 7 : 4.5), mode)
        }
    }
}
