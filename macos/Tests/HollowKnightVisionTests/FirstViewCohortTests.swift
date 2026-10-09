import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class FirstViewCohortTests: XCTestCase {
    private let width = 640, height = 96

    private func pixels(camera: Int, altered: Bool, unrelated: Bool = false) -> [UInt8] {
        (0..<(width*height)).map { index in
            let x = index % width + camera, y = index/width
            var hash = UInt64(x*73_856_093)^UInt64(y*19_349_663)^(unrelated ? 87_654 : 0)
            hash ^= hash >> 13; hash &*= 1_274_126_177
            let value = Int(UInt8(truncatingIfNeeded: hash >> 11))
            let noise = altered && x >= 272 ? ((x+y).isMultiple(of: 2) ? 64 : -64) : 0
            return UInt8(clamping: value+noise)
        }
    }

    private func image(_ values: [UInt8]) throws -> CGImage {
        let provider = try XCTUnwrap(CGDataProvider(data: Data(values) as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 8,
            bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: [],
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }

    private func returningPoses(unrelated: Bool) throws -> [CGPoint] {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.1)
        var result = GroundHypothesisTrackingResult.empty
        var frame = 0
        func update(camera: Int, altered: Bool, narrow: Bool, unknown: Bool = false) throws {
            let values = pixels(camera: camera, altered: altered, unrelated: unknown)
            let line = narrow ? CleanFloorLine(row: 40, xRange: (64-camera)...(255-camera))
                : CleanFloorLine(row: 40, xRange: 8...631)
            result = tracker.update(frame: try image(values), floorLines: [line],
                timestamp: Double(frame)/60, sourceLuma: values)
            frame += 1
        }
        // Initially only one strip is visible. Later ground has a different
        // appearance when learned; its old templates must not erase the
        // independent evidence that still recognizes the starting view.
        for _ in 0..<24 { try update(camera: 0, altered: false, narrow: true) }
        XCTAssertEqual(result.globalFeatureCount, 12)
        for step in 1...8 { try update(camera: step*8, altered: false, narrow: true) }
        for _ in 0..<40 { try update(camera: 64, altered: true, narrow: false) }
        XCTAssertGreaterThan(result.globalFeatureCount, 30)
        XCTAssertEqual(result.cameraPosition, CGPoint(x: 64, y: 0))
        tracker.reanchorLocalCamera(to: CGPoint(x: 20, y: 0))
        var poses = [CGPoint]()
        for _ in 0..<90 {
            try update(camera: 0, altered: false, narrow: false, unknown: unrelated)
            poses.append(try XCTUnwrap(result.cameraPosition))
        }
        return poses
    }

    func testOlderViewRecoversDespiteLaterChangedGroundTemplates() throws {
        let poses = try returningPoses(unrelated: false)
        XCTAssertEqual(poses.last, .zero)
        XCTAssertTrue(poses.suffix(20).allSatisfy { $0 == .zero })
    }

    func testUnrelatedViewCannotUseTheFrozenPopulationAsAFalseMatch() throws {
        XCTAssertTrue(try returningPoses(unrelated: true).allSatisfy { $0 == CGPoint(x: 20, y: 0) })
    }
}
