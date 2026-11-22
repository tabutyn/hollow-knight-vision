import CoreGraphics
import Foundation

/// Bottom-left room pixels. Quantization makes repeated captures of the same surface comparable.
struct PathPoint: Hashable, Codable, Comparable {
    let x, y: Int
    init(_ x: Int, _ y: Int) {
        self.x = x
        self.y = y
    }
    static func supports(_ p: CGPoint) -> Bool {
        (-1e8...1e8).contains(p.x) && (-1e8...1e8).contains(p.y)
    }
    init(_ p: CGPoint) { self.init(Int(p.x.rounded()), Int(p.y.rounded())) }
    var point: CGPoint { CGPoint(x: x, y: y) }
    static func < (a: Self, b: Self) -> Bool { (a.x, a.y) < (b.x, b.y) }
    func distance(_ other: Self) -> Int { abs(other.x - x) + abs(other.y - y) }
}

struct PathPlatform: Equatable, Hashable {
    var left, right, y: Int
    func contains(_ x: Int) -> Bool { (left...right).contains(x) }
}

struct PathWall: Hashable {
    var x, low, high, solidSide: Int
    var confirmedByContact = false
}

struct PathConnection: Codable, Equatable, Identifiable {
    var id = UUID()
    let first, second: PathPoint  // Ground positions, invariant to the displayed Knight height.
    let doubleJump: Bool
}

struct PathStroke: Hashable {
    enum Kind: String, Codable { case walk, jump, doubleJump, drop, wall }
    let a, b: PathPoint
    let kind: Kind
    let forward: Bool
    init(a: PathPoint, b: PathPoint, kind: Kind, forward: Bool = true) {
        self.a = a
        self.b = b
        self.kind = kind
        self.forward = forward
    }
    var horizontal: Bool { a.y == b.y }
    func contains(_ p: PathPoint) -> Bool { closest(p) == p }
    func allowsTraversal(from: PathPoint, to: PathPoint) -> Bool {
        contains(from) && contains(to) && (kind != .drop || (forward ? from <= to : to <= from))
    }
    func closest(_ p: PathPoint) -> PathPoint {
        PathPoint(min(max(a.x, b.x), max(min(a.x, b.x), p.x)), min(max(a.y, b.y), max(min(a.y, b.y), p.y)))
    }
}

struct AtlasPlatformGraph {
    let floors: [PathPlatform]
    let knightHeight, knightWidth: Int
    let strokes: [PathStroke]
    let nodes: [PathPoint: [PathPoint]]
    let walkNodes: Set<PathPoint>
    private let intersections: [PathStroke: Set<PathPoint>]

    var upperRightObjective: PathPoint? {
        guard let top = floors.map(\.y).max() else { return nil }
        // Sub-pixel world edits and noisy detections can put one tier on nearby rows.
        let tier = max(2, knightHeight / 4)
        return floors.filter { top - $0.y <= tier }.max {
            ($0.right, $0.y) < ($1.right, $1.y)
        }.map { PathPoint($0.right, $0.y + knightHeight) }
    }

    /// Join narrow visual seams up to one Knight width. A lower landing
    /// inside the opening or a measured wall at its lip preserves a real gap.
    static func stitchDetected(_ floors: [PathPlatform], walked: [PathPlatform] = [], walls: [PathWall], height: Int, width: Int,
                               supportedSeams: [PathPlatform] = [], maximumSupportedGap: Int? = nil,
                               requireWitness: Bool = false) -> [PathPlatform] {
        struct Band {
            var span: PathPlatform
            var low, high, longest: Int
            var walked: Bool
        }
        let jitter = max(2, min(8, height / 5))
        let seam = requireWitness ? 0 : max(2, min(width, height))
        let longestWitness = supportedSeams.map { $0.right - $0.left }.max() ?? 0
        let searchWidth = max(seam, min(maximumSupportedGap ?? longestWitness, longestWitness))
        let margin = maximumSupportedGap == nil ? 0 : min(3, width / 2)
        var bands = [Band](), finished = [PathPlatform]()
        let confirmed = Set(walked)
        let ordered = (floors + walked).filter { $0.left < $0.right }.sorted { ($0.left, $0.y, $0.right) < ($1.left, $1.y, $1.right) }
        for f in ordered {
            bands.removeAll { band in
                guard band.span.right + searchWidth < f.left else { return false }
                finished.append(band.span)
                return true
            }
            var best: Int?
            for i in bands.indices {
                let b = bands[i], gap = f.left - b.span.right
                guard max(b.high, f.y) - min(b.low, f.y) <= jitter else { continue }
                if gap > seam && !supportedSeams.contains(where: {
                    abs($0.y - f.y) <= 2 && abs($0.y - b.span.y) <= 2 &&
                        $0.left <= b.span.right - margin && f.left + margin <= $0.right
                }) { continue }
                if gap > 0 {
                    if walls.contains(where: { (b.span.right...f.left).contains($0.x)
                        && $0.high >= min(b.low, f.y) && $0.low <= max(b.high, f.y) + height / 2 }) { continue }
                    if ordered.contains(where: { $0.left < f.left && $0.right > b.span.right && $0.y < min(b.low, f.y) - jitter }) { continue }
                }
                if let best, abs(bands[best].span.y - f.y) <= abs(b.span.y - f.y) { continue }
                best = i
            }
            let length = f.right - f.left
            let isWalked = confirmed.contains(f)
            if let i = best {
                bands[i].span.right = max(bands[i].span.right, f.right)
                bands[i].low = min(bands[i].low, f.y)
                bands[i].high = max(bands[i].high, f.y)
                // A textured edge may extend a walked road. Its row must still
                // agree with the green contact line even if the edge is longer.
                if isWalked != bands[i].walked ? isWalked : length > bands[i].longest {
                    bands[i].span.y = f.y
                    bands[i].longest = length
                    bands[i].walked = isWalked
                }
            } else {
                bands.append(Band(span: f, low: f.y, high: f.y, longest: length, walked: isWalked))
            }
        }
        return (finished + bands.map(\.span)).sorted { ($0.left, $0.y, $0.right) < ($1.left, $1.y, $1.right) }
    }

    static func merge(_ input: [PathPlatform], height: Int, bridgeSeams: Bool = true) -> [PathPlatform] {
        // One bounded row band: a chain of one-pixel errors must never unite a stairway.
        let tolerance = max(2, min(6, height / 5))
        let seam = bridgeSeams ? max(2, min(6, height / 6)) : 0
        struct Band {
            var span: PathPlatform
            let bottom: Int
        }
        var bands = [Band]()
        for f in input.filter({ $0.left < $0.right }).sorted(by: { ($0.y, $0.left) < ($1.y, $1.left) }) {
            var joined = f
            var bottom = f.y
            // Only adjacent extents vote on each other's row. A distant ledge
            // at another elevation cannot change the clustering of this floor.
            bands.removeAll { band in
                guard f.y - band.bottom <= tolerance,
                    f.left <= band.span.right + seam, band.span.left <= f.right + seam
                else { return false }
                joined.left = min(joined.left, band.span.left)
                joined.right = max(joined.right, band.span.right)
                bottom = min(bottom, band.bottom)
                return true
            }
            bands.append(Band(span: joined, bottom: bottom))
        }
        let result = bands.map(\.span)
        return result.sorted { ($0.left, $0.y, $0.right) < ($1.left, $1.y, $1.right) }
    }

    init(
        floors raw: [PathPlatform], walls: [PathWall], height: Int, width: Int, connections: [PathConnection] = [],
        worldScale: CGPoint? = nil, previous: AtlasPlatformGraph? = nil, spikes: [PathPlatform] = [],
        doors: [(PathPoint, PathPoint)] = [], detectedGround: Bool = false, walked: [PathPlatform] = [],
        handoffSeams: [PathPlatform] = []
    ) {
        let h = max(4, height)
        let w = max(2, width)
        knightHeight = h
        knightWidth = w
        // The first sprite box after reacquisition can be crouched or partly
        // hidden. A brief combat mask may span several of those box heights.
        let merged = detectedGround ? Self.stitchDetected(raw, walked: walked, walls: walls, height: h, width: w,
            supportedSeams: handoffSeams, maximumSupportedGap: 3 * h) : Self.merge(raw, height: h)
        // A long detected floor can run behind a door or wall. Break its walk
        // edge at a standing obstruction, while preserving the two ground tops.
        let safeFloors = merged.flatMap { f -> [PathPlatform] in
            let hazards = spikes.filter { abs($0.y - f.y) <= max(2, min(6, h / 5)) }
                .sorted { $0.left < $1.left }
            var spans = [f]
            for hazard in hazards {
                let lo = hazard.left - max(1, w / 2), hi = hazard.right + max(1, w / 2)
                spans = spans.flatMap { span -> [PathPlatform] in
                    guard hi >= span.left && lo <= span.right else { return [span] }
                    var pieces = [PathPlatform]()
                    if span.left < lo - 1 { pieces.append(.init(left: span.left, right: lo - 1, y: span.y)) }
                    if hi + 1 < span.right { pieces.append(.init(left: hi + 1, right: span.right, y: span.y)) }
                    return pieces
                }
            }
            return spans
        }
        let platforms = safeFloors.flatMap { f -> [PathPlatform] in
            let cuts = Set(
                walls.filter {
                    $0.x > f.left && $0.x < f.right && $0.low <= f.y + h / 2 && $0.high > f.y + h / 2
                }.map(\.x)
            ).sorted()
            guard !cuts.isEmpty else { return [f] }
            var result = [PathPlatform]()
            var left = f.left
            for x in cuts {
                let right = x - max(1, w / 2)
                if left < right { result.append(.init(left: left, right: right, y: f.y)) }
                left = max(left, x + max(1, w / 2))
            }
            if left < f.right { result.append(.init(left: left, right: f.right, y: f.y)) }
            // At the end of a walk, a wall can be real but its clearance
            // rectangle can bite back into the footprint. Keep the traversed
            // pixels up to that endpoint. The unwalked side stays blocked.
            guard detectedGround, !walked.isEmpty else { return result }
            let footprints = walked.compactMap { walk -> PathPlatform? in
                let lo = max(f.left, walk.left), hi = min(f.right, walk.right)
                guard walk.y == f.y, lo < hi else { return nil }
                return .init(left: lo, right: hi, y: f.y)
            }
            return Self.merge(result + footprints, height: h, bridgeSeams: false)
        }.sorted { ($0.left, $0.y) < ($1.left, $1.y) }
        floors = platforms
        var edges = [PathStroke]()
        var seen = Set<PathStroke>()
        func add(_ a: PathPoint, _ b: PathPoint, _ kind: PathStroke.Kind) {
            guard a != b else { return }
            let line = PathStroke(a: min(a, b), b: max(a, b), kind: kind, forward: kind != .drop || a < b)
            if seen.insert(line).inserted { edges.append(line) }
        }
        for f in platforms { add(.init(f.left, f.y + h), .init(f.right, f.y + h), .walk) }
        func attach(_ p: PathPoint) -> Int? {
            platforms.indices.filter { platforms[$0].contains(p.x) && abs(platforms[$0].y - p.y) <= max(4, h / 3) }
                .min {
                    abs(platforms[$0].y - p.y) < abs(platforms[$1].y - p.y)
                }
        }
        var manualPairs = Set<[Int]>()
        var jumpPairs = Set<[Int]>()
        func shelf(_ a: PathPoint, _ b: PathPoint, kind: PathStroke.Kind) {
            let top = max(a.y, b.y) + max(2, h / 4)
            add(a, .init(a.x, top), kind)
            add(.init(a.x, top), .init(b.x, top), kind)
            add(.init(b.x, top), b, kind)
        }
        for link in connections {
            guard let i = attach(link.first), let j = attach(link.second), i != j else { continue }
            manualPairs.insert([min(i, j), max(i, j)])
            jumpPairs.insert([min(i, j), max(i, j)])
            shelf(
                .init(link.first.x, platforms[i].y + h), .init(link.second.x, platforms[j].y + h),
                kind: link.doubleJump ? .doubleJump : .jump)
        }
        // Measured fresh-save jump in world units (also used by the Gym's trajectory validator).
        // Gameplay estimates projection scale from the visual box until real scale is available.
        let px = worldScale?.x ?? CGFloat(h) / 1.8
        let py = worldScale?.y ?? px
        let maximumGap = 8.3 * (2 * 23 / 47.4) * px
        for i in platforms.indices {
            let a = platforms[i]
            for j in platforms.indices.dropFirst(i + 1) {
                let b = platforms[j]
                let gap = max(0, b.left - a.right)
                let rise = abs(a.y - b.y)
                if CGFloat(gap) > maximumGap { break }
                guard !manualPairs.contains([i, j]), CGFloat(rise) / py <= (23 * 23 / (2 * 47.4)),
                    gap > 0 || rise > 0
                else { continue }
                let approaches: [(Int, Int)]
                if b.left >= a.right {
                    approaches = [(max(a.left, a.right - max(1, w / 2)), min(b.right, b.left + max(1, w / 2)))]
                } else {
                    // For overlapping ledges approach an exposed lip. A ground stroke
                    // above the take-off point would require jumping through a platform.
                    let lower = a.y < b.y ? a : b
                    let upper = a.y < b.y ? b : a
                    let options = [
                        (upper.left - w, upper.left + min(w / 2, (upper.right - upper.left) / 2)),
                        (upper.right + w, upper.right - min(w / 2, (upper.right - upper.left) / 2)),
                    ]
                    approaches = options.filter { lower.contains($0.0) }.map { a.y < b.y ? $0 : ($0.1, $0.0) }
                }
                for (ax, bx) in approaches {
                    let from = PathPoint(ax, a.y + h)
                    let to = PathPoint(bx, b.y + h)
                    let top = max(from.y, to.y) + max(2, h / 4)
                    let flight = (23 + sqrt(23 * 23 - 2 * 47.4 * CGFloat(rise) / py)) / 47.4
                    guard CGFloat(abs(bx - ax)) / px <= 8.3 * flight - 0.25 else { continue }
                    let left = min(ax, bx)
                    let right = max(ax, bx)
                    // The shelf is a navigation diagram. Test the jump through the
                    // actual vertical wall profile with the measured ballistic arc.
                    let startX = a.y <= b.y ? ax : bx
                    let sign = (a.y <= b.y ? bx - ax : ax - bx) >= 0 ? 1 : -1
                    let barriers = walls.filter { $0.x >= left && $0.x <= right }
                    let horizontalTime = CGFloat(right - left) / (8.3 * px)
                    let clearsWalls = (0...50).contains { delayStep in
                        let delay = CGFloat(delayStep) / 100
                        guard delay + horizontalTime <= flight else { return false }
                        return barriers.allSatisfy { wall in
                            let t = delay + CGFloat(sign * (wall.x - startX)) / (8.3 * px)
                            let feet = CGFloat(min(a.y, b.y)) + (23 * t - 23.7 * t * t) * py
                            return feet >= CGFloat(wall.high) - 1 || feet + CGFloat(h) <= CGFloat(wall.low) + 1
                        }
                    }
                    let blocked =
                        !clearsWalls
                        || platforms.indices.contains { k in
                            guard k != i && k != j else { return false }
                            let other = platforms[k]
                            return (other.contains(ax) && other.y > a.y && other.y <= top)
                                || (other.contains(bx) && other.y > b.y && other.y <= top)
                                || (other.right > left && other.left < right && other.y >= top - h / 2
                                    && other.y <= top)
                                || (gap > 0 && other.left >= a.right && other.right <= b.left
                                    && abs(other.y - max(a.y, b.y)) <= h)
                        }
                    if !blocked {
                        shelf(from, to, kind: .jump)
                        jumpPairs.insert([i, j])
                    }
                }
            }
        }
        // From each exposed lip, model several controlled falls. A falling Knight
        // can steer horizontally for the time it takes to reach each lower floor.
        for (i, upper) in platforms.enumerated() {
            for (lip, side) in [(upper.left, -1), (upper.right, 1)] {
                let x = lip + side * max(1, (w + 1) / 2)
                guard !platforms.enumerated().contains(where: { j, f in
                    j != i && f.y == upper.y && f.contains(x)
                }) else { continue }
                let exitAtLip = doors.contains { door in
                    let (a, b) = door
                    if a.x == b.x {
                        return abs(a.x - lip) <= max(2, w) && min(a.y, b.y) <= upper.y + h && max(a.y, b.y) >= upper.y
                    }
                    return abs(a.y - upper.y) <= max(2, h / 5) && min(a.x, b.x) <= lip + w && max(a.x, b.x) >= lip - w
                }
                guard !exitAtLip else { continue }
                let stepBlocked = spikes.contains { spike in
                    abs(spike.y - upper.y) <= max(2, min(6, h / 5))
                        && spike.left <= max(lip, x) + w / 2 && spike.right >= min(lip, x) - w / 2
                } || walls.contains { wall in
                    wall.x > min(lip, x) - w / 2 && wall.x < max(lip, x) + w / 2
                        && wall.high > upper.y && wall.low < upper.y + h
                } || platforms.enumerated().contains { j, f in
                    j != i && f.y > upper.y && f.y < upper.y + h
                        && f.left <= max(lip, x) && f.right >= min(lip, x)
                }
                guard !stepBlocked else { continue }
                for (j, lower) in platforms.enumerated() where lower.y < upper.y {
                    guard !jumpPairs.contains([min(i, j), max(i, j)]) else { continue }
                    func time(_ y: Int) -> CGFloat {
                        sqrt(2 * CGFloat(upper.y - y) / (47.4 * py))
                    }
                    let duration = time(lower.y)
                    let speed = 8.3 * px
                    let half = max(1, (w - 1) / 2)
                    let nearest = min(lower.right + half, max(lower.left - half, x))
                    guard CGFloat(abs(nearest - x)) <= speed * duration else { continue }
                    let candidates = Set([nearest,
                                          lower.left - half, lower.right + half,
                                          lower.left, lower.right, (lower.left + lower.right) / 2]).sorted {
                        abs($0 - x) < abs($1 - x)
                    }
                    // A waypoint tangent to an intermediate platform allows
                    // steering around it and back toward a lower landing.
                    var waypoints: [(Int, Int)?] = [nil]
                    for (k, f) in platforms.enumerated() where k != i && k != j && f.y < upper.y && f.y > lower.y {
                        for clearance in [max(2, h / 4), 1] {
                            let y = f.y + clearance
                            guard y < upper.y else { continue }
                            for wx in [f.left - (w + 1) / 2 - 1, f.right + (w + 1) / 2 + 1] {
                                if CGFloat(abs(wx - x)) <= speed * time(y) { waypoints.append((wx, y)) }
                            }
                        }
                    }
                    for wall in walls where wall.low - h - 1 > lower.y && wall.low - h - 1 < upper.y {
                        let y = wall.low - h - 1
                        for wx in [wall.x - (w + 1) / 2, wall.x + (w + 1) / 2] {
                            if CGFloat(abs(wx - x)) <= speed * time(y) { waypoints.append((wx, y)) }
                        }
                    }
                    var selected: [(Int, Int)]?
                    search: for landing in candidates {
                        for waypoint in waypoints {
                            let midTime = waypoint.map { time($0.1) } ?? duration
                            let midX = waypoint.map { CGFloat($0.0) } ?? CGFloat(landing)
                            guard abs(midX - CGFloat(x)) <= speed * midTime,
                                abs(CGFloat(landing) - midX) <= speed * (duration - midTime) + 0.001
                            else { continue }
                            func position(_ t: CGFloat) -> CGFloat {
                                if t <= midTime { return CGFloat(x) + (midX - CGFloat(x)) * t / midTime }
                                return midX + (CGFloat(landing) - midX) * (t - midTime) / (duration - midTime)
                            }
                            let exitTime = time(max(lower.y, upper.y - h))
                            var exitPositions = [CGFloat(x), position(exitTime)]
                            if midTime < exitTime { exitPositions.append(midX) }
                            let returnsThroughLip = side < 0
                                ? exitPositions.max()! > CGFloat(upper.left) - CGFloat(w) / 2
                                : exitPositions.min()! < CGFloat(upper.right) + CGFloat(w) / 2
                            guard !returnsThroughLip else { continue }
                            let floorBlocked = platforms.enumerated().contains { k, f in
                                guard k != i && k != j && f.y < upper.y && f.y > lower.y else { return false }
                                let at = position(time(f.y))
                                return at > CGFloat(f.left) - CGFloat(w) / 2 && at < CGFloat(f.right) + CGFloat(w) / 2
                            }
                            let wallBlocked = walls.contains { wall in
                                // At the wall top the feet only touch its cap;
                                // landing on that ledge does not enter the wall.
                                guard wall.high > lower.y && wall.low < upper.y + h else { return false }
                                let top = min(upper.y, wall.high)
                                let bottom = max(lower.y, wall.low - h)
                                guard top >= bottom else { return false }
                                let t0 = time(top), t1 = time(bottom)
                                var positions = [position(t0), position(t1)]
                                if t0 < midTime && midTime < t1 { positions.append(midX) }
                                return CGFloat(wall.x) > positions.min()! - CGFloat(w) / 2
                                    && CGFloat(wall.x) < positions.max()! + CGFloat(w) / 2
                            }
                            let spikeBlocked = spikes.contains { spike in
                                guard spike.y < upper.y && spike.y >= lower.y else { return false }
                                let at = position(time(spike.y))
                                return at >= CGFloat(spike.left - w / 2) && at <= CGFloat(spike.right + w / 2)
                            }
                            guard !floorBlocked && !wallBlocked && !spikeBlocked else { continue }
                            func turnClear(_ a: Int, _ b: Int, _ y: Int) -> Bool {
                                let left = CGFloat(min(a, b)) - CGFloat(w) / 2
                                let right = CGFloat(max(a, b)) + CGFloat(w) / 2
                                return !walls.contains { wall in
                                    CGFloat(wall.x) > left && CGFloat(wall.x) < right
                                        && wall.high > y && wall.low < y + h
                                } && !platforms.contains { floor in
                                    floor.y > y && floor.y < y + h
                                        && CGFloat(floor.right) > left && CGFloat(floor.left) < right
                                } && !spikes.contains { spike in
                                    spike.y >= y && spike.y < y + h
                                        && CGFloat(spike.right) >= left && CGFloat(spike.left) <= right
                                }
                            }
                            let flight = waypoint.map { [($0.0, $0.1), (landing, lower.y)] } ?? [(landing, lower.y)]
                            var route = [(Int, Int)]()
                            var priorX = x, priorY = upper.y
                            for point in flight {
                                // Keep the orthogonal diagram on the safe side of
                                // surfaces which the ballistic trajectory skirts.
                                let cuts = Set(platforms.enumerated().compactMap { k, f -> Int? in
                                    guard k != i && k != j && f.y + 1 < priorY && f.y + 1 > point.1 else { return nil }
                                    return f.y + 1
                                } + walls.compactMap { wall -> Int? in
                                    guard wall.high + 1 < priorY && wall.high + 1 > point.1 else { return nil }
                                    return wall.high + 1
                                }).sorted(by: >)
                                var currentY = priorY
                                for y in cuts {
                                    let floorInColumn = platforms.enumerated().contains { k, f in
                                        k != i && k != j && f.y + 1 == y
                                            && CGFloat(priorX) > CGFloat(f.left) - CGFloat(w) / 2
                                            && CGFloat(priorX) < CGFloat(f.right) + CGFloat(w) / 2
                                    }
                                    let wallInColumn = walls.contains { wall in
                                        wall.high + 1 == y && abs(wall.x - priorX) * 2 < w
                                    }
                                    if floorInColumn || wallInColumn {
                                        // Prefer a visible gap above bypassed ground,
                                        // provided the full turn clears nearby solids.
                                        let raised = min(y + max(2, h / 4) - 1, currentY - 1)
                                        var turnY = y
                                        if floorInColumn && raised > y && raised > point.1 {
                                            let raisedX = Int(position(time(raised)).rounded())
                                            if turnClear(priorX, raisedX, raised) { turnY = raised }
                                        }
                                        let turnX = Int(position(time(turnY)).rounded())
                                        route.append((turnX, turnY))
                                        priorX = turnX
                                        currentY = turnY
                                    }
                                }
                                route.append(point)
                                priorX = point.0
                                priorY = point.1
                            }
                            let support = min(lower.right, max(lower.left, landing))
                            if support != landing { route.append((support, lower.y)) }
                            var diagramX = x
                            var diagramClear = true
                            for (routeX, y) in route {
                                if !turnClear(diagramX, routeX, y) { diagramClear = false; break }
                                diagramX = routeX
                            }
                            guard diagramClear else { continue }
                            selected = route
                            break search
                        }
                    }
                    guard let route = selected else { continue }
                    let edge = PathPoint(lip, upper.y + h)
                    let outside = PathPoint(x, upper.y + h)
                    add(edge, outside, .drop)
                    var previous = outside
                    for (routeX, groundY) in route {
                        let next = PathPoint(routeX, groundY + h)
                        let corner = PathPoint(previous.x, next.y)
                        add(previous, corner, .drop)
                        add(corner, next, .drop)
                        previous = next
                    }
                }
            }
        }
        for wall in walls where wall.high > wall.low {
            // A rising face opens over its adjacent floor. A descending face opens
            // away from it. Detector solid-side evidence takes precedence when known.
            var side = -wall.solidSide
            if side == 0 {
                var best = Int.max
                for f in platforms {
                    for (x, inward) in [(f.left, 1), (f.right, -1)] {
                        let base = abs(wall.low - f.y)
                        let cap = abs(wall.high - f.y)
                        let score = abs(wall.x - x) + min(base, cap)
                        if score < best && score <= 2 * w {
                            best = score
                            side = base <= cap ? inward : -inward
                        }
                    }
                }
            }
            if side != 0 { add(.init(wall.x + side * w, wall.low), .init(wall.x + side * w, wall.high), .wall) }
        }
        strokes = edges
        // Intersections, landings and short walking intervals are graph vertices.
        // Walls are displayed for future traversal and do not imply an unlocked climb.
        let routes = edges.filter { $0.kind != .wall }
        func touches(_ s: PathStroke, _ t: PathStroke) -> Bool {
            // Endpoints are ordered, all strokes are horizontal or vertical.
            s.a.x <= t.b.x && t.a.x <= s.b.x && s.a.y <= t.b.y && t.a.y <= s.b.y
        }
        let old = previous?.knightHeight == h ? previous?.intersections ?? [:] : [:]
        let changed = Set(routes).symmetricDifference(old.keys)
        let step = max(h, 8)
        func walkCuts(_ s: PathStroke, within range: ClosedRange<Int>, into points: inout Set<PathPoint>) {
            let first = Int(ceil(Double(range.lowerBound) / Double(step))) * step
            for x in stride(from: first, through: range.upperBound, by: step) { points.insert(.init(x, s.a.y)) }
        }
        var cuts = [PathStroke: Set<PathPoint>]()
        var adjacency = [PathPoint: Set<PathPoint>]()
        var walk = Set<PathPoint>()
        for i in routes.indices {
            let s = routes[i]
            var points: Set<PathPoint>
            if let unchanged = old[s], !changed.contains(where: { touches(s, $0) }) {
                points = unchanged
            } else {
                points = [s.a, s.b]
                if s.kind == .walk { walkCuts(s, within: s.a.x...s.b.x, into: &points) }
                for t in routes where t != s && touches(s, t) {
                    if s.kind == .drop || t.kind == .drop {
                        // Only the lip and landing attach to walkable ground. Diagram
                        // shelf crossings do not create physical transfer points.
                        let drop = s.kind == .drop ? s : t
                        let other = s.kind == .drop ? t : s
                        if other.kind == .walk {
                            for p in [drop.a, drop.b] where other.contains(p) { points.insert(p) }
                        } else if other.kind == .drop {
                            // Branches connect only where a segment ends, never at
                            // an incidental crossing of two flight diagrams.
                            for p in [drop.a, drop.b, other.a, other.b] where s.contains(p) && t.contains(p) {
                                points.insert(p)
                            }
                        }
                        continue
                    }
                    if s.kind != .walk && t.kind != .walk {
                        // Shelves are diagrams for separate flights. Their
                        // interior crossings do not represent a landing or a
                        // midair transfer; only adjoining ends connect.
                        for p in [t.a, t.b] where (p == s.a || p == s.b) {
                            points.insert(p)
                        }
                        continue
                    }
                    if s.horizontal != t.horizontal {
                        points.insert(s.horizontal ? PathPoint(t.a.x, s.a.y) : PathPoint(s.a.x, t.a.y))
                    } else {
                        for p in [t.a, t.b] where s.closest(p) == p { points.insert(p) }
                        if t.kind == .walk {
                            walkCuts(t, within: max(t.a.x, s.a.x)...min(t.b.x, s.b.x), into: &points)
                        }
                    }
                }
            }
            cuts[s] = points
            let ordered = points.sorted()
            if routes[i].kind == .walk { walk.formUnion(ordered) }
            for (a, b) in zip(ordered, ordered.dropFirst()) {
                if s.allowsTraversal(from: a, to: b) { adjacency[a, default: []].insert(b) }
                if s.allowsTraversal(from: b, to: a) { adjacency[b, default: []].insert(a) }
            }
        }
        intersections = cuts
        nodes = adjacency.mapValues { $0.sorted() }
        walkNodes = walk
    }

    func vertices(on stroke: PathStroke) -> Set<PathPoint> { intersections[stroke] ?? [] }

    func nearestStroke(_ p: PathPoint, within radius: Int, walkOnly: Bool = false) -> PathStroke? {
        strokes.filter { $0.kind != .wall && (!walkOnly || $0.kind == .walk) }.min {
            $0.closest(p).distance(p) < $1.closest(p).distance(p)
        }.flatMap { $0.closest(p).distance(p) <= radius ? $0 : nil }
    }
}
