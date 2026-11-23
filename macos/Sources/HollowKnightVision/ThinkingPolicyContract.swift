import Foundation

/// The model ranks options from one capture. Only Vision can bind an option to a control.
struct ThinkingPolicyContract {
    private struct Option {
        let id: String
        let description: String
        let action: BotSkillChoice
    }

    private struct Reply: Decodable {
        struct Rank: Decodable {
            let option: String
            let points: Int
        }
        let choices: [Rank]
        let shopping: [BotPurchaseWish]
        let shop: BotSkillPlan.ShopSnapshot?
    }

    private let options: [Option]

    init(sight: SkillSight?, visible: [String]) {
        var offered: [Option] = []
        if let sight, sight.gameplay {
            offered = [
                Self.movement(.explore, "Map nearby unexplored ground or follow the current route."),
                Self.movement(.traverse, "Cross a nearby obstacle using live geometry and the current route."),
                Self.movement(.fight, "Fight a nearby enemy."),
                Self.movement(.recover, "Retreat from danger or seek nearby recovery."),
                Self.movement(.progress, "Follow the current progression route or approach a visible resource.")
            ]
            let prompt = BotSkillPlan.initial.choices.first { choice in
                choice.control == .inspect && visible.contains {
                    RememberedGameText.key($0) == RememberedGameText.key(choice.cue)
                }
            }
            if let prompt {
                offered.append(Option(id: "readPrompt", description: "Read the visible \(prompt.cue) prompt.",
                                      action: prompt))
            }
        } else if let sight, sight.menu == .pause, sight.selected == "pause.continue" {
            offered = [Option(id: "resumeGame", description: "Resume from the selected Continue entry.",
                action: .init(policy: .interact, control: .confirm, points: BotSkill.interact.initialPoints,
                              cue: "pause.continue"))]
        }
        options = offered
    }

    private static func movement(_ skill: BotSkill, _ description: String) -> Option {
        Option(id: skill.rawValue, description: description,
               action: .init(policy: skill, control: .native, points: skill.initialPoints, cue: ""))
    }

    var selectionSpec: String {
        let list = options.map { "\($0.id) (starting points \($0.action.points)) — \($0.description)" }
        return """
            Rank useful options from Vision's list for this captured screen. Use current danger, the current goal, supported quest leads and the Atlas frontier. Vision runs the highest-scoring option that still matches live state.
            Each choice contains only option (copy the exact ID) and points (integer 1 to 100). Use each ID at most once. Return an empty choices array if no option is useful. Vision's policies choose movement, timing and button inputs.

            Available options
            \(list.isEmpty ? "None. Return an empty choices array." : list.joined(separator: "\n"))
            Shopping is advice. Return an empty shopping array unless the evidence supports a wish with item, desire (1 to 100), maxGeo and reserveGeo. Include shop with wallet and offers [{item,price}] only when those amounts are visible.
            """
    }

    var schema: [String: Any] {
        func object(_ properties: [String: Any], required: [String]) -> [String: Any] {
            ["type": "object", "properties": properties, "required": required, "additionalProperties": false]
        }
        func array(_ items: [String: Any], limit: Int) -> [String: Any] {
            ["type": "array", "items": items, "maxItems": limit]
        }
        let points: [String: Any] = ["type": "integer", "minimum": 1, "maximum": 100]
        let geo: [String: Any] = ["type": "integer", "minimum": 0, "maximum": 99999]
        let item: [String: Any] = ["type": "string", "minLength": 1, "maxLength": 100]
        var option: [String: Any] = ["type": "string"]
        if !options.isEmpty { option["enum"] = options.map(\.id) }
        let rank = object(["option": option, "points": points], required: ["option", "points"])
        let wish = object(["item": item, "desire": points, "maxGeo": geo, "reserveGeo": geo],
                          required: ["item", "desire", "maxGeo", "reserveGeo"])
        let offer = object(["item": item, "price": geo], required: ["item", "price"])
        let shop = object(["wallet": geo, "offers": array(offer, limit: 20)], required: ["wallet", "offers"])
        return object(["choices": array(rank, limit: options.count), "shopping": array(wish, limit: 5), "shop": shop],
                      required: ["choices", "shopping"])
    }

    func resolve(_ data: Data, goal: String) throws -> BotSkillPlan {
        guard let fields = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              Set(fields.keys).isSubset(of: ["choices", "shopping", "shop"]),
              let ranks = fields["choices"] as? [[String: Any]],
              ranks.allSatisfy({ Set($0.keys) == ["option", "points"] }) else {
            throw BotRuntimeError.incompatible("Expected option IDs and points from Vision's list.")
        }
        let reply = try JSONDecoder().decode(Reply.self, from: data)
        guard Set(reply.choices.map(\.option)).count == reply.choices.count else {
            throw BotRuntimeError.incompatible("An option may be ranked only once.")
        }
        let choices = try reply.choices.map { rank in
            guard let option = options.first(where: { $0.id == rank.option }) else {
                throw BotRuntimeError.incompatible("Vision did not offer \(rank.option) for this frame.")
            }
            return BotSkillChoice(policy: option.action.policy, control: option.action.control,
                                  points: rank.points, cue: option.action.cue)
        }
        return try BotSkillPlan(objective: goal, success: "Record the policy outcome.", choices: choices,
                               shopping: reply.shopping, shop: reply.shop).validated(allowEmptyChoices: true)
    }
}
