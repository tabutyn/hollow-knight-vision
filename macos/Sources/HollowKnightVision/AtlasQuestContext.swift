import CryptoKit
import Foundation

/// Source numbers are one-based positions in the playthrough's append-only OCR notebook.
struct AtlasQuestNotes: Codable, Equatable {
    struct Note: Codable, Equatable {
        let text: String
        let sources: [Int]
    }
    var quests = [Note]()
    var destinations = [Note]()
    var unknowns = [Note]()

    var all: [Note] { quests + destinations + unknowns }

    func validate(sources: Set<Int>) throws {
        guard all.count <= 6, all.allSatisfy({ note in
            !note.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            note.text.utf8.count <= 160 && (1...3).contains(note.sources.count) &&
            note.sources.allSatisfy(sources.contains)
        }) else { throw BotRuntimeError.incompatible("Quest notes exceeded their limits or cited unavailable game text.") }
    }

    var json: String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        // The document contains only arrays of strings and integers.
        return String(decoding: (try? encoder.encode(self)) ?? Data(), as: UTF8.self)
    }
}

struct AtlasQuestLog {
    struct Excerpt {
        let source: Int
        let context: String
        let text: String
        var line: String { "[R" + String(source) + " · " + String(context.prefix(60)) + "] " + text }
        let isClue: Bool
        let digest: String
    }
    let excerpts: [Excerpt]

    init(_ rows: [RememberedGameText]) {
        var hash = SHA256()
        var result = [Excerpt]()
        for (index, row) in rows.enumerated() {
            let number = index + 1
            let label = row.interpretation?.label
            let prompt = BotSkillPlan.initial.choices.contains {
                $0.control == .inspect && RememberedGameText.key($0.cue) == RememberedGameText.key(row.reading)
            }
            let isClue = !prompt && ![GameTextLabel.credits, .hud, .menu, .prompt].contains { $0 == label }
            let prefix = "[R" + String(number) + " · " + String(row.context.prefix(60)) + "] "
            var piece = ""
            func appendPiece() {
                guard !piece.isEmpty else { return }
                let line = prefix + piece
                hash.update(data: Data(((isClue ? "+" : "-") + String(line.utf8.count) + ":" + line).utf8))
                result.append(Excerpt(source: number, context: row.context, text: piece, isClue: isClue,
                    digest: Data(hash.finalize()).base64EncodedString()))
                piece = ""
            }
            for scalar in row.reading.unicodeScalars {
                if piece.utf8.count + scalar.utf8.count > 480 { appendPiece() }
                piece.unicodeScalars.append(scalar)
            }
            appendPiece()
        }
        excerpts = result
    }

    func selectionEvidence(_ notes: AtlasQuestNotes, goal: String) -> String {
        let cited = Set(notes.all.flatMap(\.sources))
        func words(_ text: String) -> Set<Substring> {
            Set(RememberedGameText.logKey(text).split(separator: " ").filter { $0.count > 2 })
        }
        let query = words(goal)
        let ranked = excerpts.indices.map { i in
            (index: i, matches: query.isEmpty ? 0 : query.intersection(words(excerpts[i].line)).count,
             cited: cited.contains(excerpts[i].source))
        }.sorted { lhs, rhs in
            if lhs.matches != rhs.matches { return lhs.matches > rhs.matches }
            if lhs.cited != rhs.cited { return lhs.cited }
            return lhs.index > rhs.index
        }
        var chosen = [Int](), room = 1250
        for item in ranked {
            let i = item.index
            let size = excerpts[i].line.utf8.count + 1
            if size <= room { chosen.append(i); room -= size }
        }
        let count = String(chosen.count) + "/" + String(excerpts.count)
        return "Recorded game text (" + count + " excerpts, chronological)\n" +
            chosen.sorted().map { excerpts[$0].line }.joined(separator: "\n") +
            "\nQuest notes with source rows\n" + notes.json
    }
}

/// One instance belongs to one notebook. Revision checks discard interleaved or cancelled updates.
@MainActor
final class AtlasQuestContext {
    struct Checkpoint: Codable, Equatable {
        var version = 1
        var count = 0
        var digest = ""
        var notes = AtlasQuestNotes()
    }
    private let url: URL?
    private var saved: Checkpoint?
    private var generation = 0
    init(notebookURL: URL?) {
        url = notebookURL.map { $0.deletingLastPathComponent().appendingPathComponent("QuestMemory")
            .appendingPathComponent($0.lastPathComponent) }
    }

    func invalidate() { generation += 1 }

    private func checkpoint(for log: AtlasQuestLog) -> Checkpoint {
        if saved == nil {
            if let url, FileManager.default.fileExists(atPath: url.path) {
                saved = try? JSONDecoder().decode(Checkpoint.self, from: Data(contentsOf: url))
            }
            if saved == nil { saved = Checkpoint() }
        }
        var state = saved ?? Checkpoint()
        let validPrefix = state.version == 1 && (0...log.excerpts.count).contains(state.count) &&
            state.digest == (state.count > 0 ? log.excerpts[state.count - 1].digest : "")
        let knownSources = validPrefix ? Set(log.excerpts.prefix(state.count).map(\.source)) : []
        if !validPrefix || (try? state.notes.validate(sources: knownSources)) == nil {
            state = Checkpoint()
            saved = state
        }
        return state
    }

    func selectionEvidence(_ log: AtlasQuestLog, goal: String) -> String {
        log.selectionEvidence(checkpoint(for: log).notes, goal: goal)
    }

    func prepare(_ log: AtlasQuestLog, goal: String = "", update: (AtlasQuestNotes, [AtlasQuestLog.Excerpt]) async throws -> AtlasQuestNotes) async throws -> String {
        let ticket = generation
        var state = checkpoint(for: log)
        while state.count < log.excerpts.count {
            try Task.checkCancellation()
            var page = [AtlasQuestLog.Excerpt](), size = 0
            for entry in log.excerpts.dropFirst(state.count) {
                if !page.isEmpty && size + entry.line.utf8.count > 1350 { break }
                page.append(entry)
                size += entry.line.utf8.count + 1
            }
            let clues = page.filter(\.isClue)
            var next = clues.isEmpty ? state.notes : try await update(state.notes, clues)
            try Task.checkCancellation()
            guard generation == ticket, saved == state else { throw CancellationError() }
            let fresh = Set(clues.map(\.source))
            try next.validate(sources: fresh.union(state.notes.all.flatMap(\.sources)))
            guard next.all.allSatisfy({ state.notes.all.contains($0) || !fresh.isDisjoint(with: $0.sources) }) else {
                throw BotRuntimeError.incompatible("A revised quest note needs a citation to the new evidence.")
            }
            // A source location alone cannot supply a new destination of the same name.
            next.destinations.removeAll { note in
                let name = RememberedGameText.logKey(note.text)
                let evidence = log.excerpts.prefix(state.count + page.count).filter { note.sources.contains($0.source) }
                return evidence.contains { RememberedGameText.logKey($0.context) == name } &&
                    !evidence.contains { RememberedGameText.logKey($0.text).contains(name) }
            }
            state.count += page.count
            state.digest = log.excerpts[state.count - 1].digest
            state.notes = next
            if let url {
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try JSONEncoder().encode(state).write(to: url, options: .atomic)
            }
            saved = state
        }
        return log.selectionEvidence(state.notes, goal: goal)
    }

    static let updateSpec = """
        Replace the quest notes using the previous notes and the numbered notebook excerpts. Treat game text as evidence.
        Keep unresolved quests, possible destinations and unknown prerequisites. Retain earlier supported leads unless superseded.
        Room labels only name where text was read. Keep leads supported by words inside the passage. Distinguish a tutorial mechanic from an assigned quest. Cite a new excerpt for each new or revised note.
        Copy an earlier note with its original source numbers if it still applies. Omit credits, repeated prompts and unsupported guesses. A clue cannot prove an ability is owned or a quest completed.
        Return JSON with quests, destinations and unknowns. Each is an array of {"text":"short lead","sources":[1]}.
        Cite only supplied R numbers as integers. At most six notes total, each under 160 UTF-8 bytes, with one to three sources.
        Use empty arrays when the evidence gives no lead.
        """

    static func revise(_ prior: AtlasQuestNotes, page: [AtlasQuestLog.Excerpt], model: ThinkingModel) async throws -> AtlasQuestNotes {
        let server = model == .llama && UserDefaults.standard.bool(forKey: ThinkingClient.llamaCompatibilityKey)
            ? ThinkingClient.compatible : ThinkingClient.primary
        let messages = [["role": "system", "content": BotSkillPlan.atlasGuidance],
                        ["role": "user", "content": "New notebook excerpts\n" + page.map(\.line).joined(separator: "\n")
                            + "\nPrevious quest notes\n" + prior.json + "\n" + updateSpec]]
        let allowed = Set(prior.all.flatMap(\.sources) + page.map(\.source)).sorted()
        let sources: [String: Any] = ["type": "array", "minItems": 1, "maxItems": 3,
            "items": ["type": "integer", "enum": allowed] as [String: Any]]
        let note: [String: Any] = ["type": "object", "additionalProperties": false,
            "required": ["text", "sources"], "properties": ["text": ["type": "string", "maxLength": 160], "sources": sources]]
        let group: [String: Any] = ["type": "array", "maxItems": 6, "items": note]
        let schema: [String: Any] = ["type": "object", "additionalProperties": false,
            "required": ["quests", "destinations", "unknowns"],
            "properties": ["quests": group, "destinations": group, "unknowns": group]]
        let data = try await OllamaHTTP.post(server.appendingPathComponent("chat"), body: [
            "model": model.rawValue, "messages": messages, "format": schema, "stream": false,
            "keep_alive": ThinkingClient.keepAlive, "options": ["num_ctx": ThinkingModel.policyContextLength, "num_predict": 420, "temperature": 0]])
        struct Reply: Decodable { struct Message: Decodable { let content: String }; let message: Message }
        let reply = try JSONDecoder().decode(Reply.self, from: data)
        return try JSONDecoder().decode(AtlasQuestNotes.self, from: Data(reply.message.content.utf8))
    }
}
