import Foundation

/// A thread-safe, bounded handoff from a fast frame producer to a single
/// expensive worker. It holds one active ticket and at most one latest pending
/// ticket, so slow processing cannot grow a frame backlog.
final class LatestFramePump<Item> {
    struct Ticket {
        let id: UInt64
        let epoch: UInt64
        let item: Item
    }

    struct Completion {
        /// True only when this ticket still belongs to the current epoch.
        let shouldAcceptResult: Bool
        /// The next ticket to start immediately on the worker, if any.
        let next: Ticket?
    }

    /// Lifetime counters, read atomically. Epoch changes do not erase
    /// evidence of overloaded workers or intentionally cancelled work.
    struct Statistics: Equatable {
        var submitted: UInt64 = 0
        var started: UInt64 = 0
        var completed: UInt64 = 0
        var superseded: UInt64 = 0
        var cancelled: UInt64 = 0
        var staleSubmissions: UInt64 = 0
        var staleCompletions: UInt64 = 0

        var logDescription: String {
            "submitted=\(submitted),started=\(started),completed=\(completed),superseded=\(superseded),cancelled=\(cancelled),staleSubmitted=\(staleSubmissions),staleCompleted=\(staleCompletions)"
        }
    }
    private var counters = Statistics()
    var statistics: Statistics {
        lock.lock()
        defer { lock.unlock() }
        return counters
    }

    private let lock = NSLock()
    private var currentEpoch: UInt64
    private var nextID: UInt64 = 0
    private var active: Ticket?
    private var pending: Ticket?
    private let discard: ((Item) -> Void)?

    init(epoch: UInt64 = 0, discard: ((Item) -> Void)? = nil) {
        currentEpoch = epoch
        self.discard = discard
    }

    /// Submits a frame for the current epoch. The first frame returns a ticket
    /// to start; while work is active, this replaces the pending frame.
    func submit(_ item: Item, epoch: UInt64) -> Ticket? {
        lock.lock()
        defer { lock.unlock() }
        guard epoch == currentEpoch else {
            counters.staleSubmissions &+= 1
            return nil
        }
        counters.submitted &+= 1
        let ticket = makeTicket(item: item, epoch: epoch)
        guard active != nil else {
            active = ticket
            counters.started &+= 1
            return ticket
        }
        if let pending {
            counters.superseded &+= 1
            discard?(pending.item)
        }
        pending = ticket
        return nil
    }

    /// Finishes exactly one active ticket. The caller may commit its result
    /// only when `shouldAcceptResult` is true, then starts `next` if present.
    func complete(_ ticket: Ticket) -> Completion {
        lock.lock()
        defer { lock.unlock() }
        guard active?.id == ticket.id else {
            return Completion(shouldAcceptResult: false, next: nil)
        }
        let shouldAcceptResult = ticket.epoch == currentEpoch
        counters.completed &+= 1
        if !shouldAcceptResult { counters.staleCompletions &+= 1 }
        active = nil
        guard let pending, pending.epoch == currentEpoch else {
            if let pending { counters.cancelled &+= 1; discard?(pending.item) }
            self.pending = nil
            return Completion(shouldAcceptResult: shouldAcceptResult, next: nil)
        }
        self.pending = nil
        active = pending
        counters.started &+= 1
        return Completion(shouldAcceptResult: shouldAcceptResult, next: pending)
    }

    /// Starts a new generation. It drops pending work and prevents an older
    /// active ticket from being committed when it eventually finishes.
    func invalidate(epoch: UInt64) {
        lock.lock()
        defer { lock.unlock() }
        guard epoch >= currentEpoch else { return }
        currentEpoch = epoch
        if let pending { counters.cancelled &+= 1; discard?(pending.item) }
        pending = nil
    }

    /// Drops queued work while allowing the active ticket to complete normally.
    func clearPending() {
        lock.lock()
        defer { lock.unlock() }
        if let pending { counters.cancelled &+= 1; discard?(pending.item) }
        pending = nil
    }

    var isProcessing: Bool {
        lock.lock()
        defer { lock.unlock() }
        return active != nil
    }

    var pendingCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return pending == nil ? 0 : 1
    }

    private func makeTicket(item: Item, epoch: UInt64) -> Ticket {
        defer { nextID &+= 1 }
        return Ticket(id: nextID, epoch: epoch, item: item)
    }
}
