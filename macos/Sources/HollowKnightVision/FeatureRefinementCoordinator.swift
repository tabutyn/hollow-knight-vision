import Foundation

/// Identity of a pose published by the registration owner.
struct FeatureRefinementRequest: Equatable {
    let generation: UInt64
    let timestamp: Double
}

/// Runs expensive persistent-landmark work behind a one-item latest queue.
/// Results return on `ownerQueue` in strictly increasing request order within
/// a capture generation. The caller may apply a valid global correction to a
/// newer camera pose, but must never use delayed work to stamp old pixels.
final class FeatureRefinementCoordinator<Output>: @unchecked Sendable {
    private struct Job {
        let request: FeatureRefinementRequest
        let work: () -> Output
        let receive: (Output) -> Void
    }

    private let workerQueue: DispatchQueue
    private let ownerQueue: DispatchQueue
    private let lock = NSLock()
    private var invalidBeforeGeneration: UInt64 = 0
    /// Landmark work mutates the tracker on its serial worker. Deliver its
    /// results in that same observation order, even if the display has moved
    /// on to a newer registration while a result was being calculated.
    private var lastDelivered: FeatureRefinementRequest?
    private var inFlight = false
    private var pending: Job?
    private var pendingReset: (() -> Void)?

    init(
        workerQueue: DispatchQueue = DispatchQueue(
            label: "com.ballroller.hollow-knight-vision.feature-refinement",
            qos: .utility
        ),
        ownerQueue: DispatchQueue
    ) {
        self.workerQueue = workerQueue
        self.ownerQueue = ownerQueue
    }

    /// Records that registration has advanced. Delivery deliberately does not
    /// require this exact request to remain current: a slow landmark solve may
    /// still provide a valid global correction for the current generation.
    func didPublishRegistration(_ request: FeatureRefinementRequest) {
        // Kept as an explicit registration boundary for callers. The worker
        // owns ordering; the correction policy owns whether a result can move
        // the current camera.
    }

    /// Keeps at most one queued refinement. A busy worker finishes its current
    /// item, then skips directly to the latest observation.
    func submit(
        _ request: FeatureRefinementRequest,
        work: @escaping () -> Output,
        receive: @escaping (Output) -> Void
    ) {
        lock.lock()
        guard request.generation >= invalidBeforeGeneration else {
            lock.unlock()
            return
        }
        let job = Job(request: request, work: work, receive: receive)
        if inFlight {
            pending = job
            lock.unlock()
            return
        }
        inFlight = true
        lock.unlock()
        run(job)
    }

    /// Invalidates both completed and queued corrections from an old capture
    /// generation. The caller owns tracker state and may reset it separately.
    func invalidate(before generation: UInt64) {
        lock.lock()
        invalidBeforeGeneration = max(invalidBeforeGeneration, generation)
        if lastDelivered?.generation ?? 0 < generation { lastDelivered = nil }
        if pending?.request.generation ?? 0 < generation { pending = nil }
        lock.unlock()
    }

    /// Serializes mutable tracker resets behind any active refinement. A reset
    /// is a barrier: retained newest work starts only after it completes.
    func submitReset(_ work: @escaping () -> Void) {
        lock.lock()
        pendingReset = work
        guard !inFlight else {
            lock.unlock()
            return
        }
        inFlight = true
        let reset = pendingReset
        pendingReset = nil
        lock.unlock()
        if let reset { runReset(reset) }
    }

    private func run(_ job: Job) {
        workerQueue.async { [weak self] in
            let output = job.work()
            self?.ownerQueue.async { [weak self] in
                guard let self else { return }
                self.deliver(job, output: output)
            }
        }
    }

    private func runReset(_ reset: @escaping () -> Void) {
        workerQueue.async { [weak self] in
            reset()
            self?.ownerQueue.async { [weak self] in
                self?.finishReset()
            }
        }
    }

    private func deliver(_ job: Job, output: Output) {
        lock.lock()
        let accepts = job.request.generation >= invalidBeforeGeneration
            && isMonotonicallyNewer(job.request, than: lastDelivered)
        if accepts { lastDelivered = job.request }
        lock.unlock()
        if accepts { job.receive(output) }

        lock.lock()
        if let reset = pendingReset {
            pendingReset = nil
            lock.unlock()
            runReset(reset)
            return
        }
        let next = pending
        pending = nil
        if next == nil { inFlight = false }
        lock.unlock()
        if let next { run(next) }
    }

    private func finishReset() {
        lock.lock()
        if let reset = pendingReset {
            pendingReset = nil
            lock.unlock()
            runReset(reset)
            return
        }
        let next = pending
        pending = nil
        if next == nil { inFlight = false }
        lock.unlock()
        if let next { run(next) }
    }

    private func isMonotonicallyNewer(
        _ request: FeatureRefinementRequest,
        than previous: FeatureRefinementRequest?
    ) -> Bool {
        guard let previous else { return true }
        if request.generation != previous.generation {
            return request.generation > previous.generation
        }
        return request.timestamp > previous.timestamp
    }
}
