import XCTest
import CoreGraphics
import ImageIO
@testable import HollowKnightVision

final class NotebookOpeningTests: XCTestCase {
    private let first = "In wilds beyond they speak your name with reverence and regret,"
    private let second = "For none could tame our savage souls yet you the challenge met,"
    private let third = "Under palest watch, you taught, we changed, base instincts were redeemed,"
    private let fourth = "A world you gave to bug and beast as they had never dreamed."

    @MainActor
    func testOpeningFragmentsAndSpellingsConvergeAndSurviveReopen() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("notebook.json")
        let notebook = GameTextNotebook(url: url)
        let start = Date()
        func record(_ texts: [String], at offset: Double, confidence: Float = 1) async throws {
            let lines = texts.enumerated().map { GameTextLine(id: $0.offset, text: $0.element, bounds: .zero, confidence: confidence) }
            _ = try await notebook.remember(lines, context: RememberedGameText.openingContext, language: "en",
                                            confirmed: [], date: start.addingTimeInterval(offset))
        }
        try await record(["In wild"], at: 0)
        try await record(["In wilds beyond they l", "our name with rererence and regret,"], at: 0.5)
        try await record([first.replacingOccurrences(of: "reverence", with: "rererence")], at: 1)
        try await record([first], at: 2, confidence: 0.9)
        try await record(["For none could tame our svare souls fet vou the challengc mek"], at: 3)
        try await record([second.replacingOccurrences(of: "savage", with: "sarage")], at: 3.5)
        try await record(["Under palest wal"], at: 4)
        try await record([third.replacingOccurrences(of: "watch", with: "watçh")], at: 4.5)
        try await record([third], at: 5)
        try await record(["For none could tame out sarage souls yet you the challenge met,"], at: 6)
        try await record([second.replacingOccurrences(of: "savage", with: "sayage")], at: 7)
        try await record([fourth], at: 8)
        try await record([third.replacingOccurrences(of: "we changed", with: "wre changed")], at: 17)
        try await record([third.replacingOccurrences(of: "we changed", with: "me changed"),
                          "A world you gonc to bug and beast as they had newer drcamod!"], at: 17.5)
        let reopened = try await GameTextNotebook(url: url).entries()
        XCTAssertEqual(reopened.map(\.displayText), [first, second, third, fourth])
        XCTAssertEqual(reopened.first?.text, "In wild")
        let stored = try JSONDecoder().decode([RememberedGameText].self, from: Data(contentsOf: url))
        XCTAssertEqual(stored.map(\.id), reopened.map(\.id))
    }

    func testOldFragmentRowsMigrateOnceWithTheirOriginalFirstReading() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent("notebook.json")
        let start = Date()
        let old = ["In wild", "In wilds beyond they l", "our name with rererence and regret,", first]
            .enumerated().map { i, line in
                RememberedGameText(id: "T\(9 + i)", text: line, context: RememberedGameText.openingContext,
                                   language: "en", date: start.addingTimeInterval(Double(i) * 0.5), confidence: 1)
            }
        try JSONEncoder().encode(old).write(to: file)
        let migrated = try await GameTextNotebook(url: file).entries()
        XCTAssertEqual(migrated.map(\.id), ["T9"])
        XCTAssertEqual(migrated.first?.text, "In wild")
        XCTAssertEqual(migrated.first?.displayText, first)
        let bytes = try Data(contentsOf: file)
        let reopened = try await GameTextNotebook(url: file).entries()
        XCTAssertEqual(reopened.map(\.id), ["T9"])
        XCTAssertEqual(try Data(contentsOf: file), bytes)
    }

    func testFragmentsRequireSameRecentLineAndProtectedValuesAgree() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("notebook.json")
        let book = GameTextNotebook(url: url)
        let start = Date()
        func see(_ text: String, _ y: Double, _ offset: Double, _ context: String = "Cutscene") async throws {
            let box = CGRect(x: 0.1, y: y, width: 0.8, height: 0.03)
            _ = try await book.remember([.init(id: 1, text: text, bounds: box, confidence: 1)],
                context: context, language: "en", confirmed: [], date: start.addingTimeInterval(offset))
        }
        try await see("In wild", 0.2, 0)
        try await see(first, 0.7, 1)
        try await see("Under palest wal", 0.3, 2)
        try await see(third, 0.3, 10)
        try await see("Pay 35 Geo to open the gate", 0.5, 11)
        try await see("Pay 36 Geo to open the gate", 0.5, 12)
        try await see("Travel east along the road before night", 0.5, 13)
        try await see("Travel west along the road before night", 0.5, 14)
        try await see("Travel all the way to the gate", 0.5, 15)
        try await see("Travel all the way to the gatehouse", 0.5, 16, "Elsewhere")
        let rows = try await book.entries()
        XCTAssertEqual(rows.count, 10)
        let loaded = try await GameTextNotebook(url: url).entries()
        XCTAssertEqual(loaded.map(\.id), rows.map(\.id))
        XCTAssertFalse(NotebookTextMatch.same("They had never dreamed", "They had ever dreamed"))
        XCTAssertTrue(NotebookSpelling.known("Hallownest"))
        XCTAssertTrue(NotebookSpelling.known("redeemed"))
        XCTAssertFalse(NotebookSpelling.known("rererence"))
        XCTAssertEqual(NotebookSpelling.preferred("Sarage", confidence: 1, over: "Sayage", confidence: 1, language: "en").0, "Sarage")
        XCTAssertEqual(NotebookSpelling.preferred("sarage", confidence: 1, over: "sayage", confidence: 1, language: "fr").0, "sarage")
    }

    /// Replay the raw readings before the old matcher. The saved game and original evidence are read-only.
    @MainActor
    func testReplayRecordedOpeningWhenAvailable() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let root = env["HKV_OPENING_SCAN_DIRECTORY"] else { throw XCTSkip("Supply the raw opening scan directory") }
        struct Scan: Decodable {
            struct Line: Decodable { let id: Int; let text: String; let confidence: Float; let rect: [CGFloat]? }
            let scan: Int, timestamp: Double, context: String, language: String, lines: [Line]
        }
        let dir = URL(fileURLWithPath: root)
        let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }.sorted { $0.path < $1.path }
        let reader = GameTextReader()
        let ctx = try XCTUnwrap(CGContext(data: nil, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        let image = try XCTUnwrap(ctx.makeImage())
        var previous: GameTextScan?
        var count = 0
        for file in files {
            let frame = try JSONDecoder().decode(Scan.self, from: Data(contentsOf: file))
            guard frame.scan <= 234 else { break }
            let screenshot = file.deletingPathExtension().appendingPathExtension("jpg")
            let recordedImage = FileManager.default.fileExists(atPath: screenshot.path)
                ? CGImageSourceCreateWithURL(screenshot as CFURL, nil).flatMap { CGImageSourceCreateImageAtIndex($0, 0, nil) } : nil
            let current = GameTextScan(image: recordedImage ?? image, language: frame.language, context: frame.context,
                lines: frame.lines.map { line in
                    let box = line.rect.flatMap { $0.count == 4 ? CGRect(x: $0[0], y: $0[1], width: $0[2], height: $0[3]) : nil }
                    return .init(id: line.id, text: line.text, bounds: box ?? .zero, confidence: line.confidence)
                },
                capturedAt: frame.timestamp, milliseconds: 0)
            try await reader.store(current, previous: previous?.context == current.context ? previous!.lines : [])
            previous = current
            count += 1
        }
        XCTAssertEqual(count, 234)
        let poem = reader.memory.filter { $0.context == RememberedGameText.openingContext }.map(\.displayText)
        XCTAssertEqual(Array(poem.prefix(4)), [first, second, third, fourth])
        XCTAssertEqual(poem.count, 5, "\(poem)")
        XCTAssertTrue(poem.last?.contains("MONOMON") == true)
        XCTAssertEqual(reader.memory.filter { $0.context == "Room 1" }.count, 7)
        if let output = env["HKV_OPENING_REPLAY_NOTEBOOK"] {
            try JSONEncoder().encode(reader.memory).write(to: URL(fileURLWithPath: output), options: .atomic)
        }
    }
}
