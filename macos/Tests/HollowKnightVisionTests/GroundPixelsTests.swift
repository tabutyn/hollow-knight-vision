import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundPixelsTests: XCTestCase {
    func testGrayscaleOrientationAndBoundaryPatchPreserveEveryPixel() throws {
        let width = 19, height = 15
        let pixels = (0..<(width * height)).map { UInt8(($0 * 37 + 11) % 256) }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        let image = try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
        let extracted = try XCTUnwrap(GroundPixels.luma(image, width: width, height: height))
        XCTAssertEqual(extracted, pixels)
        let patch = try XCTUnwrap(GroundPixels.patch(extracted, width: width, height: height, x: 3, y: 3))
        XCTAssertEqual(patch.count, 192)
        for index in patch.indices {
            XCTAssertEqual(patch[index], pixels[(3 + index / 16) * width + 3 + index % 16])
        }
    }

    func testOutsideOrIncompleteTextureCannotBecomePatchEvidence() {
        let pixels = [UInt8](repeating: 20, count: 16 * 12)
        XCTAssertNil(GroundPixels.patch(pixels, width: 16, height: 12, x: -1, y: 0))
        XCTAssertNil(GroundPixels.patch(pixels, width: 16, height: 12, x: 1, y: 0))
        XCTAssertNil(GroundPixels.patch(pixels, width: 16, height: 12, x: 0, y: 1))
        XCTAssertNil(GroundPixels.patch(Array(pixels.dropLast()), width: 16, height: 12, x: 0, y: 0))
    }

    func testRetiredOptionsCannotSilentlyRunDefaultExperiment() {
        XCTAssertEqual(HollowKnightVisionLaunchOptions.retiredArgument(in:
            ["vision", "--ground-snap-audit-directory=/tmp/audit"]), "--ground-snap-audit-directory=/tmp/audit")
        XCTAssertEqual(HollowKnightVisionLaunchOptions.retiredArgument(in: ["vision", "--replay", "movie.mov"]), "--replay")
        XCTAssertNil(HollowKnightVisionLaunchOptions.retiredArgument(in:
            ["vision", "--world-replay-session", "/tmp/session", "--trace-ground-tracking", "--render-frame-marker"]))
    }
}
