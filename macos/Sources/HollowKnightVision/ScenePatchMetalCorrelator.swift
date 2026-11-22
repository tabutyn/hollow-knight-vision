import CoreGraphics
import Foundation
import Metal

/// Batched normalized correlation on the same GPU used by ground processing.
/// Only candidate scores run on Metal; ambiguity checks and subpixel refinement
/// remain shared with CPU. Failure returns nil and preserves the CPU fallback.
final class ScenePatchMetalCorrelator {
    static let shared = ScenePatchMetalCorrelator()
    typealias Score = (Double, Int, Int)
    private let lock = NSLock()
    private let device: MTLDevice?
    private let queue: MTLCommandQueue?
    private let pipeline: MTLComputePipelineState?
    private var buffers = [Int: MTLBuffer]()
    var available: Bool { pipeline != nil }

    private init() {
        let device = GroundMetalResources.shared.device
        self.device = device
        queue = device?.makeCommandQueue()
        let options = MTLCompileOptions()
        options.fastMathEnabled = false
        if let device, let library = try? device.makeLibrary(source: Self.source, options: options),
           let function = library.makeFunction(name: "sceneCorrelation") {
            pipeline = try? device.makeComputePipelineState(function: function)
        } else { pipeline = nil }
    }

    func scores(_ a: CalibratedSceneMotionTracker.Grid, _ b: CalibratedSceneMotionTracker.Grid,
                points: [CGPoint], near expected: CGVector?) -> [[Score]]? {
        guard let device, let queue, let pipeline, !points.isEmpty, points.count <= 80,
              a.width == b.width, a.height == b.height, a.width == 160, a.height >= 24,
              a.pixels.count == a.width * a.height, b.pixels.count == b.width * b.height,
              a.exclusions.count <= 128, b.exclusions.count <= 128,
              points.allSatisfy({ $0.x.isFinite && $0.y.isFinite && $0.x >= 0 && $0.x < CGFloat(a.width)
                  && $0.y >= 0 && $0.y < CGFloat(a.height) }) else { return nil }
        if let expected {
            guard expected.dx.isFinite, expected.dy.isFinite,
                  abs(expected.dx) <= CGFloat(a.width), abs(expected.dy) <= CGFloat(a.height) else { return nil }
        }
        return lock.withLock {
            let radiusX = expected == nil ? 8 : 2, radiusY = expected == nil ? 16 : 2
            let centerX = Int(expected?.dx.rounded() ?? 0), centerY = Int(expected?.dy.rounded() ?? 0)
            let candidateCount = (2 * radiusX + 1) * (2 * radiusY + 1)
            let count = candidateCount * points.count
            let settings = [UInt32(a.width), UInt32(a.height), UInt32(points.count), UInt32(candidateCount),
                            UInt32(a.exclusions.count), UInt32(b.exclusions.count)]
            let search = [Int32(centerX), Int32(centerY), Int32(radiusX), Int32(radiusY)]
            let centers = points.map { SIMD2<Int32>(Int32($0.x.rounded()), Int32($0.y.rounded())) }
            func masks(_ values: [CGRect]) -> [SIMD4<Float>] {
                values.map { SIMD4(Float($0.minX), Float($0.minY), Float($0.maxX), Float($0.maxY)) }
            }
            func input<T>(_ slot: Int, _ values: [T]) -> MTLBuffer? {
                values.withUnsafeBytes { raw in
                    let size = max(16, raw.count)
                    if buffers[slot].map({ $0.length < size }) ?? true {
                        buffers[slot] = device.makeBuffer(length: size, options: .storageModeShared)
                    }
                    guard let buffer = buffers[slot] else { return nil }
                    if let start = raw.baseAddress, raw.count > 0 { buffer.contents().copyMemory(from: start, byteCount: raw.count) }
                    return buffer
                }
            }
            guard let source = input(0, a.pixels.map(Float.init)), let target = input(1, b.pixels.map(Float.init)),
                  let pointBuffer = input(2, centers), let parameters = input(3, settings),
                  let searchBuffer = input(4, search), let sourceMasks = input(5, masks(a.exclusions)),
                  let targetMasks = input(6, masks(b.exclusions)),
                  let output = input(7, [Float](repeating: -2, count: count)),
                  let command = queue.makeCommandBuffer(), let encoder = command.makeComputeCommandEncoder() else { return nil }
            encoder.setComputePipelineState(pipeline)
            for (i, buffer) in [source, target, pointBuffer, parameters, searchBuffer, sourceMasks, targetMasks, output].enumerated() {
                encoder.setBuffer(buffer, offset: 0, index: i)
            }
            encoder.dispatchThreads(MTLSize(width: count, height: 1, depth: 1),
                threadsPerThreadgroup: MTLSize(width: min(128, pipeline.maxTotalThreadsPerThreadgroup), height: 1, depth: 1))
            encoder.endEncoding(); command.commit(); command.waitUntilCompleted()
            guard command.status == .completed else { return nil }
            let values = output.contents().bindMemory(to: Float.self, capacity: count)
            return points.indices.map { point in
                (0..<candidateCount).compactMap { index -> Score? in
                    let score = values[point * candidateCount + index]
                    guard score.isFinite, score >= -1.1 else { return nil }
                    return (Double(score), index % (2 * radiusX + 1) + centerX - radiusX,
                            index / (2 * radiusX + 1) + centerY - radiusY)
                }
            }
        }
    }

    private static let source = """
    #include <metal_stdlib>
    using namespace metal;
    bool masked(int x, int y, device const float4* masks, uint count) {
        for (uint i = 0; i < count; ++i) {
            float4 r = masks[i];
            if (float(x-8) < r.z && float(x+8) > r.x && float(y-6) < r.w && float(y+6) > r.y) return true;
        }
        return false;
    }
    kernel void sceneCorrelation(device const float* a [[buffer(0)]], device const float* b [[buffer(1)]],
        device const int2* points [[buffer(2)]], constant uint* p [[buffer(3)]], constant int* search [[buffer(4)]],
        device const float4* am [[buffer(5)]], device const float4* bm [[buffer(6)]],
        device float* output [[buffer(7)]], uint index [[thread_position_in_grid]]) {
        uint candidateCount = p[3];
        if (index >= p[2] * candidateCount) return;
        output[index] = -2;
        int2 point = points[index / candidateCount];
        int x = point.x, y = point.y, width = int(p[0]), height = int(p[1]);
        int candidate = int(index % candidateCount), columns = 2 * search[2] + 1;
        int dx = candidate % columns + search[0] - search[2];
        int dy = candidate / columns + search[1] - search[3];
        if (x < 9 || x >= width-9 || y < 7 || y >= height-7 ||
            x+dx < 8 || x+dx >= width-8 || y+dy < 6 || y+dy >= height-6 ||
            masked(x, y, am, p[4]) || masked(x+dx, y+dy, bm, p[5])) return;
        float sumA=0, sumB=0, aa=0, bb=0, ab=0;
        for (int row=-6; row<6; ++row) {
            for (int col=-8; col<8; ++col) {
                float av=a[(y+row)*width+x+col], bv=b[(y+row+dy)*width+x+col+dx];
                sumA+=av; sumB+=bv; aa+=av*av; bb+=bv*bv; ab+=av*bv;
            }
        }
        float varA=aa-sumA*sumA/192.0f, varB=bb-sumB*sumB/192.0f;
        if (varA < 768 || varB < 768) return;
        output[index]=(ab-sumA*sumB/192.0f)/sqrt(varA*varB);
    }
    """
}
