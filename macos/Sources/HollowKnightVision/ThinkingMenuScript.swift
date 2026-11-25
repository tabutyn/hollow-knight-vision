import CoreGraphics
import Foundation

enum ThinkingHandoff: Equatable {
    case status(String)
    case menuAdvanced(String)

    var text: String {
        switch self {
        case .status(let text): return text
        case .menuAdvanced(let id): return "Menu advanced · " + id
        }
    }
}

struct ThinkingMenuPage: Equatable {
    struct Element: Equatable {
        let id: String
        let center: CGPoint  // Origin at the bottom left.
        var label: String = ""
        var canSelect = true
    }
    let stateID: String
    let selected: String?
    let elements: [Element]

    init(stateID: String, selected: String? = nil, elements: [Element] = []) {
        self.stateID = stateID
        self.elements = elements
        self.selected = elements.first(where: { $0.id == selected })?.id
    }

    var pageKey: [String] { [stateID, selected ?? ""] + elements.map(\.id).sorted() }

    func control(toward id: String) -> ThinkingMenuInput? {
        guard let a = elements.first(where: { $0.id == selected }),
              let b = elements.first(where: { $0.id == id && $0.canSelect }) else { return nil }
        if a.id == b.id { return .confirm }
        let dx = b.center.x - a.center.x, dy = b.center.y - a.center.y
        guard abs(dx) + abs(dy) > 1 else { return nil }
        return abs(dy) >= abs(dx) ? (dy > 0 ? .up : .down) : (dx > 0 ? .right : .left)
    }

    var forward: ThinkingMenuInput {
        let ys = elements.map(\.center.y), xs = elements.map(\.center.x)
        guard let top = ys.max(), let bottom = ys.min(), let left = xs.min(), let right = xs.max() else { return .down }
        return top - bottom < 5 && right - left > 5 ? .right : .down
    }
}

/// A detector supplies a stable state ID and the pixels that established it.
struct ThinkingScreenHistory {
    struct Screen {
        let id: String
        let generation: UInt64
        let page: ThinkingMenuPage?
        let image: CGImage
    }
    private(set) var latest: Screen?
    private(set) var visit: UInt64 = 0
    private(set) var previousStateID: String?
    private(set) var previousConfirmedElement: String?
    private var lastState: (id: String, generation: UInt64)?
    private var confirmation: (element: String, time: TimeInterval)?

    mutating func lostState() { latest = nil }

    mutating func confirmed(_ element: String, on stateID: String, at time: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        guard latest?.id == stateID else { return }
        confirmation = (element, time)
    }

    mutating func record(_ screen: Screen, at time: TimeInterval = ProcessInfo.processInfo.systemUptime) -> Bool {
        let entered = latest?.id != screen.id || latest?.generation != screen.generation
        let changed = entered || latest?.page?.pageKey != screen.page?.pageKey
        if entered {
            visit &+= 1
            previousStateID = lastState?.generation == screen.generation ? lastState?.id : nil
            // Retain a confirmed input across a short fade. A later manual visit
            // must not inherit an old navigation intent.
            previousConfirmedElement = previousStateID == nil ? nil : confirmation.flatMap {
                (0...5).contains(time - $0.time) ? $0.element : nil
            }
            confirmation = nil
            lastState = (screen.id, screen.generation)
        }
        latest = screen
        return changed
    }
}

enum ThinkingMenuInput { case up, down, left, right, confirm }

struct ThinkingMenuScript {
    enum Request: Equatable {
        case select(String), more, wait

        var selection: [String: String] {
            switch self {
            case .select(let id): return ["menu": "select", "elementId": id]
            case .more: return ["menu": "more"]
            case .wait: return ["menu": "wait"]
            }
        }
    }
    let page: ThinkingMenuPage
    let request: Request

    // One pulse per newly verified focus. A lost selector, loop, changed menu or
    // unchanged highlight ends the script without guessing where focus went.
    @MainActor
    func run(current: () -> ThinkingMenuPage?, press: (ThinkingMenuInput) -> Bool,
             wait: () async throws -> Void = { try await Task.sleep(for: .milliseconds(50)) }) async -> ThinkingHandoff {
        if request == .wait { return .status("Waiting for new menu evidence.") }
        guard var live = current(), live.stateID == page.stateID,
              page.selected == nil || live.pageKey == page.pageKey else {
            return .status("Skipped · menu or highlight changed.")
        }
        // A state can be recognized before its highlight. Keep the chosen ID bound to
        // that state and wait briefly for verified focus before sending any input.
        if live.selected == nil {
            for _ in 0..<40 {
                do { try await wait() } catch { return .status("Stopped · menu request cancelled.") }
                guard let observed = current(), observed.stateID == page.stateID else { return .status("Stopped · menu changed.") }
                live = observed
                if live.selected != nil { break }
            }
        }
        guard live.selected != nil else { return .status("Waiting for a verified menu highlight.") }
        var visited = Set<String>()
        while !Task.isCancelled, let highlighted = live.selected, visited.insert(highlighted).inserted {
            let button: ThinkingMenuInput
            switch request {
            case .wait: return .status("Waiting for new menu evidence.")
            case .more: button = live.forward
            case .select(let id):
                guard let step = live.control(toward: id) else { return .status("Stopped · menu element disappeared.") }
                button = step
            }
            guard press(button) else { return .status("Stopped · menu controls unavailable.") }
            // Let the input bridge release the 100 ms pulse before sending another.
            var next: ThinkingMenuPage?
            for tick in 1...40 {
                do { try await wait() } catch { return .status("Stopped · menu request cancelled.") }
                if button == .confirm && tick >= 3 { return .status("Sent confirm · " + highlighted) }
                guard tick >= 3, let observed = current() else { continue }
                guard observed.stateID == page.stateID else { return .status("Stopped · menu changed.") }
                if observed.selected != nil && observed.pageKey != live.pageKey { next = observed; break }
            }
            guard let next else { return .status("Stopped · no menu advance observed.") }
            if request == .more, let selected = next.selected { return .menuAdvanced(selected) }
            live = next
        }
        return .status("Stopped · menu navigation ended.")
    }
}
