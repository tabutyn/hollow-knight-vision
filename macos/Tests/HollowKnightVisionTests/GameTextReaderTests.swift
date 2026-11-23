import XCTest
import ImageIO
@testable import HollowKnightVision

final class GameTextReaderTests: XCTestCase {
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

    @MainActor
    func testNotebookFollowsPlaythroughAndDropsLateProofreading() async throws {
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
        let started = expectation(description: "proofreading began")
        var release: CheckedContinuation<Void, Never>?
        let cleanup = Task { @MainActor in
            await reader.cleanReadings(preferred: .gemma, find: { model in
                .init(model: model, server: ThinkingClient.primary, contextLength: nil)
            }, correct: { batch, _ in
                await withCheckedContinuation { release = $0; started.fulfill() }
                return [batch[0].id: .init(text: "Collect SOUL", label: .tutorial, model: "test")]
            })
        }
        await fulfillment(of: [started], timeout: 5)
        await reopen(b)
        XCTAssertTrue(reader.memory.isEmpty)
        try await see("Map Shop")
        release?.resume()
        await cleanup.value
        XCTAssertEqual(reader.memory.map(\.reading), ["Map Shop"])
        await reopen(a)
        XCTAssertEqual(reader.memory.map(\.text), ["Collect SOLIL"])
        let store = GameTextNotebook(url: directory.appendingPathComponent(a.id.uuidString + ".json"))
        try await store.interpret([reader.memory[0].id: .init(text: "Collect SOUL", label: .tutorial, model: "test")])
        await reopen(nil)
        await reopen(a)
        XCTAssertEqual(reader.memory.first?.text, "Collect SOLIL")
        XCTAssertEqual(reader.memory.first?.interpretation?.label, .tutorial)
        XCTAssertEqual(reader.memory.first?.reading, "Collect SOUL")
        XCTAssertEqual(reader.loggedText, ["Tutorial · Collect SOUL"])
        let evidence = await reader.evidence(image: image, context: "Elsewhere", language: "en", question: "SOUL?", enabled: false)
        XCTAssertTrue(evidence.contains("[Room · Tutorial] Collect SOUL"))
        await reopen(SaveProfileAtlas.target(for: .init(slot: 1, isNew: true), active: a, saved: []))
        XCTAssertTrue(reader.memory.isEmpty)
        let newEvidence = await reader.evidence(image: image, context: "Room", language: "en", question: "SOUL?", enabled: false)
        XCTAssertTrue(newEvidence.isEmpty)
        XCTAssertTrue(reader.loggedText.isEmpty)
    }

    @MainActor
    func testOpeningLinesFollowConfirmedSaveAndMergeRevisedCredits() async throws {
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
        XCTAssertEqual(reader.entries.count, 1)
        XCTAssertEqual(reader.loggedText, ["Music and sound credit · " + revised])
        XCTAssertEqual(reader.memory.map(\.text), [initial, revised])
    }

    @MainActor
    func testExplicitGoalBelongsToTheSaveAndCanReturnToExploration() throws {
        let suite = "hkv-goal-tests-" + UUID().uuidString
        let prefs = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { prefs.removePersistentDomain(forName: suite) }
        let reader = GameTextReader(preferences: prefs)
        let first = SaveProfileAtlas(slot: 1), next = SaveProfileAtlas(slot: 1)
        reader.selectProfile(first)
        XCTAssertEqual(reader.currentGoal, BotSkillPlan.defaultObjective)
        reader.setGoal("Reach the next bench")
        reader.selectProfile(next)
        XCTAssertEqual(reader.currentGoal, BotSkillPlan.defaultObjective)
        reader.selectProfile(first)
        XCTAssertEqual(reader.currentGoal, "Reach the next bench")
        reader.setGoal("")
        XCTAssertEqual(reader.currentGoal, BotSkillPlan.defaultObjective)
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
            let lines = text.enumerated().map { GameTextLine(id: $0.offset, text: $0.element, bounds: .zero, confidence: 1) }
            try await reader.store(.init(image: image, language: "en", context: room, lines: lines, capturedAt: Double(i + 2), milliseconds: 0),
                                   previous: previous)
            previous = lines
        }
        XCTAssertEqual(reader.memory.map(\.text), ["Collect SOUIL to HEAL"])
        XCTAssertEqual(reader.memory.first?.isPassage, true)
        XCTAssertEqual(reader.skillReading?.visible, ["Collect SOUIL", "to HEAL"])
    }

    func testProofreadKeepsPricesAndRejectsInventedText() {
        let direction = "Travellers, I hold these memories close to my heart as you once did."
        XCTAssertFalse(GameTextCleanup.hasControlInstruction(direction, passage: true, language: "en"))
        XCTAssertFalse(GameTextCleanup.hasControlInstruction("PRESS A to start", passage: true, language: "en"))
        XCTAssertEqual(GameTextCleanup.conservativeText("Map 35", raw: "Map 55"), "Map 55")
        XCTAssertEqual(GameTextCleanup.conservativeText("Buy the map at Dirtmouth", raw: "Map"), "Map")
        XCTAssertEqual(GameTextCleanup.conservativeText("Collect SOUL", raw: "Collect SOLIL"), "Collect SOUL")
    }

    func testLocalModelProofreadsRecordedTablet() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let path = env["HKV_PROOFREAD_TABLET"] else { throw XCTSkip("Requires a local model and the recorded tablet OCR.") }
        struct Recording: Decodable { let storedPassage: String }
        let raw = try JSONDecoder().decode(Recording.self, from: Data(contentsOf: URL(fileURLWithPath: path))).storedPassage
        let model = try XCTUnwrap(ThinkingModel(rawValue: env["HKV_TEST_OLLAMA_MODEL"] ?? ThinkingModel.gemma.rawValue))
        try await ThinkingClient.load(model) { print("Load · " + $0) }
        let found = try await GameTextCleanup.resident(preferred: model)
        let resident = try XCTUnwrap(found)
        XCTAssertEqual(resident.model, model)
        let row = RememberedGameText(id: "tablet", text: raw, context: "King's Pass tablet", language: "en", date: Date(), isPassage: true)
        let cleaned = try await GameTextCleanup.correct([row], with: resident)
        let value = try XCTUnwrap(cleaned["tablet"])
        print("Local tablet reading (\(value.model), \(value.label)): \(value.text)")
        XCTAssertTrue(value.text.contains("dream"))
        XCTAssertTrue(value.text.contains("SOUL"))
        XCTAssertTrue(value.text.contains("enemies"))
        XCTAssertEqual(value.label, .tutorial)
        let credit = RememberedGameText(id: "credit", text: "Composer: Christopher Larkin",
            context: "Credits", language: "en", date: Date())
        let credits = try await GameTextCleanup.correct([credit], with: resident)
        let composer = try XCTUnwrap(credits[credit.id])
        print("Local credit heading: \(composer.displayLabel) · \(composer.text)")
        XCTAssertEqual(composer.label, .credits)
        XCTAssertTrue(composer.text.contains("Christopher Larkin"))
    }

    func testVisionBoxMapsIntoDisplayedFrame() {
        let line = GameTextLine(id: 1, text: "Bench", bounds: .init(x: 0.2,y: 0.7,width: 0.3,height: 0.1), confidence: 1)
        let rect = line.displayRect(in: .init(width: 640,height: 360))
        XCTAssertEqual(rect.minX, 128, accuracy: 0.001)
        XCTAssertEqual(rect.minY, 72, accuracy: 0.001)
        XCTAssertEqual(rect.height, 36, accuracy: 0.001)
    }
}
