import Foundation

/// Enemy boxes use the Knight as their origin. All lengths are fractions of image width, with Y up.
struct BotCombat {
    struct Target {
        let dx, dy, width, height: Double
        let knightWidth, knightHeight: Double
        var distance: Double { hypot(dx, dy) }
        var distanceX: Double { max(0, abs(dx) - width / 2) }
        var distanceY: Double { max(0, abs(dy) - height / 2) }
    }

    enum Encounter {
        case engage(Target), reacquire
    }

    private var room: Int?
    private var generation: UInt64?
    private var lastSeen = -Double.infinity
    private var lastEnemyPosition: CGPoint?
    private var lastSwing = -Double.infinity
    private var lastJump = -Double.infinity
    private var inReachSince: Double?
    private var outsideReachSince: Double?

    mutating func observe(_ row: BotObservation) -> Encounter? {
        let time = row.captureTimestamp
        if row.roomID != room || row.generation != generation { self = Self() }
        room = row.roomID
        generation = row.generation
        let features = row.features
        let detected = row.detectionTimestamp
        let freshActors = detected.map { (0...BotObservationContract.maximumDetectionAge).contains(time - $0) } == true
        if freshActors, row.enemySource != "visual-unavailable", features[4] > 0 {
            var targets = [Target]()
            for i in 0..<BotObservationContract.enemyCapacity {
                let base = 96 + i * 6
                guard features[base + 5] == 1, features[base + 4] >= 0.5,
                      features[base + 2] > 0, features[base + 3] > 0 else { continue }
                targets.append(Target(dx: features[base], dy: features[base + 1],
                    width: features[base + 2], height: features[base + 3],
                    knightWidth: max(0.012, features[2]), knightHeight: max(0.02, features[3])))
            }
            if let nearest = targets.min(by: { $0.distance < $1.distance }) {
                var target = nearest
                if let previous = lastEnemyPosition, time - lastSeen <= 0.35 {
                    // Resolve nearly tied enemies by continuity to avoid turning on every frame.
                    let nearby = targets.filter { $0.distance <= nearest.distance + nearest.knightWidth }
                    func displacement(_ t: Target) -> Double {
                        hypot(features[0] + t.dx - previous.x, features[1] + t.dy - previous.y)
                    }
                    if let match = nearby.min(by: { displacement($0) < displacement($1) }),
                       displacement(match) <= 2 * nearest.knightWidth { target = match }
                }
                lastEnemyPosition = CGPoint(x: features[0] + target.dx, y: features[1] + target.dy)
                lastSeen = detected ?? time
                return .engage(target)
            }
        }
        // A detection flicker is common during a swing. Release buttons while looking again.
        inReachSince = nil
        outsideReachSince = nil
        if time - lastSeen <= 0.35 { return .reacquire }
        lastEnemyPosition = nil
        return nil
    }

    mutating func action(_ target: Target, actor: BotAction, learnedCombat: Bool,
                         previous: BotAction, now: Double) throws -> BotAction {
        let toward = target.dx < 0 ? 1 : 2
        let reach = max(1.5 * target.knightWidth, 0.75 * target.knightHeight)
        let verticalStrike = abs(target.dy) > abs(target.dx) && target.distanceX <= target.knightWidth
        let inReach = verticalStrike
            ? target.distanceY <= target.knightHeight / 2 + reach
            : target.distanceX <= target.knightWidth / 2 + reach && target.distanceY <= target.knightHeight / 2
        if inReach { inReachSince = inReachSince ?? now } else { inReachSince = nil }
        let approach = !inReach || target.distanceX > reach
        let aligned = abs(target.dx) < target.knightWidth / 2
        var movement = approach && !aligned ? toward : (learnedCombat ? actor.values[0] : 0)
        // Give the actor space for a retreat or a jumping approach just outside nail range.
        let close = target.distanceX <= target.knightWidth / 2 + 1.5 * reach &&
            target.distanceY <= target.knightHeight / 2 + reach
        if close && !inReach { outsideReachSince = outsideReachSince ?? now } else { outsideReachSince = nil }
        if learnedCombat && close && (inReach || now - (outsideReachSince ?? now) < 0.4) {
            movement = actor.values[0]
        }
        var aim = verticalStrike ? (target.dy > 0 ? 1 : 2) : 0
        var jump = learnedCombat || approach ? actor.values[2] : 0
        let sinceSwing = now - lastSwing
        let demonstrationSwing = learnedCombat && actor.values[3] == 1
        // One input edge per swing. An older or timid actor still attacks in range.
        let fallbackDue = inReach && (!learnedCombat || now - (inReachSince ?? now) >= 0.2) &&
            sinceSwing >= (learnedCombat ? 0.65 : 0.42)
        let swing = previous.values[3] == 0 && (fallbackDue || (close && demonstrationSwing && sinceSwing >= 0.28))
        if swing {
            lastSwing = now
            if !verticalStrike { movement = toward }
            if learnedCombat && abs(target.dy) > target.knightHeight * 0.25 && actor.values[1] != 0 {
                aim = actor.values[1]
            }
        }
        if !learnedCombat && target.dy > target.knightHeight && target.distanceX < reach * 3 {
            if now - lastJump >= 0.65 && previous.values[2] == 0 { lastJump = now }
            jump = now - lastJump < 0.2 ? 1 : 0
        }
        return try BotAction(values: [movement, aim, jump, swing ? 1 : 0])
    }
}
