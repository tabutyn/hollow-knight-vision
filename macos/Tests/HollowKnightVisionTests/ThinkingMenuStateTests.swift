import CoreGraphics
import XCTest
@testable import HollowKnightVision

@MainActor
final class ThinkingMenuStateTests: XCTestCase {
    private func image(_ width: Int) throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: width, height: 2, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        return try XCTUnwrap(context.makeImage())
    }

    private func element(_ id: String) -> ThinkingMenuPage.Element {
        .init(id: id, center: CGPoint(x: 10, y: 20), label: id)
    }

    func testTitlePromptListsEachSelectableIDOnceAndOffersOnlyAvailableActions() throws {
        let elements = [
            ThinkingMenuPage.Element(id: "main-title.start-game", center: .zero, label: "Start Game"),
            .init(id: "shared.options", center: .zero, label: "Options"),
            .init(id: "protected", center: .zero, label: "Unavailable", canSelect: false)
        ]
        let page = ThinkingMenuPage(stateID: "main-title", selected: elements[0].id, elements: elements)
        let contract = ThinkingPolicyContract(sight: nil, visible: [], capturedMenu: page)
        let prompt = contract.selectionSpec
        XCTAssertTrue(prompt.contains("Choose Start Game [highlighted]. Reply with "))
        let schema = try XCTUnwrap(contract.schema["oneOf"] as? [[String: Any]])
        let select = try XCTUnwrap(schema.first?["properties"] as? [String: [String: Any]])
        XCTAssertEqual(select["elementId"]?["enum"] as? [String], elements.prefix(2).map(\.id))
        XCTAssertTrue(prompt.contains("Options — shared.options"))
        for entry in elements.prefix(2) {
            XCTAssertEqual(prompt.components(separatedBy: entry.id).count, 2)
            let reply = try JSONSerialization.data(withJSONObject: ["menu": "select", "elementId": entry.id])
            XCTAssertEqual(try contract.menuScript(reply).request, .select(entry.id))
        }
        XCTAssertTrue(prompt.contains(#"{"menu":"more"}"#))
        XCTAssertFalse(prompt.contains("protected"))
        XCTAssertFalse(prompt.contains(#"{"menu":"wait"}"#))
        let blocked = ThinkingPolicyContract(sight: nil, visible: [], allowsMore: false, capturedMenu: page)
        XCTAssertFalse(blocked.selectionSpec.contains(#"{"menu":"more"}"#))
        XCTAssertThrowsError(try blocked.menuScript(Data(#"{"menu":"more"}"#.utf8)))
        let empty = ThinkingPolicyContract(sight: nil, visible: [], capturedMenu: .init(stateID: "main-title"))
        XCTAssertTrue(empty.selectionSpec.contains(#"{"menu":"wait"}"#))
        XCTAssertFalse(empty.selectionSpec.contains("elementId"))
    }

    func testEachMenuEntryReachesModelDuringNavigationAndWaitsForMissingElements() async throws {
        let pages = [
            ThinkingMenuPage(stateID: "pause", selected: "quit", elements: [element("quit")]),
            ThinkingMenuPage(stateID: "quit-confirmation", selected: "yes", elements: [element("yes")]),
            ThinkingMenuPage(stateID: "title", selected: "options", elements: [element("options")]),
            ThinkingMenuPage(stateID: "settings"),
            ThinkingMenuPage(stateID: "settings", selected: "audio", elements: [element("audio")]),
            ThinkingMenuPage(stateID: "audio", selected: "back", elements: [element("back")])
        ]
        let images = try pages.indices.map { try image(10 + $0) }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let session = ThinkingModelSession(isEnabled: true)
        session.select(.gemma) { _ in }
        let reader = GameTextReader()
        var history = ThinkingScreenHistory(), cursor = 0
        var requested = [String](), pressed = [String]()
        let finished = expectation(description: "Audio was observed after each previous menu")
        let inspector = ThinkingInspector(directory: dir) { _, _, context in
            let page = try XCTUnwrap(context.contract.menuPage)
            XCTAssertTrue(context.user.hasPrefix("Menu: " + page.stateID.replacingOccurrences(of: "-", with: " ").capitalized))
            XCTAssertFalse(context.user.contains("Unique OCR readings"))
            requested.append(page.stateID)
            XCTAssertTrue(context.image === images[cursor])
            XCTAssertEqual(context.contract.offer["stateId"] as? String, page.stateID)
            if page.stateID == "quit-confirmation" {
                XCTAssertTrue(context.user.contains("Previous menu: pause"))
                XCTAssertTrue(context.user.contains("Last confirmed option: quit"))
            }
            if let id = page.selected { return #"{"menu":"select","elementId":"\#(id)"}"# }
            return #"{"menu":"wait"}"#
        }
        func publish() {
            let page = pages[cursor], image = images[cursor]
            let screen = ThinkingScreenHistory.Screen(id: page.stateID, generation: 4, page: page, image: image)
            let changed = history.record(screen)
            XCTAssertTrue(changed)
            XCTAssertFalse(history.record(screen))
            let state = ThinkingSignals(profile: nil, generation: 4, room: page.stateID, mode: "modded",
                selectedMenu: page.selected, menuImage: image, menuPage: page, stateID: page.stateID, stateVisit: history.visit,
                previousStateID: history.previousStateID, previousConfirmedElement: history.previousConfirmedElement)
            func snapshot() -> ThinkingFrame {
                ThinkingFrame(image: images[0], profile: nil, generation: 4, context: page.stateID, mode: "modded",
                    atlas: "", sight: nil, language: "en", readsText: false)
            }
            inspector.observe(state, reader: reader, session: session, dispatch: dispatch, snapshot: snapshot)
            inspector.observe(state, reader: reader, session: session, dispatch: dispatch, snapshot: snapshot)
        }
        func dispatch(_ output: ThinkingInspector.Output) async -> ThinkingHandoff? {
            guard let script = output.script else { XCTFail("Expected menu command"); return nil }
            let result = await script.run(current: { pages[cursor] }, press: { button in
                XCTAssertEqual(button, .confirm)
                pressed.append(pages[cursor].selected!)
                history.confirmed(pages[cursor].selected!, on: pages[cursor].stateID)
                if cursor < pages.count - 1 {
                    cursor += 1
                    publish() // The next screen arrives during the current script.
                }
                return true
            }, wait: {})
            if script.request == .wait {
                guard cursor == 3 else { XCTFail("Unexpected wait"); return nil }
                cursor += 1
                publish()
            }
            if script.page.stateID == "audio" { finished.fulfill() }
            return result
        }
        publish()
        await fulfillment(of: [finished], timeout: 5)
        XCTAssertEqual(requested, pages.map(\.stateID))
        XCTAssertEqual(pressed, ["quit", "yes", "options", "audio", "back"])
    }

    func testNextScreenRemembersConfirmedInputAcrossFadeOnlyForSameSessionAndRecentPress() throws {
        let pixels = try image(1)
        var history = ThinkingScreenHistory()
        func visit(_ id: String, _ time: TimeInterval, generation: UInt64 = 1) {
            XCTAssertTrue(history.record(.init(id: id, generation: generation, page: nil, image: pixels), at: time))
        }
        visit("pause", 1)
        history.confirmed("quit", on: "pause", at: 2)
        history.lostState()
        visit("confirmation", 3)
        XCTAssertEqual(history.previousStateID, "pause")
        XCTAssertEqual(history.previousConfirmedElement, "quit")
        history.confirmed("yes", on: "confirmation", at: 4)
        visit("title", 20)
        XCTAssertNil(history.previousConfirmedElement)
        history.confirmed("start", on: "title", at: 21)
        visit("profiles", 22, generation: 2)
        XCTAssertNil(history.previousStateID)
        XCTAssertNil(history.previousConfirmedElement)
    }

    func testFastTransitionsQueueSourceFramesAndOnlyNewestReplyCanAct() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let session = ThinkingModelSession(isEnabled: true)
        session.select(.gemma) { _ in }
        let reader = GameTextReader()
        let ids = ["station", "deck", "station"]
        var requests = [String](), widths = [Int](), handoffs = [UInt64]()
        let done = expectation(description: "Returned to original state")
        let inspector = ThinkingInspector(directory: dir) { _, _, context in
            requests.append(try XCTUnwrap(context.contract.menuPage?.stateID))
            widths.append(try XCTUnwrap(context.image?.width))
            return #"{"menu":"select","elementId":"back"}"#
        }
        var history = ThinkingScreenHistory()
        for (index, id) in ids.enumerated() {
            let page = ThinkingMenuPage(stateID: id, selected: "back", elements: [element("back")])
            let source = try image(index + 5)
            XCTAssertTrue(history.record(.init(id: id, generation: 1, page: page, image: source)))
            let signals = ThinkingSignals(profile: nil, generation: 1, room: "shared context", mode: "modded",
                selectedMenu: "back", menuImage: source, menuPage: page, stateID: id, stateVisit: history.visit)
            inspector.observe(signals, reader: reader, session: session, dispatch: { result in
                handoffs.append(result.input.frame.stateVisit!)
                done.fulfill()
                return .status("Selected back")
            }, snapshot: {
                ThinkingFrame(image: source, profile: nil, generation: 1, context: id,
                    mode: "modded", atlas: "", sight: nil, language: "en", readsText: false)
            })
        }
        await fulfillment(of: [done], timeout: 5)
        XCTAssertEqual(requests, ids)
        XCTAssertEqual(widths, [5, 6, 7])
        XCTAssertEqual(handoffs, [3])
    }

    func testAnchorCanBeChosenBeforeFocusButOnlyVerifiedTargetReceivesConfirm() async throws {
        let target = "settings.audio"
        let page = ThinkingMenuPage(stateID: "setup", elements: [element(target)])
        let offer = ThinkingPolicyContract(sight: nil, visible: [], capturedMenu: page)
        let script = try offer.menuScript(Data(#"{"menu":"select","elementId":"settings.audio"}"#.utf8))
        XCTAssertThrowsError(try offer.menuScript(Data(#"{"menu":"more"}"#.utf8)))
        var ticks = 0, pressed = [ThinkingMenuInput]()
        let result = await script.run(current: {
            ThinkingMenuPage(stateID: page.stateID, selected: ticks >= 2 ? target : nil, elements: page.elements)
        }, press: { pressed.append($0); XCTAssertGreaterThanOrEqual(ticks, 2); return true }, wait: { ticks += 1 })
        XCTAssertEqual(pressed, [.confirm])
        XCTAssertEqual(result, .status("Sent confirm · " + target))
        let empty = ThinkingPolicyContract(sight: nil, visible: [], capturedMenu: .init(stateID: "unmapped-screen"))
        XCTAssertThrowsError(try empty.menuScript(Data(#"{"menu":"select","elementId":"settings.audio"}"#.utf8)))
        let wait = try empty.menuScript(Data(#"{"menu":"wait"}"#.utf8))
        _ = await wait.run(current: { nil }, press: { _ in XCTFail("Unexpected input"); return false })
    }

    func testEnteringGameplayBeforeRouteTelemetryStillOffersExplore() throws {
        let contract = ThinkingPolicyContract(sight: nil, visible: [], recognizedGameplay: true)
        let reply = Data(#"{"policy":"explore","parameters":{}}"#.utf8)
        XCTAssertEqual(try contract.resolve(reply).choices.first?.policy, .explore)
        XCTAssertThrowsError(try contract.resolve(Data(#"{"policy":"traverse","parameters":{"waypointId":0}}"#.utf8)))
    }

    func testRecordedMenusReachLocalGemmaWhenRequested() async throws {
        let env = ProcessInfo.processInfo.environment
        guard env["HKV_THINKING_MENU_STATES"] == "1" else { throw XCTSkip("Requires local Gemma and recorded English menu images") }
        let contexts: [LabelingContext] = [.mainTitle, .options, .audio, .selectProfile, .quitToMenu]
        let store = LabelingExampleStore(), examples = try store.loadExamples()
        let catalog = try MenuStencilCatalog(examplesRootURL: store.rootURL, calibration: .loadDefault(), includedContexts: Set(contexts))
        let session = ThinkingModelSession(isEnabled: true)
        session.select(.gemma) { _ in }
        let reader = GameTextReader()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        var evidence = [[String: Any]]()
        for menu in contexts {
            let example = try XCTUnwrap(examples.first {
                $0.manifest.contextIdentifier == menu.storageIdentifier && $0.manifest.languageIdentifier == "en"
            })
            let source = try store.loadImage(for: example)
            let stencil = MenuStencilTracker(catalog: catalog).observe(source, timestamp: 1)
            let status = LiveGameStatusResolver.resolve([], menuStencil: stencil)
            XCTAssertEqual(status?.context.labelingContext, menu)
            let page = try XCTUnwrap(ThinkingMenuPage(stencil))
            XCTAssertEqual(page.stateID, menu.storageIdentifier)
            let inspector = ThinkingInspector(directory: directory)
            let done = expectation(description: page.stateID + " returned a usable local decision")
            let state = ThinkingSignals(profile: nil, generation: 1, room: page.stateID, mode: "modded",
                selectedMenu: page.selected, menuImage: source, menuPage: page, stateID: page.stateID, stateVisit: 1)
            inspector.observe(state, reader: reader, session: session, dispatch: { result in
                XCTAssertTrue(result.validated)
                XCTAssertTrue(result.input.context.image === source)
                XCTAssertNotNil(result.script)
                if let choice = result.script?.request.selection {
                    evidence.append(["state": page.stateID, "screenshot": example.id.uuidString,
                                     "user": result.input.context.user, "offer": result.input.context.contract.offer, "reply": choice])
                    print("Local Gemma · \(page.stateID) · \(choice)")
                }
                done.fulfill()
                return .status("Recorded reply")
            }, snapshot: {
                ThinkingFrame(image: source, profile: nil, generation: 1, context: page.stateID,
                    mode: "modded", atlas: "", sight: nil, language: "en", readsText: true)
            })
            await fulfillment(of: [done], timeout: 180)
            if let issue = inspector.issue { XCTFail(issue); return }
        }
        XCTAssertEqual(evidence.count, contexts.count)
        if let path = env["HKV_MENU_STATE_EVIDENCE"] {
            try JSONSerialization.data(withJSONObject: evidence, options: [.prettyPrinted, .sortedKeys])
                .write(to: URL(fileURLWithPath: path), options: .atomic)
        }
    }
}
