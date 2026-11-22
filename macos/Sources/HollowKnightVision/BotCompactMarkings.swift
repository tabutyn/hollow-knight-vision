import Foundation

/// Schema 14: 16 closest shared slots, each [start_dx, start_dy, end_dx, end_dy, kind, present].
/// kind is Spike=0, Ground=.33, Wall=.66, Door=1. Endpoints are relative to the
/// Knight and divided by frame width. Counts describe ALL visible markings,
/// including any beyond capacity; unknown channels have a separate known flag.
enum BotCompactMarkings {
    static let capacity = 16
    static let fields = ["start_dx", "start_dy", "end_dx", "end_dy", "kind", "present"]
    static let kinds = ["ground", "wall", "spike", "door"]
    static let metadataNames = kinds.map { "marking_\($0)_known" }
        + kinds.map { "marking_\($0)_count" } + ["marking_overflow"]
    static func names(for version: Int) -> [String] {
        metadataNames + (0..<(version == 13 ? 64 : capacity)).flatMap { i in fields.map { "marking_\(i)_\($0)" } }
    }
    static let names = names(for: 14)
    static let legacyNames = names(for: 13)
    private struct Segment: Hashable {
        let kind: Int
        let x1, y1, x2, y2: Double
        func distanceSquared(x: Double, y: Double) -> Double {
            let dx = x2 - x1, dy = y2 - y1
            let t = min(1, max(0, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)))
            return pow(x - x1 - t * dx, 2) + pow(y - y1 - t * dy, 2)
        }
    }
    static func features(_ geometry: BotScreenTerrain?, knightX: Double, knightY: Double, version: Int = 14) -> [Float] {
        let capacity = version == 13 ? 64 : Self.capacity
        let names = Self.names(for: version)
        let empty = [Float](repeating: 0, count: names.count)
        guard let geometry, geometry.width.isFinite, geometry.height.isFinite,
              geometry.width > 0, geometry.height > 0, knightX.isFinite, knightY.isFinite else { return empty }
        let w = geometry.width, h = geometry.height, kx = knightX * w, ky = knightY * w
        var segments = Set<Segment>()
        func add(_ kind: Int, _ ax: Double, _ ay: Double, _ bx: Double, _ by: Double) {
            guard [ax, ay, bx, by].allSatisfy(\.isFinite) else { return }
            if ay == by, (0...h).contains(ay) {
                let left = max(0, min(w, min(ax, bx))), right = max(0, min(w, max(ax, bx)))
                if right > left { segments.insert(.init(kind: kind, x1: left, y1: ay, x2: right, y2: ay)) }
            } else if ax == bx, (0...w).contains(ax) {
                let low = max(0, min(h, min(ay, by))), high = max(0, min(h, max(ay, by)))
                if high > low { segments.insert(.init(kind: kind, x1: ax, y1: low, x2: ax, y2: high)) }
            }
        }
        for s in geometry.floors { add(1, s.minimumX, s.y, s.maximumX, s.y) }
        for s in geometry.walls { add(2, s.x, s.bottomY, s.x, s.topY) }
        for s in geometry.spikes ?? [] { add(3, s.x1, s.y1, s.x2, s.y2) }
        for s in geometry.doors ?? [] { add(4, s.x1, s.y1, s.x2, s.y2) }
        let ordered = segments.sorted {
            ($0.distanceSquared(x: kx, y: ky), $0.kind, $0.x1, $0.y1, $0.x2, $0.y2)
                < ($1.distanceSquared(x: kx, y: ky), $1.kind, $1.x1, $1.y1, $1.x2, $1.y2)
        }
        let counts = (1...4).map { kind in min(4, Float(segments.filter { $0.kind == kind }.count) / Float(capacity)) }
        var result: [Float] = [1, 1, geometry.spikes == nil ? 0 : 1, geometry.doors == nil ? 0 : 1]
        result += counts
        result.append(min(4, Float(max(0, ordered.count - capacity)) / Float(capacity)))
        for s in ordered.prefix(capacity) {
            result += [(s.x1 - kx) / w, (s.y1 - ky) / w, (s.x2 - kx) / w, (s.y2 - ky) / w,
                       (version == 13 ? Double(s.kind) / 4 : [1: 0.33, 2: 0.66, 3: 0.0, 4: 1.0][s.kind]!), 1].map { Float(min(4, max(-4, $0))) }
        }
        result += [Float](repeating: 0, count: names.count - result.count)
        return result
    }
}

/// Local geometry in Knight heights. The policy receives the observed shelf
/// edges and their displacement across frames independently of camera coverage.
enum BotLandingGeometry {
    static let names = ["landing_present", "landing_left", "landing_right", "landing_clearance",
                        "landing_dx", "landing_dy"]
    static let absent = [Float](repeating: 0, count: names.count)

    static func features(markings: [Float], height: Float, grounded: Bool?, previous: ArraySlice<Float>?) -> [Float] {
        guard grounded == false, height > 0.005, height.isFinite,
              markings.count == BotCompactMarkings.names.count else { return absent }
        var best: (left: Float, right: Float, clearance: Float, rank: Float)?
        for offset in stride(from: BotCompactMarkings.metadataNames.count, to: markings.count,
                             by: BotCompactMarkings.fields.count) {
            guard markings[offset + 5] > 0.5, abs(markings[offset + 4] - 0.33) < 0.00001,
                  markings[offset + 1] == markings[offset + 3] else { continue }
            let left = markings[offset] / height, right = markings[offset + 2] / height
            let clearance = -markings[offset + 1] / height - 1
            let gap = max(0, left, -right)
            guard (0.35...3).contains(right-left), (-0.07...2.75).contains(clearance), gap <= 2.5 else { continue }
            let rank = clearance + 0.05 * gap
            if let best, best.rank <= rank { continue }
            best = (left, right, clearance, rank)
        }
        guard let best else { return absent }
        func bounded(_ value: Float) -> Float { max(-4, min(4, value)) }
        let left = bounded(best.left), right = bounded(best.right)
        var dx: Float = 0, dy: Float = 0
        if let previous, previous.count == names.count {
            let start = previous.startIndex
            let oldLeft = previous[start+1], oldRight = previous[start+2], oldClearance = previous[start+3]
            let sameWidth = abs((right-left)-(oldRight-oldLeft)) <= 0.12
            if previous[start] == 1, sameWidth, abs(best.clearance-oldClearance) <= 1.5 {
                dx = bounded((oldLeft + oldRight - left - right) / 2)
                dy = best.clearance - oldClearance
            }
        }
        return [1, left, right, best.clearance, dx, dy]
    }
}
