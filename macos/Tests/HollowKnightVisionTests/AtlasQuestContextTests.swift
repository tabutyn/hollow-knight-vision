import ImageIO
import XCTest
@testable import HollowKnightVision

@MainActor
final class AtlasQuestContextTests: XCTestCase {
    private func row(_ text: String, _ context: String = "King's Pass") -> RememberedGameText {
        .init(id: text, text: text, context: context, language: "en", date: Date())
    }

    func testOnlyNewEvidenceUpdatesTheSavedNotesAndCorrectionsReplay() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("save.json")
        let context = AtlasQuestContext(notebookURL: url)
        let first = row("A ferry travels east.")
        let second = row("The eastern ferry is closed.")
        let lead = AtlasQuestNotes(destinations: [.init(text: "Investigate the eastern ferry.", sources: [1])])
        _ = try await context.prepare(AtlasQuestLog([first])) { old, batch in
            XCTAssertTrue(old.all.isEmpty)
            XCTAssertEqual(batch.map(\.source), [1])
            return lead
        }
        let reopened = AtlasQuestContext(notebookURL: url)
        let blocked = AtlasQuestNotes(unknowns: [.init(text: "Find when the eastern ferry reopens.", sources: [1, 2])])
        _ = try await reopened.prepare(AtlasQuestLog([first, second])) { old, batch in
            XCTAssertEqual(old, lead)
            XCTAssertEqual(batch.map(\.source), [2])
            return blocked
        }
        let unchanged = try await reopened.prepare(AtlasQuestLog([first, second])) { _,_ in
            XCTFail("Repeated inference for an unchanged notebook"); return .init()
        }
        XCTAssertTrue(unchanged.contains(blocked.json))
        _ = try await reopened.prepare(AtlasQuestLog([row("A lift travels west."), second])) { old, batch in
            XCTAssertTrue(old.all.isEmpty)
            XCTAssertEqual(batch.map(\.source), [1, 2])
            return .init()
        }
    }

    func testLongJournalPagesAndRejectedCitationsNeverAdvancePastUnprocessedText() async throws {
        let journal = AtlasQuestLog([row(String(repeating: "古", count: 1700))])
        let context = AtlasQuestContext(notebookURL: nil)
        do {
            _ = try await context.prepare(journal) { _,_ in .init(quests: [.init(text: "Go east.", sources: [999])]) }
            XCTFail("Accepted an invented source")
        } catch { XCTAssertFalse(error is CancellationError) }
        var pages = [[String]]()
        let selection = try await context.prepare(journal) { _, batch in
            pages.append(batch.map(\.line))
            return .init()
        }
        XCTAssertGreaterThan(pages.count, 1)
        XCTAssertEqual(pages.flatMap { $0 }, journal.excerpts.map(\.line))
        XCTAssertTrue(selection.contains("/" + String(journal.excerpts.count) + " excerpts"))
        XCTAssertLessThan(selection.utf8.count, 1500)
    }

    func testDecisionRetrievesAnOldClueAndPlacesTheContractAfterLiveEvidence() throws {
        let rows = [row("The stag station is beyond the lever.")] + (1...35).map { row("Ordinary passage number " + String($0)) }
        let evidence = AtlasQuestLog(rows).selectionEvidence(.init(), goal: "Find the stag station")
        XCTAssertTrue(evidence.contains("[R1 · King's Pass]"))
        let request = try ThinkingClient.request(model: .gemma, image: nil,
            context: ThinkingContext(evidence: evidence + "\nLive decision evidence\nCurrent goal: Find the stag station", hasImage: false))
        let body = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? [String: Any])
        XCTAssertEqual((body["options"] as? [String: Int])?["num_ctx"], ThinkingModel.policyContextLength)
        let messages = try XCTUnwrap(body["messages"] as? [[String: Any]])
        XCTAssertEqual(messages.count, 2)
        XCTAssertTrue((messages[0]["content"] as? String)?.contains(BotSkillPlan.atlasGuidance) == true)
        let content = try XCTUnwrap(messages[1]["content"] as? String)
        let sections = ["Recorded game text", "Quest notes with source rows", "Live decision evidence", "Policy selection specification"]
        var cursor = content.startIndex
        for section in sections {
            cursor = try XCTUnwrap(content.range(of: section, range: cursor..<content.endIndex)).upperBound
        }
    }

    func testBareInteractionCueRemainsVisibleInHistoryWithoutBecomingAQuest() async throws {
        let context = AtlasQuestContext(notebookURL: nil)
        let first = row("INSPECT")
        let memory = try await context.prepare(AtlasQuestLog([first])) { _,_ in
            XCTFail("A button prompt created a quest update"); return .init()
        }
        XCTAssertTrue(memory.contains("[R1 · King's Pass] INSPECT"))
        let fact = row("The eastern ferry is closed.")
        let old = AtlasQuestNotes(unknowns: [.init(text: "When does the ferry open?", sources: [2])])
        _ = try await context.prepare(AtlasQuestLog([first, fact])) { _,page in
            XCTAssertEqual(page.map(\.source), [2]); return old
        }
        let updated = AtlasQuestLog([first, fact, row("The west gate is open.")])
        do {
            _ = try await context.prepare(updated) { _,_ in .init(destinations: [.init(text: "West gate", sources: [2])]) }
            XCTFail("Used an earlier ferry citation to justify a new gate")
        } catch { XCTAssertFalse(error is CancellationError) }
    }

    func testDestinationNeedsMoreThanTheRoomLabel() async throws {
        let context = AtlasQuestContext(notebookURL: nil)
        let notes = AtlasQuestNotes(destinations: [.init(text: "Dirtmouth", sources: [1])])
        let seen = try await context.prepare(AtlasQuestLog([row("A gate leads east.", "Dirtmouth")])) { _,_ in notes }
        XCTAssertTrue(seen.contains("\"destinations\":[]"))
        let confirmed = try await context.prepare(AtlasQuestLog([row("Return to Dirtmouth.", "Dirtmouth")])) { _,_ in notes }
        XCTAssertTrue(confirmed.contains(notes.json))
    }

    func testCancelledUpdateCannotReplaceNotes() async throws {
        let context = AtlasQuestContext(notebookURL: nil)
        let journal = AtlasQuestLog([row("Go west")])
        do {
            _ = try await context.prepare(journal) { _,_ in context.invalidate(); return .init() }
            XCTFail("Accepted a superseded update")
        } catch is CancellationError {}
        _ = try await context.prepare(journal) { old, page in
            XCTAssertTrue(old.all.isEmpty); XCTAssertEqual(page.count, 1)
            return .init()
        }
    }

    func testLocalGemmaRevisesNotesThenProposesAnEligiblePolicy() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let path = env["HKV_QUEST_TABLET_FIXTURE"] else { throw XCTSkip("Set HKV_QUEST_TABLET_FIXTURE to the recorded tablet directory") }
        let fixture = URL(fileURLWithPath: path)
        let payload = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: fixture.appendingPathComponent("recall.json"))) as? [String: Any])
        let prompt = try XCTUnwrap((payload["prompt"] as? [String])?.first)
        let passage = try XCTUnwrap(payload["storedPassage"] as? String)
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(fixture.appendingPathComponent("inspect.png") as CFURL, nil))
        let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        let context = AtlasQuestContext(notebookURL: nil)
        var transitions = [[String: String]]()
        func revise(_ old: AtlasQuestNotes, _ page: [AtlasQuestLog.Excerpt]) async throws -> AtlasQuestNotes {
            let next = try await AtlasQuestContext.revise(old, page: page, model: .gemma)
            transitions.append(["previous": old.json, "newText": page.map(\.line).joined(separator: "\n"), "next": next.json])
            return next
        }
        let first = row(prompt)
        _ = try await context.prepare(AtlasQuestLog([first]), update: revise)
        let memory = try await context.prepare(AtlasQuestLog([first, row(passage)]), update: revise)
        let goal = BotSkillPlan.defaultObjective
        let evidence = memory + "\nLive decision evidence\nKing's Pass, gameplay. Visible OCR: " + prompt +
            "\nCurrent goal: " + goal
        let sight = SkillSight(context: "King's Pass", capturedAt: 1, generation: 1, gameplay: true,
                               menu: nil, selected: nil, lines: [prompt], hasAbilityControls: true)
        let request = ThinkingContext(evidence: evidence, hasImage: true, sight: sight, visible: [prompt])
        let answer = try await ThinkingClient.answer(model: .gemma, image: frame, context: request)
        let plan = try request.contract.resolve(Data(answer.utf8))
        XCTAssertEqual(transitions.count, 1, "A bare INSPECT prompt must not create a quest")
        XCTAssertEqual(plan.choose(sight)?.control, .inspect, answer)
        let hidden = SkillSight(context: sight.context, capturedAt: 2, generation: 1, gameplay: true,
                               menu: nil, selected: nil, lines: [], hasAbilityControls: true)
        XCTAssertNotEqual(plan.choose(hidden)?.control, .inspect, "A remembered prompt incorrectly authorized Inspect")
        if let trace = env["HKV_QUEST_TRACE"] {
            let object: [String: Any] = ["updates": transitions, "memory": memory, "response": answer, "selected": plan.choose(sight)?.control.rawValue ?? ""]
            try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]).write(to: URL(fileURLWithPath: trace))
        }
    }
}
