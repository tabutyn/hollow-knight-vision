import Foundation
import os

/// One model reply selects one complete action from the captured offer.
struct ThinkingPolicyContract {
    private struct Option {
        let policy: String
        let instruction: String
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
        menuPage = Self.activeMenu(sight: sight, captured: capturedMenu, recognizedGameplay: recognizedGameplay)
        self.allowsMore = allowsMore
        decisionID = Self.serial.withLock { $0 += 1; return $0 }
        var offered: [Option] = []
        func add(_ wire: String, _ params: [String: Any], _ skill: BotSkill, _ control: SkillControl,
                 instruction: String, cue: String = "", target: BotPolicyTarget? = nil, evidence: [String: Any] = [:]) {
            let binding = target.flatMap { target in sight?.scene.map { BotPolicyBinding(room: $0.room, target: target) } }
            let promptStart = control == .inspect ? sight?.promptStart(for: cue) ?? sight?.capturedAt : nil
            offered.append(Option(policy: wire, instruction: instruction, params: params, evidence: evidence, action: BotSkillChoice(policy: skill,
                control: control, points: skill.initialPoints, cue: cue, binding: binding, promptFirstSeen: promptStart)))
        }
        if menuPage == nil, let sight, sight.gameplay {
            if let scene = sight.scene {
                for (handle, route) in scene.routes.enumerated() {
                    let hint = route.hint
                    let instruction = "Follow waypoint (\(route.waypoint.x), \(route.waypoint.y)) toward (\(route.goal.x), \(route.goal.y)) in \(route.chart)"
                        + (hint.frontier ? " at the unexplored boundary" : "")
                    add("traverse", ["waypointId": handle], .traverse, .native, instruction: instruction, target: .traverse(route),
                        evidence: ["roomId": scene.room, "chart": route.chart, "waypoint": [route.waypoint.x, route.waypoint.y],
                                   "goal": [route.goal.x, route.goal.y], "road_dx": hint.dx, "road_dy": hint.dy,
                                   "road_distance": hint.distance, "road_frontier": hint.frontier])
                }
            }
            for (id, cue) in ["Inspect", "Listen"].enumerated() where visible.filter({
                RememberedGameText.key($0) == RememberedGameText.key(cue)
            }).count == 1 {
                add("interact", ["verb": id == 0 ? "inspect" : "talk", "targetId": id], .interact, .inspect,
                    instruction: id == 0 ? "Read the sign at the Inspect prompt" : "Talk to the character at the Listen prompt", cue: cue)
            }
            let frontier = sight.scene?.routes.first(where: { $0.hint.frontier })
            // When the only actionable evidence is an interaction prompt, use it.
            if offered.isEmpty || sight.scene?.routes.isEmpty == false {
                let instruction = frontier.map { "Explore unmapped ground toward (\($0.goal.x), \($0.goal.y)) in \($0.chart)" }
                    ?? "Explore reachable, unmapped ground"
                add("explore", [:], .explore, .native, instruction: instruction, target: frontier.map(BotPolicyTarget.traverse),
                    evidence: frontier.map { ["chart": $0.chart, "frontierX": $0.goal.x, "frontierY": $0.goal.y] } ?? [:])
            }
        }
        if offered.isEmpty, menuPage == nil, recognizedGameplay {
            // The game detector can establish gameplay before route telemetry arrives.
            add("explore", [:], .explore, .native, instruction: "Explore reachable, unmapped ground")
        }
        options = offered
    }

    static func activeMenu(sight: SkillSight?, captured: ThinkingMenuPage?, recognizedGameplay: Bool) -> ThinkingMenuPage? {
        captured ?? (!recognizedGameplay && sight?.gameplay == false ? sight?.menuPage : nil)
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

    var selectionSpec: String { prompt(evidence: "") }

    func prompt(evidence: String) -> String {
        if let page = menuPage {
            let menu = page.stateID.replacingOccurrences(of: "-", with: " ").capitalized
            let navigation: (task: String, target: String?)
            switch page.stateID {
            case "main-title": navigation = ("Start the game so the Knight can explore.", "main-title.start-game")
            case "pause": navigation = ("Resume the game to continue exploring.", "pause.continue")
            case "select-profile": navigation = ("Choose a save slot to enter gameplay and explore.", nil)
            case "quit-to-menu", "quit-game": navigation = ("Cancel quitting to continue exploring.", LabelingClassIdentity.no)
            case "clear-save": navigation = ("Keep the save and return to profile selection.", LabelingClassIdentity.no)
            case "options", "audio", "video", "controller", "keyboard", "game-options", "extras", "mods", "screen-scale":
                navigation = ("Leave this menu to get back to the game.", LabelingClassIdentity.back)
            default: navigation = ("Choose the option that leads toward gameplay and exploration.", nil)
            }
            let entries = page.elements.filter(\.canSelect)
            var lines = ["Menu: " + menu, navigation.task]
            if !evidence.isEmpty { lines.append(evidence) }
            func marker(_ entry: ThinkingMenuPage.Element) -> String {
                entry.id == page.selected ? " [highlighted]" : ""
            }
            let recommended = entries.first { $0.id == navigation.target }
            let others = entries.filter { $0.id != recommended?.id }
            if !others.isEmpty {
                lines.append(#"To select "# + (recommended == nil ? "an" : "another") + #" option, use {"menu":"select","elementId":"ID"} with one ID below."#)
                for entry in others {
                    let name = entry.label.isEmpty || entry.label == entry.id ? entry.id : entry.label + " — " + entry.id
                    lines.append("• " + name + marker(entry))
                }
            }
            if menuRequests.contains(.more) { lines.append(#"Only if the needed option is missing, move the highlight with {"menu":"more"}."#) }
            if menuRequests.contains(.wait) { lines.append(#"No selectable option is visible. Reply with {"menu":"wait"} until one appears."#) }
            if let recommended {
                let name = recommended.label.isEmpty || recommended.label == recommended.id ? "this option" : recommended.label
                let reply = ThinkingMenuScript.Request.select(recommended.id)
                lines.append("Choose " + name + marker(recommended) + ". Reply with " + Self.json(reply.selection))
            }
            return lines.joined(separator: "\n")
        }

        var lines = ["Map new ground. Choose your next action from the captured gameplay frame.",
                     "Reply with the JSON on one line below."]
        lines += options.map { "• " + $0.instruction + " — " + Self.json($0.selection) }
        if !evidence.isEmpty { lines.append("\n" + evidence) }
        return lines.joined(separator: "\n")
    }

    // Ollama's grammar is narrowed to the legal replies from this screenshot.
    var schema: [String: Any] {
        func exact(_ value: Any) -> [String: Any] {
            guard let object = value as? [String: Any] else { return ["const": value] }
            return ["type": "object", "properties": object.mapValues(exact),
                    "required": object.keys.sorted(), "additionalProperties": false]
        }
        var alternatives: [[String: Any]]
        if menuPage != nil {
            let requests = menuRequests
            let ids = requests.compactMap { if case .select(let id) = $0 { id } else { nil } }
            alternatives = ids.isEmpty ? [] : [
                ["type": "object", "required": ["elementId", "menu"], "additionalProperties": false,
                 "properties": ["menu": ["const": "select"], "elementId": ["type": "string", "enum": ids]]]
            ]
            for request in requests where request == .wait || request == .more {
                alternatives.append(exact(request.selection))
            }
        } else {
            alternatives = selections.map(exact)
        }
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
