import Foundation

/// Center and extent in bottom-left screen coordinates, normalized by frame width.
struct BotObjectBounds: Codable, Equatable {
    let x, y, width, height: Double

    var valid: Bool {
        [x, y, width, height].allSatisfy { $0.isFinite && (0...4).contains($0) }
    }
}

extension BotObservation {
    func nearestObject(in objects: [BotObjectBounds]?) -> BotObjectBounds? {
        guard features.count >= 5, features[4] > 0 else { return nil }
        let x = features[0], y = features[1]
        return objects?.filter(\.valid).min {
            hypot($0.x - x, $0.y - y) < hypot($1.x - x, $1.y - y)
        }
    }

    var nearestGeoCache: BotObjectBounds? { nearestObject(in: geoCaches) }

    var geoTarget: BotCombat.Target? {
        guard let cache = nearestGeoCache, let detectionTimestamp,
              (0...BotObservationContract.maximumDetectionAge).contains(captureTimestamp - detectionTimestamp)
        else { return nil }
        return .init(dx: cache.x - features[0], dy: cache.y - features[1], width: cache.width,
                     height: cache.height, knightWidth: max(0.012, features[2]), knightHeight: max(0.02, features[3]))
    }
}

/// Keep a deposit sighted during a fight while the camera follows the enemy.
struct BotGeoPursuit {
    private var remembered: (space: String, room: Int, generation: UInt64, x: Double, y: Double, time: Double)?
    private var lastSwing = -Double.infinity

    mutating func target(_ row: BotObservation) -> BotCombat.Target? {
        let pose = row.blockadePose
        if let cache = row.nearestGeoCache, let visible = row.geoTarget {
            if let pose, pose.valid {
                remembered = (pose.space, row.roomID, row.generation, pose.x + cache.x * pose.width,
                              pose.y + cache.y * pose.width, row.capturedAt)
            } else { remembered = nil }
            return visible
        }
        guard let saved = remembered, let pose, pose.valid, saved.space == pose.space,
              saved.room == row.roomID, saved.generation == row.generation, row.capturedAt - saved.time <= 20
        else { remembered = nil; return nil }
        guard row.features[4] > 0, row.geoCaches != nil, let timestamp = row.detectionTimestamp,
              (0...BotObservationContract.maximumDetectionAge).contains(row.captureTimestamp - timestamp) else { return nil }
        let x = (saved.x - pose.x) / pose.width, y = (saved.y - pose.y) / pose.width
        if (0.02...0.98).contains(x) {
            // Once the rock disappears, step into its drop before exploring again.
            guard row.capturedAt - lastSwing < 0.65, abs(x - row.features[0]) > row.features[2] / 2
            else { remembered = nil; return nil }
        }
        return .init(dx: x - row.features[0], dy: y - row.features[1], width: 0, height: 0,
                     knightWidth: max(0.012, row.features[2]), knightHeight: max(0.02, row.features[3]))
    }

    mutating func didSwing(at time: Double) { lastSwing = time }
}
