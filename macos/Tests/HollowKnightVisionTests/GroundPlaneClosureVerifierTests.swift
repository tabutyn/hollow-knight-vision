import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundPlaneClosureVerifierTests: XCTestCase {
    private func ground(y: CGFloat, spans: [ClosedRange<CGFloat>] = [0...320]) -> LiveWorldGroundGeometry {
        LiveWorldGroundGeometry(lines: spans.enumerated().map { index, span in
            LiveGroundLineEvidence(segmentID: index, start: CGPoint(x: span.lowerBound, y: y),
                end: CGPoint(x: span.upperBound, y: y))
        })
    }

    private func image(dx: Int = 0, dy: Int = 0, pattern: Int = 0,
                       black: ClosedRange<Int>? = nil) throws -> CGImage {
        let width = 320, height = 120
        var pixels = [UInt8](repeating: 0, count: width * height)
        for row in 0..<height {
            for x in 0..<width {
                if black?.contains(x) == true { continue }
                let wx = x + dx, wy = height - row + dy
                if pattern == 1 { pixels[row * width + x] = 0 }
                else if pattern == 2 { pixels[row * width + x] = UInt8((wx % 4) * 50 + (wy % 3) * 10) }
                else {
                    var value = UInt64(wx + 10000) &* 0x9e3779b185ebca87
                    value ^= UInt64(wy + 10000) &* 0xc2b2ae3d27d4eb4f
                    value ^= value >> 33
                    value = value &* 0xff51afd7ed558ccd
                    pixels[row * width + x] = UInt8(truncatingIfNeeded: value >> 24)
                }
            }
        }
        let data = Data(pixels) as CFData
        let provider = try XCTUnwrap(CGDataProvider(data: data))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: 0), provider: provider, decode: nil,
            shouldInterpolate: false, intent: .defaultIntent))
    }

    func testMeasuredFloorRefinesBroadSceneryProposal() throws {
        let result = try XCTUnwrap(GroundPlaneClosureVerifier.verify(reference: image(),
            referenceGround: ground(y: 30), referencePose: CGPoint(x: 100, y: 200),
            current: image(dx: 20, dy: 10), currentGround: ground(y: 20),
            proposedPose: CGPoint(x: 123, y: 208), solveWidth: 320))
        XCTAssertEqual(result.cameraPose.x, 120, accuracy: 0.01)
        XCTAssertEqual(result.cameraPose.y, 210, accuracy: 0.01)
        XCTAssertGreaterThanOrEqual(result.supportedPatches, 4)
        XCTAssertGreaterThan(result.horizontalSpan, 64)
        XCTAssertGreaterThan(result.meanCorrelation, 0.99)
    }

    func testBackgroundDisplacementDoesNotEstablishGroundConnection() throws {
        XCTAssertNil(try GroundPlaneClosureVerifier.verify(reference: image(),
            referenceGround: ground(y: 30), referencePose: .zero,
            current: image(dx: 20, dy: 10), currentGround: ground(y: 20),
            proposedPose: CGPoint(x: 60, y: 10), solveWidth: 320))
    }

    func testBlackAndPeriodicFloorsAreNotUniqueEvidence() throws {
        for pattern in [1, 2] {
            XCTAssertNil(try GroundPlaneClosureVerifier.verify(reference: image(pattern: pattern),
                referenceGround: ground(y: 30), referencePose: .zero,
                current: image(dx: 20, dy: 10, pattern: pattern), currentGround: ground(y: 20),
                proposedPose: CGPoint(x: 20, y: 10), solveWidth: 320))
        }
    }

    func testObservedSpansExcludeMovingOccluder() throws {
        let result = try XCTUnwrap(GroundPlaneClosureVerifier.verify(reference: image(),
            referenceGround: ground(y: 30), referencePose: .zero,
            current: image(dx: 20, dy: 10, black: 100...200),
            currentGround: ground(y: 20, spans: [0...90, 210...320]),
            proposedPose: CGPoint(x: 22, y: 10), solveWidth: 320))
        XCTAssertEqual(result.cameraPose, CGPoint(x: 20, y: 10))
        XCTAssertGreaterThan(result.horizontalSpan, 150)
    }

    func testUnobservedOrWrongElevationFloorCannotValidateProposal() throws {
        for currentGround in [ground(y: 20, spans: []), ground(y: 50), ground(y: 20, spans: [0...20])] {
            XCTAssertNil(try GroundPlaneClosureVerifier.verify(reference: image(),
                referenceGround: ground(y: 30), referencePose: .zero,
                current: image(dx: 20, dy: 10), currentGround: currentGround,
                proposedPose: CGPoint(x: 20, y: 10), solveWidth: 320))
        }
    }

    func testSearchBoundaryDoesNotAcceptClippedOptimum() throws {
        XCTAssertNil(try GroundPlaneClosureVerifier.verify(reference: image(),
            referenceGround: ground(y: 30), referencePose: .zero,
            current: image(dx: 20, dy: 10), currentGround: ground(y: 20),
            proposedPose: CGPoint(x: 24, y: 10), solveWidth: 320))
    }
}
