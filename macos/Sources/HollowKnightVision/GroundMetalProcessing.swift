import CoreGraphics
import CoreImage
import Foundation
import Metal

enum GroundComputeBackend: String, Equatable {
    case cpu
    case metal
    case automatic
}

struct GroundPatchSearchSource {
    let x: Int
    let y: Int
    let pixels: [UInt8]
    let mean: Float
    let energy: Float

    init(x: Int, y: Int, pixels: [UInt8]) {
        self.x = x
        self.y = y
        self.pixels = pixels
        var sum: Float = 0
        var squared: Float = 0
        for pixel in pixels {
            let value = Float(pixel)
            sum += value
            squared += value * value
        }
        let count = Float(max(1, pixels.count))
        mean = sum / count
        energy = max(0, squared / count - mean * mean)
    }
}

struct GroundPatchSearchSample {
    let sourceIndex: Int
    /// Nil represents the fixed error used when a target falls outside every
    /// currently detected ground row. Missing/out-of-bounds targets are not
    /// added to a candidate at all, matching the production CPU search.
    let targetOffset: Int?
}

struct GroundPatchSearchCandidate {
    let dx: Int
    let dy: Int
    let samples: [GroundPatchSearchSample]
}

struct GroundPatchSearchResult: Equatable {
    let dx: Int
    let dy: Int
    let error: CGFloat
    let support: Int
}

struct GroundPatchSearchExecution {
    let results: [GroundPatchSearchResult]
    let backend: GroundComputeBackend
    let candidateCount: Int
    let patchComparisonCount: Int
    let elapsed: TimeInterval
}

/// Shared Apple-silicon compute resources. Core Image and the optional Metal
/// ground processors use the same physical device so unified-memory buffers do
/// not bounce between independent GPU contexts.
final class GroundMetalResources {
    static let shared = GroundMetalResources()

    let device: MTLDevice?

    private init() {
        device = MTLCreateSystemDefaultDevice()
    }

    func makeImageContext() -> CIContext {
        guard let device else {
            return CIContext(options: [.cacheIntermediates: false])
        }
        return CIContext(
            mtlDevice: device,
            options: [.cacheIntermediates: false]
        )
    }
}

private final class GroundMetalBufferPool {
    private let device: MTLDevice
    private var buffers = [String: MTLBuffer]()

    init(device: MTLDevice) {
        self.device = device
    }

    func sharedBuffer<T>(name: String, values: [T]) -> MTLBuffer? {
        let length = max(1, values.withUnsafeBytes { $0.count })
        guard let buffer = buffer(
            name: name,
            length: length,
            options: .storageModeShared
        ) else { return nil }
        if !values.isEmpty {
            values.withUnsafeBytes { source in
                UnsafeMutableRawBufferPointer(
                    start: buffer.contents(),
                    count: source.count
                ).copyMemory(from: source)
            }
        }
        return buffer
    }

    func buffer(
        name: String,
        length: Int,
        options: MTLResourceOptions
    ) -> MTLBuffer? {
        if let existing = buffers[name], existing.length >= length {
            return existing
        }
        guard let created = device.makeBuffer(
            length: max(1, length),
            options: options
        ) else { return nil }
        buffers[name] = created
        return created
    }
}

final class GroundPatchMatcher {
    static let shared = GroundPatchMatcher()

    private let metal: GroundMetalPatchMatcher?

    var metalAvailable: Bool { metal != nil }

    init(device: MTLDevice? = GroundMetalResources.shared.device) {
        metal = device.flatMap(GroundMetalPatchMatcher.init)
    }

    func search(
        sources: [GroundPatchSearchSource],
        pixels: [UInt8],
        width: Int,
        height: Int,
        candidates: [GroundPatchSearchCandidate],
        backend requestedBackend: GroundComputeBackend
    ) -> GroundPatchSearchExecution {
        let candidateCount = candidates.count
        let comparisonCount = candidates.reduce(0) { $0 + $1.samples.count }
        let started = ProcessInfo.processInfo.systemUptime
        let selected: GroundComputeBackend
        let results: [GroundPatchSearchResult]
        switch requestedBackend {
        case .cpu:
            selected = .cpu
            results = Self.cpuSearch(
                sources: sources,
                pixels: pixels,
                width: width,
                height: height,
                candidates: candidates
            )
        case .metal, .automatic:
            if let metalResults = metal?.search(
                sources: sources,
                pixels: pixels,
                width: width,
                height: height,
                candidates: candidates
            ) {
                selected = .metal
                results = metalResults
            } else {
                selected = .cpu
                results = Self.cpuSearch(
                    sources: sources,
                    pixels: pixels,
                    width: width,
                    height: height,
                    candidates: candidates
                )
            }
        }
        return GroundPatchSearchExecution(
            results: results,
            backend: selected,
            candidateCount: candidateCount,
            patchComparisonCount: comparisonCount,
            elapsed: ProcessInfo.processInfo.systemUptime - started
        )
    }

    private static func cpuSearch(
        sources: [GroundPatchSearchSource],
        pixels: [UInt8],
        width: Int,
        height: Int,
        candidates: [GroundPatchSearchCandidate]
    ) -> [GroundPatchSearchResult] {
        guard width > 0, height > 0, pixels.count >= width * height else { return [] }
        var targetCache = [Int: (pixels: [UInt8], mean: Float, energy: Float)]()
        return candidates.compactMap { candidate in
            var count = 0
            var support = 0
            var minimumInlierX = Int.max
            var maximumInlierX = Int.min
            var cost: Float = 0
            for sample in candidate.samples {
                guard sources.indices.contains(sample.sourceIndex) else { continue }
                let source = sources[sample.sourceIndex]
                let error: Float
                if let offset = sample.targetOffset {
                    let target: (pixels: [UInt8], mean: Float, energy: Float)
                    if let cached = targetCache[offset] {
                        target = cached
                    } else if let sampled = targetPatch(
                        offset: offset,
                        pixels: pixels,
                        width: width,
                        height: height
                    ) {
                        target = sampled
                        targetCache[offset] = sampled
                    } else {
                        continue
                    }
                    error = patchError(source: source, target: target)
                } else {
                    error = 32
                }
                count += 1
                cost += min(32, error)
                if error <= 18 {
                    support += 1
                    minimumInlierX = min(minimumInlierX, source.x)
                    maximumInlierX = max(maximumInlierX, source.x)
                }
            }
            guard count >= 3, support >= 3, support * 2 > count,
                  maximumInlierX - minimumInlierX >= 32 else { return nil }
            return GroundPatchSearchResult(
                dx: candidate.dx,
                dy: candidate.dy,
                error: CGFloat(cost / Float(count)),
                support: support
            )
        }
    }

    private static func targetPatch(
        offset targetOffset: Int,
        pixels: [UInt8],
        width: Int,
        height: Int
    ) -> (pixels: [UInt8], mean: Float, energy: Float)? {
        guard targetOffset >= 0,
              targetOffset + 11 * width + 15 < pixels.count,
              targetOffset / width + 12 <= height else { return nil }
        var target = [UInt8]()
        target.reserveCapacity(192)
        var sum: Float = 0
        var squared: Float = 0
        for row in 0..<12 {
            let offset = targetOffset + row * width
            for column in 0..<16 {
                let pixel = pixels[offset + column]
                target.append(pixel)
                let value = Float(pixel)
                sum += value
                squared += value * value
            }
        }
        let mean = sum / 192
        let energy = max(0, squared / 192 - mean * mean)
        return (target, mean, energy)
    }

    private static func patchError(
        source: GroundPatchSearchSource,
        target: (pixels: [UInt8], mean: Float, energy: Float)
    ) -> Float {
        guard source.pixels.count == target.pixels.count,
              target.energy >= max(1, source.energy * 0.1) else { return .infinity }
        let gain = min(2, max(0.5, sqrt(source.energy / target.energy)))
        var error: Float = 0
        for index in source.pixels.indices {
            error += abs(
                Float(source.pixels[index]) - source.mean
                    - (Float(target.pixels[index]) - target.mean) * gain
            )
        }
        return error / 192
    }
}

private final class GroundMetalPatchMatcher {
    private let device: MTLDevice
    private let queue: MTLCommandQueue
    private let pipeline: MTLComputePipelineState
    private let lock = NSLock()
    private let buffers: GroundMetalBufferPool

    init?(_ device: MTLDevice) {
        self.device = device
        guard let queue = device.makeCommandQueue(),
              let library = try? device.makeLibrary(source: Self.source, options: nil),
              let function = library.makeFunction(name: "scoreGroundCandidates"),
              let pipeline = try? device.makeComputePipelineState(function: function)
        else { return nil }
        self.queue = queue
        self.pipeline = pipeline
        buffers = GroundMetalBufferPool(device: device)
    }

    func search(
        sources: [GroundPatchSearchSource],
        pixels: [UInt8],
        width: Int,
        height: Int,
        candidates: [GroundPatchSearchCandidate]
    ) -> [GroundPatchSearchResult]? {
        guard !sources.isEmpty, !candidates.isEmpty,
              pixels.count >= width * height else { return [] }
        lock.lock()
        defer { lock.unlock() }

        var sourceMetadata = [SIMD4<Int32>]()
        var sourceStatistics = [SIMD2<Float>]()
        var sourcePixels = [UInt8]()
        for source in sources {
            guard source.pixels.count == 192 else { return nil }
            let offset = sourcePixels.count
            sourceMetadata.append(SIMD4(Int32(source.x), Int32(source.y), Int32(offset), 0))
            sourceStatistics.append(SIMD2(source.mean, source.energy))
            sourcePixels.append(contentsOf: source.pixels)
        }

        var candidateMetadata = [SIMD4<Int32>]()
        var sampleMetadata = [SIMD4<Int32>]()
        for candidate in candidates {
            let sampleOffset = sampleMetadata.count
            for sample in candidate.samples {
                sampleMetadata.append(SIMD4(
                    Int32(sample.sourceIndex),
                    Int32(sample.targetOffset ?? -1),
                    0,
                    0
                ))
            }
            candidateMetadata.append(SIMD4(
                Int32(candidate.dx),
                Int32(candidate.dy),
                Int32(sampleOffset),
                Int32(candidate.samples.count)
            ))
        }

        guard let lumaBuffer = buffers.sharedBuffer(name: "match-luma", values: pixels),
              let sourcePixelBuffer = buffers.sharedBuffer(
                name: "match-source-pixels", values: sourcePixels
              ),
              let sourceMetadataBuffer = buffers.sharedBuffer(
                name: "match-source-metadata", values: sourceMetadata
              ),
              let sourceStatisticsBuffer = buffers.sharedBuffer(
                name: "match-source-statistics", values: sourceStatistics
              ),
              let candidateBuffer = buffers.sharedBuffer(
                name: "match-candidates", values: candidateMetadata
              ),
              let sampleBuffer = buffers.sharedBuffer(
                name: "match-samples", values: sampleMetadata
              ),
              let output = buffers.buffer(
                name: "match-output",
                length: MemoryLayout<SIMD2<Float>>.stride * candidates.count,
                options: .storageModeShared
              ),
              let command = queue.makeCommandBuffer(),
              let encoder = command.makeComputeCommandEncoder()
        else { return nil }
        var dimensions = SIMD2<UInt32>(UInt32(width), UInt32(height))
        encoder.setComputePipelineState(pipeline)
        encoder.setBuffer(lumaBuffer, offset: 0, index: 0)
        encoder.setBuffer(sourcePixelBuffer, offset: 0, index: 1)
        encoder.setBuffer(sourceMetadataBuffer, offset: 0, index: 2)
        encoder.setBuffer(sourceStatisticsBuffer, offset: 0, index: 3)
        encoder.setBuffer(candidateBuffer, offset: 0, index: 4)
        encoder.setBuffer(sampleBuffer, offset: 0, index: 5)
        encoder.setBuffer(output, offset: 0, index: 6)
        encoder.setBytes(&dimensions, length: MemoryLayout<SIMD2<UInt32>>.stride, index: 7)
        dispatch(encoder, count: candidates.count, pipeline: pipeline)
        encoder.endEncoding()
        command.commit()
        command.waitUntilCompleted()
        guard command.status == .completed else { return nil }

        let values = output.contents().bindMemory(
            to: SIMD2<Float>.self,
            capacity: candidates.count
        )
        var results = [GroundPatchSearchResult]()
        results.reserveCapacity(candidates.count)
        for index in candidates.indices {
            let value = values[index]
            guard value.x >= 0 else { continue }
            results.append(GroundPatchSearchResult(
                dx: candidates[index].dx,
                dy: candidates[index].dy,
                error: CGFloat(value.x),
                support: Int(value.y.rounded())
            ))
        }
        return results
    }

    private func dispatch(
        _ encoder: MTLComputeCommandEncoder,
        count: Int,
        pipeline: MTLComputePipelineState
    ) {
        let width = min(pipeline.maxTotalThreadsPerThreadgroup, 256)
        encoder.dispatchThreads(
            MTLSize(width: count, height: 1, depth: 1),
            threadsPerThreadgroup: MTLSize(width: width, height: 1, depth: 1)
        )
    }

    private static let source = #"""
    #include <metal_stdlib>
    using namespace metal;

    kernel void scoreGroundCandidates(
        const device uchar *luma [[buffer(0)]],
        const device uchar *sourcePixels [[buffer(1)]],
        const device int4 *sourceMetadata [[buffer(2)]],
        const device float2 *sourceStatistics [[buffer(3)]],
        const device int4 *candidates [[buffer(4)]],
        const device int4 *samples [[buffer(5)]],
        device float2 *output [[buffer(6)]],
        constant uint2 &dimensions [[buffer(7)]],
        uint candidateIndex [[thread_position_in_grid]]) {
        int4 candidate = candidates[candidateIndex];
        int count = 0;
        int support = 0;
        int minimumX = 2147483647;
        int maximumX = -2147483647;
        float cost = 0.0f;
        for (int sampleIndex = 0; sampleIndex < candidate.w; ++sampleIndex) {
            int4 sample = samples[candidate.z + sampleIndex];
            int sourceIndex = sample.x;
            int4 metadata = sourceMetadata[sourceIndex];
            float2 statistics = sourceStatistics[sourceIndex];
            float error = 32.0f;
            if (sample.y >= 0) {
                float sum = 0.0f;
                float squared = 0.0f;
                for (int row = 0; row < 12; ++row) {
                    int target = sample.y + row * int(dimensions.x);
                    for (int column = 0; column < 16; ++column) {
                        float value = float(luma[target + column]);
                        sum += value;
                        squared += value * value;
                    }
                }
                float mean = sum / 192.0f;
                float energy = max(0.0f, squared / 192.0f - mean * mean);
                if (energy >= max(1.0f, statistics.y * 0.1f)) {
                    float gain = clamp(sqrt(statistics.y / energy), 0.5f, 2.0f);
                    float total = 0.0f;
                    int sourceOffset = metadata.z;
                    int sourcePixel = 0;
                    for (int row = 0; row < 12; ++row) {
                        int target = sample.y + row * int(dimensions.x);
                        for (int column = 0; column < 16; ++column) {
                            total += abs(
                                float(sourcePixels[sourceOffset + sourcePixel]) - statistics.x
                                    - (float(luma[target + column]) - mean) * gain
                            );
                            sourcePixel += 1;
                        }
                    }
                    error = total / 192.0f;
                } else {
                    error = INFINITY;
                }
            }
            count += 1;
            cost += min(32.0f, error);
            if (error <= 18.0f) {
                support += 1;
                minimumX = min(minimumX, metadata.x);
                maximumX = max(maximumX, metadata.x);
            }
        }
        bool valid = count >= 3 && support >= 3 && support * 2 > count
            && maximumX - minimumX >= 32;
        output[candidateIndex] = valid
            ? float2(cost / float(count), float(support))
            : float2(-1.0f, 0.0f);
    }
    """#
}

struct GroundImageProcessingResult {
    let groundEdges: [UInt8]
    let groundPixels: [UInt8]
    let backend: GroundComputeBackend
    let elapsed: TimeInterval
}

final class GroundImageProcessor {
    static let shared = GroundImageProcessor()

    private let metal: GroundMetalImageProcessor?

    var metalAvailable: Bool { metal != nil }

    init(device: MTLDevice? = GroundMetalResources.shared.device) {
        metal = device.flatMap(GroundMetalImageProcessor.init)
    }

    func process(
        luma: [UInt8],
        width: Int,
        height: Int,
        validSourcePixels: [UInt8],
        validRows: Range<Int>?,
        backend requestedBackend: GroundComputeBackend = .automatic
    ) -> GroundImageProcessingResult? {
        let started = ProcessInfo.processInfo.systemUptime
        // Repeated M4 measurements put this small preprocessing workload at
        // parity with Metal once dispatch and synchronization are included.
        // Automatic therefore keeps the lower-variance CPU path. Explicit
        // Metal remains available for parity checks and future profiling.
        if requestedBackend == .metal,
           let value = metal?.process(
            luma: luma,
            width: width,
            height: height,
            validSourcePixels: validSourcePixels,
            validRows: validRows
           ) {
            return GroundImageProcessingResult(
                groundEdges: value.0,
                groundPixels: value.1,
                backend: .metal,
                elapsed: ProcessInfo.processInfo.systemUptime - started
            )
        }
        guard luma.count == width * height,
              validSourcePixels.count == luma.count else { return nil }
        var edges = [UInt8](repeating: 0, count: luma.count)
        if height > 1 {
            for index in 0..<(width * (height - 1)) where validSourcePixels[index] != 0 {
                edges[index] = UInt8(abs(Int(luma[index]) - Int(luma[index + width])))
            }
        }
        let analysis = GroundEdgeAnalysis(
            width: width,
            height: height,
            groundEdgePixels: edges,
            sourceLuma: luma,
            validSourcePixels: validSourcePixels,
            validRows: validRows
        )
        return GroundImageProcessingResult(
            groundEdges: edges,
            groundPixels: GroundLineDetector.cpuGroundDetectPixels(from: analysis),
            backend: .cpu,
            elapsed: ProcessInfo.processInfo.systemUptime - started
        )
    }
}

private final class GroundMetalImageProcessor {
    private let device: MTLDevice
    private let queue: MTLCommandQueue
    private let edgePipeline: MTLComputePipelineState
    private let sumPipeline: MTLComputePipelineState
    private let responsePipeline: MTLComputePipelineState
    private let lock = NSLock()
    private let buffers: GroundMetalBufferPool

    init?(_ device: MTLDevice) {
        self.device = device
        guard let queue = device.makeCommandQueue(),
              let library = try? device.makeLibrary(source: Self.source, options: nil),
              let edge = library.makeFunction(name: "groundEdges"),
              let sums = library.makeFunction(name: "groundHorizontalSums"),
              let response = library.makeFunction(name: "groundResponses"),
              let edgePipeline = try? device.makeComputePipelineState(function: edge),
              let sumPipeline = try? device.makeComputePipelineState(function: sums),
              let responsePipeline = try? device.makeComputePipelineState(function: response)
        else { return nil }
        self.queue = queue
        self.edgePipeline = edgePipeline
        self.sumPipeline = sumPipeline
        self.responsePipeline = responsePipeline
        buffers = GroundMetalBufferPool(device: device)
    }

    func process(
        luma: [UInt8],
        width: Int,
        height: Int,
        validSourcePixels: [UInt8],
        validRows: Range<Int>?
    ) -> ([UInt8], [UInt8])? {
        let count = width * height
        guard count > 0, luma.count == count, validSourcePixels.count == count else { return nil }
        lock.lock()
        defer { lock.unlock() }
        guard let lumaBuffer = buffers.sharedBuffer(name: "image-luma", values: luma),
              let validityBuffer = buffers.sharedBuffer(
                name: "image-validity", values: validSourcePixels
              ),
              let edgeBuffer = buffers.buffer(
                name: "image-edges", length: count, options: .storageModeShared
              ),
              let sumsBuffer = buffers.buffer(
                name: "image-sums",
                length: count * MemoryLayout<Int32>.stride,
                options: .storageModePrivate
              ),
              let responseBuffer = buffers.buffer(
                name: "image-response", length: count, options: .storageModeShared
              ),
              let command = queue.makeCommandBuffer()
        else { return nil }
        var parameters = SIMD4<UInt32>(
            UInt32(width),
            UInt32(height),
            UInt32(validRows?.lowerBound ?? 0),
            UInt32(validRows?.upperBound ?? 0)
        )
        encode(
            command: command,
            pipeline: edgePipeline,
            buffers: [lumaBuffer, validityBuffer, edgeBuffer],
            parameters: &parameters,
            count: count
        )
        encode(
            command: command,
            pipeline: sumPipeline,
            buffers: [edgeBuffer, sumsBuffer],
            parameters: &parameters,
            count: count
        )
        encode(
            command: command,
            pipeline: responsePipeline,
            buffers: [sumsBuffer, responseBuffer],
            parameters: &parameters,
            count: count
        )
        command.commit()
        command.waitUntilCompleted()
        guard command.status == .completed else { return nil }
        return (
            Array(UnsafeBufferPointer(
                start: edgeBuffer.contents().bindMemory(to: UInt8.self, capacity: count),
                count: count
            )),
            Array(UnsafeBufferPointer(
                start: responseBuffer.contents().bindMemory(to: UInt8.self, capacity: count),
                count: count
            ))
        )
    }

    private func encode(
        command: MTLCommandBuffer,
        pipeline: MTLComputePipelineState,
        buffers: [MTLBuffer],
        parameters: inout SIMD4<UInt32>,
        count: Int
    ) {
        guard let encoder = command.makeComputeCommandEncoder() else { return }
        encoder.setComputePipelineState(pipeline)
        for (index, buffer) in buffers.enumerated() {
            encoder.setBuffer(buffer, offset: 0, index: index)
        }
        encoder.setBytes(
            &parameters,
            length: MemoryLayout<SIMD4<UInt32>>.stride,
            index: buffers.count
        )
        let width = min(pipeline.maxTotalThreadsPerThreadgroup, 256)
        encoder.dispatchThreads(
            MTLSize(width: count, height: 1, depth: 1),
            threadsPerThreadgroup: MTLSize(width: width, height: 1, depth: 1)
        )
        encoder.endEncoding()
    }

    private static let source = #"""
    #include <metal_stdlib>
    using namespace metal;

    kernel void groundEdges(
        const device uchar *luma [[buffer(0)]],
        const device uchar *validity [[buffer(1)]],
        device uchar *edges [[buffer(2)]],
        constant uint4 &p [[buffer(3)]],
        uint index [[thread_position_in_grid]]) {
        uint count = p.x * p.y;
        if (index >= count) return;
        uint row = index / p.x;
        edges[index] = row + 1 < p.y && validity[index] != 0
            ? uchar(abs(int(luma[index]) - int(luma[index + p.x])))
            : uchar(0);
    }

    kernel void groundHorizontalSums(
        const device uchar *edges [[buffer(0)]],
        device int *sums [[buffer(1)]],
        constant uint4 &p [[buffer(2)]],
        uint index [[thread_position_in_grid]]) {
        uint count = p.x * p.y;
        if (index >= count) return;
        uint x = index % p.x;
        uint row = index / p.x;
        if (x < 16 || x > p.x - 16) {
            sums[index] = 0;
            return;
        }
        int value = 0;
        uint start = row * p.x + x - 16;
        for (uint column = 0; column < 32; ++column) value += int(edges[start + column]);
        sums[index] = value;
    }

    kernel void groundResponses(
        const device int *sums [[buffer(0)]],
        device uchar *output [[buffer(1)]],
        constant uint4 &p [[buffer(2)]],
        uint index [[thread_position_in_grid]]) {
        uint count = p.x * p.y;
        if (index >= count) return;
        uint x = index % p.x;
        uint row = index / p.x;
        if (x < 16 || x > p.x - 16 || row < 4 || row > p.y - 4
            || (p.w != 0 && (row - 4 < p.z || row + 4 >= p.w))) {
            output[index] = uchar(0);
            return;
        }
        int a = sums[(row - 4) * p.x + x];
        int b = sums[(row - 3) * p.x + x];
        int c = sums[(row - 2) * p.x + x];
        int d = sums[(row - 1) * p.x + x];
        int e = sums[row * p.x + x];
        int f = sums[(row + 1) * p.x + x];
        int g = sums[(row + 2) * p.x + x];
        int h = sums[(row + 3) * p.x + x];
        int response = (e - d) * 4 + (f - c) * 3 + (g - b) * 2 + h - a;
        output[index] = response > 0 ? uchar(clamp(response / 320, 0, 255)) : uchar(0);
    }
    """#
}
