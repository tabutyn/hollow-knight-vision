import AppKit
import Combine
import CryptoKit
import Foundation

struct BotSnippet: Identifiable {
    struct Input: Decodable { let timestamp: Double; let button: String; let isPressed: Bool }
    let directory: URL
    let manifest: BotDemonstrationManifest
    let observations: [BotObservation]
    let inputs: [Input]
    let error: String?
    var maskedKnightFrames = Set<Int>()
    var replayPathHints = [Int: BotRoadHint]()
    func pathHint(at index: Int) -> BotRoadHint? {
        guard observations.indices.contains(index) else { return nil }
        let row = observations[index]
        return row.hackerPathVersion == 1 ? row.roadHint : replayPathHints[index]
    }
    var id: UUID { manifest.id }
    var domain: BotDomain { manifest.botDomain ?? (observations.contains { $0.terrainSource == "human-authored" } ? .hacker : .gameplay) }
    var isHacker: Bool { !observations.isEmpty && observations.allSatisfy { $0.knightSource == "hacker-frame" && $0.enemySource == "hacker-frame" && $0.terrainSource == "human-authored" } }
    var canTrain: Bool { error == nil && manifest.canTrain(hasOccludedKnightFrames: !maskedKnightFrames.isEmpty) }
    func heldButtons(at time: Double) -> Set<String> {
        var held = Set<String>()
        for input in inputs where input.timestamp <= time {
            if input.isPressed { held.insert(input.button.lowercased()) } else { held.remove(input.button.lowercased()) }
        }
        return held
    }
    func controls(at time: Double) -> String {
        let held = heldButtons(at: time)
        return held.isEmpty ? "Released" : held.sorted().map { $0.uppercased() }.joined(separator: " + ")
    }
    static func loadManifest(_ directory: URL) throws -> BotDemonstrationManifest {
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(BotDemonstrationManifest.self, from: Data(contentsOf: directory.appendingPathComponent("manifest.json")))
    }
    static func load(_ directory: URL, manifest knownManifest: BotDemonstrationManifest? = nil) throws -> Self {
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        let manifest = try knownManifest ?? loadManifest(directory)
        var observations = [BotObservation](), inputs = [Input](), failure: String?
        do {
            guard let names = BotObservationContract.names(for: manifest.schemaVersion), manifest.featureNames == names else {
                throw BotRuntimeError.incompatible("Incompatible recording schema")
            }
            for line in try String(contentsOf: directory.appendingPathComponent("events.jsonl"), encoding: .utf8).split(separator: "\n") {
                let data = Data(line.utf8)
                let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
                if object?["type"] as? String == "observation" {
                    let observation = try decoder.decode(BotObservation.self, from: data)
                    guard observation.features.count == names.count else {
                        throw BotRuntimeError.incompatible("Incompatible observation length")
                    }
                    observations.append(observation)
                } else if object?["type"] as? String == "input" {
                    inputs.append(try decoder.decode(Input.self, from: data))
                }
            }
            if observations.count != manifest.observationCount || inputs.count != manifest.inputEventCount {
                failure = "Recording counts are incomplete."
            }
        } catch { failure = error.localizedDescription }
        let eventsURL = directory.appendingPathComponent("events.jsonl")
        let sidecarURL = directory.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("exit-paths-v1")
            .appendingPathComponent("demo-\(manifest.id.uuidString.lowercased()).json")
        let hints = (try? BotClipPathSidecar.hints(sidecar: Data(contentsOf: sidecarURL),
            events: Data(contentsOf: eventsURL), id: manifest.id, replayID: manifest.sourceReplayID,
            observations: observations)) ?? [:]
        let masked = observations.indices.filter { BotKnightOcclusionWindow.deadline(for: observations[$0]) != nil }
        return Self(directory: directory, manifest: manifest, observations: observations, inputs: inputs.sorted { $0.timestamp < $1.timestamp }, error: failure,
                    maskedKnightFrames: Set(masked), replayPathHints: hints)
    }
}

/// Replay-derived vectors are bound to the exact source clip and row identity.
enum BotClipPathSidecar {
    private struct Document: Decodable {
        struct Row: Decodable {
            let index: Int
            let generation: UInt64
            let captureTimestamp: Double
            let hackerPathVersion: Int
            let roadHint: BotRoadHint?
        }
        let schemaVersion: Int
        let id: UUID
        let replayID: UUID
        let eventsSHA256: String
        let rows: [Row]
    }
    static func hints(sidecar: Data, events: Data, id: UUID, replayID: UUID,
                      observations: [BotObservation]) throws -> [Int: BotRoadHint] {
        let document = try JSONDecoder().decode(Document.self, from: sidecar)
        let hash = SHA256.hash(data: events).map { String(format: "%02x", $0) }.joined()
        guard document.schemaVersion == 1, document.id == id, document.replayID == replayID,
              document.eventsSHA256 == hash else { return [:] }
        var hints = [Int: BotRoadHint]()
        for frame in document.rows {
            guard observations.indices.contains(frame.index), frame.hackerPathVersion == 1 else { continue }
            let observation = observations[frame.index]
            guard observation.generation == frame.generation,
                  observation.captureTimestamp == frame.captureTimestamp,
                  let hint = frame.roadHint, hint.dx.isFinite, hint.dy.isFinite else { continue }
            hints[frame.index] = hint
        }
        return hints
    }
}

@MainActor final class BotTrainingController: ObservableObject {
    @Published private(set) var snippets = [BotSnippet]()
    @Published private(set) var captureManifests = [BotDemonstrationManifest]()
    @Published private(set) var versions = [BotModelVersion]()
    @Published private(set) var isRunning = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var message: String?
    @Published private(set) var errorMessage: String?
    func clearError() { errorMessage = nil }
    @Published private(set) var progress = ""
    @Published private(set) var activeID = "v1"
    @Published private(set) var canRestore = false
    @Published private(set) var clipNumbers = [String: Int]()
    private var process: Process?
    private var cancelled = false
    private var refreshGeneration = 0
    private var refreshPending = false
    private var summaryGeneration = 0
    private let loadSnippet: (URL, BotDemonstrationManifest) throws -> BotSnippet
    let store: BotModelStore
    init(store: BotModelStore = BotModelStore(),
         loadSnippet: @escaping (URL, BotDemonstrationManifest) throws -> BotSnippet = { try BotSnippet.load($0, manifest: $1) }) {
        self.store = store; self.loadSnippet = loadSnippet
    }
    var selected: [BotSnippet] { snippets.filter(\.canTrain) }
    private var trainingParent: BotModelVersion? { versions.first { $0.id == activeID } }
    var newClips: [BotSnippet] {
        let lastRun = versions.first?.includedClipIDs ?? []
        return selected.filter { !lastRun.contains($0.id) }
    }
    var newCaptureManifests: [BotDemonstrationManifest] {
        if !isRefreshing { return newClips.map(\.manifest) }
        let included = versions.first?.includedClipIDs ?? []
        return captureManifests.filter { $0.status == "completed" && !included.contains($0.id) }
    }
    var newClipCount: Int { newCaptureManifests.count }
    var summaryFrameCount: Int {
        if isRefreshing {
            let validated = Dictionary(uniqueKeysWithValues: snippets.map { ($0.id, $0) })
            return captureManifests.reduce(0) { total, manifest in
                guard let clip = validated[manifest.id] else { return total + manifest.usableObservationCount }
                return total + (clip.canTrain ? manifest.usableObservationCount + clip.maskedKnightFrames.count : 0)
            }
        }
        return selected.reduce(0) { $0 + $1.manifest.usableObservationCount + $1.maskedKnightFrames.count }
    }
    var canUpgradeObservations: Bool {
        selected.count >= 2 && trainingParent.map { $0.policySchema < BotObservationContract.nearestGeometryPolicyVersion } == true
    }
    var hasFreshTrainingClips: Bool {
        let known = trainingParent?.includedClipIDs ?? []
        return selected.lazy.filter { !known.contains($0.id) }.prefix(2).count == 2
    }
    private var canRescoreAttackHolds: Bool {
        guard let parent = trainingParent, parent.heldAttackCost != BotAtlasScore.heldAttackCost else { return false }
        return selected.contains { parent.validationClipIDs.contains($0.id) }
            && selected.contains { !parent.validationClipIDs.contains($0.id) }
    }
    var canTrain: Bool { !isRunning && !isRefreshing && selected.count >= 2 && (hasFreshTrainingClips || canUpgradeObservations || canRescoreAttackHolds) }
    func clipName(_ snippet: BotSnippet) -> String {
        "Clip \(clipNumbers[snippet.id.uuidString] ?? 0)"
    }
    func versionLabel(_ identifier: String) -> String {
        let ordered = Array(versions.reversed())
        return "v\((ordered.firstIndex { $0.id == identifier } ?? 0) + 1).0"
    }
    private func refreshSummaries() {
        summaryGeneration += 1
        let generation = summaryGeneration, root = store.root, domain = store.domain
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let directories = (try? FileManager.default.contentsOfDirectory(at: root.appendingPathComponent("demonstrations"), includingPropertiesForKeys: nil)) ?? []
            let manifests = directories.filter { $0.lastPathComponent.hasPrefix("demo-") }.compactMap { try? BotSnippet.loadManifest($0) }
                .filter { $0.botDomain == domain }.sorted { $0.createdAt > $1.createdAt }
            DispatchQueue.main.async {
                guard let self, self.summaryGeneration == generation else { return }
                // Legacy clips have no domain in their manifest; retain only those
                // already assigned by event validation, and only while still on disk.
                let legacy = self.snippets.filter { $0.manifest.botDomain == nil && FileManager.default.fileExists(atPath: $0.directory.path) }.map(\.manifest)
                self.captureManifests = (manifests + legacy).sorted { $0.createdAt > $1.createdAt }
            }
        }
    }
    func refresh() {
        refreshSummaries()
        guard !isRefreshing else { refreshPending = true; return }
        isRefreshing = true; refreshGeneration += 1
        let generation = refreshGeneration, root = store.root, domain = store.domain, loadSnippet = self.loadSnippet
        refreshVersions()
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var entries = [(URL, BotDemonstrationManifest)](), errors = [String]()
            let directories = (try? FileManager.default.contentsOfDirectory(at: root.appendingPathComponent("demonstrations"), includingPropertiesForKeys: nil)) ?? []
            for directory in directories where directory.lastPathComponent.hasPrefix("demo-") {
                do {
                    let manifest = try BotSnippet.loadManifest(directory)
                    if manifest.botDomain == nil || manifest.botDomain == domain { entries.append((directory, manifest)) }
                } catch { errors.append("\(directory.lastPathComponent): \(error.localizedDescription)") }
            }
            var snippets = [BotSnippet]()
            for (directory, manifest) in entries {
                do { let clip = try loadSnippet(directory, manifest); if clip.domain == domain { snippets.append(clip) } }
                catch { errors.append("\(directory.lastPathComponent): \(error.localizedDescription)") }
            }
            let loaded = snippets.sorted { $0.manifest.createdAt > $1.manifest.createdAt }
            DispatchQueue.main.async {
                guard let self, self.refreshGeneration == generation else { return }
                self.snippets = loaded
                if !self.refreshPending {
                    self.summaryGeneration += 1
                    self.captureManifests = loaded.map(\.manifest)
                }
                let numbersURL = self.store.root.appendingPathComponent("clip-numbers-by-domain-\(domain.rawValue).json")
                var numbers = (try? Data(contentsOf: numbersURL)).flatMap { try? JSONDecoder().decode([String: Int].self, from: $0) } ?? [:]
                var next = (numbers.values.max() ?? 0) + 1
                for clip in loaded.reversed() where numbers[clip.id.uuidString] == nil { numbers[clip.id.uuidString] = next; next += 1 }
                self.clipNumbers = numbers
                do { try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
                    try JSONEncoder().encode(numbers).write(to: numbersURL, options: .atomic)
                } catch { errors.append(error.localizedDescription) }
                if !errors.isEmpty { self.message = errors.joined(separator: "\n"); self.errorMessage = self.message }
                self.isRefreshing = false
                if self.refreshPending { self.refreshPending = false; self.refresh() }
            }
        }
    }
    func delete(_ snippet: BotSnippet) {
        guard !isRunning else { return }
        do {
            let name = clipName(snippet)
            try BotClipDeletion.delete(snippet: snippet, root: store.root)
            snippets.removeAll { $0.id == snippet.id }
            summaryGeneration += 1
            captureManifests.removeAll { $0.id == snippet.id }
            if isRefreshing { refreshPending = true }
            message = "Deleted \(name). Existing model versions and training snapshots are preserved."
        } catch { message = error.localizedDescription; errorMessage = message }
    }
    func refreshVersions() {
        do {
            versions = try store.versions(); let selection = try store.selection()
            activeID = selection.directory; canRestore = selection.previousDirectory != nil
        } catch { message = error.localizedDescription; errorMessage = message }
    }
    func activate(_ version: BotModelVersion, reload: () -> Void) {
        do { try store.activate(version); refreshVersions(); reload(); message = "Activated \(store.domain.title) \(versionLabel(version.id))." }
        catch { message = error.localizedDescription; errorMessage = message }
    }
    func restore(reload: () -> Void) {
        do { try store.restorePrevious(); refreshVersions(); reload(); message = "Restored the previous bot version." }
        catch { message = error.localizedDescription; errorMessage = message }
    }
    func useTrainedVersion(runID: String, reload: () -> Void) {
        do {
            let completed = try store.versions().filter { $0.result != nil && store.canActivate($0) }
            guard completed.contains(where: { $0.id == "bot-\(runID)" && $0.result?.id == runID }),
                  let version = completed.first else {
                throw BotRuntimeError.incompatible("The completed run's verified export is missing.")
            }
            try store.activate(version)
            refreshVersions()
            reload()
            message = "Training complete. \(store.domain.title) is using \(versionLabel(version.id))."
        } catch {
            refreshVersions()
            message = "Could not activate the trained \(store.domain.title) bot: \(error.localizedDescription)"
            errorMessage = message
        }
    }

    func start(bundle: Bundle = .main, reload: @escaping () -> Void) {
        guard canTrain else { return }
        do {
            let resources = bundle.resourceURL ?? bundle.bundleURL
            let script = resources.appendingPathComponent("BotPython/train_bot_job.py")
            guard FileManager.default.fileExists(atPath: script.path) else { throw BotRuntimeError.incompatible("The bot training helper is missing. Rebuild Vision.") }
            let environment = ProcessInfo.processInfo.environment
            var pythonCandidates = [String]()
            if let configured = environment["HKV_BOT_PYTHON"] { pythonCandidates.append(configured) }
            pythonCandidates.append(store.root.appendingPathComponent("training-env/bin/python").path)
            pythonCandidates.append(resources.appendingPathComponent("bot-training-environment.txt").path)
            let candidates = pythonCandidates.map { path -> String in
                path.hasSuffix(".txt") ? ((try? String(contentsOfFile: path, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "") : path
            }
            guard let python = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else {
                throw BotRuntimeError.incompatible("Bot training needs its Python environment. Run macos/setup-bot-trainer.sh, then rebuild Vision.")
            }
            let id = UUID().uuidString.lowercased(), run = store.root.appendingPathComponent("training-runs/\(id)", isDirectory: true)
            try FileManager.default.createDirectory(at: run, withIntermediateDirectories: true)
            let selection = run.appendingPathComponent("selection.json")
            try JSONEncoder().encode(selected.map { $0.id.uuidString }).write(to: selection, options: .atomic)
            let log = run.appendingPathComponent("training.log")
            FileManager.default.createFile(atPath: log.path, contents: nil)
            let handle = try FileHandle(forWritingTo: log)
            let process = Process(); process.executableURL = URL(fileURLWithPath: python)
            process.arguments = [script.path, "--root", store.root.path, "--selection", selection.path,
                                 "--run-id", id, "--domain", store.domain.rawValue, "--follow-playback"]
            let baseline = try store.playbackDirectory()
            if baseline.lastPathComponent != "v1" || FileManager.default.fileExists(atPath: baseline.appendingPathComponent("manifest.json").path) {
                _ = try BotNativePolicy.load(from: baseline)
                process.arguments?.append(contentsOf: ["--baseline", baseline.path])
            }
            guard let executable = bundle.executableURL else { throw BotRuntimeError.incompatible("Missing wall detection helper.") }
            process.arguments?.append(contentsOf: ["--wall-extractor", executable.path])
            if canUpgradeObservations { process.arguments?.append("--upgrade-observations") }
            process.currentDirectoryURL = resources
            var childEnvironment = environment; childEnvironment["PYTHONUNBUFFERED"] = "1"
            childEnvironment["OMP_NUM_THREADS"] = "2"; childEnvironment["MPLCONFIGDIR"] = run.appendingPathComponent("matplotlib").path
            process.environment = childEnvironment; process.standardOutput = handle; process.standardError = handle
            process.terminationHandler = { [weak self] process in
                try? handle.close()
                DispatchQueue.main.async {
                    guard let self else { return }
                    self.isRunning = false; self.process = nil; self.progress = ""
                    let tail = (try? String(contentsOf: log, encoding: .utf8))?.split(separator: "\n").suffix(5).joined(separator: "\n") ?? ""
                    if !self.cancelled, process.terminationStatus == 0 {
                        self.useTrainedVersion(runID: id, reload: reload)
                    } else {
                        self.refreshVersions()
                        self.message = self.cancelled ? "Training cancelled." : "Training failed.\n\(tail)"
                        if !self.cancelled { self.errorMessage = self.message }
                    }
                }
            }
            cancelled = false; message = nil; progress = "Training and validating selected clips…"
            try process.run(); self.process = process; isRunning = true
        } catch { message = error.localizedDescription; errorMessage = message }
    }
    func cancel() { guard isRunning else { return }; cancelled = true; process?.terminate() }
}
