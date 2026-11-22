import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundReferenceAdmissionTests: XCTestCase {
    func testNewPlatformsRemainRecoverableWhenPreferredOldFloorIsOccluded() throws {
        let width = 320, height = 180
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.2, productionCameraSolver: true)
        var result = GroundHypothesisTrackingResult.empty
        for index in 0...80 {
            let shift = max(0, index - 45) * 2
            let pixels: [UInt8] = (0..<(width * height)).map { offset in
                let x = offset % width, y = offset / width
                if index >= 45 && (140..<152).contains(y) { return 0 }
                var hash = UInt64((x + shift) * 73_856_093) ^ UInt64(y * 19_349_663)
                hash ^= hash >> 13; hash &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: hash >> 11)
            }
            let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
            let frame = try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
                bitsPerPixel: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                bitmapInfo: CGBitmapInfo(rawValue: 0), provider: provider,
                decode: nil, shouldInterpolate: false, intent: .defaultIntent))
            var lines = index < 45 ? [CleanFloorLine(row: 140, xRange: 0...319)] : []
            if index >= 20 {
                lines += [CleanFloorLine(row: 60, xRange: 32...287),
                          CleanFloorLine(row: 100, xRange: 32...287)]
            }
            result = tracker.update(frame: frame, floorLines: lines,
                timestamp: Double(index) / 60, sourceLuma: pixels)
        }
        XCTAssertTrue(result.poseVerified)
        XCTAssertEqual(try XCTUnwrap(result.cameraPosition).x, 70, accuracy: 2)
        XCTAssertTrue(result.atlasLines.contains { abs($0.atlasStart.y - 80) < 2 })
    }

    func testStationaryNewOverlayRowsCannotOutvotePreviouslyEstablishedMovingFloor() throws {
        let width = 320, height = 180
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.2, productionCameraSolver: true)
        let floor = CleanFloorLine(row: 140, xRange: 0...319)
        let overlayRows = [20, 50, 80, 110]
        var result = GroundHypothesisTrackingResult.empty
        for index in 0...80 {
            let shift = max(0, index - 45) * 2
            let overlay = index >= 30
            let pixels: [UInt8] = (0..<(width * height)).map { offset in
                let y = offset / width, x = offset % width
                let fixed = overlay && overlayRows.contains { (0..<12).contains(y - $0) }
                var hash = UInt64((x + (fixed ? 0 : shift)) * 73_856_093)
                    ^ UInt64(y * 19_349_663)
                hash ^= hash >> 13; hash &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: hash >> 11)
            }
            let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
            let frame = try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
                bitsPerPixel: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                bitmapInfo: CGBitmapInfo(rawValue: 0), provider: provider,
                decode: nil, shouldInterpolate: false, intent: .defaultIntent))
            let rows = overlay ? overlayRows.map { CleanFloorLine(row: $0, xRange: 32...287) } : []
            result = tracker.update(frame: frame, floorLines: rows + [floor],
                timestamp: Double(index) / 60, sourceLuma: pixels)
        }
        XCTAssertTrue(result.poseVerified)
        XCTAssertTrue(result.hasConfirmedGround)
        XCTAssertEqual(try XCTUnwrap(result.cameraPosition).x, 70, accuracy: 2,
                       "A stationary title card cannot freeze camera motion while the established floor moves")
        XCTAssertTrue(result.atlasLines.allSatisfy { abs($0.atlasStart.y - 40) < 2 })
    }
}
