import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class BroadTextureLongRowTests: XCTestCase {
    func testManyIndependentPatchesRecoverModeratelyNoisyLanding() throws {
        let width = 640, height = 360
        func pixels(_ dy: Int, noise: Int) -> [UInt8] {
            (0..<(width * height)).map { i in
                let x = i % width, y = i / width - dy
                var h = UInt64(bitPattern: Int64(x * 73_856_093))
                    ^ UInt64(bitPattern: Int64(y * 19_349_663))
                h ^= h >> 13; h &*= 1_274_126_177
                let jitter = noise == 0 ? 0 : (i * 17 + 31) % (noise * 2 + 1) - noise
                return UInt8(clamping: Int(UInt8(truncatingIfNeeded: h >> 11)) + jitter)
            }
        }
        let solver = GroundCameraSolver()
        _ = solver.solve(pixels: pixels(0, noise: 0), width: width, height: height,
            lines: [120, 140].map { CleanFloorLine(row: $0, xRange: 16...623) },
            timestamp: 0, exclusions: [])
        let found = try XCTUnwrap(solver.solve(pixels: pixels(64, noise: 12),
            width: width, height: height,
            lines: [184, 204].map { CleanFloorLine(row: $0, xRange: 16...623) },
            timestamp: 1.0 / 60, exclusions: []))
        XCTAssertEqual(found.imageTranslation, CGVector(dx: 0, dy: 64))
        XCTAssertGreaterThanOrEqual(found.support, 12)
        XCTAssertGreaterThan(found.error, 6)
        XCTAssertLessThanOrEqual(found.error, 10)
    }

    func testRepeatedTextureCannotChooseAnAmbiguousLongRowPhase() {
        let width = 640, height = 360
        let pixels: [UInt8] = (0..<(width * height)).map { i in
            let x = (i % width) % 16, y = (i / width) % 16
            return UInt8((x * 37 + y * 71 + x * y * 3) % 256)
        }
        let solver = GroundCameraSolver()
        _ = solver.solve(pixels: pixels, width: width, height: height,
            lines: [120, 140].map { CleanFloorLine(row: $0, xRange: 16...623) },
            timestamp: 0, exclusions: [])
        XCTAssertNil(solver.solve(pixels: pixels, width: width, height: height,
            lines: [184, 204].map { CleanFloorLine(row: $0, xRange: 16...623) },
            timestamp: 1.0 / 60, exclusions: []))
    }
}
