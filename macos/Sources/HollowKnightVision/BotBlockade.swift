import Foundation

/// Bottom-left camera origin and scale in the owning atlas coordinate system.
struct BotBlockadePose: Codable, Equatable {
    let space: String
    let x, y, width: Double
    var valid: Bool { !space.isEmpty && [x, y, width].allSatisfy(\.isFinite) && width > 0 }

    static func visualSpace(_ island: LiveIslandCapture) -> String {
        "visual/\(island.roomID)/\(island.id)/\(island.startedAt.bitPattern)"
    }
    static func visual(_ island: LiveIslandCapture, width: Double) -> Self? {
        guard island.measured else { return nil }
        return Self(space: visualSpace(island), x: island.localPose.x, y: island.localPose.y, width: width)
    }
    static func hacker(_ sample: ReceiverGroundTruthSample) -> Self? {
        guard sample.cameraAvailable, sample.heroAvailable, !sample.sceneName.isEmpty,
              let w = sample.projectionPixelWidth, let h = sample.projectionPixelHeight,
              let sx = sample.pixelsPerWorldUnitX, let sy = sample.pixelsPerWorldUnitY,
              w > 0, h > 0, sx > 0, sy > 0, abs(sx / sy - 1) < 0.02 else { return nil }
        return Self(space: "hacker/" + sample.sceneName, x: sample.cameraX - Double(w) / (2 * sx),
                    y: sample.cameraY - Double(h) / (2 * sy), width: Double(w) / sx)
    }
}

struct BotBlockadeMark: Codable, Equatable {
    let space: String
    let x, y: Double
    let direction: Int
    let viewWidth: Double
    var crossed: Bool
}

struct BotBlockadeEvidence: Codable, Equatable {
    let mark: BotBlockadeMark
    // Target offset from the present Knight, normalized by image width.
    let dx, dy: Double

    func blocksApproach(to encounter: BotCombat.Encounter?) -> Bool {
        guard !mark.crossed else { return false }
        guard case let .engage(enemy) = encounter else { return true }
        // An enemy on the Knight's side of the contact is still reachable.
        let side = Double(mark.direction)
        return enemy.dx * side - enemy.width / 2 > max(0, dx * side)
    }
}

struct BotBlockadeMap {
    private(set) var marks = [BotBlockadeMark]()
    private(set) var revision = 0
    static func file(for domain: BotDomain, world: URL? = nil) -> URL {
        (world ?? BotModelStore.defaultRoot).appendingPathComponent("blockade-map/\(domain.rawValue).json")
    }
    mutating func register(_ mark: BotBlockadeMark, radius: Double) {
        if let i = marks.indices.min(by: { distance(to: mark, at: $0) < distance(to: mark, at: $1) }),
           distance(to: mark, at: i) <= radius {
            if marks[i].crossed != mark.crossed { marks[i].crossed = mark.crossed; revision += 1 }
        } else { marks.append(mark); revision += 1 }
    }
    private func distance(to mark: BotBlockadeMark, at index: Int) -> Double {
        let other = marks[index]
        guard other.space == mark.space, other.direction == mark.direction else { return .infinity }
        return hypot(mark.x - other.x, mark.y - other.y)
    }
    mutating func cross(_ index: Int) { marks[index].crossed = true; revision += 1 }
}

struct BotBlockadeTracker {
    private struct Sample {
        let x, y, time: Double
        func moved(_ offset: CGPoint) -> Self { Self(x: x + offset.x, y: y + offset.y, time: time) }
    }
    private struct Push {
        let direction: Int
        var anchor: Sample
        var samples: Int
    }
    private var previous: Sample?
    private var push: Push?
    private var space: String?
    private var generation: UInt64?
    private var lastDetection: Double?
    private var lastEvidence: BotBlockadeEvidence?

    /// Offsets translate an owning visual island into this frame's island-local coordinates.
    mutating func observe(_ row: BotObservation, pose: BotBlockadePose?, direction: Int,
                          map: inout BotBlockadeMap, offset: (String) -> CGPoint? = { _ in nil }) -> BotBlockadeEvidence? {
        // Bridge one missed sighting without treating an extrapolated Knight as stationary.
        if row.knightPrediction != nil, row.isUsable, generation == row.generation,
           let previous, (0...0.25).contains(row.capturedAt - previous.time), direction == push?.direction { return nil }
        guard let pose, pose.valid, row.issues.allSatisfy({ $0 == "missing-terrain-analysis" }),
              row.knightPrediction == nil,
              row.features.count >= 144, row.features[4] >= 0.5,
              let detection = row.detectionTimestamp,
              (0...BotObservationContract.maximumDetectionAge).contains(row.captureTimestamp - detection)
        else { self = Self(); return nil }
        let time = row.capturedAt, width = pose.width, f = row.features
        let sample = Sample(x: pose.x + f[0] * width, y: pose.y + f[1] * width, time: time)
        if let space, space != pose.space, let shift = offset(space) {
            previous = previous?.moved(shift)
            if let anchor = push?.anchor { push?.anchor = anchor.moved(shift) }
            self.space = pose.space
        }
        if space != pose.space || generation != row.generation || previous.map({
            !(0...0.35).contains(time - $0.time) || hypot(sample.x - $0.x, sample.y - $0.y) > 0.18 * width
        }) == true { self = Self() }
        space = pose.space; generation = row.generation
        guard lastDetection != detection else { return lastEvidence }
        lastDetection = detection
        let sign = Double(direction)
        if abs(direction) == 1, row.grounded != false {
            // Measure the Knight in camera-compensated coordinates. At a camera
            // limit the screen position can still advance, and during camera
            // easing the screen position can change against a stationary door.
            // Mod telemetry also rules out a stationary midair hit reaction.
            let toleranceX = width * max(0.0015, min(0.006, f[2] * 0.15))
            let toleranceY = width * max(0.006, min(0.018, f[3] * 0.25))
            var drive = push ?? Push(direction: direction, anchor: sample, samples: 0)
            if drive.direction != direction || abs(sample.x - drive.anchor.x) > toleranceX
                || abs(sample.y - drive.anchor.y) > toleranceY {
                drive = Push(direction: direction, anchor: sample, samples: 0)
            }
            drive.samples += 1
            if drive.samples >= 2, time - drive.anchor.time >= 0.15 {
                let wall = BotBlockadeMark(space: pose.space, x: sample.x + sign * width * max(0.008, f[2] / 2),
                                           y: sample.y, direction: direction, viewWidth: width, crossed: false)
                map.register(wall, radius: 0.045 * width)
            }
            push = drive
        } else {
            push = nil
        }
        var nearest: BotBlockadeEvidence?
        for i in map.marks.indices where !map.marks[i].crossed {
            let mark = map.marks[i]
            guard let translation = mark.space == pose.space ? CGPoint.zero : offset(mark.space) else { continue }
            let x = mark.x + translation.x, y = mark.y + translation.y
            let dx = (x - sample.x) / width, dy = (y - sample.y) / width
            guard abs(dy) <= 0.065 else { continue }
            let ahead = dx * Double(mark.direction)
            if let previous, ahead < -0.018, abs(previous.y - y) <= 0.065 * width,
               (x - previous.x) * Double(mark.direction) >= -0.018 * width {
                map.cross(i); continue
            }
            if (-0.018...0.095).contains(ahead), nearest.map({ abs(dx) < abs($0.dx) }) ?? true {
                nearest = BotBlockadeEvidence(mark: mark, dx: dx, dy: dy)
            }
        }
        previous = sample
        lastEvidence = nearest
        return nearest
    }
}


/// A visit owns only contacts measured since its fresh start. Historical clip
/// and atlas files are never inputs to a live visit.
struct BotBlockadeVisit {
    private(set) var map = BotBlockadeMap()
    private var tracker = BotBlockadeTracker()
    private var generation: UInt64?
    private var roomID: Int?

    mutating func observe(_ row: BotObservation, pose: BotBlockadePose?, direction: Int,
                          offset: (String) -> CGPoint? = { _ in nil }) -> BotBlockadeEvidence? {
        if generation != row.generation || roomID != row.roomID {
            self = Self()
            generation = row.generation
            roomID = row.roomID
        }
        return tracker.observe(row, pose: pose, direction: direction, map: &map, offset: offset)
    }
}
