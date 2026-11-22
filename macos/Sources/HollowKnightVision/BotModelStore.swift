import Foundation

struct BotTrainingScore: Codable {
    struct Head: Codable {
        let name: String
        let accuracy: Double
        let balancedAccuracy: Double
    }
    let negativeLogLikelihood: Double
    let exactActionAccuracy: Double
    let heads: [Head]
}

struct BotTrainingResult: Codable {
    let id: String
    let name: String
    let modelDirectory: String
    let createdAt: String
    let trainingExamples: Int
    let validationExamples: Int
    let candidate: BotTrainingScore
    let baseline: BotTrainingScore?
    var candidateSequential: BotTrainingScore? = nil
    var baselineSequential: BotTrainingScore? = nil
    struct ParentPolicy: Codable {
        let directory, modelSHA256: String
    }
    var parentPolicy: ParentPolicy? = nil
    var lastGymPolicy: ParentPolicy? = nil
    struct GymEvidence: Codable {
        let parent: String?
        let trainingSteps: Int?
    }
    var gymEvidence: GymEvidence? = nil
    let baselineSHA256: String?
    let baselineError: String?
    let eligibleForActivation: Bool
    let reason: String
    struct WallEvidence: Codable {
        let trainingWithWall: Int
        let analyzed: Int
        let withWall: Int
        let actionsChangedWhenMasked: Int
    }
    var wallEvidence: WallEvidence? = nil
    struct PitTraining: Codable {
        struct Evaluation: Codable { let episodes: Int; let landed: Int }
        let heldOut: Evaluation
    }
    var pitTraining: PitTraining? = nil
    struct AtlasTraining: Codable {
        struct Evaluation: Codable { let episodes, choseFrontier, clearStarts: Int; let meanNewCells: Double }
        let heldOut: Evaluation
    }
    var atlasTraining: AtlasTraining? = nil
    struct CombatTraining: Codable {
        let clips, validationClips: Int
        let attackRecall, releaseRecall: Double?
    }
    var combatTraining: CombatTraining? = nil
    struct EnemyOutcomes: Codable { let damage, kills: Int }
    var enemyOutcomes: EnemyOutcomes? = nil
    struct BlockadeTraining: Codable {
        let clips, examples, attackExamples: Int
    }
    var blockadeTraining: BlockadeTraining? = nil
    let nativeParity: Bool
    var botDomain: BotDomain? = nil
}

struct BotModelVersion: Identifiable {
    let directory: URL
    let name: String
    let result: BotTrainingResult?
    let includedClipIDs: Set<UUID>
    let policySchema: Int
    var heldAttackCost: Double? = nil
    var validationClipIDs: Set<UUID> = []
    var id: String { directory.lastPathComponent }
}

struct BotModelStore {
    struct Selection: Codable {
        let directory: String
        let previousDirectory: String?
        var manualChoiceThrough: String? = nil
    }
    private static let selectionLock = NSLock()
    static var defaultRoot: URL { BotDemonstrationRecorder.defaultRootURL.deletingLastPathComponent() }
    let root: URL
    let domain: BotDomain
    init(root: URL = Self.defaultRoot, domain: BotDomain = .hacker) { self.root = root; self.domain = domain }
    var legacyModels: URL { root.appendingPathComponent("models", isDirectory: true) }
    var models: URL { legacyModels.appendingPathComponent(domain.rawValue, isDirectory: true) }
    var selectionURL: URL { models.appendingPathComponent("active.json") }

    func directory(named name: String) throws -> URL {
        guard !name.isEmpty, name != ".", name != "..", !name.contains("/"), !name.contains("\\") else {
            throw BotRuntimeError.incompatible("Invalid bot version directory.")
        }
        let local = models.appendingPathComponent(name, isDirectory: true)
        if FileManager.default.fileExists(atPath: local.path) { return local }
        let legacy = legacyModels.appendingPathComponent(name, isDirectory: true)
        if legacyDomain(legacy) == domain { return legacy }
        return local
    }
    func selection() throws -> Selection {
        guard FileManager.default.fileExists(atPath: selectionURL.path) else {
            if domain == .hacker,
               let data = try? Data(contentsOf: legacyModels.appendingPathComponent("active.json")),
               let legacy = try? JSONDecoder().decode(Selection.self, from: data),
               legacyDomain(legacyModels.appendingPathComponent(legacy.directory)) == .hacker {
                return Selection(directory: legacy.directory, previousDirectory: nil)
            }
            return Selection(directory: "v1", previousDirectory: nil)
        }
        let value = try JSONDecoder().decode(Selection.self, from: Data(contentsOf: selectionURL))
        _ = try directory(named: value.directory)
        if let previous = value.previousDirectory { _ = try directory(named: previous) }
        return value
    }
    func activeDirectory() throws -> URL { try directory(named: selection().directory) }

    // A training job can finish while Vision is closed. An explicit rollback
    // applies until the next completed version for this domain.
    func playbackDirectory() throws -> URL {
        try Self.selectionLock.withLock {
            let saved = try selection()
            let current = try directory(named: saved.directory)
            guard let latest = try versions().first(where: { $0.result != nil && canActivate($0) }),
                  latest.id != saved.directory, latest.id != saved.manualChoiceThrough,
                  (try? BotNativePolicy.load(from: latest.directory)) != nil else { return current }
            try saveSelection(latest.id, previous: saved, manualChoiceThrough: nil)
            return try directory(named: latest.id)
        }
    }

    func versions() throws -> [BotModelVersion] {
        let local = (try? FileManager.default.contentsOfDirectory(at: models, includingPropertiesForKeys: nil)) ?? []
        let legacy = ((try? FileManager.default.contentsOfDirectory(at: legacyModels, includingPropertiesForKeys: nil)) ?? [])
            .filter { legacyDomain($0) == domain }
        return (local + legacy)
            .compactMap { url in
                guard let data = try? Data(contentsOf: url.appendingPathComponent("manifest.json")),
                      let manifest = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                      FileManager.default.fileExists(atPath: url.appendingPathComponent("runtime-manifest.json").path)
                else { return nil }
                let result = (try? Data(contentsOf: url.appendingPathComponent("training-result.json")))
                    .flatMap { try? JSONDecoder().decode(BotTrainingResult.self, from: $0) }
                // Partial candidates are never offered as usable versions.
                guard url.lastPathComponent == "v1" || result != nil else { return nil }
                guard result?.botDomain == nil || result?.botDomain == domain else { return nil }
                let report = manifest["sourceReport"] as? [String: Any]
                let sources = report?["sessions"] as? [[String: Any]] ?? []
                var clips = (manifest["trainingSessions"] as? [String] ?? []) + (manifest["validationSessions"] as? [String] ?? [])
                clips += sources.compactMap { $0["id"] as? String }
                if let strategy = manifest["validationStrategy"] as? [String: Any], let source = strategy["sourceSession"] as? String {
                    clips.append(source)
                }
                return BotModelVersion(directory: url, name: manifest["name"] as? String ?? url.lastPathComponent,
                                       result: result, includedClipIDs: Set(clips.compactMap(UUID.init(uuidString:))),
                                       policySchema: manifest["schemaVersion"] as? Int ?? 1,
                                       heldAttackCost: manifest["heldAttackCost"] as? Double,
                                       validationClipIDs: Set((manifest["validationSessions"] as? [String] ?? []).compactMap(UUID.init(uuidString:))))
            }.sorted { ($0.result?.createdAt ?? "") > ($1.result?.createdAt ?? "") }
    }

    func canActivate(_ version: BotModelVersion) -> Bool {
        guard let expected = try? directory(named: version.id),
              expected.standardizedFileURL == version.directory.standardizedFileURL else { return false }
        if let result = version.result {
            return result.nativeParity && result.modelDirectory == version.id
                && (result.botDomain == nil || result.botDomain == domain)
        }
        return version.id == "v1"
    }

    private func legacyDomain(_ url: URL) -> BotDomain? {
        guard let data = try? Data(contentsOf: url.appendingPathComponent("manifest.json")),
              let manifest = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let value = manifest["botDomain"] as? String { return BotDomain(rawValue: value) }
        if url.lastPathComponent == "v1" { return .gameplay }
        return url.lastPathComponent.hasPrefix("bot-") ? .hacker : nil
    }

    func activate(_ version: BotModelVersion) throws {
        try Self.selectionLock.withLock {
            guard canActivate(version) else { throw BotRuntimeError.incompatible("This version needs a verified native export for this bot mode.") }
            try select(version.id)
        }
    }
    func restorePrevious() throws {
        try Self.selectionLock.withLock {
            guard let previous = try selection().previousDirectory else { return }
            try select(previous)
        }
    }
    private func select(_ name: String) throws {
        let target = try directory(named: name)
        _ = try BotNativePolicy.load(from: target)
        let newest = try versions().first(where: { $0.result != nil && canActivate($0) })?.id
        try saveSelection(name, previous: selection(), manualChoiceThrough: name == newest ? nil : newest)
    }
    private func saveSelection(_ name: String, previous: Selection, manualChoiceThrough: String?) throws {
        guard previous.directory != name || previous.manualChoiceThrough != manualChoiceThrough else { return }
        let prior: String?
        if previous.directory == name { prior = previous.previousDirectory }
        else { prior = (try? BotNativePolicy.load(from: directory(named: previous.directory))) != nil ? previous.directory : nil }
        try FileManager.default.createDirectory(at: models, withIntermediateDirectories: true)
        try JSONEncoder().encode(Selection(directory: name, previousDirectory: prior, manualChoiceThrough: manualChoiceThrough))
            .write(to: selectionURL, options: .atomic)
    }
}
