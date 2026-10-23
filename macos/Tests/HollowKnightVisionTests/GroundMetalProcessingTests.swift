import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundMetalProcessingTests: XCTestCase {
    private func searchFixture(radius: Int) throws -> (
        sources: [GroundPatchSearchSource],
        pixels: [UInt8],
        candidates: [GroundPatchSearchCandidate]
    ) {
        let width = 640
        let height = 360
        let pixels = (0..<(width * height)).map {
            UInt8(truncatingIfNeeded: $0 &* 73 ^ ($0 / width) &* 131 ^ ($0 >> 5))
        }
        let sourceXs = Array(stride(from: 48, through: 560, by: 32))
        let sources = try sourceXs.map { x -> GroundPatchSearchSource in
            let values = try XCTUnwrap(GroundPixels.patch(
                pixels, width: width, height: height, x: x, y: 180
            ))
            return GroundPatchSearchSource(x: x, y: 180, pixels: values)
        }
        var candidates = [GroundPatchSearchCandidate]()
        for dy in -3...3 {
            for dx in (-radius)...radius {
                let samples = sources.indices.compactMap { index -> GroundPatchSearchSample? in
                    let x = sources[index].x + dx
                    let y = sources[index].y + dy
                    guard x >= 0, x + 16 <= width, y >= 0, y + 12 <= height else {
                        return nil
                    }
                    return GroundPatchSearchSample(
                        sourceIndex: index,
                        targetOffset: y * width + x
                    )
                }
                candidates.append(GroundPatchSearchCandidate(
                    dx: dx, dy: dy, samples: samples
                ))
            }
        }
        return (sources, pixels, candidates)
    }

    func testMetalGroundImageProcessingMatchesCPUExactly() throws {
        let width = 640
        let height = 360
        let luma = (0..<(width * height)).map {
            UInt8(truncatingIfNeeded: $0 &* 73 ^ ($0 / width) &* 131)
        }
        var validity = [UInt8](repeating: 1, count: luma.count)
        for row in 40...100 {
            validity.replaceSubrange(
                (row * width + 50)...(row * width + 170),
                with: repeatElement(0, count: 121)
            )
        }
        let processor = GroundImageProcessor()
        let cpu = try XCTUnwrap(processor.process(
            luma: luma, width: width, height: height,
            validSourcePixels: validity, validRows: nil, backend: .cpu
        ))
        let metal = try XCTUnwrap(processor.process(
            luma: luma, width: width, height: height,
            validSourcePixels: validity, validRows: nil, backend: .metal
        ))
        let automatic = try XCTUnwrap(processor.process(
            luma: luma, width: width, height: height,
            validSourcePixels: validity, validRows: nil, backend: .automatic
        ))
        XCTAssertEqual(metal.backend, .metal)
        XCTAssertEqual(automatic.backend, .cpu)
        XCTAssertEqual(automatic.groundEdges, cpu.groundEdges)
        XCTAssertEqual(automatic.groundPixels, cpu.groundPixels)
        XCTAssertEqual(metal.groundEdges, cpu.groundEdges)
        let differences = zip(metal.groundPixels, cpu.groundPixels).enumerated().filter {
            $0.element.0 != $0.element.1
        }
        XCTAssertTrue(
            differences.isEmpty,
            differences.prefix(12).map {
                "index=\($0.offset) metal=\($0.element.0) cpu=\($0.element.1)"
            }.joined(separator: " ")
        )
    }

    func testMetalPatchSearchMatchesCPUSelection() throws {
        let fixture = try searchFixture(radius: 96)
        let matcher = GroundPatchMatcher()
        let cpu = matcher.search(
            sources: fixture.sources,
            pixels: fixture.pixels,
            width: 640,
            height: 360,
            candidates: fixture.candidates,
            backend: .cpu
        )
        let metal = matcher.search(
            sources: fixture.sources,
            pixels: fixture.pixels,
            width: 640,
            height: 360,
            candidates: fixture.candidates,
            backend: .metal
        )
        XCTAssertEqual(metal.backend, .metal)
        XCTAssertEqual(metal.results.map { [$0.dx, $0.dy, $0.support] },
                       cpu.results.map { [$0.dx, $0.dy, $0.support] })
        XCTAssertEqual(metal.results.count, cpu.results.count)
        for (gpu, reference) in zip(metal.results, cpu.results) {
            XCTAssertEqual(gpu.error, reference.error, accuracy: 0.002)
        }
    }

    func testWiderMetalSearchRecoversMotionOutsideFormerRadius() throws {
        let width = 640
        let height = 200
        func pixels(dx: Int) -> [UInt8] {
            (0..<(width * height)).map { index in
                let x = index % width - dx
                let y = index / width
                guard x >= 0, x < width else { return 0 }
                var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663)
                hash ^= hash >> 13
                hash &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: hash >> 11)
            }
        }
        let line = [CleanFloorLine(row: 80, xRange: 16...623)]
        let former = GroundCameraSolver(
            matcherBackend: .cpu,
            maximumLocalSearchRadius: 96
        )
        let widened = GroundCameraSolver(
            matcherBackend: .metal,
            maximumLocalSearchRadius: 192
        )
        _ = former.solve(
            pixels: pixels(dx: 0), width: width, height: height,
            lines: line, timestamp: 0, exclusions: []
        )
        _ = widened.solve(
            pixels: pixels(dx: 0), width: width, height: height,
            lines: line, timestamp: 0, exclusions: []
        )
        XCTAssertNil(former.solve(
            pixels: pixels(dx: -140), width: width, height: height,
            lines: line, timestamp: 1, exclusions: []
        ))
        let recovered = try XCTUnwrap(widened.solve(
            pixels: pixels(dx: -140), width: width, height: height,
            lines: line, timestamp: 1, exclusions: []
        ))
        XCTAssertEqual(recovered.imageTranslation.dx, -140)
        XCTAssertEqual(widened.lastPatchSearchExecution?.backend, .metal)
    }

    func testMetalSolverMatchesCPUAcrossMotionSequence() throws {
        let width = 320
        let height = 180
        func pixels(dx: Int, dy: Int) -> [UInt8] {
            (0..<(width * height)).map { index in
                let x = index % width - dx
                let y = index / width - dy
                guard x >= 0, x < width, y >= 0, y < height else { return 0 }
                var hash = UInt64(x * 73_856_093) ^ UInt64(y * 19_349_663)
                hash ^= hash >> 13
                hash &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: hash >> 11)
            }
        }
        let cpu = GroundCameraSolver(matcherBackend: .cpu)
        let metal = GroundCameraSolver(matcherBackend: .metal)
        let positions = [
            (0, 0), (-4, 0), (-11, 2), (-19, 7), (-31, 12),
            (-46, 8), (-62, 1), (-40, -4), (-17, -1), (0, 0),
        ]
        for (index, position) in positions.enumerated() {
            let line = [CleanFloorLine(
                row: 70 + position.1,
                xRange: 16...303
            )]
            let timestamp = Double(index) / 30
            let cpuResult = cpu.solve(
                pixels: pixels(dx: position.0, dy: position.1),
                width: width, height: height, lines: line,
                timestamp: timestamp, exclusions: []
            )
            let metalResult = metal.solve(
                pixels: pixels(dx: position.0, dy: position.1),
                width: width, height: height, lines: line,
                timestamp: timestamp, exclusions: []
            )
            XCTAssertEqual(metalResult?.imageTranslation, cpuResult?.imageTranslation)
            XCTAssertEqual(metalResult?.support, cpuResult?.support)
            if let metalError = metalResult?.error, let cpuError = cpuResult?.error {
                XCTAssertEqual(metalError, cpuError, accuracy: 0.002)
            } else {
                XCTAssertEqual(metalResult == nil, cpuResult == nil)
            }
        }
    }

    func testReleaseMetalSearchCapacityBenchmark() throws {
        guard ProcessInfo.processInfo.environment["HKV_GPU_BENCHMARK"] == "1" else {
            throw XCTSkip("GPU capacity benchmark is opt-in")
        }
        let matcher = GroundPatchMatcher()
        let ordinary = try searchFixture(radius: 96)
        let wide = try searchFixture(radius: 192)
        _ = matcher.search(
            sources: ordinary.sources, pixels: ordinary.pixels,
            width: 640, height: 360, candidates: ordinary.candidates,
            backend: .metal
        )
        func times(
            fixture: (
                sources: [GroundPatchSearchSource],
                pixels: [UInt8],
                candidates: [GroundPatchSearchCandidate]
            ),
            backend: GroundComputeBackend,
            count: Int = 9
        ) -> [Double] {
            (0..<count).map { _ in
                matcher.search(
                    sources: fixture.sources,
                    pixels: fixture.pixels,
                    width: 640,
                    height: 360,
                    candidates: fixture.candidates,
                    backend: backend
                ).elapsed * 1_000
            }
        }
        let cpu = times(fixture: ordinary, backend: .cpu)
        let gpu = times(fixture: ordinary, backend: .metal)
        let wideGPU = times(fixture: wide, backend: .metal)
        let cpuMedian = cpu.sorted()[cpu.count / 2]
        let gpuMedian = gpu.sorted()[gpu.count / 2]
        let wideMedian = wideGPU.sorted()[wideGPU.count / 2]
        print(
            "GROUND_METAL_SEARCH_MS radius96 cpuMedian=\(cpuMedian) "
                + "metalMedian=\(gpuMedian) speedup=\(cpuMedian / gpuMedian) "
                + "radius192 metalMedian=\(wideMedian) "
                + "candidateGain=\(Double(wide.candidates.count) / Double(ordinary.candidates.count))"
        )
        XCTAssertLessThan(gpuMedian, cpuMedian)
    }

    func testReleaseMetalGroundImageBenchmark() throws {
        guard ProcessInfo.processInfo.environment["HKV_GPU_BENCHMARK"] == "1" else {
            throw XCTSkip("GPU ground-image benchmark is opt-in")
        }
        let width = 640
        let height = 360
        let luma = (0..<(width * height)).map {
            UInt8(truncatingIfNeeded: $0 &* 73 ^ ($0 / width) &* 131)
        }
        let validity = [UInt8](repeating: 1, count: luma.count)
        let processor = GroundImageProcessor()
        _ = processor.process(
            luma: luma, width: width, height: height,
            validSourcePixels: validity, validRows: nil, backend: .metal
        )
        func times(_ backend: GroundComputeBackend) -> [Double] {
            (0..<15).map { _ in
                processor.process(
                    luma: luma, width: width, height: height,
                    validSourcePixels: validity, validRows: nil, backend: backend
                )!.elapsed * 1_000
            }
        }
        let cpu = times(.cpu)
        let metal = times(.metal)
        let cpuMedian = cpu.sorted()[cpu.count / 2]
        let metalMedian = metal.sorted()[metal.count / 2]
        print(
            "GROUND_METAL_IMAGE_640x360_MS cpuMedian=\(cpuMedian) "
                + "metalMedian=\(metalMedian) speedup=\(cpuMedian / metalMedian)"
        )
        // This is a measurement rather than a performance gate. On the M4 the
        // small workload is near parity, so production intentionally selects
        // CPU for .automatic while retaining explicit Metal for profiling.
    }
}
