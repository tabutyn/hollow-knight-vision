import SwiftUI

struct RememberedGameText: Codable, Identifiable {
    static let openingContext = "Opening cutscene"
    var id: String
    let text: String
    let context: String
    let language: String
    let date: Date
    var isPassage: Bool? = nil

    var interpretation: GameTextInterpretation?
    var bestText: String?
    var confidence: Float?
    var matchingVersion: Int?
    var otherContexts: [String]?
    var displayText: String { bestText ?? text }

    // Recurring English OCR fragments seen in title art, including during an unlabeled capture.
    static func isTitleArtifact(_ text: String, context: String, language: String) -> Bool {
        guard text.count <= 8, NotebookSpelling.usesEnglish(language) else { return false }
        let screen = context.lowercased().replacingOccurrences(of: " · ", with: " - ").components(separatedBy: " - ")[0]
        guard ["game capture", "main title", "main-title"].contains(screen) else { return false }
        let token = text.lowercased().filter { $0.isLetter || $0.isNumber }
        return ["ce", "ces", "cei", "ceis", "coi"].contains(token)
    }

    var isReadable: Bool {
        ([context] + (otherContexts ?? [])).contains { !Self.isTitleArtifact(displayText, context: $0, language: language) }
    }

    static func key(_ text: String) -> String {
        text.lowercased().split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    static func logKey(_ text: String) -> String {
        text.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).joined(separator: " ")
    }
}

/// Conservative matching for repeated OCR. Numbers and short control words must agree.
enum NotebookTextMatch {
    static func key(_ text: String) -> String {
        RememberedGameText.logKey(text.precomposedStringWithCanonicalMapping)
    }

    static func same(_ lhs: String, _ rhs: String) -> Bool {
        let a = key(lhs), b = key(rhs)
        if a == b { return !a.isEmpty }
        let left = a.split(separator: " "), right = b.split(separator: " ")
        guard left.count == right.count, !left.isEmpty else { return false }
        var differences = 0
        let protected = NotebookLineMatch.protected
        for (x, y) in zip(left, right) where x != y {
            guard !x.contains(where: { $0.isNumber }), !y.contains(where: { $0.isNumber }),
                  !protected.contains(x), !protected.contains(y),
                  min(x.count, y.count) >= (left.count == 1 ? 7 : 4) else { return false }
            // Accept a single OCR insertion, deletion, substitution or adjacent transposition.
            let first = Array(x), second = Array(y)
            let prefix = zip(first, second).prefix { $0 == $1 }.count
            let u = first.dropFirst(prefix), v = second.dropFirst(prefix)
            let close: Bool
            switch first.count - second.count {
            case 1: close = u.dropFirst().elementsEqual(v)
            case -1: close = v.dropFirst().elementsEqual(u)
            case 0: close = u.dropFirst().elementsEqual(v.dropFirst()) ||
                (u.count >= 2 && u.prefix(2).elementsEqual(v.prefix(2).reversed()) && u.dropFirst(2).elementsEqual(v.dropFirst(2)))
            default: close = false
            }
            guard close else { return false }
            differences += 1
            if differences > max(1, (left.count + 2) / 3) { return false }
        }
        return true
    }
}

struct GameTextLogEntry: Identifiable {
    let id: String
    var reading: RememberedGameText
    var contexts: [String]
    var updated: Date

    static func current(_ rows: [RememberedGameText]) -> [Self] {
        rows.map { row in
            Self(id: row.id, reading: row, contexts: [row.context] + (row.otherContexts ?? []), updated: row.date)
        }.sorted { $0.updated == $1.updated ? $0.id < $1.id : $0.updated > $1.updated }
    }
}

actor GameTextNotebook {
    static let directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("HollowKnightVision/Notebooks", isDirectory: true)
    let url: URL?
    private var contents: [RememberedGameText]?
    private var exact = [String: Int]()
    private var buckets = [String: [Int]]()

    private struct Sighting {
        let context: String
        let date: Date
        let bounds: CGRect?
    }
    private var recent = [String: Sighting]()
    private var positions = [String: Int]()

    private func index(_ row: RememberedGameText, at index: Int) {
        let key = NotebookTextMatch.key(row.displayText)
        exact[row.language + "|" + key] = index
        buckets[row.language + "|" + String(key.split(separator: " ").count), default: []].append(index)
        positions[row.id] = index
    }

    private func rebuild(_ rows: [RememberedGameText]) {
        exact.removeAll(keepingCapacity: true)
        buckets.removeAll(keepingCapacity: true)
        positions.removeAll(keepingCapacity: true)
        for (i, row) in rows.enumerated() { index(row, at: i) }
    }

    /// A fuller observation can explain several fragments left by earlier scans.
    /// A later short observation can refer back to one full line, but cannot join two full lines.
    private func merge(_ input: RememberedGameText, into rows: inout [RememberedGameText],
                       bounds: CGRect? = nil, trace: GameTextScanTrace.Decisions? = nil) -> Bool {
        let key = NotebookTextMatch.key(input.displayText)
        let tokens = key.split(separator: " ")
        let bucket = input.language + "|" + String(tokens.count)
        let exactIndex = exact[input.language + "|" + key]
        let strict = exactIndex ?? buckets[bucket]?.first { i in
            let row = rows[i]
            if let seen = recent[row.id], seen.date == input.date { return false }
            return NotebookTextMatch.same(row.displayText, input.displayText)
        }
        var fragments = [Int](), variants = [Int](), complete = [Int]()
        recent = recent.filter { input.date.timeIntervalSince($0.value.date) <= 30 }
        for (id, seen) in recent {
            let elapsed = input.date.timeIntervalSince(seen.date)
            guard elapsed > 0, let i = positions[id], rows[i].language == input.language,
                  NotebookLineMatch.scopeAllows(seen.bounds, bounds) else { continue }
            let old = NotebookTextMatch.key(rows[i].displayText).split(separator: " ")
            let located = seen.bounds.map { !$0.isEmpty } == true && bounds.map { !$0.isEmpty } == true
            if located, elapsed <= 2, old.count != tokens.count, tokens.joined().count >= 6,
               old.joined() == tokens.joined(), NotebookSpelling.usesEnglish(input.language),
               !NotebookSpelling.known(tokens.joined()),
               (old + tokens).allSatisfy({ $0.allSatisfy(\.isLetter) }) {
                variants.append(i)
                continue
            }
            guard seen.context == input.context else { continue }
            if old.count == tokens.count && strict != nil { continue }
            switch NotebookLineMatch.relation(old, tokens, allowFragments: elapsed <= 3, samePosition: located && elapsed <= 3) {
            case .longer: fragments.append(i)
            case .shorter: complete.append(i)
            case .variant: variants.append(i)
            case nil: break
            }
        }
        let whole = strict ?? variants.min()
        let matching = whole ?? fragments.min() ?? (complete.count == 1 ? complete.first : nil)
        let sight = Sighting(context: input.context, date: input.date, bounds: bounds)
        guard let i = matching else {
            var row = input
            row.matchingVersion = 2
            // A short first reading may have become the stable ID of a longer, distinct line.
            if positions[row.id] != nil { row.id = UUID().uuidString }
            index(row, at: rows.count)
            rows.append(row)
            recent[row.id] = sight
            trace?.matched(input: input.text, entryID: row.id, inserted: true, changed: true)
            return true
        }
        var row = rows[i]
        let previous = row.displayText
        var changed = false
        if whole != nil {
            let (preferred, confidence) = NotebookSpelling.preferred(previous, confidence: row.confidence ?? 0,
                over: input.displayText, confidence: input.confidence ?? 0, language: input.language)
            if preferred != previous || confidence > (row.confidence ?? 0) {
                row.bestText = preferred
                row.confidence = confidence
                changed = true
            }
        } else if fragments.contains(i) {
            row.bestText = input.displayText
            row.confidence = input.confidence
            changed = true
        }
        let retired = fragments.filter { $0 != i }.sorted(by: >)
        for source in [input] + retired.map({ rows[$0] }) {
            for context in [source.context] + (source.otherContexts ?? []) where row.context != context && !(row.otherContexts ?? []).contains(context) {
                row.otherContexts = (row.otherContexts ?? []) + [context]
                changed = true
            }
            if source.isPassage == true && row.isPassage != true { row.isPassage = true; changed = true }
        }
        rows[i] = row
        let retiredIDs = retired.map { rows[$0].id }
        for j in retired {
            recent[rows[j].id] = nil
            rows.remove(at: j)
        }
        if !retired.isEmpty || NotebookTextMatch.key(previous) != NotebookTextMatch.key(row.displayText) {
            rebuild(rows)
        }
        recent[row.id] = sight
        changed = changed || !retired.isEmpty
        trace?.matched(input: input.text, entryID: row.id, inserted: false, changed: changed, absorbed: retiredIDs)
        return changed
    }

    private func saveMerged(_ observations: [RememberedGameText], bounds: [CGRect] = [],
                            trace: GameTextScanTrace.Decisions? = nil) throws -> [RememberedGameText] {
        var rows = try entries()
        var changed = false
        for (i, row) in observations.enumerated() {
            if merge(row, into: &rows, bounds: bounds.isEmpty ? nil : bounds[i], trace: trace) { changed = true }
        }
        if changed {
            do { try write(rows) }
            catch { contents = nil; rebuild([]); recent = [:]; throw error }
        }
        return rows
    }

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
        _ = try saveMerged(observations)
    }

    func entries() throws -> [RememberedGameText] {
        if let contents { return contents }
        let rows: [RememberedGameText]
        if let url, FileManager.default.fileExists(atPath: url.path) {
            rows = try JSONDecoder().decode([RememberedGameText].self, from: Data(contentsOf: url))
        } else { rows = [] }
        if rows.allSatisfy({ $0.matchingVersion == 2 }) {
            rebuild(rows)
            contents = rows
            return rows
        }
        rebuild([])
        recent = [:]
        var unique = [RememberedGameText]()
        for row in rows { _ = merge(row, into: &unique) }
        do { try write(unique) }
        catch { rebuild([]); recent = [:]; throw error }
        recent = [:]
        return unique
    }

    func remember(_ lines: [GameTextLine], context: String, language: String, confirmed: Set<String>, passage: Bool = false, date: Date = Date(), trace: GameTextScanTrace.Decisions? = nil) throws -> [RememberedGameText] {
        var added = [RememberedGameText]()
        var bounds = [CGRect]()
        for line in lines {
            let key = RememberedGameText.key(line.text)
            guard !RememberedGameText.isTitleArtifact(line.text, context: context, language: language),
                  key.count >= 2, key.contains(where: { $0.isLetter }),
                  line.confidence >= 0.8 || (line.confidence >= 0.45 && confirmed.contains(key)) else { continue }
            let id = ([language, context, key] + (passage ? ["passage"] : [])).joined(separator: "\u{1f}")
            added.append(.init(id: id, text: line.text, context: context, language: language, date: date, isPassage: passage ? true : nil, confidence: line.confidence))
            bounds.append(line.bounds)
        }
        return try saveMerged(added, bounds: bounds, trace: trace)
    }

    static func recall(_ rows: [RememberedGameText], question: String, context: String,
                       current: [String], language: String) -> [String] {
        let words = Set(question.lowercased().components(separatedBy: CharacterSet.letters.inverted).filter { $0.count > 2 })
        let onScreen = Set(current.map(RememberedGameText.key))
        let matches = GameTextLogEntry.current(rows).map(\.reading).filter {
            $0.language == language && !onScreen.contains(RememberedGameText.key($0.displayText))
        }
        let scored: [(score: Int, rank: Int, entry: RememberedGameText)] = matches.enumerated().map { index, item in
            let tokens = Set(item.displayText.lowercased().components(separatedBy: CharacterSet.letters.inverted))
            return (words.intersection(tokens).count * 3 + (context == item.context ? 1 : 0) + (item.isPassage == true ? 2 : 0), index, item)
        }
        let ordered = scored.sorted { $0.score == $1.score ? $0.rank < $1.rank : $0.score > $1.score }
        return ordered.prefix(4).map {
            let entry = $0.entry
            return "[" + entry.context + "] " + entry.displayText
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

    var readableLines: [GameTextLine] {
        lines.filter { !RememberedGameText.isTitleArtifact($0.text, context: context, language: language) }
    }
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
        settledPassage = nil
        inspection = (sight.context, start, Set(sight.lines.map(RememberedGameText.key)), nil)
    }
    @Published private(set) var profile: SaveProfileAtlas?
    @Published private(set) var loggedText = [String]()
    var transcript: String { entries.map(\.reading.displayText).joined(separator: "\n\n") }

    private let directory: URL
    private let trace = GameTextScanTrace()
    private var notebook: GameTextNotebook
    private var openingSource: GameTextNotebook?
    private var revision = -1
    private var settledPassage: (context: String, language: String, lines: [String])?

    init(directory: URL = GameTextNotebook.directory, notebook: GameTextNotebook? = nil) {
        self.directory = directory
        self.notebook = notebook ?? GameTextNotebook()
    }

    func selectProfile(_ next: SaveProfileAtlas?) {
        guard next != profile else { return }
        openingSource = profile == nil && next != nil ? notebook : nil
        profile = next
        loggedText = []
        notebook = next.map { GameTextNotebook(url: directory.appendingPathComponent($0.id.uuidString + ".json")) } ?? GameTextNotebook()
        revision = -1
        memory = []
        entries = []
        inspection = nil
        settledPassage = nil
        skillReading = nil
        scan = nil
        issue = nil
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
        memory = snapshot.rows.filter(\.isReadable)
        entries = GameTextLogEntry.current(memory)
        let lines = entries.prefix(4).map { $0.reading.displayText }
        if lines != loggedText { loggedText = lines }
    }

    func store(_ next: GameTextScan, previous: [GameTextLine]) async throws {
        let linesToRead = next.readableLines
        let source = notebook
        let date = Date(timeIntervalSinceNow: next.capturedAt - ProcessInfo.processInfo.systemUptime)
        let decisions = trace.map { _ in GameTextScanTrace.Decisions() }
        let confirmed = Set(linesToRead.filter { line in previous.contains { NotebookTextMatch.same($0.text, line.text) } }.map { RememberedGameText.key($0.text) })
        let visible = linesToRead.filter { $0.confidence >= 0.45 }.map(\.text)
        var passage: String?
        if let pending = inspection, next.context != pending.context || next.capturedAt - pending.started > 12 {
            inspection = nil
        }
        if var pending = inspection {
            if next.capturedAt > pending.started {
                let newLines = linesToRead.filter { line in
                    let key = RememberedGameText.key(line.text)
                    return line.confidence >= 0.45 && !pending.old.contains(key) && key.filter(\.isLetter).count >= 4
                }
                let page = newLines.map(\.text).joined(separator: " ")
                if !page.isEmpty && pending.candidate.map({ NotebookTextMatch.same(page, $0) }) == true {
                    passage = page
                    settledPassage = (next.context, next.language, newLines.map(\.text))
                    inspection = nil
                    let entry = GameTextLine(id: 0, text: page, bounds: .zero, confidence: newLines.map(\.confidence).min() ?? 0)
                    _ = try await source.remember([entry], context: next.context, language: next.language,
                                                   confirmed: [RememberedGameText.key(page)], passage: true, date: date, trace: decisions)
                } else {
                    pending.candidate = page
                    inspection = pending
                }
            }
        } else {
            // A cutscene or scrolling credit can disappear before the next frame.
            let transient = ["Game capture", "Credits", RememberedGameText.openingContext].contains(next.context)
            var pageIDs = Set<Int>()
            if let settledPassage, settledPassage.context == next.context, settledPassage.language == next.language {
                let lines = linesToRead.filter { line in
                    line.confidence >= 0.45 && settledPassage.lines.contains { NotebookTextMatch.same($0, line.text) }
                }
                pageIDs = Set(lines.map(\.id))
                let page = lines.map(\.text).joined(separator: " ")
                if lines.isEmpty { self.settledPassage = nil }
                else if NotebookTextMatch.same(page, settledPassage.lines.joined(separator: " ")) {
                    let entry = GameTextLine(id: 0, text: page, bounds: .zero, confidence: lines.map(\.confidence).min() ?? 0)
                    _ = try await source.remember([entry], context: next.context, language: next.language,
                                                  confirmed: [RememberedGameText.key(page)], passage: true, date: date, trace: decisions)
                }
            } else { settledPassage = nil }
            let stable = linesToRead.filter {
                let key = RememberedGameText.key($0.text)
                return (confirmed.contains(key) || (transient && $0.confidence >= 0.8)) && !pageIDs.contains($0.id)
            }
            _ = try await source.remember(stable, context: next.context, language: next.language, confirmed: confirmed, date: date, trace: decisions)
        }
        guard !Task.isCancelled, notebook === source else { return }
        // A cue that leaves and re-enters view can refer to another tablet or NPC in the same room.
        let prior = skillReading.flatMap {
            $0.context == next.context && (0...2.5).contains(next.capturedAt - $0.capturedAt) ? $0 : nil
        }
        var reading = SkillReading(context: next.context, capturedAt: next.capturedAt, visible: visible, passage: passage)
        let keys = visible.map(RememberedGameText.key)
        for cue in ["Inspect", "Listen"].map(RememberedGameText.key) where keys.filter({ $0 == cue }).count == 1 {
            reading.promptStarts[cue] = prior?.promptStarts[cue] ?? next.capturedAt
        }
        skillReading = reading
        try await refresh(source)
        if let trace, let decisions, notebook === source, !Task.isCancelled {
            do { try trace.write(scan: next, profile: profile, rows: memory, decisions: decisions) }
            catch { NSLog("Notebook trace failed: %@", error.localizedDescription) }
        }
    }

    func policyContext() async throws -> String {
        let source = notebook
        try await refresh(source)
        try Task.checkCancellation()
        guard source === notebook else { throw CancellationError() }
        var budget = 1250
        var excerpts = [String]()
        for (index, row) in memory.enumerated().reversed() {
            let line = "[R\(index + 1) · \(row.context)] " + String(row.displayText.prefix(300))
            if line.utf8.count + 1 <= budget {
                excerpts.append(line)
                budget -= line.utf8.count + 1
            }
        }
        return "Unique OCR readings from this playthrough\n" + excerpts.reversed().joined(separator: "\n")
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
                        skillReading = nil
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
        if let scan, scan.image === image, scan.language == language, scan.context == context {
            return scan.readableLines.map(\.text)
        }
        let result = try? await Self.recognize(image, language: language, context: context)
        return result?.readableLines.map(\.text) ?? []
    }
}
