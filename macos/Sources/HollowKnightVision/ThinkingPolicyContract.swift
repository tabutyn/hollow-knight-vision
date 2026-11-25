import Foundation
import os

/// One model reply selects one complete action from the captured offer.
struct ThinkingPolicyContract {
    private struct Option {
        let policy: String
        let params: [String: Any]
        let evidence: [String: Any]
        var selection: [String: Any] { ["policy": policy, "parameters": params] }
        let action: BotSkillChoice
    }

    private static let objective = "expandAtlas"
    private static let serial = OSAllocatedUnfairLock(initialState: 0)
    let decisionID: Int
    private let options: [Option]
    let menuPage: ThinkingMenuPage?
    private let allowsMore: Bool

    static let policySchemaText = schemaResource("thinking-policy")
    static let menuSchemaText = schemaResource("thinking-menu")

    private static func schemaResource(_ name: String) -> String {
        let url = Bundle.main.url(forResource: name, withExtension: "schema.json")
            ?? Bundle.module.url(forResource: name, withExtension: "schema.json")
        guard let url, let data = try? Data(contentsOf: url),
              let schema = try? JSONSerialization.jsonObject(with: data),
              let compact = try? JSONSerialization.data(withJSONObject: schema, options: [.sortedKeys]) else {
            preconditionFailure("Missing bundled schema " + name)
        }
        return String(decoding: compact, as: UTF8.self)
    }

    struct CandidateRow: Identifiable {
        let id: String
        let policy: String
        let target: String
        let parameters: String
        let selected: Bool
    }

    // Both the UI and the request are projections of this captured offer.
    func rows(selectedBy plan: BotSkillPlan? = nil, script: ThinkingMenuScript? = nil) -> [CandidateRow] {
        if menuPage != nil {
            return menuRequests.enumerated().map { i, request in
                let title: String, target: String
                switch request {
                case .select(let id): title = "Menu · select"; target = id
                case .more: title = "Menu · more"; target = "Advance and capture"
                case .wait: title = "Menu · wait"; target = "Wait for menu evidence"
                }
                return CandidateRow(id: "c\(i)", policy: title, target: target, parameters: Self.json(request.selection),
                                    selected: script?.request == request)
            }
        }
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
            case nil: target = option.action.policy == .explore ? "Find unmapped ground" : option.action.cue
            }
            let data = try? JSONSerialization.data(withJSONObject: option.params, options: [.sortedKeys])
            return CandidateRow(id: id, policy: option.policy, target: target,
                parameters: data.map { String(decoding: $0, as: UTF8.self) } ?? "",
                selected: choices.contains { $0.candidateID == id })
        }
    }

    init(sight: SkillSight?, visible: [String], allowsMore: Bool = true, capturedMenu: ThinkingMenuPage? = nil,
         recognizedGameplay: Bool = false) {
        self.menuPage = capturedMenu ?? (!recognizedGameplay && sight?.gameplay == false ? sight?.menuPage : nil)
        self.allowsMore = allowsMore
        decisionID = Self.serial.withLock { $0 += 1; return $0 }
        var offered: [Option] = []
        func add(_ wire: String, _ params: [String: Any], _ skill: BotSkill, _ control: SkillControl,
                 cue: String = "", target: BotPolicyTarget? = nil, evidence: [String: Any] = [:]) {
            let binding = target.flatMap { target in sight?.scene.map { BotPolicyBinding(room: $0.room, target: target) } }
            let promptStart = control == .inspect ? sight?.promptStart(for: cue) ?? sight?.capturedAt : nil
            offered.append(Option(policy: wire, params: params, evidence: evidence, action: BotSkillChoice(policy: skill,
                control: control, points: skill.initialPoints, cue: cue, binding: binding, promptFirstSeen: promptStart)))
        }
        if menuPage == nil, let sight, sight.gameplay {
            if let scene = sight.scene {
                for (handle, route) in scene.routes.enumerated() {
                    let hint = route.hint
                    add("traverse", ["waypointId": handle], .traverse, .native, target: .traverse(route),
                        evidence: ["roomId": scene.room, "chart": route.chart, "waypoint": [route.waypoint.x, route.waypoint.y],
                                   "goal": [route.goal.x, route.goal.y], "road_dx": hint.dx, "road_dy": hint.dy,
                                   "road_distance": hint.distance, "road_frontier": hint.frontier])
                }
            }
            for (id, cue) in ["Inspect", "Listen"].enumerated() where visible.filter({
                RememberedGameText.key($0) == RememberedGameText.key(cue)
            }).count == 1 {
                add("interact", ["verb": id == 0 ? "inspect" : "talk", "targetId": id], .interact, .inspect, cue: cue)
            }
            let frontier = sight.scene?.routes.first(where: { $0.hint.frontier })
            // When the only actionable evidence is an interaction prompt, use it.
            if offered.isEmpty || sight.scene?.routes.isEmpty == false {
                add("explore", [:], .explore, .native, target: frontier.map(BotPolicyTarget.traverse),
                    evidence: frontier.map { ["chart": $0.chart, "frontierX": $0.goal.x, "frontierY": $0.goal.y] } ?? [:])
            }
        }
        if offered.isEmpty, menuPage == nil, recognizedGameplay {
            // The game detector can establish gameplay before route telemetry arrives.
            add("explore", [:], .explore, .native)
        }
        options = offered
    }

    private var menuRequests: [ThinkingMenuScript.Request] {
        guard let menuPage else { return [] }
        let entries = menuPage.elements.filter(\.canSelect).map { ThinkingMenuScript.Request.select($0.id) }
        if entries.isEmpty { return [.wait] }
        return allowsMore && menuPage.selected != nil ? entries + [.more] : entries
    }

    private var selections: [[String: Any]] {
        menuPage == nil ? options.map(\.selection) : menuRequests.map(\.selection)
    }

    var hasChoices: Bool { !selections.isEmpty }

    var offer: [String: Any] {
        let candidates: [[String: Any]] = menuPage == nil
            ? options.map { ["selection": $0.selection, "evidence": $0.evidence] }
            : selections.map { ["selection": $0] }
        var result: [String: Any] = ["objective": Self.objective, "candidates": candidates]
        if let menuPage {
            result["stateId"] = menuPage.stateID
            result["highlight"] = menuPage.selected
            result["elements"] = menuPage.elements.map { ["id": $0.id, "label": $0.label] }
        }
        return result
    }

    private static func json(_ object: Any) -> String {
        String(decoding: try! JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]), as: UTF8.self)
    }

    var selectionSpec: String {
        """
        Gameplay reply schema
        \(Self.policySchemaText)
        Menu script reply schema
        \(Self.menuSchemaText)
        This screenshot uses the \(menuPage == nil ? "gameplay" : "menu script") schema. Copy exactly one offered selection as JSON.
        \(menuPage == nil ? "Inspect a new sign when its prompt is available. Explore seeks unmapped ground. Traverse goes to a specific offered waypoint." : "Choose the visible element that advances the objective. The highlight is the cursor's starting position. To explore, start or resume play, or back out of unrelated settings. On a confirmation screen, consider the previous confirmed element if supplied. Quit is available when appropriate. More advances the highlight and asks again. Wait leaves the controls idle.")
        \(Self.json(offer))
        """
    }

    // Ollama's grammar is narrowed to the legal replies from this screenshot.
    var schema: [String: Any] {
        func exact(_ value: Any) -> [String: Any] {
            guard let object = value as? [String: Any] else { return ["const": value] }
            return ["type": "object", "properties": object.mapValues(exact),
                    "required": object.keys.sorted(), "additionalProperties": false]
        }
        let alternatives = selections.map(exact)
        if alternatives.isEmpty { return ["not": [String: Any]()] }
        return alternatives.count == 1 ? alternatives[0] : ["oneOf": alternatives]
    }

    private func index(of data: Data) throws -> Int {
        guard let fields = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let index = selections.firstIndex(where: { Self.json($0) == Self.json(fields) }) else {
            throw BotRuntimeError.incompatible("The reply was not offered for this screenshot.")
        }
        return index
    }

    func menuScript(_ data: Data) throws -> ThinkingMenuScript {
        guard let menuPage else { throw BotRuntimeError.incompatible("Expected a gameplay policy.") }
        return try ThinkingMenuScript(page: menuPage, request: menuRequests[index(of: data)])
    }

    func resolve(_ data: Data) throws -> BotSkillPlan {
        guard menuPage == nil else { throw BotRuntimeError.incompatible("Expected a menu script command.") }
        let index = try index(of: data)
        var choice = options[index].action
        choice.candidateID = "c\(index)"
        return try BotSkillPlan(objective: Self.objective, success: "Record the policy outcome.",
            choices: [choice], shopping: [], decisionID: decisionID).validated()
    }
}
