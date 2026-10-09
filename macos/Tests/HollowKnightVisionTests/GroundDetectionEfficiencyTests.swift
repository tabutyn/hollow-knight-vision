import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundDetectionEfficiencyTests: XCTestCase {
    private func image(width: Int, height: Int) throws -> CGImage {
        let pixels = (0..<(width * height)).map { UInt8(truncatingIfNeeded: $0 &* 73 ^ ($0 / width) &* 131) }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(pixels) as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }

    // Deliberately retain the pre-optimization implementation as an independent
    // pixel-exact oracle and timing baseline. Never used by the app.
    private func referenceEdges(_ image: CGImage, excluding: [CGRect]) -> GroundEdgeAnalysis {
        let width = image.width, height = image.height
        var luma = [UInt8](repeating: 0, count: width * height)
        luma.withUnsafeMutableBytes { bytes in
            let context = CGContext(data: bytes.baseAddress, width: width, height: height,
                bitsPerComponent: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                bitmapInfo: CGImageAlphaInfo.none.rawValue)!
            context.interpolationQuality = .none
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
        let excluded = excluding.map { $0.insetBy(dx: -24, dy: -12) }
        var edges = [UInt8](repeating: 0, count: luma.count)
        for row in 0..<height {
            let frameY = CGFloat(height) - CGFloat(row) - 0.5
            let masks = excluded.filter { frameY >= $0.minY && frameY <= $0.maxY }
            for x in 0..<width {
                let frameX = CGFloat(x) + 0.5
                guard !masks.contains(where: { frameX >= $0.minX && frameX <= $0.maxX }),
                      row + 1 < height else { continue }
                edges[row * width + x] = UInt8(abs(Int(luma[row * width + x])
                    - Int(luma[(row + 1) * width + x])))
            }
        }
        return GroundEdgeAnalysis(width: width, height: height, groundEdgePixels: edges)
    }

    private func referenceKernel(_ input: GroundEdgeAnalysis) -> [UInt8] {
        let width = input.width, height = input.height
        var output = [UInt8](repeating: 0, count: width * height)
        guard width >= 32, height >= 8 else { return output }
        var sums = [Int32](repeating: 0, count: output.count)
        for row in 0..<height {
            let offset = row * width
            var sum = input.groundEdgePixels[offset..<(offset + 32)].reduce(0) { $0 + Int($1) }
            for x in 16...(width - 16) {
                sums[offset + x] = Int32(sum)
                if x < width - 16 {
                    sum -= Int(input.groundEdgePixels[offset + x - 16])
                    sum += Int(input.groundEdgePixels[offset + x + 16])
                }
            }
        }
        let weights: [Int32] = [-1, -2, -3, -4, 4, 3, 2, 1]
        var responses = [Int32](repeating: 0, count: output.count)
        for row in 4...(height - 4) {
            for x in 16...(width - 16) {
                var response: Int32 = 0
                for k in 0..<8 { response += weights[k] * sums[(row - 4 + k) * width + x] }
                if response > 0 {
                    responses[row * width + x] = response
                }
            }
        }
        for index in output.indices where responses[index] > 0 {
            output[index] = UInt8(clamping: Int(Int64(responses[index]) / 320))
        }
        return output
    }

    func testRasterizedMasksArePixelExactIncludingFractionalAndOffscreenRectangles() throws {
        for (width, height) in [(2, 2), (32, 8), (101, 77), (640, 360)] {
            let frame = try image(width: width, height: height)
            for masks: [CGRect] in [[], [.zero], [CGRect(x: -80, y: -30, width: 65, height: 47)],
                [CGRect(x: 37.5, y: 15.25, width: 10.1, height: 4.5),
                 CGRect(x: 45.8, y: 24.5, width: 22.5, height: 30.5)],
                [CGRect(x: 0, y: 0, width: width, height: height)]] {
                let actual = try XCTUnwrap(GroundLineDetector.analyze(frame, excluding: masks))
                XCTAssertEqual(actual.groundEdgePixels, referenceEdges(frame, excluding: masks).groundEdgePixels)
            }
        }
    }

    func testPairedKernelIsPixelExactForNoiseAndBorders() {
        for (width, height) in [(8, 4), (32, 8), (33, 9), (97, 67), (640, 360)] {
            let pixels = (0..<(width * height)).map { UInt8(truncatingIfNeeded: $0 &* 1237 ^ ($0 >> 3)) }
            let input = GroundEdgeAnalysis(width: width, height: height, groundEdgePixels: pixels)
            XCTAssertEqual(GroundLineDetector.groundDetectPixels(from: input), referenceKernel(input))
        }
    }

    func testSharedLineAnalysisPreservesLinesDebugPixelsAndReferenceDetection() throws {
        let frame = try image(width: 160, height: 100)
        let edges = try XCTUnwrap(GroundLineDetector.analyze(frame))
        let comparison = GroundLineDetector.compare(edges)
        let theory = try XCTUnwrap(GroundLineDetector.theory(from: comparison, tuning: .default))
        XCTAssertEqual(GroundLineDetector.cleanFloorLines(from: theory),
            GroundLineDetector.cleanFloorLines(from: comparison, tuning: .default))
        XCTAssertEqual(GroundLineDetector.detect(in: theory, searchFrameYRange: 0...100),
            GroundLineDetector.detect(in: comparison, searchFrameYRange: 0...100, tuning: .default))
        let shared = try XCTUnwrap(GroundLineDetector.groundStageImage(from: theory)?.dataProvider?.data)
        let original = try XCTUnwrap(GroundLineDetector.groundStageImage(from: comparison, tuning: .default)?.dataProvider?.data)
        XCTAssertEqual(shared as Data, original as Data)
    }

    func testReleaseEdgeAndKernelTimingAtCaptureResolution() throws {
        let frame = try image(width: 640, height: 360)
        let masks = [CGRect(x: 12, y: 300, width: 160, height: 45),
                     CGRect(x: 280, y: 65, width: 40, height: 50)]
        var oldTimes = [Double](), newTimes = [Double]()
        for _ in 0..<12 {
            var start = ProcessInfo.processInfo.systemUptime
            let old = referenceKernel(referenceEdges(frame, excluding: masks))
            oldTimes.append((ProcessInfo.processInfo.systemUptime - start) * 1_000)
            start = ProcessInfo.processInfo.systemUptime
            let edges = try XCTUnwrap(GroundLineDetector.analyze(frame, excluding: masks))
            let new = GroundLineDetector.groundDetectPixels(from: edges)
            newTimes.append((ProcessInfo.processInfo.systemUptime - start) * 1_000)
            XCTAssertEqual(old, new)
        }
        print("GROUND_EDGE_KERNEL_640x360_MS oldMedian=\(oldTimes.sorted()[6]) newMedian=\(newTimes.sorted()[6])")
    }
}
