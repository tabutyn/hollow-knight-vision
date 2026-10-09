import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundBootstrapRecoveryTests: XCTestCase {
    private let width = 320, height = 100
    private let lines = [CleanFloorLine(row: 40, xRange: 16...303)]

    private func pixels(seed: UInt64) -> [UInt8] {
        (0..<(width * height)).map { index in
            let x = index % width, y = index / width
            var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663) ^ seed
            hash ^= hash >> 13
            hash &*= 1_274_126_177
            return UInt8(truncatingIfNeeded: hash >> 11)
        }
    }

    private func image(_ pixels: [UInt8]) throws -> CGImage {
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height,
            bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: width,
            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: [], provider: provider,
            decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }

    func testUntrustedFirstViewCannotPreventStableNewGroundFromInitializing() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.2)
        let original = pixels(seed: 0), current = pixels(seed: 87_654)
        _ = tracker.update(frame: try image(original), floorLines: lines,
            timestamp: 0, sourceLuma: original)
        let frame = try image(current)
        var confirmedAt: Double?
        for index in 1...60 {
            let result = tracker.update(frame: frame, floorLines: lines,
                timestamp: Double(index)/60, sourceLuma: current)
            if result.poseVerified && result.hasConfirmedGround && confirmedAt == nil {
                confirmedAt = Double(index)/60
            }
        }
        XCTAssertLessThan(try XCTUnwrap(confirmedAt), 0.6)
    }

    func testEstablishedMapSurvivesUnrelatedPixelsWithoutReseedingThem() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.2)
        let original = pixels(seed: 0), unrelated = pixels(seed: 87_654)
        let originalImage = try image(original), unrelatedImage = try image(unrelated)
        var result = GroundHypothesisTrackingResult.empty
        for index in 0..<60 {
            result = tracker.update(frame: originalImage, floorLines: lines,
                timestamp: Double(index)/60, sourceLuma: original)
        }
        let established = result.atlasFeatures
        XCTAssertGreaterThan(result.globalFeatureCount, 0)
        for index in 60..<150 {
            result = tracker.update(frame: unrelatedImage, floorLines: lines,
                timestamp: Double(index)/60, sourceLuma: unrelated)
            XCTAssertFalse(result.poseVerified)
            XCTAssertEqual(result.atlasFeatures, established)
        }
    }

    func testContinuallyChangingBootstrapImagesCannotInventConfirmedGround() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.2)
        for index in 0..<90 {
            let values = pixels(seed: UInt64(index) * 87_654)
            let result = tracker.update(frame: try image(values), floorLines: lines,
                timestamp: Double(index)/60, sourceLuma: values)
            XCTAssertFalse(result.hasConfirmedGround)
            XCTAssertEqual(result.globalFeatureCount, 0)
        }
    }
}
