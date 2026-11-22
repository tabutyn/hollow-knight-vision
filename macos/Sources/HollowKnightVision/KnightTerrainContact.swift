import CoreGraphics
import Foundation

struct KnightTerrainSupport {
    var floor: CleanFloorLine?
    var wall: DetectedWall?
}

/// A mask may sweep through several old Knight positions. Find the bright mask
/// and its dark eye again in the actual capture before measuring displacement.
struct KnightFootLocator {
    struct Measurement { let foot: CGPoint; let partial: Bool }
    private var last: (foot: CGPoint, time: Double, identity: Int, generation: UInt64)?
    private var lastFullTime = -Double.infinity

    mutating func locate(_ analysis: GroundEdgeAnalysis, near box: CGRect?, direction: Int, time: Double,
                         room: Int, generation: UInt64) -> Measurement? {
        let scale = CGFloat(analysis.width) / 640
        let prior = last.flatMap { value -> CGPoint? in
            guard value.identity == room, value.generation == generation, time > value.time,
                  time - value.time <= 0.13, time - lastFullTime <= 0.55 else { return nil }
            return value.foot
        }
        let priorSearch = prior.map {
            CGRect(x: $0.x - 18 * scale, y: CGFloat(analysis.height) - $0.y,
                   width: 36 * scale, height: 38 * scale)
        }
        var found = Self.find(analysis, near: box ?? priorSearch, direction: direction, prior: prior)
        // A moving Knight may briefly leave the detector's crop (sometimes it
        // collapses to one horn). Reacquire the full face in the prior image
        // neighborhood. The previous position itself never counts as a sample.
        if found?.partial != false, box != nil, let prior, let priorSearch,
           let face = Self.find(analysis, near: priorSearch, direction: direction, prior: prior),
           !face.partial, abs(face.foot.x - prior.x) <= 18 * scale, abs(face.foot.y - prior.y) <= 18 * scale {
            found = face
        }
        guard let measurement = found else { return nil }
        last = (measurement.foot, time, room, generation)
        if !measurement.partial { lastFullTime = time }
        return measurement
    }

    private static func find(_ analysis: GroundEdgeAnalysis, near box: CGRect?, direction: Int, prior: CGPoint?) -> Measurement? {
        let w = analysis.width, h = analysis.height
        guard let luma = analysis.sourceLuma, let box, w > 0, h > 0,
              luma.count == w * h, !box.isEmpty, [box.minX, box.minY, box.width, box.height].allSatisfy(\.isFinite)
        else { return nil }
        let scale = CGFloat(w) / 640
        let topBox = CGRect(x: box.minX, y: CGFloat(h) - box.maxY, width: box.width, height: box.height)
        let search = topBox.insetBy(dx: -9 * scale, dy: -24 * scale).integral
            .intersection(CGRect(x: 0, y: 0, width: w, height: h))
        guard !search.isNull, search.width <= 105 * scale, search.height <= 155 * scale else { return nil }
        let x0 = Int(search.minX), y0 = Int(search.minY), sw = Int(search.width), sh = Int(search.height)
        var visited = [Bool](repeating: false, count: sw * sh)
        var match: CGPoint?
        var partials = [CGPoint]()
        for sy in 0..<sh {
            for sx in 0..<sw {
                let seed = sy * sw + sx
                if visited[seed] || luma[(y0 + sy) * w + x0 + sx] < 190 { continue }
                var queue = [seed], next = 0, left = sx, right = sx, top = sy, bottom = sy
                visited[seed] = true
                while next < queue.count {
                    let i = queue[next], x = i % sw, y = i / sw
                    next += 1
                    left = min(left, x); right = max(right, x); top = min(top, y); bottom = max(bottom, y)
                    for (nx, ny) in [(x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)] {
                        guard nx >= 0, nx < sw, ny >= 0, ny < sh else { continue }
                        let j = ny * sw + nx
                        if !visited[j] && luma[(ny + y0) * w + nx + x0] >= 190 {
                            queue.append(j); visited[j] = true
                        }
                    }
                }
                let bw = right - left + 1, bh = bottom - top + 1
                guard left > 0, right + 1 < sw, top > 0, bottom + 1 < sh,
                      CGFloat(bw) >= 4 * scale, CGFloat(bw) <= 24 * scale,
                      CGFloat(bh) >= 7 * scale, CGFloat(bh) <= 30 * scale,
                      queue.count * 4 >= bw * bh else { continue }
                // At least two dark eye pixels flanked by the same bright face.
                var eye = 0
                for y in (top + bh / 3)...bottom {
                    let row = (y + y0) * w + x0
                    var bright = false, dark = 0
                    for x in left...right {
                        if luma[row + x] >= 190 { if bright { eye += dark }; bright = true; dark = 0 }
                        else if bright && luma[row + x] <= 75 { dark += 1 }
                    }
                }
                let foot = CGPoint(x: CGFloat(x0) + CGFloat(left + right) / 2,
                                   y: CGFloat(y0 + bottom) + 15 * scale)
                guard foot.y < CGFloat(h) else { continue }
                if eye >= 2 && CGFloat(bw) >= 8 * scale {
                    guard match == nil else { return nil }
                    match = foot
                } else if let prior, abs(direction) == 1, CGFloat(bw) <= 16 * scale,
                          CGFloat(bh) <= 29 * scale, queue.count * 3 >= bw * bh {
                    // Moving into a foreground lip can hide the eye. The outer
                    // cheek remains on the opposite side of the collision.
                    let outer = CGFloat(x0 + (direction < 0 ? right : left))
                    let x = outer + CGFloat(direction) * 7 * scale
                    if abs(x - prior.x) <= 15 * scale && abs(foot.y - prior.y) <= 27 * scale {
                        partials.append(CGPoint(x: x, y: foot.y))
                    }
                }
            }
        }
        if let match { return Measurement(foot: match, partial: false) }
        return partials.count == 1 ? Measurement(foot: partials[0], partial: true) : nil
    }
}

/// Two measured positions at the same elevation bracket the floor between them
/// when horizontal input accounts for the travel and the camera stays level.
/// Missing sprite pixels do not themselves add ground.
private struct KnightLevelWalk {
    private struct Run {
        let started: Double
        let y: CGFloat               // world pixels, y increasing downward
        let cameraY: CGFloat
        var lastSeen: Double
        var lastX: CGFloat
        var left: CGFloat
        var right: CGFloat
        var sightings = 1
        var leftSeconds = 0.0
        var rightSeconds = 0.0
        var confirmed = false
        var offRowSince: Double?
        var standingSince: Double?
        var establishedStandingLevel = false
    }
    private var run: Run?
    private var previousTime: Double?
    private var previousDirection = 0
    private var previousCamera: CGPoint?
    private var previousCameraTime: Double?
    private var screenRest: (foot: CGPoint, since: Double, seen: Double)?

    mutating func observe(foot: CGPoint?, camera: CGPoint?, direction: Int, time: Double,
                          width: Int, height: Int, jump: Bool) -> CleanFloorLine? {
        guard !jump, time.isFinite, width > 0, height > 0 else { self = Self(); return nil }
        let scale = CGFloat(width) / 640
        let dt = time - (previousTime ?? time)
        if !(0...0.18).contains(dt) || previousTime == time {
            self = Self()
        }
        // Before the first registered camera pair the stationary sprite can
        // establish an elevation prior. It contributes no world coordinates.
        if camera == nil, direction == 0, let foot, foot.x.isFinite, foot.y.isFinite {
            if let prior = screenRest, time - prior.seen <= 0.15,
               hypot(foot.x - prior.foot.x, foot.y - prior.foot.y) <= 2.5 * scale {
                screenRest = (prior.foot, prior.since, time)
            } else { screenRest = (foot, time, time) }
        }
        if var r = run {
            let sinceCamera = time - (previousCameraTime ?? time)
            var cameraDiscontinuity = false
            if let next = camera, let last = previousCamera {
                cameraDiscontinuity = abs(next.x - last.x) > (5 + 550 * sinceCamera) * scale
                    || abs(next.y - r.cameraY) > 3.5 * scale
            }
            if sinceCamera > 0.18 || cameraDiscontinuity || time - r.lastSeen > 1.25 {
                run = nil
            } else {
                if previousDirection == -1 || direction == -1 { r.leftSeconds += dt }
                if previousDirection == 1 || direction == 1 { r.rightSeconds += dt }
                run = r
            }
        }
        previousTime = time
        previousDirection = direction
        guard let camera, camera.x.isFinite, camera.y.isFinite else { return nil }
        previousCamera = camera
        previousCameraTime = time
        guard let foot, foot.x.isFinite, foot.y.isFinite else { return nil }
        let x = foot.x + camera.x, y = foot.y - camera.y
        func seed() -> Run {
            var seeded = Run(started: time, y: y, cameraY: camera.y, lastSeen: time, lastX: x, left: x, right: x)
            if let rest = screenRest, rest.seen - rest.since >= 0.14, time - rest.seen <= 0.15,
               hypot(foot.x - rest.foot.x, foot.y - rest.foot.y) <= 2.5 * scale {
                seeded.establishedStandingLevel = true
            }
            return seeded
        }
        guard var r = run else { run = seed(); return nil }
        let dx = x - r.lastX, dy = abs(y - r.y)
        // The mask bobs during walking and attacks. A larger excursion, or
        // travel inconsistent with the keys since the last sighting, starts a
        // different interval. There is no velocity-only extension of a floor.
        // Idle and attack sprites can lean sideways while the feet stay put.
        // No direction means no growth of the measured span.
        let lean: Double = direction == 0 ? 12 : 4
        guard dy <= 9 * scale, dx <= (lean + 480 * r.rightSeconds) * scale,
              -dx <= (lean + 480 * r.leftSeconds) * scale else { run = seed(); return nil }
        guard dy <= 2.5 * scale else {
            // A running animation can bob for part of a stride. A sustained
            // change in observed elevation needs a new level measurement.
            let since = r.offRowSince ?? time
            r.offRowSince = since
            let levelEstablished = r.confirmed || r.establishedStandingLevel
            run = !levelEstablished || time - since > 0.35 ? seed() : r
            return nil
        }
        r.offRowSince = nil
        if direction == 0 && abs(dx) <= 3 * scale {
            let since = r.standingSince ?? time
            r.standingSince = since
            if time - since >= 0.14 { r.establishedStandingLevel = true }
        } else { r.standingSince = nil }
        let currentRow = Int((r.y + camera.y).rounded())
        if r.leftSeconds > 0 { r.left = min(r.left, x) }
        if r.rightSeconds > 0 { r.right = max(r.right, x) }
        r.sightings += 1
        r.confirmed = r.confirmed || (time - r.started >= 0.24 && r.sightings >= 4 && r.right - r.left >= 4 * scale)
        r.lastSeen = time
        r.lastX = x
        r.leftSeconds = 0
        r.rightSeconds = 0
        run = r
        guard r.confirmed, abs(direction) == 1, (0..<height).contains(currentRow) else { return nil }
        let left = max(0, Int((r.left - camera.x - 4 * scale).rounded()))
        let right = min(width - 1, Int((r.right - camera.x + 4 * scale).rounded()))
        return left < right ? .init(row: currentRow, xRange: left...right) : nil
    }
}

struct KnightTerrainContactTracker {
    private struct Sample {
        let t: Double
        let foot: CGPoint  // camera-compensated, top-left pixel axes
        let direction: Int
        let partial: Bool
    }
    private var samples = [Sample]()
    private var roomID: Int?
    private var generation: UInt64?
    private var frameWidth: Int?
    private var blockedAt: (x: CGFloat, direction: Int)?
    private var lastMissingFootTime = -Double.infinity
    private var levelWalk = KnightLevelWalk()
    private var lastMeasuredIsland: Int?
    private var awaitingCamera = false
    mutating func observe(foot: CGPoint?, camera: CGPoint?, direction: Int, time: Double,
                          room: Int, generation: UInt64, width: Int, height: Int,
                          walls: [DetectedWall], partial: Bool = false, jumpHeld: Bool = false,
                          measuredIsland: Int? = nil) -> KnightTerrainSupport {
        if roomID != room || self.generation != generation || frameWidth != width {
            self = Self()
        }
        roomID = room; self.generation = generation; frameWidth = width
        if camera != nil {
            if awaitingCamera, let lastMeasuredIsland, lastMeasuredIsland != measuredIsland { levelWalk = .init() }
            lastMeasuredIsland = measuredIsland
        }
        awaitingCamera = camera == nil
        var result = KnightTerrainSupport(floor: levelWalk.observe(foot: partial ? nil : foot, camera: camera,
            direction: direction, time: time, width: width, height: height, jump: jumpHeld))
        if samples.last.map({ time <= $0.t || time - $0.t > 0.13 }) == true {
            samples.removeAll(keepingCapacity: true)
            blockedAt = nil
        }
        if blockedAt?.direction != direction { blockedAt = nil }
        guard let camera, let foot, time.isFinite, camera.x.isFinite, camera.y.isFinite,
              foot.x.isFinite, foot.y.isFinite, width > 0, height > 0 else {
            lastMissingFootTime = time
            samples.removeAll(keepingCapacity: true)
            blockedAt = nil
            return result
        }
        let scale = CGFloat(width) / 640
        let world = CGPoint(x: camera.x + foot.x, y: foot.y - camera.y)
        samples.append(.init(t: time, foot: world, direction: direction, partial: partial))
        samples.removeAll { time - $0.t > 0.55 }
        if samples.count > 96 { samples.removeFirst(samples.count - 96) }
        let recent = samples.filter { time - $0.t <= 0.24 }
        guard let first = recent.first, time - first.t >= 0.16, recent.count >= 3 else { return result }
        func spread(_ values: [CGFloat]) -> CGFloat { (values.max() ?? 0) - (values.min() ?? 0) }
        guard first.t > lastMissingFootTime, abs(direction) == 1, recent.allSatisfy({ $0.direction == direction }),
              spread(recent.map { $0.foot.x }) <= 3.5 * scale else { return result }
        // The white mask leans toward the facing side of the collider.
        let face = foot.x + CGFloat(direction) * 3 * scale
        let torso = foot.y - 14 * scale
        let adjacent = walls.contains { wall in
            wall.solidOnRight == (direction > 0) && abs(CGFloat(wall.x) - face) <= 8 * scale
                && CGFloat(wall.top) <= torso + 15 * scale && CGFloat(wall.bottom) >= torso - 15 * scale
        }
        // A movement lock can also make held input appear blocked. Require
        // corroboration from the approach, a wall edge, or continuing sprite motion.
        let approach = samples.prefix { $0.t < first.t }.reversed().prefix { $0.t > lastMissingFootTime && $0.direction == direction }
            .contains { (first.foot.x - $0.foot.x) * CGFloat(direction) >= 4 * scale }
        let continuing = blockedAt.map { abs($0.x - world.x) <= 3 * scale } ?? false
        let occluded = recent.filter(\.partial).count >= 3
        let sliding = spread(recent.map { $0.foot.y }) >= 8 * scale
        if approach || adjacent || continuing || occluded || sliding {
            if !continuing { blockedAt = (world.x, direction) }
            let x = Int(face.rounded())
            let sweep = recent.map { $0.foot.y + camera.y - 14 * scale }
            let a = max(0, Int(((sweep.min() ?? torso) - 5 * scale).rounded()))
            let b = min(height - 1, Int(((sweep.max() ?? torso) + 7 * scale).rounded()))
            if (0..<width).contains(x), a < b {
                result.wall = .init(x: x, top: a, bottom: b, solidOnRight: direction > 0)
            }
        }
        return result
    }
}
