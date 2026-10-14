import CoreGraphics
import Foundation

/// Menu recognition can take longer than a display frame when it searches all
/// languages. Hacker already has an exact camera, so its capture path reads a
/// recent result while a bounded worker searches. Gameplay keeps exact-frame,
/// synchronous recognition. Call admission/reset/catalog methods on sampleQueue.
final class LiveMenuStencilWorker: @unchecked Sendable {
    struct Observation {
        let result: MenuStencilResult?
        let diagnostic: String?
    }

    struct Snapshot {
        let observation: Observation
        let timestamp: TimeInterval
    }

    private struct Frame {
        let image: CGImage
        let timestamp: TimeInterval
    }

    static let maximumResultAge: TimeInterval = 0.25
    private let queue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.menu-stencil", qos: .userInitiated
    )
    private let pump = LatestFramePump<Frame>()
    private let lock = NSLock()
    private var latest: Snapshot?
    private var generation: UInt64 = 0 // sampleQueue
    private var workerGeneration: UInt64 = 0 // queue
    private var lastProcessedTimestamp: TimeInterval? // queue
    private let recognize: (CGImage, TimeInterval) -> Observation
    private let resetTracker: () -> Void
    private let replaceTrackerCatalog: (MenuStencilCatalog) -> Void
    private let didProcess: (TimeInterval) -> Void

    init(
        recognize: @escaping (CGImage, TimeInterval) -> Observation,
        reset: @escaping () -> Void = {},
        replaceCatalog: @escaping (MenuStencilCatalog) -> Void = { _ in },
        didProcess: @escaping (TimeInterval) -> Void = { _ in }
    ) {
        self.recognize = recognize
        resetTracker = reset
        replaceTrackerCatalog = replaceCatalog
        self.didProcess = didProcess
    }

    convenience init(tracker: MenuStencilTracker, didProcess: @escaping (TimeInterval) -> Void) {
        self.init(
            recognize: { image, timestamp in
                Observation(result: tracker.observe(image, timestamp: timestamp),
                            diagnostic: tracker.selectorCalibrationDiagnostic)
            },
            reset: { tracker.reset(preservingLanguage: true) },
            replaceCatalog: { tracker.replaceCatalog($0) },
            didProcess: didProcess
        )
    }

    func observe(_ image: CGImage, timestamp: TimeInterval, asynchronously: Bool) -> Snapshot? {
        guard timestamp.isFinite else { return nil }
        let frame = Frame(image: image, timestamp: timestamp)
        if asynchronously {
            if let ticket = pump.submit(frame, epoch: generation) { enqueue(ticket) }
            return snapshot(at: timestamp)
        }
        pump.clearPending()
        return queue.sync {
            guard let result = process(frame) else { return nil }
            publish(result)
            return result
        }
    }

    func snapshot(at timestamp: TimeInterval) -> Snapshot? {
        lock.lock(); defer { lock.unlock() }
        guard let latest, timestamp.isFinite,
              timestamp >= latest.timestamp,
              timestamp - latest.timestamp <= Self.maximumResultAge else { return nil }
        return latest
    }

    func reset() { invalidate { self.resetTracker() } }

    func replaceCatalog(_ catalog: MenuStencilCatalog) {
        invalidate { self.replaceTrackerCatalog(catalog) }
    }

    private func invalidate(_ update: () -> Void) {
        generation &+= 1
        pump.invalidate(epoch: generation)
        queue.sync {
            workerGeneration = generation
            lastProcessedTimestamp = nil
            update()
            lock.lock()
            latest = nil
            lock.unlock()
        }
    }

    private func enqueue(_ ticket: LatestFramePump<Frame>.Ticket) {
        queue.async { [weak self] in
            guard let self else { return }
            let result = ticket.epoch == self.workerGeneration
                ? self.process(ticket.item) : nil
            let completion = self.pump.complete(ticket)
            if completion.shouldAcceptResult, let result { self.publish(result) }
            if let next = completion.next { self.enqueue(next) }
        }
    }

    private func process(_ frame: Frame) -> Snapshot? {
        // A synchronous Gameplay frame may overtake a queued Hacker scan.
        guard lastProcessedTimestamp.map({ frame.timestamp > $0 }) ?? true else { return nil }
        lastProcessedTimestamp = frame.timestamp
        let startedAt = ProcessInfo.processInfo.systemUptime
        let observation = recognize(frame.image, frame.timestamp)
        didProcess(ProcessInfo.processInfo.systemUptime - startedAt)
        return Snapshot(observation: observation, timestamp: frame.timestamp)
    }

    private func publish(_ result: Snapshot) {
        lock.lock()
        latest = result
        lock.unlock()
    }
}
