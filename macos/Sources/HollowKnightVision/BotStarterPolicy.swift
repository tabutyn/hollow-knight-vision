import Foundation

/// Both runtimes resolve the same prefix lists in recording order, oldest frame first.
struct BotSkillInputLayout {
    private struct Prefixes: Decodable { let current, history: [String] }
    private static let views = Result {
        let url = Bundle.main.url(forResource: "playing-policy-inputs", withExtension: "json")
            ?? Bundle.module.url(forResource: "playing-policy-inputs", withExtension: "json")
        guard let url else { throw BotRuntimeError.incompatible("Missing playing policy input contract.") }
        return try JSONDecoder().decode([String: Prefixes].self, from: Data(contentsOf: url))
    }

    let skill: BotSkill
    let columns: [Int]
    private let current: [String: Int]
    private let rawWidth: Int

    init(skill: BotSkill, schema: Int) throws {
        guard (14...16).contains(schema), let names = BotObservationContract.policyNames(for: schema),
              let view = try Self.views.get()[skill.rawValue] else {
            throw BotRuntimeError.incompatible("No movement input layout for this policy.")
        }
        self.skill = skill
        let frames = BotObservationContract.historyLength
        rawWidth = names.count * frames
        var selected = [Int](), newest = [String: Int]()
        for frame in 0..<frames {
            let latest = frame == frames - 1
            let prefixes = latest ? view.current : view.history
            for (i, name) in names.enumerated() where prefixes.contains(where: { $0.isEmpty || name.hasPrefix($0) }) {
                if latest { newest[name] = selected.count }
                selected.append(frame * names.count + i)
            }
        }
        columns = selected
        current = newest
    }

    func action(raw: [Float], motor: BotAction?, waypoint: BotRoadHint? = nil) throws -> BotAction {
        guard raw.count == rawWidth else { throw BotRuntimeError.incompatible("Incomplete starter observation.") }
        let input = columns.map { raw[$0] }
        func value(_ name: String) -> Float { current[name].map { input[$0] } ?? 0 }
        func move(_ dx: Float, jump: Bool = false, attack: Bool = false) -> BotAction {
            var buttons: InputBridgeButtons = dx < -0.01 ? .left : dx > 0.01 ? .right : []
            if jump && value("previous_jump") < 0.5 { buttons.insert(.actionZ) }
            if attack && value("previous_attack") < 0.5 { buttons.insert(.actionX) }
            return BotAction(buttons: buttons)
        }
        let enemy = value("enemy_0_present") > 0.5
        let dx = value("enemy_0_x"), dy = value("enemy_0_y")
        let nearby = enemy && abs(dx) < 0.17 && abs(dy) < 0.16
        let blocked = value("blockade_direction")
        switch skill {
        case .fight:
            if enemy { return move(dx, attack: nearby) }
            return motor ?? .neutral
        case .recover:
            if nearby { return move(dx >= 0 ? -1 : 1) }
            if value("health_known") > 0.5, value("health_fraction") <= 2.0 / 16,
               value("lifeblood_cocoon_present") > 0.5 {
                let target = value("lifeblood_cocoon_dx")
                return move(target, attack: abs(target) < 0.10 && abs(value("lifeblood_cocoon_dy")) < 0.12)
            }
            return .neutral
        case .interact:
            return .neutral // Interaction consumes screen cues through SkillSight.
        case .explore, .traverse, .progress:
            if let waypoint, skill == .traverse || skill == .explore {
                return move(Float(waypoint.dx), jump: waypoint.dy > 0.20 || abs(blocked) > 0.5)
            }
            if skill == .progress && value("geo_cache_present") > 0.5 {
                let target = value("geo_cache_dx")
                return move(target, attack: abs(target) < 0.10 && abs(value("geo_cache_dy")) < 0.12)
            }
            for target in ["shared_path", "road"] where value(target + "_present") > 0.5 {
                return move(value(target + "_dx"), jump: abs(blocked) > 0.5)
            }
            if abs(blocked) > 0.5 { return move(blocked, jump: true) }
            if skill == .explore && value("atlas_present") > 0.5 {
                let direction = value("atlas_right") - value("atlas_left")
                if abs(direction) > 0.01 { return move(direction) }
            }
            return motor ?? (skill == .progress ? .neutral : move(1))
        }
    }
}
