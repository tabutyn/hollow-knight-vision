import SwiftUI

struct RememberedGameText: Codable, Identifiable {
    static let openingContext = "Opening cutscene"
    let id: String
    let text: String
    let context: String
    let language: String
    let date: Date
    var isPassage: Bool? = nil

    var interpretation: GameTextInterpretation?
    var reading: String { interpretation?.text ?? text }

    static func key(_ text: String) -> String {
        text.lowercased().split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    static func logKey(_ text: String) -> String {
        text.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).joined(separator: " ")
    }
}

struct GameTextLogEntry: Identifiable {
    let id: String
    var reading: RememberedGameText
    var contexts: [String]
    var updated: Date

    static func current(_ rows: [RememberedGameText]) -> [Self] {
        var entries = [String: Self]()
        var aliases = [String: String]()
        var keys = [String: String]()
        for row in rows {
            let key = row.language + "|" + RememberedGameText.logKey(row.reading)
            let id = row.interpretation?.sameAs.flatMap { aliases[$0] } ?? keys[key] ?? row.id
            aliases[row.id] = id
            keys[key] = id
            if var entry = entries[id] {
                if !entry.contexts.contains(row.context) { entry.contexts.append(row.context) }
                if let edit = row.interpretation,
                   (entry.reading.interpretation == nil || entry.reading.reading.count <= row.reading.count || edit.sameAs != nil) {
                    entry.reading = row
                }
                entry.updated = max(entry.updated, row.date)
                entries[id] = entry
            } else {
                entries[id] = Self(id: id, reading: row, contexts: [row.context], updated: row.date)
            }
        }
        return entries.values.sorted { $0.updated == $1.updated ? $0.id < $1.id : $0.updated > $1.updated }
    }
}

actor GameTextNotebook {
    static let directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("HollowKnightVision/Notebooks", isDirectory: true)
    let url: URL?
    private var contents: [RememberedGameText]?
    private var ids = Set<String>()

    private var revision = 0
    init(url: URL? = nil) { self.url = url }

    func snapshot() throws -> (revision: Int, rows: [RememberedGameText]) {
        (revision, try entries())
    }

    private func write(_ rows: [RememberedGameText]) throws {
        if let url {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(rows).write(to: url, options: .atomic)
        }
        contents = rows
        revision += 1
    }

    func interpret(_ corrections: [String: GameTextInterpretation]) throws {
        var rows = try entries()
        var changed = false
        for i in rows.indices where rows[i].interpretation == nil {
            if let correction = corrections[rows[i].id] {
                rows[i].interpretation = correction
                changed = true
            }
        }
        if changed { try write(rows) }
    }

    func importObservations(_ observations: [RememberedGameText]) throws {
        let current = try entries()
        let existing = ids
        let added = observations.filter { !existing.contains($0.id) }
        guard !added.isEmpty else { return }
        try write(current + added)
        ids.formUnion(added.map(\.id))
    }

    func entries() throws -> [RememberedGameText] {
        if let contents { return contents }
        let rows: [RememberedGameText]
        if let url, FileManager.default.fileExists(atPath: url.path) {
            rows = try JSONDecoder().decode([RememberedGameText].self, from: Data(contentsOf: url))
        } else { rows = [] }
        contents = rows
        ids = Set(rows.map(\.id))
        return rows
    }

    func remember(_ lines: [GameTextLine], context: String, language: String, confirmed: Set<String>, passage: Bool = false) throws -> [RememberedGameText] {
        let current = try entries()
        var added = [RememberedGameText]()
        var newIDs = Set<String>()
        for line in lines {
            let key = RememberedGameText.key(line.text)
            guard key.count >= 2, key.contains(where: { $0.isLetter }),
                  line.confidence >= 0.8 || (line.confidence >= 0.45 && confirmed.contains(key)) else { continue }
            let id = ([language, context, key] + (passage ? ["passage"] : [])).joined(separator: "\u{1f}")
            guard !ids.contains(id), newIDs.insert(id).inserted else { continue }
            added.append(.init(id: id, text: line.text, context: context, language: language, date: Date(), isPassage: passage ? true : nil))
        }
        guard !added.isEmpty else { return current }
        let updated = current + added
        try write(updated)
        ids.formUnion(newIDs)
        return updated
    }

    static func recall(_ rows: [RememberedGameText], question: String, context: String,
                       current: [String], language: String) -> [String] {
        let words = Set(question.lowercased().components(separatedBy: CharacterSet.letters.inverted).filter { $0.count > 2 })
        let onScreen = Set(current.map(RememberedGameText.key))
        let matches = GameTextLogEntry.current(rows).map(\.reading).filter {
            $0.language == language && !onScreen.contains(RememberedGameText.key($0.text))
        }
        let scored: [(score: Int, rank: Int, entry: RememberedGameText)] = matches.enumerated().map { index, item in
            let tokens = Set(item.reading.lowercased().components(separatedBy: CharacterSet.letters.inverted))
            return (words.intersection(tokens).count * 3 + (context == item.context ? 1 : 0) + (item.isPassage == true ? 2 : 0), index, item)
        }
        let ordered = scored.sorted { $0.score == $1.score ? $0.rank < $1.rank : $0.score > $1.score }
        return ordered.prefix(4).map {
            let entry = $0.entry
            let label = entry.interpretation.map { " · " + $0.displayLabel } ?? ""
            return "[" + entry.context + label + "] " + entry.reading
        }
    }
}

struct GameTextScan {
    let image: CGImage
    let language: String
    let context: String
    let lines: [GameTextLine]
    let capturedAt: Double
    let milliseconds: Double
}

@MainActor
final class GameTextReader: ObservableObject {
    @Published private(set) var scan: GameTextScan?
    @Published private(set) var memory = [RememberedGameText]()
    @Published private(set) var entries = [GameTextLogEntry]()
    @Published private(set) var issue: String?
    @Published private(set) var skillReading: SkillReading?
    private var inspection: (context: String, started: Double, old: Set<String>, candidate: String?)?
    func beginInspection(_ sight: SkillSight, at start: Double) {
        inspection = (sight.context, start, Set(sight.lines.map(RememberedGameText.key)), nil)
    }
    @Published private(set) var profile: SaveProfileAtlas?
    @Published private(set) var statedGoal = ""
    var currentGoal: String { statedGoal.isEmpty ? BotSkillPlan.defaultObjective : statedGoal }
    private let preferences: UserDefaults
    private var goalKey: String? { profile.map { "HollowKnightVision.goal." + $0.id.uuidString } }

    func setGoal(_ goal: String) {
        statedGoal = String(goal.trimmingCharacters(in: .whitespacesAndNewlines).prefix(400))
        if let goalKey { preferences.set(statedGoal, forKey: goalKey) }
    }

    @Published private(set) var loggedText = [String]()

    @Published private(set) var cleanupStatus: String?
    private let directory: URL
    private var notebook: GameTextNotebook
    private var questContext: AtlasQuestContext
    private var questLog: AtlasQuestLog?
    private var summarizedRevision = -1
    private var openingSource: GameTextNotebook?
    private var revision = -1
    private var passageLines = Set<String>()

    init(directory: URL = GameTextNotebook.directory, notebook: GameTextNotebook? = nil, preferences: UserDefaults = .standard) {
        self.preferences = preferences
        self.directory = directory
        self.notebook = notebook ?? GameTextNotebook()
        self.questContext = AtlasQuestContext(notebookURL: notebook?.url)
    }

    func selectProfile(_ next: SaveProfileAtlas?) {
        guard next != profile else { return }
        openingSource = profile == nil && next != nil ? notebook : nil
        profile = next
        statedGoal = goalKey.flatMap { preferences.string(forKey: $0) } ?? ""
        loggedText = []
        notebook = next.map { GameTextNotebook(url: directory.appendingPathComponent($0.id.uuidString + ".json")) } ?? GameTextNotebook()
        questContext.invalidate()
        questContext = AtlasQuestContext(notebookURL: notebook.url)
        questLog = nil
        summarizedRevision = -1
        revision = -1
        memory = []
        entries = []
        inspection = nil
        passageLines = []
        skillReading = nil
        scan = nil
        issue = nil
        cleanupStatus = nil
    }

    private func refresh(_ source: GameTextNotebook) async throws {
        if notebook === source, let opening = openingSource {
            let readings = try await opening.entries().filter { $0.context == RememberedGameText.openingContext }
            guard !Task.isCancelled, notebook === source else { return }
            try await source.importObservations(readings)
            if notebook === source { openingSource = nil }
        }
        let snapshot = try await source.snapshot()
        guard !Task.isCancelled, notebook === source, snapshot.revision > revision else { return }
        revision = snapshot.revision
        memory = snapshot.rows
        questLog = nil
        entries = GameTextLogEntry.current(memory)
        let recent = entries.lazy.compactMap { entry in
            entry.reading.interpretation.map { $0.displayLabel + " · " + $0.text }
        }.prefix(4)
        let lines = Array(recent)
        if lines != loggedText { loggedText = lines }
    }

    func store(_ next: GameTextScan, previous: [GameTextLine]) async throws {
        let source = notebook
        let confirmed = Set(previous.map { RememberedGameText.key($0.text) })
        let visible = next.lines.filter { $0.confidence >= 0.45 }.map(\.text)
        passageLines.formIntersection(visible.map(RememberedGameText.key))
        var passage: String?
        if let pending = inspection, next.context != pending.context || next.capturedAt - pending.started > 12 {
            inspection = nil
        }
        if var pending = inspection {
            if next.capturedAt > pending.started {
                let newLines = next.lines.filter { line in
                    let key = RememberedGameText.key(line.text)
                    return line.confidence >= 0.45 && !pending.old.contains(key) && key.filter(\.isLetter).count >= 4
                }
                let page = newLines.map(\.text).joined(separator: " ")
                if !page.isEmpty && page == pending.candidate {
                    passage = page
                    passageLines.formUnion(newLines.map { RememberedGameText.key($0.text) })
                    inspection = nil
                    let entry = GameTextLine(id: 0, text: page, bounds: .zero, confidence: 1)
                    _ = try await source.remember([entry], context: next.context, language: next.language,
                                                   confirmed: [], passage: true)
                } else {
                    pending.candidate = page
                    inspection = pending
                }
            }
        } else {
            // A cutscene or scrolling credit can disappear before the next frame.
            let transient = ["Game capture", "Credits", RememberedGameText.openingContext].contains(next.context)
            let stable = next.lines.filter {
                let key = RememberedGameText.key($0.text)
                return (confirmed.contains(key) || (transient && $0.confidence >= 0.8)) && !passageLines.contains(key)
            }
            _ = try await source.remember(stable, context: next.context, language: next.language, confirmed: confirmed)
        }
        guard !Task.isCancelled, notebook === source else { return }
        skillReading = SkillReading(context: next.context, capturedAt: next.capturedAt, visible: visible, passage: passage)
        try await refresh(source)
    }

    private func updatePolicyMemory(model: ThinkingModel) async throws {
        let source = notebook
        try await refresh(source)
        try Task.checkCancellation()
        guard source === notebook else { throw CancellationError() }
        let context = questContext, currentRevision = revision
        let log = questLog ?? AtlasQuestLog(memory)
        questLog = log
        _ = try await context.prepare(log) { notes, page in
            try await AtlasQuestContext.revise(notes, page: page, model: model)
        }
        try Task.checkCancellation()
        guard source === notebook else { throw CancellationError() }
        summarizedRevision = currentRevision
    }

    func policyContext(goal: String) async throws -> String {
        let source = notebook
        try await refresh(source)
        try Task.checkCancellation()
        guard source === notebook else { throw CancellationError() }
        let log = questLog ?? AtlasQuestLog(memory)
        questLog = log
        return questContext.selectionEvidence(log, goal: goal)
    }

    func cleanReadings(preferred: ThinkingModel, updateQuestNotes: Bool = false,
                       find: (ThinkingModel) async throws -> GameTextCleanup.Resident? = GameTextCleanup.resident,
                       correct: (([RememberedGameText], GameTextCleanup.Resident) async throws -> [String: GameTextInterpretation])? = nil) async {
        let source = notebook
        var deferredIDs = Set<String>()
        var batchSize = 4
        while !Task.isCancelled, notebook === source {
            var delay = 3
            do {
                try await refresh(source)
                guard !Task.isCancelled, notebook === source else { return }
                let pending = memory.filter { $0.interpretation == nil }
                if pending.isEmpty {
                    if cleanupStatus != nil { cleanupStatus = nil }
                } else if let resident = try await find(preferred) {
                    guard !Task.isCancelled, notebook === source else { return }
                    var batch = [RememberedGameText]()
                    var characters = 0
                    for row in pending where !deferredIDs.contains(row.id) {
                        if batch.count == batchSize || (!batch.isEmpty && characters + row.text.count > 1000) { break }
                        batch.append(row)
                        characters += row.text.count
                    }
                    if batch.isEmpty {
                        deferredIDs = []
                        delay = 30
                    } else {
                        cleanupStatus = "Proofreading with " + resident.model.title + "…"
                        deferredIDs.formUnion(batch.map(\.id))
                        let edits: [String: GameTextInterpretation]
                        if let correct { edits = try await correct(batch, resident) }
                        else {
                            edits = try await GameTextCleanup.correct(batch, with: resident, existing: entries.map(\.reading))
                        }
                        guard !Task.isCancelled, notebook === source else { return }
                        try await source.interpret(edits)
                        try await refresh(source)
                        delay = 1
                    }
                } else {
                    guard !Task.isCancelled, notebook === source else { return }
                    cleanupStatus = "OCR captured. Waiting for the selected model to review it."
                    delay = 12
                }
                if updateQuestNotes && summarizedRevision != revision && notebook === source && !Task.isCancelled {
                    do { try await updatePolicyMemory(model: preferred) }
                    catch {
                        guard !Task.isCancelled, notebook === source else { return }
                        cleanupStatus = "Quest notes pending. " + error.localizedDescription
                        delay = 20
                    }
                }
            } catch {
                guard !Task.isCancelled, notebook === source else { return }
                // A failed multi-row reply can still be recovered one reading at a time.
                batchSize = 1
                cleanupStatus = "Proofreading paused. " + error.localizedDescription
                delay = 20
            }
            do { try await Task.sleep(for: .seconds(delay)) } catch { return }
        }
    }

    static func recognize(_ image: CGImage, language: String, context: String) async throws -> GameTextScan {
        let capturedAt = ProcessInfo.processInfo.systemUptime
        return try await Task.detached(priority: .utility) {
            let start = ProcessInfo.processInfo.systemUptime
            let lines = try GameTextRecognition.read(image, language: language)
            return GameTextScan(image: image, language: language, context: context, lines: lines,
                                capturedAt: capturedAt, milliseconds: (ProcessInfo.processInfo.systemUptime - start) * 1000)
        }.value
    }

    func monitor(enabled: Bool, language: String, frame: () -> (CGImage, String)?) async {
        scan = nil
        skillReading = nil
        let source = notebook
        do { try await refresh(source) } catch { if notebook === source { issue = error.localizedDescription } }
        guard enabled, !Task.isCancelled else { return }
        while !Task.isCancelled {
            if let (image, context) = frame() {
                if scan?.image !== image || scan?.context != context {
                    do {
                        let next = try await Self.recognize(image, language: language, context: context)
                        guard !Task.isCancelled, notebook === source else { return }
                        let previous = scan?.context == context ? scan?.lines ?? [] : []
                        scan = next
                        try await store(next, previous: previous)
                        if notebook === source { issue = nil }
                    } catch {
                        guard !Task.isCancelled, notebook === source else { return }
                        scan = nil
                        issue = error.localizedDescription
                    }
                }
            } else if scan != nil { scan = nil; skillReading = nil }
            let interval = scan?.context == RememberedGameText.openingContext ? 450 : 850
            do { try await Task.sleep(for: .milliseconds(interval)) } catch { return }
        }
    }

    func evidence(image: CGImage, context: String, language: String, question: String, enabled: Bool) async -> String {
        await evidenceDetails(image: image, context: context, language: language, question: question, enabled: enabled).text
    }

    func evidenceDetails(image: CGImage?, context: String, language: String, question: String, enabled: Bool) async -> (text: String, lines: [String]) {
        let source = notebook
        if memory.isEmpty {
            do { try await refresh(source) } catch { if notebook === source { issue = error.localizedDescription } }
        }
        let current: [String]
        if enabled, let image { current = await visibleLines(in: image, language: language, context: context) }
        else { current = [] }
        guard !Task.isCancelled, notebook === source else { return ("", []) }
        let recalled = GameTextNotebook.recall(memory, question: question, context: context, current: current, language: language)
        var parts = [String]()
        if !current.isEmpty { parts.append("OCR on the attached frame:\n" + current.joined(separator: "\n").prefix(650)) }
        let keys = Set(current.map(RememberedGameText.key))
        let screen = RememberedGameText.key(current.joined(separator: " "))
        let interpreted = memory.reversed().compactMap { row -> String? in
            guard row.context == context, row.language == language, let result = row.interpretation else { return nil }
            let raw = RememberedGameText.key(row.text)
            guard keys.contains(raw) || (row.isPassage == true && screen.contains(raw)) else { return nil }
            return "[\(result.label.title)] \(result.text)"
        }.prefix(4)
        if !interpreted.isEmpty { parts.append("Likely transcription of visible text:\n" + interpreted.joined(separator: "\n").prefix(650)) }
        if !recalled.isEmpty { parts.append("Earlier game text (may be from another visit):\n" + recalled.joined(separator: "\n").prefix(400)) }
        return (parts.joined(separator: "\n\n"), current)
    }

    func visibleLines(in image: CGImage, language: String, context: String) async -> [String] {
        if let scan, scan.image === image, scan.language == language {
            return scan.lines.map(\.text)
        }
        let result = try? await Self.recognize(image, language: language, context: context)
        return result?.lines.map(\.text) ?? []
    }
}
