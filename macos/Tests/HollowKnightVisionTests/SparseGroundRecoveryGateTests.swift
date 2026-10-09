import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class SparseGroundRecoveryGateTests: XCTestCase {
    func testCoherentMeasuredDescentRecoversOnlyAfterSecondObservation() {
        var gate = SparseGroundRecoveryGate()
        XCTAssertFalse(gate.accepts(candidate: CGPoint(x: 2, y: 80), current: CGPoint(x: 0, y: 100),
            solveWidth: 640, timestamp: 1, textureSupport: 3, textureError: 5.5))
        XCTAssertTrue(gate.accepts(candidate: CGPoint(x: 4, y: 71), current: CGPoint(x: 0, y: 98),
            solveWidth: 640, timestamp: 1.017, textureSupport: 3, textureError: 9.8))
        XCTAssertFalse(gate.accepts(candidate: CGPoint(x: 6, y: 62), current: CGPoint(x: 0, y: 96),
            solveWidth: 640, timestamp: 1.034, textureSupport: 3, textureError: 7))
    }

    func testStaticContradictoryStaleWeakAndOversizedEvidenceCannotRecover() {
        for kind in ["static", "wrong direction", "stale", "weak", "huge", "reset"] {
            var gate = SparseGroundRecoveryGate()
            _ = gate.accepts(candidate: CGPoint(x: 2, y: 80), current: CGPoint(x: 0, y: 100),
                solveWidth: 640, timestamp: 1, textureSupport: 3, textureError: 5.5)
            let y: CGFloat = kind == "static" ? 80 : kind == "wrong direction" ? 89 : kind == "huge" ? -90 : 71
            if kind == "reset" { gate.reset() }
            XCTAssertFalse(gate.accepts(candidate: CGPoint(x: 4, y: y), current: CGPoint(x: 0, y: 98),
                solveWidth: 640, timestamp: kind == "stale" ? 1.2 : 1.017,
                textureSupport: 3, textureError: kind == "weak" ? 20 : 9.8), kind)
        }
    }
}
