import CoreGraphics
import Foundation

/// The PPO actor sees the two lips and the Knight in the same normalized
/// coordinates used by train_pit_policy.py. It never receives a room or clip ID.
struct BotPitPolicy: Codable {
    let schemaVersion: Int
    let checkpointSHA256: String
    let layers: [BotRuntimeLayer]

    func action(_ input: [Float]) throws -> BotAction {
        let logits = try BotNativePolicy.forward(input, layers: layers)
        let horizontal = (0..<3).max { logits[$0] < logits[$1] } ?? 0
        return try BotAction(values: [horizontal, 0, logits[4] > logits[3] ? 1 : 0, 0])
    }
}

struct BotPitPerception {
    private struct Platform {
        var left, right, y: Double
    }
    private struct Opening {
        let left, right, leftY, rightY, bottom: Double
    }
    private var previous: Opening?
    private var lastSeen = -Double.infinity
    private var generation: UInt64?
    private var room: Int?
    private var landings = 0

    mutating func input(_ row: BotObservation, action: BotAction, requireDropEvidence: Bool = false) -> [Float]? {
        let f = row.features
        guard row.terrainSource == "detected", f.count >= 144, f[6] == 1 else { self = Self(); return nil }
        if row.generation != generation || row.roomID != room || !(0...0.35).contains(row.captureTimestamp - lastSeen) {
            self = Self()
        }
        generation = row.generation; room = row.roomID
        let visible = f[4] > 0
        let x = visible ? f[0] : 0.5
        let feet = visible ? f[1] - f[3] / 2 : nil
        var platforms = [Platform]()
        if let terrain = row.detectedTerrain, terrain.width > 0 {
            platforms = terrain.floors.map {
                Platform(left: $0.minimumX / terrain.width, right: $0.maximumX / terrain.width, y: $0.y / terrain.width)
            }
        } else {
            for i in stride(from: 16, to: 96, by: 5) where f[i + 4] == 1 {
                platforms.append(Platform(left: f[0] + f[i], right: f[0] + f[i+1], y: f[1] + f[i+2]))
            }
        }
        // Join fragments on the same shelf before estimating the mouth width.
        var shelves = [Platform]()
        for p in platforms.sorted(by: { $0.left < $1.left }) {
            if let i = shelves.lastIndex(where: { abs($0.y-p.y) < 0.013 && p.left - $0.right < 0.073 }) {
                shelves[i].right = max(shelves[i].right, p.right)
            } else { shelves.append(p) }
        }
        var best: Opening?
        var bestScore = -Double.infinity
        for a in shelves where a.right-a.left >= 0.035 {
            for b in shelves where b.right-b.left >= 0.035 {
                let width = b.left-a.right
                guard (0.075...0.48).contains(width), abs(a.y-b.y) < 0.25 else { continue }
                let low = min(a.y,b.y)
                let between = shelves.filter { min($0.right,b.left) - max($0.left,a.right) > 0.012 }
                guard !between.contains(where: { $0.y >= low-0.022 }) else { continue }
                let floor = between.map(\.y).filter { (0.028...0.28).contains(low-$0) }.max()
                let distance = max(0, a.right-x, x-b.left)
                let continuous = previous.map {
                    abs($0.left-a.right) < 0.105 && abs($0.right-b.left) < 0.105 &&
                    abs($0.leftY-a.y) < 0.065 && abs($0.rightY-b.y) < 0.065
                } ?? false
                if continuous {
                    guard distance < 0.34 else { continue }
                } else {
                    guard distance < (visible ? 0.045 : 0.075),
                          feet.map({ $0 < low-0.02 }) ?? true else { continue }
                    // On flat ground the Knight and the pixels beneath its feet can
                    // disappear together. Two coplanar fragments alone do not prove a drop.
                    if requireDropEvidence && floor == nil {
                        guard let feet, feet < low - max(0.045, f[3] * 0.5) else { continue }
                    }
                    // When both the bottom and the Knight are obscured, require
                    // a broad opening directly beneath the camera center.
                    if floor == nil && feet == nil && (width < 0.13 || distance > 0.04) { continue }
                }
                let bottom = floor ?? (continuous ? previous?.bottom : nil) ?? min(feet ?? low-0.07, low-0.035)
                let score = (continuous ? 2.0 : 0) + (floor == nil ? 0 : 1) - distance*3 - abs((a.right+b.left)/2-x)
                if score > bestScore {
                    bestScore = score
                    best = Opening(left: a.right, right: b.left, leftY: a.y, rightY: b.y, bottom: bottom)
                }
            }
        }
        if best != nil { lastSeen = row.captureTimestamp }
        guard let hole = best ?? previous, row.captureTimestamp-lastSeen <= 0.25 else { previous = nil; return nil }
        previous = hole
        let y = feet ?? hole.bottom
        let onLeft = x < hole.left-0.012 && abs(y-hole.leftY) < 0.025
        let onRight = x > hole.right+0.012 && abs(y-hole.rightY) < 0.025
        landings = visible && (onLeft || onRight) ? landings+1 : 0
        if landings >= 2 { previous = nil; landings = 0; return nil }
        let geometry = [hole.left-x, hole.right-x, hole.leftY-y, hole.rightY-y, y-hole.bottom]
        return geometry.map { Float(min(4, max(-4, $0*6))) } +
            [visible ? 1 : 0, action.previousFeatures[0], Float(action.values[2])]
    }
}
