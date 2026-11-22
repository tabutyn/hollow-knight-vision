import Foundation

/// Corroborate a visual obstruction at the same room coordinates before it
/// changes a route. A moving enemy or a slash can have a strong vertical edge.
struct VisualPathWallHistory {
    private struct Sample {
        let time: Double
        let walls: [PathWall]
        let floorEdges: [PathPlatform]
    }
    private var samples = [Sample]()

    mutating func observe(_ walls: [PathWall], floorEdges: [PathPlatform], time: Double) -> [PathWall] {
        guard time.isFinite else { return [] }
        if let previous = samples.last {
            guard time != previous.time else { return [] }
            if !(0...0.25).contains(time - previous.time) { samples.removeAll() }
        }
        samples.append(.init(time: time, walls: walls, floorEdges: floorEdges))
        if samples.count > 4 { samples.removeFirst() }
        guard corroborated else { return [] }
        return walls.compactMap { wall in
            var common = wall
            for sample in samples.dropLast() {
                let matches = sample.walls.filter { abs($0.x - wall.x) <= 3 && $0.solidSide == wall.solidSide }
                func overlap(_ other: PathWall) -> Int { min(common.high, other.high) - max(common.low, other.low) }
                guard let best = matches.max(by: { overlap($0) < overlap($1) }), overlap(best) > 0 else { return nil }
                common.low = max(common.low, best.low)
                common.high = min(common.high, best.high)
            }
            common.confirmedByContact = common.confirmedByContact && samples.dropLast().contains { sample in
                sample.walls.contains { $0.confirmedByContact && $0.solidSide == wall.solidSide &&
                    abs($0.x - wall.x) <= 3 && $0.low < common.high && $0.high > common.low }
            }
            return common
        }
    }

    /// Remember where two images independently showed an uninterrupted floor
    /// edge. Exclude standing obstructions observed in either view.
    func exposedFloors(height: Int, width: Int) -> [PathPlatform] {
        guard samples.count >= 2 else { return [] }
        let latest = samples[samples.count - 1], prior = samples[samples.count - 2]
        let walls = latest.walls + prior.walls
        var result = [PathPlatform]()
        for floor in latest.floorEdges {
            for other in prior.floorEdges where abs(other.y - floor.y) <= 2 {
                var left = max(other.left, floor.left)
                let right = min(other.right, floor.right)
                guard right - left > width else { continue }
                let body = floor.y + height / 2
                let cuts = walls.filter { left < $0.x && $0.x < right && $0.low <= body && body < $0.high }
                    .map(\.x).sorted()
                for x in cuts {
                    if x - left > width { result.append(.init(left: left, right: x - 1, y: floor.y)) }
                    left = x + 1
                }
                if right - left > width { result.append(.init(left: left, right: right, y: floor.y)) }
            }
        }
        return result
    }

    private var corroborated: Bool {
        samples.count == 4 && samples[3].time - samples[0].time >= 0.25
    }

    /// Retire only the body-height portion that several clear images contradict.
    /// Occluded, offscreen and stitched-over pixels provide no such evidence.
    func clearCrossings(of wall: PathWall, height: Int, width: Int) -> [PathPlatform] {
        guard corroborated, let latest = samples.last else { return [] }
        let margin = max(2, width / 2)
        func crosses(_ floor: PathPlatform) -> Bool {
            floor.left + margin <= wall.x && wall.x <= floor.right - margin
        }
        return latest.floorEdges.filter { floor in
            guard crosses(floor), floor.y < wall.high, floor.y + height > wall.low else { return false }
            return samples.allSatisfy { sample in
                sample.floorEdges.contains { crosses($0) && abs($0.y - floor.y) <= 2 }
                    && !sample.walls.contains {
                        abs($0.x - wall.x) <= 3 && $0.high > floor.y && $0.low < floor.y + height
                    }
            }
        }
    }
}
