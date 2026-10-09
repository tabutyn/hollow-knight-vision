import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundCaptureGapTests: XCTestCase {
    func testMeasuredCaptureGapRequiresBroadPreciseBoundedEvidence() {
        func accepts(_ elapsed: Double?, support: Int = 9, error: CGFloat = 15.4,
                     y: CGFloat = 604) -> Bool {
            GroundPoseContinuityGate.accepts(candidate: CGPoint(x: 1248, y: y),
                current: CGPoint(x: 1228, y: 549), solveWidth: 640,
                globalMatchCount: 0, hasGlobalCorrection: false,
                localTextureSupport: support, localTextureError: error,
                captureElapsed: elapsed)
        }
        XCTAssertTrue(accepts(0.202))
        for elapsed: Double? in [nil, 0.05, 0.5, .nan, -.infinity] {
            XCTAssertFalse(accepts(elapsed))
        }
        XCTAssertFalse(accepts(0.202, support: 7))
        XCTAssertFalse(accepts(0.202, error: 22))
        XCTAssertFalse(accepts(0.202, error: .nan))
        XCTAssertFalse(accepts(0.202, y: 670))
    }

    func testRetainedPixelMotionCanConfirmRecoveryBeyondHeldPose() throws {
        let width = 640, height = 200
        func pixels(_ cameraY: Int) -> [UInt8] {
            (0..<(width * height)).map { i in
                let x = i % width, y = i / width - cameraY
                var h = UInt64(bitPattern: Int64(x * 73_856_093))
                    ^ UInt64(bitPattern: Int64(y * 19_349_663))
                h ^= h >> 13; h &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: h >> 11)
            }
        }
        let solver = GroundCameraSolver()
        var gate = SparseGroundRecoveryGate()
        // A held published pose trails private motion. Independent pixels
        // continue measuring the real camera while the first proposal waits.
        var measured = CGPoint(x: 0, y: 40)
        var timestamp = 0.0
        for frame in 0..<24 {
            timestamp = Double(frame) / 60
            _ = solver.solve(pixels: pixels(40), width: width, height: height,
                lines: [CleanFloorLine(row: 80, xRange: 16...623)],
                timestamp: timestamp, exclusions: [])
        }
        for (index, actualY) in [64, 68, 72].enumerated() {
            timestamp += index == 0 ? 0.05 : 1.0 / 60
            let solution = try XCTUnwrap(solver.solve(pixels: pixels(actualY),
                width: width, height: height,
                lines: [CleanFloorLine(row: 40 + actualY, xRange: 16...623)],
                timestamp: timestamp, exclusions: []))
            measured.x -= solution.imageTranslation.dx
            measured.y += solution.imageTranslation.dy
            XCTAssertEqual(measured.y, CGFloat(actualY), accuracy: 0.001)
            let accepted = gate.accepts(candidate: measured, current: .zero,
                solveWidth: 640, timestamp: timestamp,
                textureSupport: solution.support, textureError: solution.error)
            XCTAssertEqual(accepted, index == 1)
        }
    }
}
