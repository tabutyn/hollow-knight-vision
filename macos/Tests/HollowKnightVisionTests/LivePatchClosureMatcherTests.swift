import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LivePatchClosureMatcherTests: XCTestCase {
    func testRecoversPixelDisplacementDespiteExposureAndForegroundOcclusion() throws {
        let source = try image()
        let current = try image(dx: 2, dy: -3, gain: 0.65, bias: 20, occlusion: true)
        let input = try reference(source, id: 0, pose: CGPoint(x: 100, y: 50))
        let matcher = LivePatchClosureMatcher()
        let match = try XCTUnwrap(matcher.match(frame: current, solveWidth: 640,
            excluding: [], references: [input], sourceProvider: { _ in source }))
        XCTAssertEqual(match.proposedCameraPose.x, 92, accuracy: 0.3)
        XCTAssertEqual(match.proposedCameraPose.y, 38, accuracy: 0.3)
        XCTAssertGreaterThanOrEqual(match.inlierReferenceFeatureIDs.count, 6)
        XCTAssertLessThanOrEqual(match.inlierReferenceFeatureIDs.count, 80)
        XCTAssertEqual(match.inlierCurrentFeatureIndices.count, match.inlierReferenceFeatureIDs.count)
        XCTAssertEqual(matcher.lastSourceLoadCount, 1)
        _ = matcher.match(frame: current, solveWidth: 640, excluding: [],
            references: [input], sourceProvider: { _ in XCTFail("Immutable pixels should be cached"); return nil })
        XCTAssertEqual(matcher.lastSourceLoadCount, 0)
        matcher.reset()
        XCTAssertEqual(matcher.cachedGridCount, 0)
    }

    func testRejectsAmbiguousReferenceLocationsAndReinforcesCoincidentViews() throws {
        let source = try image()
        let first = try reference(source, id: 0)
        let second = try reference(source, id: 1, pose: CGPoint(x: 200, y: 0))
        let matcher = LivePatchClosureMatcher()
        XCTAssertNil(matcher.match(frame: source, solveWidth: 640, excluding: [],
            references: [first, second], sourceProvider: { _ in source }))
        let agreeing = try reference(source, id: 2, pose: CGPoint(x: 0.1, y: 0))
        let match = try XCTUnwrap(matcher.match(frame: source, solveWidth: 640, excluding: [],
            references: [first, agreeing], sourceProvider: { _ in source }))
        XCTAssertEqual(match.ambiguityRatio, 0)
    }

    func testRejectsDarkMaskedRepeatedUnrelatedAndOutOfSearchViews() throws {
        let source = try image(), input = try reference(source, id: 0)
        let matcher = LivePatchClosureMatcher()
        for target in [try image(gain: 0, bias: 0), try image(seed: 777), try image(dx: 8)] {
            XCTAssertNil(matcher.match(frame: target, solveWidth: 640, excluding: [],
                references: [input], sourceProvider: { _ in source }))
        }
        XCTAssertNil(matcher.match(frame: source, solveWidth: 640,
            excluding: [CGRect(x: 0, y: 0, width: 640, height: 360)],
            references: [input], sourceProvider: { _ in source }))
        let periodic = try image(repeated: true)
        let repeated = try reference(periodic, id: 1)
        XCTAssertNil(matcher.match(frame: periodic, solveWidth: 640, excluding: [],
            references: [repeated], sourceProvider: { _ in periodic }))
        XCTAssertNil(matcher.match(frame: source, solveWidth: 320, excluding: [],
            references: [input], sourceProvider: { _ in source }))
    }

    func testSearchAndCacheStayBoundedAndChangedSourceIdentityReloads() throws {
        let source = try image()
        var inputs = try (0..<96).map { try reference(source, id: $0) }
        let matcher = LivePatchClosureMatcher()
        _ = matcher.match(frame: source, solveWidth: 640, excluding: [], references: inputs,
            sourceProvider: { _ in source })
        XCTAssertEqual(matcher.lastReferenceCount, 32)
        XCTAssertEqual(matcher.lastSourceLoadCount, 32)
        _ = matcher.match(frame: source, solveWidth: 640, excluding: [], references: Array(inputs[32..<64]),
            sourceProvider: { _ in source })
        _ = matcher.match(frame: source, solveWidth: 640, excluding: [], references: Array(inputs[64..<96]),
            sourceProvider: { _ in source })
        XCTAssertEqual(matcher.cachedGridCount, 64)
        inputs[95] = try reference(source, id: 95, timestamp: 999)
        _ = matcher.match(frame: source, solveWidth: 640, excluding: [], references: [inputs[95]],
            sourceProvider: { _ in source })
        XCTAssertEqual(matcher.lastSourceLoadCount, 1)
    }

    private func reference(_ image: CGImage, id: Int, pose: CGPoint = .zero,
                           timestamp: Double = 0) throws -> LiveWorldPatchReference {
        let identity = UUID(uuidString: String(format: "00000000-0000-0000-0000-%012llx", id))!
        let reference = try XCTUnwrap(GlobalFeatureMatcher().makeReference(id: identity,
            frame: image, cameraPose: pose, solveWidth: 640))
        let ground = LiveWorldGroundGeometry(lines: [.init(segmentID: 0,
            start: CGPoint(x: 0, y: 10), end: CGPoint(x: 640, y: 10))])
        let observation = LiveWorldObservation(id: id, sourceObservationID: id, timestamp: timestamp,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
            excludedRects: [], rawPose: pose, optimizedPose: pose, groundGeometry: ground)
        return LiveWorldPatchReference(observation: observation, reference: reference)
    }

    private func image(dx: Int = 0, dy: Int = 0, gain: Double = 1, bias: Double = 0,
                       occlusion: Bool = false, repeated: Bool = false, seed: UInt64 = 1) throws -> CGImage {
        let width = 640, height = 360
        var pixels = [UInt8](repeating: 255, count: width * height * 4)
        for y in 0..<height { for x in 0..<width {
            let sx = x / 4 - dx + 1000, sy = y / 4 - dy + 1000
            var value = UInt64(sx) &* 0x9e3779b185ebca87
            value ^= UInt64(sy) &* 0xc2b2ae3d27d4eb4f
            value ^= seed &* 0x165667b19e3779f9
            value ^= value >> 33; value = value &* 0xff51afd7ed558ccd
            let raw = repeated ? Double((sx % 4) * 40 + (sy % 4) * 20)
                : Double(UInt8(truncatingIfNeeded: value >> 24))
            let byte = occlusion && (240..<400).contains(x) ? 0
                : UInt8(clamping: Int((raw * gain + bias).rounded()))
            let index = (y * width + x) * 4
            pixels[index] = byte; pixels[index + 1] = byte; pixels[index + 2] = byte
        } }
        let data = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 32, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: data, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }
}
