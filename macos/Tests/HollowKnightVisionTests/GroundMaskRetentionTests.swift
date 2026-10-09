import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundMaskRetentionTests: XCTestCase {
    private let width = 320, height = 96
    private var lines: [CleanFloorLine] { [CleanFloorLine(row: 40, xRange: 16...303)] }

    private func pixels(camera: Int) -> [UInt8] {
        (0..<(width * height)).map { index in
            let x = index % width + camera, y = index / width
            var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663)
            hash ^= hash >> 13; hash &*= 1_274_126_177
            return UInt8(truncatingIfNeeded: hash >> 11)
        }
    }

    private func update(_ tracker: GroundHypothesisTracker, pixels: [UInt8],
                        frame: Int, masks: [CGRect] = []) throws -> GroundHypothesisTrackingResult {
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        let image = try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: [], provider: provider, decode: nil, shouldInterpolate: false,
            intent: .defaultIntent))
        return tracker.update(frame: image, floorLines: lines, timestamp: Double(frame) / 60,
            sourceLuma: pixels, protectedOcclusions: masks)
    }

    private func established() throws -> (GroundHypothesisTracker, GroundHypothesisTrackingResult) {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.1)
        var result = GroundHypothesisTrackingResult.empty
        for frame in 0..<24 { result = try update(tracker, pixels: pixels(camera: 0), frame: frame) }
        XCTAssertEqual(result.globalFeatureCount, 18)
        XCTAssertTrue(result.poseVerified)
        return (tracker, result)
    }

    func testFullyMaskedRecoveryWaitsForVisiblePixelsWithoutDiscardingGround() throws {
        let (tracker, initial) = try established()
        let moved = pixels(camera: 4)
        let hidden = try update(tracker, pixels: moved, frame: 24,
            masks: [CGRect(x: 0, y: 32, width: width, height: 32)])
        XCTAssertFalse(hidden.poseVerified)
        XCTAssertEqual(hidden.cameraPosition, .zero)
        XCTAssertEqual(hidden.localTextureSupport, 0)
        XCTAssertEqual(hidden.globalFeatureCount, initial.globalFeatureCount)
        let revealed = try update(tracker, pixels: moved, frame: 25)
        XCTAssertTrue(revealed.poseVerified)
        XCTAssertEqual(revealed.cameraPosition, CGPoint(x: 4, y: 0))
    }

    func testSparseVisibleRecoveryReportsPixelCostInsteadOfZeroSpatialResidual() throws {
        let (tracker, _) = try established()
        var moved = pixels(camera: 4)
        for index in moved.indices {
            moved[index] = UInt8(clamping: Int(moved[index]) + (index.isMultiple(of: 2) ? 8 : -8))
        }
        // Cover every strip-solver sample, leaving independent active tiles.
        // Feature recovery must use those visible tiles and report their noise.
        let masks = stride(from: 16, through: 256, by: 48).map {
            CGRect(x: $0, y: 36, width: 2, height: 24)
        }
        let recovered = try update(tracker, pixels: moved, frame: 24, masks: masks)
        XCTAssertTrue(recovered.poseVerified)
        XCTAssertEqual(recovered.cameraPosition, CGPoint(x: 4, y: 0))
        XCTAssertGreaterThan(try XCTUnwrap(recovered.localTextureError), 7)
        XCTAssertLessThan(try XCTUnwrap(recovered.localTextureError), 9)
        XCTAssertEqual(recovered.residualRMS, 0)
    }

    func testPartialMaskRetainsReferencePixelsAndFeatureIdentity() throws {
        let (tracker, initial) = try established()
        let original = pixels(camera: 0)
        let mask = CGRect(x: 64, y: 36, width: 64, height: 24)
        var covered = original
        for y in 36..<60 { for x in 64..<128 {
            covered[y * width + x] = UInt8(clamping: Int(original[y * width + x]) + 12)
        } }
        var result = initial
        for frame in 24..<44 { result = try update(tracker, pixels: covered, frame: frame, masks: [mask]) }
        XCTAssertTrue(result.poseVerified)
        XCTAssertEqual(result.cameraPosition, .zero)
        let hidden = result.features.filter { $0.imageRect.intersects(mask) }
        XCTAssertFalse(hidden.isEmpty)
        XCTAssertTrue(hidden.allSatisfy { $0.classification == .occluded })
        for feature in result.atlasFeatures {
            let before = try XCTUnwrap(initial.atlasFeatures.first {
                $0.segmentID == feature.segmentID && $0.sequenceIndex == feature.sequenceIndex
            })
            XCTAssertEqual(feature.referencePixels, before.referencePixels)
        }
        let revealed = try update(tracker, pixels: original, frame: 44)
        let hiddenIDs = Set(hidden.map(\.id))
        XCTAssertEqual(Set(revealed.features.filter {
            hiddenIDs.contains($0.id) && $0.classification != .occluded
        }.map(\.id)), hiddenIDs)
    }
}
