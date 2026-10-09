import Foundation

/// Bounded timing samples. Recording is one lock and one ring write per stage;
/// percentile sorting happens only when the periodic status log asks for it.
final class FramePipelineTelemetry {
    enum Stage: CaseIterable {
        case captureDelivery
        case captureToStage
        case framePreparation
        case menuStencil
        case hudStencil
        case motionQueueWait
        case poseAge
        case motion
        case groundDetection
        case groundTracking
        case globalSearch
        case objectModel
        case objectPixelRefinement
        case objectTracking
        case renderQueueWait
        case render
        case mainQueueWait
        case captureToDraw
        case gpuQueueWait
        case gpuExecution
        case drawToPresent
        case captureToPresent
        case captureToPublish
    }
    struct Summary: Equatable { let count: Int; let p50Milliseconds: Double; let p95Milliseconds: Double }
    private let lock = NSLock()
    private let capacity: Int
    private var samples: [Stage: [Double]] = [:]
    private var nextIndex: [Stage: Int] = [:]

    init(capacity: Int = 120) { precondition(capacity > 0); self.capacity = capacity }

    func record(_ stage: Stage, seconds: Double) {
        guard seconds.isFinite, seconds >= 0 else { return }
        lock.lock()
        defer { lock.unlock() }
        var values = samples[stage, default: []]
        if values.count < capacity { values.append(seconds) }
        else {
            let index = nextIndex[stage, default: 0]
            values[index] = seconds
            nextIndex[stage] = (index + 1) % capacity
        }
        samples[stage] = values
    }

    func summary(for stage: Stage) -> Summary? {
        lock.lock()
        let values = samples[stage, default: []]
        lock.unlock()
        guard !values.isEmpty else { return nil }
        let ordered = values.sorted()
        func percentile(_ fraction: Double) -> Double {
            ordered[Int((Double(ordered.count - 1) * fraction).rounded())] * 1_000
        }
        return Summary(count: ordered.count, p50Milliseconds: percentile(0.50), p95Milliseconds: percentile(0.95))
    }
}
