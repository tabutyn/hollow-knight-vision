import XCTest
import ImageIO
@testable import HollowKnightVision

final class GameTextReaderTests: XCTestCase {
    @MainActor
    func testTitleFragmentsStayInRawScanAndOutOfNotebookAndPolicy() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let profile = SaveProfileAtlas(slot: 1)
        let url = directory.appendingPathComponent(profile.id.uuidString + ".json")
        let legacy = RememberedGameText(id: "old", text: "Ces", context: "Game capture", language: "en",
                                        date: Date(), matchingVersion: 2)
        try JSONEncoder().encode([legacy]).write(to: url)
        let reader = GameTextReader(directory: directory)
        reader.selectProfile(profile)
        let image = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8, bytesPerRow: 8,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)?.makeImage())
        let texts = ["Ces", "Ce.s", "Ce s", "Yes", "No", "Off", "Start Game"]
        let lines = texts.enumerated().map { GameTextLine(id: $0.offset, text: $0.element, bounds: .zero, confidence: 1) }
        let scan = GameTextScan(image: image, language: "en", context: "Main Title - Start Game",
                                lines: lines, capturedAt: 10, milliseconds: 0)
        try await reader.store(scan, previous: lines)
        let policy = try await reader.policyContext()
        XCTAssertEqual(scan.lines.map(\.text), texts)
        XCTAssertEqual(scan.readableLines.map(\.text), ["Yes", "No", "Off", "Start Game"])
        XCTAssertEqual(Set(reader.entries.map(\.reading.displayText)), Set(scan.readableLines.map(\.text)))
        XCTAssertFalse(policy.contains("Ces"))
        XCTAssertTrue(policy.contains("Start Game"))
        let original = try JSONDecoder().decode([RememberedGameText].self, from: Data(contentsOf: url))
        XCTAssertTrue(original.contains { $0.id == legacy.id && $0.text == legacy.text })
        XCTAssertFalse(RememberedGameText.isTitleArtifact("Ces", context: "Main Title", language: "fr"))
        XCTAssertFalse(RememberedGameText.isTitleArtifact("Ces", context: "King's Pass", language: "en"))
        XCTAssertFalse(RememberedGameText.isTitleArtifact("Ce2", context: "Game capture", language: "en"))
    }

    func testNotebookPersistsUniqueReadingsAndRetrievesOlderClue() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).appendingPathComponent("readings.json")
        defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent()) }
        let notebook = GameTextNotebook(url: url)
        let clue = GameTextLine(id: 1, text: "Hold Focus to heal", bounds: .zero, confidence: 0.6)
        let map = GameTextLine(id: 2, text: "Map Shop", bounds: .zero, confidence: 0.98)
        let initial = try await notebook.remember([clue, map], context: "Room 1", language: "en", confirmed: [])
        XCTAssertEqual(initial.map(\.text), [map.text])
        _ = try await notebook.remember([clue, map], context: "Room 1", language: "en", confirmed: ["hold focus to heal"])
        let reopened = try await GameTextNotebook(url: url).entries()
        XCTAssertEqual(reopened.count, 2)
        let remembered = GameTextNotebook.recall(reopened, question: "How do I heal?", context: "Room 9",
                                                 current: [map.text], language: "en")
        XCTAssertEqual(remembered, ["[Room 1] Hold Focus to heal"])
    }

    func testRepeatedOCRKeepsOneObservedReadingAcrossFramesRoomsAndReopen() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("save.json")
        let book = GameTextNotebook(url: file)
        func see(_ text: String, _ confidence: Float, _ room: String = "Pass", _ language: String = "en") async throws {
            let line = GameTextLine(id: 1, text: text, bounds: .zero, confidence: confidence)
            _ = try await book.remember([line], context: room, language: language, confirmed: [])
        }
        try await see("Collect SOUIL by striking enemnies.", 0.82)
        try await see("Collect SOUL by striking enemies.", 0.98)
        try await see("COLLECT SOUL  by striking enemies!", 0.91, "Crossroads")
        try await see("Map costs 35 Geo", 1)
        try await see("Map costs 36 Geo", 1)
        try await see("Travel east", 1)
        try await see("Travel west", 1)
        try await see("Travel east", 1, "Pass", "fr")
        let reopened = try await GameTextNotebook(url: file).entries()
        XCTAssertEqual(reopened.count, 6)
        XCTAssertEqual(reopened[0].text, "Collect SOUIL by striking enemnies.")
        XCTAssertEqual(reopened[0].displayText, "Collect SOUL by striking enemies.")
        XCTAssertEqual(reopened[0].otherContexts, ["Crossroads"])
        XCTAssertFalse(NotebookTextMatch.same("Inspect", "Insect"))
    }

    @MainActor
    func testNotebookFollowsPlaythroughAndCanOpenLegacyReadings() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let reader = GameTextReader(directory: directory)
        let a = SaveProfileAtlas(slot: 1), b = SaveProfileAtlas(slot: 2)
        let image = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8,
            bytesPerRow: 8, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)?.makeImage())
        func see(_ text: String, context: String = "Room", stable: Bool = true) async throws {
            let line = GameTextLine(id: 1, text: text, bounds: .zero, confidence: 1)
            try await reader.store(.init(image: image, language: "en", context: context, lines: [line],
                                         capturedAt: 10, milliseconds: 0), previous: stable ? [line] : [])
        }
        func reopen(_ profile: SaveProfileAtlas?) async {
            reader.selectProfile(profile)
            await reader.monitor(enabled: false, language: "en") { nil }
        }

        try await see("Title menu")
        XCTAssertEqual(reader.memory.map(\.text), ["Title menu"])
        await reopen(a)
        try await see("Collect SOLIL")
        await reopen(b)
        XCTAssertTrue(reader.memory.isEmpty)
        try await see("Map Shop")
        XCTAssertEqual(reader.memory.map(\.displayText), ["Map Shop"])
        let otherSave = try await reader.policyContext()
        XCTAssertTrue(otherSave.contains("Map Shop"))
        XCTAssertFalse(otherSave.contains("Collect SOLIL"))
        await reopen(a)
        XCTAssertEqual(reader.memory.map(\.text), ["Collect SOLIL"])
        let store = GameTextNotebook(url: directory.appendingPathComponent(a.id.uuidString + ".json"))
        try await store.interpret([reader.memory[0].id: .init(text: "Collect SOUL", label: .tutorial, model: "test")])
        await reopen(nil)
        await reopen(a)
        XCTAssertEqual(reader.memory.first?.text, "Collect SOLIL")
        XCTAssertEqual(reader.memory.first?.interpretation?.label, .tutorial)
        XCTAssertEqual(reader.memory.first?.displayText, "Collect SOLIL")
        XCTAssertEqual(reader.loggedText, ["Collect SOLIL"])
        let evidence = await reader.evidence(image: image, context: "Elsewhere", language: "en", question: "SOUL?", enabled: false)
        XCTAssertTrue(evidence.contains("[Room] Collect SOLIL"))
        await reopen(SaveProfileAtlas.target(for: .init(slot: 1, isNew: true), active: a, saved: []))
        XCTAssertTrue(reader.memory.isEmpty)
        let newEvidence = await reader.evidence(image: image, context: "Room", language: "en", question: "SOUL?", enabled: false)
        XCTAssertTrue(newEvidence.isEmpty)
        XCTAssertTrue(reader.loggedText.isEmpty)
    }

    @MainActor
    func testOpeningCreditsRemainDistinctDespiteALegacyModelAnnotation() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let reader = GameTextReader(directory: directory)
        let image = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8,
            bytesPerRow: 8, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)?.makeImage())
        let initial = "Composer: Christopher Larkin"
        let revised = "Composer and sound designer: Christopher Larkin"
        for (index, text) in ["Title text", initial, revised].enumerated() {
            let line = GameTextLine(id: index, text: text, bounds: .zero, confidence: 1)
            let context = index == 0 ? "Main Title" : RememberedGameText.openingContext
            try await reader.store(.init(image: image, language: "en", context: context, lines: [line],
                capturedAt: Double(index), milliseconds: 0), previous: index == 0 ? [line] : [])
        }
        XCTAssertEqual(reader.memory.count, 3)
        let selected = SaveProfileAtlas(slot: 1)
        reader.selectProfile(selected)
        await reader.monitor(enabled: false, language: "en") { nil }
        XCTAssertEqual(reader.memory.map(\.text), [initial, revised])
        let file = directory.appendingPathComponent(selected.id.uuidString + ".json")
        let notebook = GameTextNotebook(url: file)
        let observations = try await notebook.entries()
        try await notebook.interpret([
            observations[0].id: .init(text: initial, label: .credits, model: "test", detail: "Composer credit"),
            observations[1].id: .init(text: revised, label: .credits, model: "test",
                                      detail: "Music and sound credit", sameAs: observations[0].id)
        ])
        reader.selectProfile(nil)
        reader.selectProfile(selected)
        await reader.monitor(enabled: false, language: "en") { nil }
        XCTAssertEqual(reader.entries.count, 2)
        XCTAssertEqual(Set(reader.loggedText), Set([initial, revised]))
        XCTAssertEqual(reader.memory.map(\.text), [initial, revised])
    }

    @MainActor
    func testInspectStoresOneSettledPageAndRetainsRawActionEvidence() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let reader = GameTextReader(directory: directory)
        reader.selectProfile(SaveProfileAtlas(slot: 1))
        let room = "King's Pass"
        let image = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8,
            bytesPerRow: 8, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)?.makeImage())
        reader.beginInspection(.init(context: room, capturedAt: 1, generation: 1, gameplay: true,
            menu: nil, selected: nil, lines: ["INSPECT"], hasAbilityControls: true), at: 1)
        var previous = [GameTextLine]()
        for (i, text) in [["Collect"], ["Collect SOUIL", "to HEAL"], ["Collect SOUIL", "to HEAL"], ["Collect SOUIL", "to HEAL"]].enumerated() {
            let lines = text.enumerated().map { GameTextLine(id: $0.offset, text: $0.element, bounds: .zero, confidence: 0.9) }
            try await reader.store(.init(image: image, language: "en", context: room, lines: lines, capturedAt: Double(i + 2), milliseconds: 0),
                                   previous: previous)
            previous = lines
        }
        XCTAssertEqual(reader.memory.map(\.text), ["Collect SOUIL to HEAL"])
        XCTAssertEqual(reader.memory.first?.isPassage, true)
        XCTAssertEqual(reader.skillReading?.visible, ["Collect SOUIL", "to HEAL"])
        let corrected = ["Collect SOUL", "to HEAL"].enumerated().map {
            GameTextLine(id: $0.offset, text: $0.element, bounds: .zero, confidence: 1)
        }
        try await reader.store(.init(image: image, language: "en", context: room, lines: corrected, capturedAt: 7, milliseconds: 0), previous: previous)
        XCTAssertEqual(reader.memory.count, 1)
        XCTAssertEqual(reader.memory[0].displayText, "Collect SOUL to HEAL")
    }

    func testVisionBoxMapsIntoDisplayedFrame() {
        let line = GameTextLine(id: 1, text: "Bench", bounds: .init(x: 0.2,y: 0.7,width: 0.3,height: 0.1), confidence: 1)
        let rect = line.displayRect(in: .init(width: 640,height: 360))
        XCTAssertEqual(rect.minX, 128, accuracy: 0.001)
        XCTAssertEqual(rect.minY, 72, accuracy: 0.001)
        XCTAssertEqual(rect.height, 36, accuracy: 0.001)
    }
}
