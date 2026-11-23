import Foundation

private struct PathSearch {
    var distance: [PathPoint: Int] = [:]
    var firstHop: [PathPoint: PathPoint] = [:]
    private var predecessor: [PathPoint: PathPoint] = [:]

    func route(to target: PathPoint) -> [PathPoint] {
        var path = [target]
        var point = target
        while let prior = predecessor[point] {
            path.append(prior)
            point = prior
        }
        return path.reversed()
    }

    init(from start: PathPoint, graph: [PathPoint: [PathPoint]], registered: (PathPoint) -> Bool) {
        var heap = [(Int, PathPoint)]()
        func push(_ item: (Int, PathPoint)) {
            var i = heap.count
            heap.append(item)
            while i > 0 {
                let parent = (i - 1) / 2
                if heap[parent] <= item { break }
                heap[i] = heap[parent]
                i = parent
            }
            heap[i] = item
        }
        func pop() -> (Int, PathPoint) {
            let first = heap[0]
            let last = heap.removeLast()
            guard !heap.isEmpty else { return first }
            var i = 0
            while 2 * i + 1 < heap.count {
                var child = 2 * i + 1
                if child + 1 < heap.count && heap[child + 1] < heap[child] { child += 1 }
                if last <= heap[child] { break }
                heap[i] = heap[child]
                i = child
            }
            heap[i] = last
            return first
        }
        distance[start] = 0
        push((0, start))
        while !heap.isEmpty {
            let (cost, p) = pop()
            guard distance[p] == cost else { continue }
            for n in graph[p] ?? [] where registered(n) {
                // Long descents can cross several unexplored camera cells
                // between two registered floors.
                let length = p.distance(n)
                let cell = Int(BotAtlasCoverage.cellSize)
                let intervals = max(1, (length + cell - 1) / cell)
                if (1..<intervals).contains(where: { i in
                    !registered(PathPoint(p.x + (n.x - p.x) * i / intervals, p.y + (n.y - p.y) * i / intervals))
                }) { continue }
                let candidate = cost + length
                if candidate < distance[n, default: Int.max] {
                    distance[n] = candidate
                    predecessor[n] = p
                    firstHop[n] = p == start ? n : firstHop[p]
                    push((candidate, n))
                }
            }
        }
    }
}

struct AtlasPathNavigation {
    private struct CrossingLeg {
        let stroke: PathStroke
        let from, to: PathPoint

        func closest(_ p: PathPoint) -> PathPoint {
            PathPoint(min(max(from.x, to.x), max(min(from.x, to.x), p.x)),
                      min(max(from.y, to.y), max(min(from.y, to.y), p.y)))
        }
    }
    private var crossing: [CrossingLeg] = []
    private var walked = Set<PathPoint>()  // small spatial bins, robust to a corrected ground row
    private(set) var goal: PathPoint?
    private var lastPoint: PathPoint?
    private var bestRemaining: Int?
    // Paying for the same directed part of a platform twice makes a
    // corrected or temporarily lost frontier profitable to oscillate around.
    private struct Travel: Hashable {
        let point: PathPoint
        let direction: Int
    }
    private var credited = Set<Travel>()
    private var lastTime: Double?
    private var previousVector: BotRoadHint?
    private(set) var alignment: Double?
    private var heading: PathPoint?
    private var inheritedHeading: PathPoint?
    private(set) var objective: PathPoint?
    private(set) var next: PathPoint?
    private(set) var current: PathPoint?
    private(set) var selectedRoute: [PathPoint] = []
    private(set) var automatic = true

    mutating func setObjective(_ p: PathPoint?) {
        previousVector = nil
        alignment = nil
        selectedRoute.removeAll()
        crossing.removeAll()
        objective = p
        automatic = p == nil
        heading = nil
        inheritedHeading = nil
        goal = nil
        bestRemaining = nil
        next = nil
    }

    mutating func inheritAutomaticDirection(from source: Self) {
        guard automatic, source.automatic, let direction = source.heading ?? source.inheritedHeading else { return }
        // Different visual islands can disagree on origin. Only a direction
        // has meaning on both; the destination still needs a reachable route.
        inheritedHeading = direction
        interrupt()
    }

    mutating func interrupt() {
        previousVector = nil
        alignment = nil
        selectedRoute.removeAll()
        crossing.removeAll()
        lastTime = nil
        lastPoint = nil
        current = nil
        next = nil
    }

    /// Applies only to a verified translation between two charts of the same room.
    /// Reproject the navigation height and start a fresh physical-motion baseline.
    mutating func translate(by offset: PathPoint, fromHeight oldHeight: Int, toHeight: Int) {
        let dx = offset.x, dy = offset.y + toHeight - oldHeight
        func shift(_ p: PathPoint) -> PathPoint { PathPoint(p.x + dx,p.y + dy) }
        objective = objective.map(shift)
        goal = goal.map(shift)
        let from = max(4,oldHeight/3), to = max(4,toHeight/3)
        walked = Set(walked.map { key in
            let p = shift(.init(key.x*from+from/2,key.y*from+from/2))
            return PathPoint(Int(floor(Double(p.x)/Double(to))),Int(floor(Double(p.y)/Double(to))))
        })
        let oldLane = max(6,oldHeight/2), newLane = max(6,toHeight/2)
        credited = Set(credited.map { sample in
            let horizontal = abs(sample.direction) == 1
            let lane = sample.point.x*oldLane + (horizontal ? dy : dx)
            let along = sample.point.y + (horizontal ? dx : dy)
            return Travel(point:PathPoint(Int((Double(lane)/Double(newLane)).rounded()),along),direction:sample.direction)
        })
        bestRemaining = nil
        interrupt()
    }

    mutating func mergeExploration(from source: Self) {
        walked.formUnion(source.walked)
        credited.formUnion(source.credited)
    }

    // The Knight can arc above the schematic shelf or travel past its end
    // before touching down. Keep only projections onto the planned crossing.
    private func flightProjection(_ position: PathPoint, graph: AtlasPlatformGraph) -> (stroke: PathStroke, point: PathPoint)? {
        guard let first = crossing.first, let last = crossing.last else { return nil }
        let h = graph.knightHeight
        let allowance: Int
        switch first.stroke.kind {
        case .doubleJump: allowance = 9 * h
        case .jump: allowance = 5 * h
        case .drop: allowance = h
        default: return nil
        }
        let besideShelf = crossing.contains {
            (min($0.from.x, $0.to.x) - graph.knightWidth...max($0.from.x, $0.to.x) + graph.knightWidth).contains(position.x)
        }
        let aboveLanding = abs(position.x - last.to.x) <= 2 * h && graph.floors.contains {
            $0.y + h == last.to.y && $0.contains(last.to.x) && $0.contains(position.x)
        }
        let nearTakeoff = abs(position.x - first.from.x) <= 2 * h
            && position.y >= first.from.y - h / 2
        guard besideShelf || aboveLanding || nearTakeoff, position.y >= min(first.from.y, last.to.y) - h / 2,
              position.y <= max(first.from.y, last.to.y) + allowance,
              crossing.allSatisfy({ graph.strokes.contains($0.stroke) }) else { return nil }
        // At a bend both legs can be equally close. Prefer the outgoing leg.
        var best: (stroke: PathStroke, point: PathPoint)?
        for leg in crossing {
            let point = leg.closest(position)
            if let best, best.point.distance(position) < point.distance(position) { continue }
            best = (leg.stroke, point)
        }
        return best
    }

    /// Horizontal turns are drawn at the bottom of each descent for readability.
    /// Follow the validated drift during the fall. Heights along a free fall
    /// correspond to elapsed time squared, with lateral motion linear in time.
    private func driftDirection(at position: PathPoint, height: Int) -> CGFloat? {
        guard let launch = crossing.first, launch.stroke.kind == .drop,
              let index = crossing.firstIndex(where: {
                  $0.from.y >= position.y && position.y > $0.to.y && $0.from.x == $0.to.x
              }) else { return nil }
        let leg = crossing[index]
        let turns = crossing.dropFirst(index + 1).prefix { $0.from.y == leg.to.y && $0.to.y == leg.to.y }
        let destinationX = turns.last?.to.x ?? leg.from.x
        let top = launch.from.y
        func elapsed(_ y: Int) -> Double { sqrt(Double(max(0, top - y))) }
        let startTime = elapsed(leg.from.y)
        let duration = elapsed(leg.to.y) - startTime
        guard duration > 0 else { return nil }
        let lookAhead = max(leg.to.y, position.y - max(1, height / 4))
        let fraction = (elapsed(lookAhead) - startTime) / duration
        let desired = Double(leg.from.x) + Double(destinationX - leg.from.x) * fraction
        let error = desired - Double(position.x)
        return abs(error) <= 0.5 ? 0 : (error > 0 ? 1 : -1)
    }

    mutating func observe(
        _ g: AtlasPlatformGraph, feet: PathPoint, viewWidth: CGFloat,
        timestamp: Double, measured: Bool, grounded: Bool? = nil, revision: Int = 0,
        registered: (PathPoint) -> Bool = { _ in true }
    ) -> (BotRoadHint?, Double?) {
        alignment = nil
        let h = g.knightHeight
        let position = PathPoint(feet.x, feet.y + h)
        let surface = grounded == false ? nil : g.nearestStroke(position, within: grounded == true ? h : max(2,h/3), walkOnly: true)
        let airborne = surface == nil && grounded != true ? flightProjection(position, graph: g) : nil
        guard viewWidth > 0, let line = surface ?? airborne?.stroke ?? g.nearestStroke(position, within: h) else {
            interrupt()
            return (nil, nil)
        }
        let start = airborne?.point ?? line.closest(position)
        let bin = max(4, h / 3)
        func bucket(_ p: PathPoint) -> PathPoint {
            PathPoint(Int(floor(Double(p.x) / Double(bin))), Int(floor(Double(p.y) / Double(bin))))
        }
        if measured && grounded != false && line.kind == .walk && abs(start.y - position.y) <= max(4, h / 3) {
            for p in g.vertices(on: line) where p.distance(start) <= max(h / 2, 4) { walked.insert(bucket(p)) }
        }
        // A click and the Knight can lie mid-edge. Insert both projections in the
        // local search, splitting exactly the two touched edges without rebuilding the map.
        var nodes = g.nodes
        func attach(_ p: PathPoint, edge: PathStroke) {
            guard nodes[p] == nil else { return }
            let on = g.vertices(on: edge)
            for n in [on.filter { $0 < p }.max(), on.filter { p < $0 }.min()].compactMap({ $0 }) {
                if edge.allowsTraversal(from: p, to: n) { nodes[p, default: []].append(n) }
                if edge.allowsTraversal(from: n, to: p) { nodes[n, default: []].append(p) }
            }
        }
        attach(start, edge: line)
        let oldGoal = automatic ? goal : objective
        var projection: PathPoint?
        if let oldGoal, let stroke = g.nearestStroke(oldGoal, within: h, walkOnly: automatic) {
            let projected = stroke.closest(oldGoal)
            projection = projected
            attach(projected, edge: stroke)
            if stroke == line && projected != start && line.allowsTraversal(from: start, to: projected),
               !g.vertices(on: line).contains(where: { min(start, projected) < $0 && $0 < max(start, projected) }) {
                nodes[start, default: []].append(projected)
            }
        }
        let search = PathSearch(from: start, graph: nodes) { point in
            if !automatic || registered(point) { return true }
            // A jump shelf is drawn slightly above the higher walk line. The
            // extra height is diagram space, not an unexplored landing.
            let shelfLift = max(2, h / 4)
            guard !g.walkNodes.contains(point), registered(PathPoint(point.x, point.y - shelfLift)) else { return false }
            return g.strokes.contains {
                ($0.kind == .jump || $0.kind == .doubleJump) && $0.horizontal && $0.contains(point)
            }
        }
        // A revision can add a distant ledge, or shift this floor a few rows.
        // Finish the current reachable destination before choosing another.
        var target = projection
        if automatic {
            // A frontier chosen before the next ledge was visible can sit a few
            // pixels beyond its takeoff. Continue via that takeoff as it comes
            // into reach instead of asking the Knight to visit the old lip first.
            func continuation(past frontier: PathPoint) -> PathPoint? {
                guard line.kind == .walk, line.contains(frontier), start.distance(frontier) <= h else { return nil }
                let exits = g.vertices(on: line).filter { point in
                    point.distance(frontier) <= max(2, g.knightWidth) &&
                        (nodes[point] ?? []).contains { !line.contains($0) }
                }
                guard !exits.isEmpty else { return nil }
                let candidates = g.walkNodes.filter {
                    !line.contains($0) && !walked.contains(bucket($0)) && search.distance[$0] != nil
                }.sorted { (search.distance[$0]!, $0) > (search.distance[$1]!, $1) }
                return candidates.first { candidate in
                    let route = search.route(to: candidate)
                    guard let departure = route.prefix(while: line.contains).last else { return false }
                    return exits.contains(departure)
                }
            }
            func followsHeading(_ p: PathPoint) -> Bool {
                guard let desired = inheritedHeading else { return true }
                guard let next = search.firstHop[p], search.distance[p,default:0] >= max(3,h/3) else { return false }
                return (next.x-start.x)*desired.x + (next.y-start.y)*desired.y > 0
            }
            if let p = projection, search.distance[p] != nil, !walked.contains(bucket(p)),
               start.distance(p) > max(3,h/3), followsHeading(p) {
                target = continuation(past: p) ?? p
            } else {
                let candidates = g.walkNodes.filter { search.distance[$0] != nil && !walked.contains(bucket($0)) }
                let aligned = inheritedHeading == nil ? candidates : candidates.filter(followsHeading)
                target = (aligned.isEmpty ? candidates : aligned).max {
                    (search.distance[$0]!, $0) < (search.distance[$1]!, $1)
                }
            }
            objective = target
        }
        // Score the goal that was in force when the action was taken, even if
        // that action discovers a farther frontier or finishes the last ground.
        var progress = 0.0
        let dt = timestamp - (lastTime ?? timestamp)
        if measured, let old = projection, let oldDistance = search.distance[old], let best = bestRemaining,
           let lastPoint, let current, let next, dt > 0, dt < 0.45, lastPoint.distance(position) <= 3*h {
            let horizontal = next.x != current.x
            let sign = horizontal ? (next.x-current.x).signum() : (next.y-current.y).signum()
            let previous = horizontal ? current.x : current.y
            let now = horizontal ? start.x : start.y
            let physical = horizontal ? position.x-lastPoint.x : position.y-lastPoint.y
            let count = max(0,min(best-oldDistance, min((now-previous)*sign, physical*sign)))
            var newDistance = count
            if automatic && count > 0 {
                let laneSize = max(6,h/2)
                let lane = Int((Double(horizontal ? current.y : current.x)/Double(laneSize)).rounded())
                newDistance = 0
                for offset in 0..<count {
                    let point = PathPoint(lane,previous + sign*offset)
                    if credited.insert(Travel(point:point,direction:horizontal ? sign : 2*sign)).inserted { newDistance += 1 }
                }
            }
            progress = Double(newDistance)/Double(h)
        }
        let projectedMotion: Double?
        if measured, let vector = previousVector, let lastPoint, dt > 0, dt < 0.45,
           lastPoint.distance(position) <= 3 * h {
            projectedMotion = (Double(position.x - lastPoint.x) * vector.dx
                              + Double(position.y - lastPoint.y) * vector.dy) / Double(h)
        } else { projectedMotion = nil }
        guard let target, let remaining = search.distance[target] else {
            goal = nil
            bestRemaining = nil
            interrupt()
            alignment = projectedMotion
            return (nil, measured && automatic ? progress : nil)
        }
        var origin = start
        var waypoint = search.firstHop[target] ?? start
        if airborne == nil || target != projection {
            crossing.removeAll(keepingCapacity: true)
            let takeoffTolerance = max(2, min(g.knightWidth / 2, h / 2))
            let preparationDistance = max(takeoffTolerance, 2 * h)
            if waypoint != start {
                let route = search.route(to: target)
                for (a, b) in zip(route, route.dropFirst()) {
                    if crossing.isEmpty {
                        if line.kind == .walk && line.contains(b) { continue }
                        guard start.distance(a) <= preparationDistance else { break }
                    }
                    guard let stroke = g.strokes.first(where: {
                        $0.kind != .walk && $0.kind != .wall && $0.allowsTraversal(from: a, to: b)
                    }) else { break }
                    crossing.append(CrossingLeg(stroke: stroke, from: a, to: b))
                    if g.walkNodes.contains(b) { break }
                }
                // Remember the crossing early enough to recognize a real jump
                // launched before the schematic takeoff. Until then retain the
                // walking direction toward that takeoff.
                if let first = crossing.first, line.kind != .walk || start.distance(first.from) <= takeoffTolerance {
                    origin = first.from
                    waypoint = first.to
                }
            }
        }
        if waypoint != origin {
            heading = PathPoint((waypoint.x-origin.x).signum(),(waypoint.y-origin.y).signum())
            inheritedHeading = nil
        }
        if goal == nil || target != projection {
            bestRemaining = nil
        }
        if measured { bestRemaining = min(bestRemaining ?? remaining, remaining) }
        goal = target
        if !automatic { selectedRoute = search.route(to: target) }
        lastTime = measured ? timestamp : nil
        lastPoint = measured ? position : nil
        current = origin
        next = waypoint
        let drift = surface == nil && grounded != true ? driftDirection(at: position, height: h) : nil
        let dx = drift ?? CGFloat(waypoint.x - origin.x)
        let dy = drift == nil ? CGFloat(waypoint.y - origin.y) : -1
        let length = max(1, hypot(dx, dy))
        let hint = BotRoadHint(dx: dx / length, dy: dy / length, clearance: min(1, CGFloat(h) / viewWidth),
                               distance: min(1, CGFloat(remaining) / (4 * viewWidth)), frontier: automatic)
        previousVector = measured && (dx != 0 || dy != 0) ? hint : nil
        alignment = projectedMotion
        return (hint, measured ? progress : nil)
    }
}
