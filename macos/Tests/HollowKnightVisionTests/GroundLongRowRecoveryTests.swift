import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundLongRowRecoveryTests: XCTestCase {
    private let width = 640, height = 360

    private func pixels(dy: Int, salt: Int = 0, noise: Int = 0) -> [UInt8] {
        (0..<(width * height)).map { i in
            let x = i % width, y = i / width - dy
            var h = UInt64(bitPattern: Int64(x * 73_856_093))
                ^ UInt64(bitPattern: Int64(y * 19_349_663)) ^ UInt64(salt)
            h ^= h >> 13; h &*= 1_274_126_177
            let value = Int(UInt8(truncatingIfNeeded: h >> 11))
            let jitter = noise == 0 ? 0 : (i * 17 + 31) % (noise * 2 + 1) - noise
            return UInt8(clamping: value + jitter)
        }
    }

    func testSuddenLandingShiftRequiresBroadPreciseTexture() throws {
        for dy in [-96, -64, -40, 40, 64, 96] {
            let solver = GroundCameraSolver()
            _ = solver.solve(pixels: pixels(dy: 0), width: width, height: height,
                lines: [CleanFloorLine(row: 140, xRange: 16...623)],
                timestamp: 0, exclusions: [])
            let result = try XCTUnwrap(solver.solve(pixels: pixels(dy: dy, noise: 4),
                width: width, height: height,
                lines: [CleanFloorLine(row: 140 + dy, xRange: 16...623)],
                timestamp: 1.0 / 60, exclusions: []))
            XCTAssertEqual(result.imageTranslation, CGVector(dx: 0, dy: dy))
            XCTAssertGreaterThanOrEqual(result.support, 8)
            XCTAssertLessThanOrEqual(result.error, 6)
        }
    }

    func testLongRowRejectsWrongTextureWrongRowNarrowMaskedAndOversized() {
        for mode in ["unrelated", "wrong-row", "narrow", "masked", "oversized", "noisy"] {
            let solver = GroundCameraSolver()
            let range = mode == "narrow" ? 16...79 : 16...623
            _ = solver.solve(pixels: pixels(dy: 0), width: width, height: height,
                lines: [CleanFloorLine(row: 140, xRange: range)],
                timestamp: 0, exclusions: [])
            let dy = mode == "oversized" ? 112 : 64
            let result = solver.solve(pixels: pixels(dy: dy,
                salt: mode == "unrelated" ? 17791 : 0, noise: mode == "noisy" ? 24 : 0),
                width: width, height: height,
                lines: [CleanFloorLine(row: 140 + dy + (mode == "wrong-row" ? 16 : 0), xRange: range)],
                timestamp: 1.0 / 60,
                exclusions: mode == "masked" ? [CGRect(x: 0, y: 200, width: 640, height: 20)] : [])
            XCTAssertNil(result, mode)
        }
    }
}
