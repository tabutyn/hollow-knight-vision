import CoreGraphics
import Foundation

/// A short expert correction bundled with the trained actor. Frame checks use
/// only the live image, visual Knight estimate and detected screen-space floors.
struct BotRecoveryManeuver: Codable {
    struct Step: Codable {
        let time: Double
        let floors: [[Double]]
        let knight: [Double]?
    }
    struct Control: Codable {
        let time: Double
        let values: [Int]
    }
    let session: UUID
    let duration: Double
    let firstSighting: Double
    let startImage: Data
    let track: [Step]
    let controls: [Control]
}

struct BotRecoveryDriver {
    let maneuver: BotRecoveryManeuver
    private let reference: [UInt8]
    private var used = false
    private var room = 0
    private var generation: UInt64 = 0
    private(set) var began: Double?

    init?(_ maneuver: BotRecoveryManeuver) {
        guard (0.4...2.6).contains(maneuver.duration), (0.2...1.2).contains(maneuver.firstSighting),
              maneuver.track.count <= 30, maneuver.track.first?.time == 0,
              maneuver.controls.count <= 128, maneuver.controls.first?.time == 0,
              maneuver.track.allSatisfy({ row in
                  (0...maneuver.duration).contains(row.time) && row.floors.count <= 16 &&
                  row.floors.allSatisfy { $0.count == 3 && $0.allSatisfy(\.isFinite) } &&
                  (row.knight.map { $0.count == 2 && $0.allSatisfy(\.isFinite) } ?? true)
              }), zip(maneuver.track, maneuver.track.dropFirst()).allSatisfy({ $0.time < $1.time }),
              maneuver.controls.allSatisfy({ (0...maneuver.duration).contains($0.time) && (try? BotAction(values: $0.values)) != nil }),
              zip(maneuver.controls, maneuver.controls.dropFirst()).allSatisfy({ $0.time < $1.time }),
              maneuver.startImage.count <= 512 * 1024, let image = ImageFileIO.decode(maneuver.startImage),
              let reference = Self.thumbnail(image) else { return nil }
        self.maneuver = maneuver
        self.reference = reference
    }

    mutating func cancel() { began = nil }

    mutating func action(_ row: BotObservation, image: CGImage?, masked: Bool, now: Double) -> BotAction? {
        let f = row.features
        let floors = stride(from: 16, to: 96, by: 5).compactMap { i -> [Double]? in
            f[i+4] == 1 ? [f[0]+f[i], f[0]+f[i+1], f[1]+f[i+2]] : nil
        }
        if began == nil {
            guard !used, masked, let image, let first = maneuver.track.first,
                  Self.matchesFloors(first.floors, floors, requireRelief: true),
                  let signature = Self.thumbnail(image), Self.matchesView(signature, reference) else { return nil }
            began = now; used = true; room = row.roomID; generation = row.generation
        }
        let elapsed = now - began!
        let observedElapsed = max(0, row.capturedAt - began!)
        guard row.roomID == room, row.generation == generation, elapsed < maneuver.duration,
              maneuver.track.contains(where: { step in
                  guard abs(step.time - observedElapsed) <= 0.3,
                        Self.matchesFloors(step.floors, floors, requireRelief: false) else { return false }
                  if f[4] > 0, let knight = step.knight {
                      return hypot(f[0] - knight[0], f[1] - knight[1]) < 0.11
                  }
                  return true
              }), let edge = maneuver.controls.last(where: { $0.time <= elapsed }) else {
            cancel(); return nil
        }
        return try? BotAction(values: edge.values)
    }

    /// Compare sampled heights rather than segment indices so a split ledge
    /// still matches. Require agreement at two distinct elevations at entry.
    static func matchesFloors(_ expected: [[Double]], _ actual: [[Double]], requireRelief: Bool) -> Bool {
        var overlap = 0, matches = 0
        var lowest = Double.infinity, highest = -Double.infinity
        for i in 0..<48 {
            let x = (Double(i) + 0.5) / 48
            let a = expected.filter { $0[0] <= x && x <= $0[1] }
            let b = actual.filter { $0[0] <= x && x <= $0[1] }
            guard !a.isEmpty, !b.isEmpty else { continue }
            overlap += 1
            if let matched = a.first(where: { line in b.contains { abs($0[2] - line[2]) < 0.016 } }) {
                matches += 1; lowest = min(lowest, matched[2]); highest = max(highest, matched[2])
            }
        }
        return overlap >= 10 && matches * 4 >= overlap * 3 && (!requireRelief || highest - lowest > 0.04)
    }

    private static func thumbnail(_ image: CGImage) -> [UInt8]? {
        let w = 64, h = 36
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w,
                                  space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let pixels = ctx.data?.assumingMemoryBound(to: UInt8.self) else { return nil }
        // Omit the HUD; compare the game view even when health changes.
        return Array(UnsafeBufferPointer(start: pixels.advanced(by: 8*w), count: (h-8)*w))
    }

    private static func matchesView(_ live: [UInt8], _ ref: [UInt8]) -> Bool {
        var squaredError = 0.0, signal = 0.0, sum = 0.0
        for (a,b) in zip(live, ref) {
            let value = Double(b), diff = Double(a) - value
            squaredError += diff * diff; signal += value * value; sum += value
        }
        let variance = signal - sum * sum / Double(ref.count)
        return variance > Double(ref.count) * 16 && squaredError < 0.25 * variance
    }
}
