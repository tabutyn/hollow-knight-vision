import Foundation

/// Optional per-skill ability classifier. Geometry and the four trained movement outputs
/// still belong to the active, checksum-verified playing policy.
struct BotLearnedSkill: Codable {
    let version: Int
    let domain: BotDomain
    let skill: BotSkill
    let observationSchema: Int
    let historyLength: Int
    let labels: [String]
    let layers: [BotRuntimeLayer]
    var inputColumns: [Int]? = nil

    static func load(domain: BotDomain, skill: BotSkill, schema: Int, root: URL = BotModelStore.defaultRoot) throws -> Self? {
        let file = root.appendingPathComponent("skill-actors").appendingPathComponent(domain.rawValue)
            .appendingPathComponent(skill.rawValue + ".json")
        guard FileManager.default.fileExists(atPath: file.path) else { return nil }
        guard (try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max) <= 4 * 1024 * 1024 else {
            throw BotRuntimeError.incompatible("Skill actor is too large.")
        }
        let actor = try JSONDecoder().decode(Self.self, from: Data(contentsOf: file))
        try actor.validate(domain: domain, skill: skill, schema: schema)
        return actor
    }

    func validate(domain: BotDomain, skill: BotSkill, policy: BotNativePolicy) throws {
        try validate(domain: domain, skill: skill, schema: policy.document.observationSchemaVersion)
    }

    private func validate(domain: BotDomain, skill: BotSkill, schema: Int) throws {
        guard let names = BotObservationContract.policyNames(for: schema),
              self.domain == domain, self.skill == skill,
              observationSchema == schema, historyLength == BotObservationContract.historyLength,
              labels.first == "none", labels.count >= 2, Set(labels).count == labels.count,
              Set(labels.dropFirst()).isSubset(of: skill.learnableAbilities) else {
            throw BotRuntimeError.incompatible("Skill actor does not match the selected policy or controls.")
        }
        let rawWidth = names.count * historyLength
        let width = try BotNativePolicy.inputWidth(version: version, columns: inputColumns, rawWidth: rawWidth)
        try BotNativePolicy.validate(layers, inputs: width,
                                     outputs: labels.count, maximumWidth: 128)
    }

    func action(for input: [Float], base: BotAction) throws -> BotAction {
        let features: [Float]
        if let columns = inputColumns {
            guard columns.allSatisfy(input.indices.contains) else {
                throw BotRuntimeError.incompatible("Incomplete observation for skill actor.")
            }
            features = columns.map { input[$0] }
        } else {
            features = input
        }
        let logits = try BotNativePolicy.forward(features, layers: layers)
        guard let best = logits.indices.max(by: { logits[$0] < logits[$1] }), best != 0,
              let ability = RecordedGameButton(rawValue: labels[best]) else { return base }
        if ability == .a { return BotAction(buttons: .actionA) }
        return BotAction(buttons: base.buttons.union(ability.bridgeButton))
    }
}

extension BotSkill {
    var learnableAbilities: Set<String> {
        switch self {
        case .recover: ["a"]
        case .fight: ["quickCast", "dash", "dreamNail"]
        case .traverse: ["dash", "superDash"]
        case .progress: ["dreamNail"]
        case .explore: ["dash", "superDash"]
        case .interact: []
        }
    }
}
