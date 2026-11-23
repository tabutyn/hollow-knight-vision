import Foundation

// Present only on an exact matched receiver frame. Reserve SOUL cannot start Focus.
struct BotRecoveryInput: Codable, Equatable, Sendable {
    let soul, health, maxHealth: Int
    let canFocus: Bool
}

struct BotSceneActor: Codable, Equatable {
    let id: Int
    let dx, dy: Double
    var distance: Double { hypot(dx, dy) }
}

struct BotRouteTarget: Codable, Equatable {
    let chart: String
    let goal, waypoint: PathPoint
    let hint: BotRoadHint

    func matches(_ other: Self) -> Bool {
        chart == other.chart && goal == other.goal && waypoint == other.waypoint && hint.frontier == other.hint.frontier
    }
}

enum BotPolicyTarget: Codable, Equatable {
    case traverse(BotRouteTarget), fight(Int), lifeblood(Int), focus
}

struct BotPolicyBinding: Codable, Equatable {
    let room: Int
    let target: BotPolicyTarget
}

struct BotPolicyScene: Codable, Equatable {
    let room: Int
    let grounded: Bool
    let recovery: BotRecoveryInput?
    let enemies, cocoons: [BotSceneActor]
    var routes: [BotRouteTarget] = []

    var focusOpening: Bool {
        grounded && !enemies.contains { abs($0.dx) < 0.20 && abs($0.dy) < 0.16 }
    }
    var canStartFocus: Bool {
        guard let recovery else { return false }
        return focusOpening && recovery.canFocus && (33...99).contains(recovery.soul)
            && recovery.health > 0 && recovery.health < recovery.maxHealth
    }
    func allows(_ binding: BotPolicyBinding) -> Bool {
        guard room == binding.room else { return false }
        switch binding.target {
        case .traverse(let target): return routes.contains { $0.matches(target) }
        case .fight(let id): return enemies.contains { $0.id == id }
        case .lifeblood(let id): return cocoons.contains { $0.id == id }
        case .focus: return canStartFocus
        }
    }
}
