import Foundation
import os

/// The model scores handles from one immutable offer. The binding stays in Vision.
struct ThinkingPolicyContract {
    private struct Option {
        let policy: String
        let params: [String: Any]
        let action: BotSkillChoice
    }

    private static let serial = OSAllocatedUnfairLock(initialState: 0)
    let decisionID: Int
    private let options: [Option]

    struct CandidateRow: Identifiable {
        let id: String
        let policy: String
        let target: String
        let parameters: String
        let score: Int?
    }

    // Both the UI and the request are projections of this captured offer.
    func rows(scoredBy plan: BotSkillPlan? = nil) -> [CandidateRow] {
        let choices = plan?.decisionID == decisionID ? plan?.choices ?? [] : []
        return options.enumerated().map { index, option in
            let id = "c\(index)"
            let target: String
            switch option.action.binding?.target {
            case .traverse(let route):
                target = "\(route.hint.frontier ? "Frontier" : "Route") · waypoint (\(route.waypoint.x), \(route.waypoint.y))"
            case .fight(let actor): target = "Enemy \(actor)"
            case .lifeblood(let actor): target = "Lifeblood cocoon \(actor)"
            case .focus: target = "Focus"
            case nil: target = option.action.cue
            }
            let data = try? JSONSerialization.data(withJSONObject: option.params, options: [.sortedKeys])
            return CandidateRow(id: id, policy: option.policy, target: target,
                parameters: data.map { String(decoding: $0, as: UTF8.self) } ?? "",
                score: choices.first { $0.candidateID == id }?.points)
        }
    }

    init(sight: SkillSight?, visible: [String]) {
        decisionID = Self.serial.withLock { $0 += 1; return $0 }
        var offered: [Option] = []
        func add(_ wire: String, _ params: [String: Any], _ skill: BotSkill, _ control: SkillControl,
                 cue: String = "", target: BotPolicyTarget? = nil) {
            let binding = target.flatMap { target in sight?.scene.map { BotPolicyBinding(room: $0.room, target: target) } }
            let promptStart = control == .inspect ? sight?.promptStart(for: cue) ?? sight?.capturedAt : nil
            offered.append(Option(policy: wire, params: params, action: BotSkillChoice(policy: skill,
                control: control, points: skill.initialPoints, cue: cue, binding: binding, promptFirstSeen: promptStart)))
        }
        if let sight, sight.gameplay {
            if let scene = sight.scene {
                for (handle, route) in scene.routes.enumerated() {
                    let hint = route.hint
                    add("traverse", ["roomId": scene.room, "routeId": handle, "waypointId": handle,
                                     "road_dx": hint.dx, "road_dy": hint.dy, "road_distance": hint.distance,
                                     "road_frontier": hint.frontier], .traverse, .native, target: .traverse(route))
                }
                // Unity instance IDs may be negative. Only local, unsigned handles go on the wire.
                if let enemy = scene.enemies.first {
                    add("fight", ["enemyId": 0], .fight, .native, target: .fight(enemy.id))
                }
                if scene.canStartFocus { add("recover", ["method": "focus"], .recover, .focus, target: .focus) }
                if let source = scene.cocoons.first {
                    add("recover", ["method": "lifeblood", "sourceId": 0], .recover, .native, target: .lifeblood(source.id))
                }
            }
            for (id, cue) in ["Inspect", "Listen"].enumerated() where visible.filter({
                RememberedGameText.key($0) == RememberedGameText.key(cue)
            }).count == 1 {
                add("interact", ["verb": id == 0 ? "inspect" : "talk", "targetId": id], .interact, .inspect, cue: cue)
            }
        } else if let sight, sight.menu == .pause, sight.selected == "pause.continue" {
            add("menu", ["menuId": 0, "entryId": 0], .interact, .confirm, cue: "pause.continue")
        }
        options = offered
    }

    var offer: [String: Any] {
        var candidates = [String: Any]()
        for (index, option) in options.enumerated() {
            candidates["c\(index)"] = ["policy": option.policy, "params": option.params] as [String: Any]
        }
        return ["version": 2, "decisionId": decisionID, "objective": "expandAtlas", "candidates": candidates]
    }

    var selectionSpec: String {
        let encoded = try! JSONSerialization.data(withJSONObject: offer, options: [.sortedKeys])
        return """
            Score useful candidates in the following offer. Return only scores keyed by offered IDs with integer priorities 1 to 100. Omit unsuitable candidates. An empty scores object abstains. Higher wins, then earlier offer order. Consider immediate danger and supported quest evidence. Traverse follows the offered waypoint. road_frontier marks an exploration target. Recover with focus heals a missing mask.
            \(String(decoding: encoded, as: UTF8.self))
            """
    }

    var schema: [String: Any] {
        let point: [String: Any] = ["type": "integer", "minimum": 1, "maximum": 100]
        let fields = Dictionary(uniqueKeysWithValues: options.indices.map { ("c\($0)", point) })
        let scores: [String: Any] = ["type": "object", "properties": fields, "additionalProperties": false]
        return ["type": "object", "properties": ["scores": scores], "required": ["scores"], "additionalProperties": false]
    }

    func resolve(_ data: Data) throws -> BotSkillPlan {
        guard let fields = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              Set(fields.keys) == ["scores"], let scores = fields["scores"] as? [String: Any],
              Set(scores.keys).isSubset(of: Set(options.indices.map { "c\($0)" })) else {
            throw BotRuntimeError.incompatible("Expected scores keyed by IDs from this decision.")
        }
        var choices = [BotSkillChoice]()
        for (index, option) in options.enumerated() {
            guard let value = scores["c\(index)"] else { continue }
            guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
                  (1...100).contains(number.intValue), number.doubleValue == Double(number.intValue) else {
                throw BotRuntimeError.incompatible("Policy scores must be integers from 1 to 100.")
            }
            var choice = option.action
            choice.points = number.intValue
            choice.candidateID = "c\(index)"
            choices.append(choice)
        }
        return try BotSkillPlan(objective: "expandAtlas", success: "Record the policy outcome.",
            choices: choices, shopping: [], decisionID: decisionID).validated(allowEmptyChoices: true)
    }
}
