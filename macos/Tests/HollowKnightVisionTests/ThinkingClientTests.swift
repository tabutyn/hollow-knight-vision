import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class ThinkingClientTests: XCTestCase {
    @MainActor
    func testDisabledThinkingLeavesModelIdleAndSkipsCaptureAndInference() async throws {
        let session = ThinkingModelSession(isEnabled: false)
        session.select(.gemma, force: true) { _ in XCTFail("Model load requested") }
        XCTAssertEqual(session.state, .idle)
        do {
            try await session.waitUntilReady(for: .gemma)
            XCTFail("Disabled session reported ready")
        } catch is CancellationError {}
        let frame = try testFrame()
        do {
            _ = try await session.answer(model: .gemma,
                context: ThinkingContext(evidence: "", image: frame))
            XCTFail("Disabled session requested inference")
        } catch is CancellationError {}
        let inspector = ThinkingInspector()
        let finished = expectation(description: "Disabled monitor returns immediately")
        let monitoring = Task {
            await inspector.monitor(reader: GameTextReader(), session: session, signals: { XCTFail("Read event state"); return nil }) {
                XCTFail("Thinking sampled a frame"); return nil
            }
            finished.fulfill()
        }
        await fulfillment(of: [finished], timeout: 1)
        monitoring.cancel()
        await monitoring.value
        XCTAssertNil(inspector.frame)
        XCTAssertFalse(session.answering)
        XCTAssertEqual(session.state, .idle)
    }

    @MainActor
    func testModelSwitchStartsLoadingImmediatelyAndDiscardsSupersededReply() async throws {
        let session = ThinkingModelSession(isEnabled: true)
        let first = ThinkingModel.gemma
        let second = ThinkingModel.qwen
        let started = expectation(description: "first load started")
        let secondStarted = expectation(description: "second load started")
        var firstReply: CheckedContinuation<Void, Never>?
        var secondReply: CheckedContinuation<Void, Never>?
        session.select(first) { _ in await withCheckedContinuation { firstReply = $0; started.fulfill() } }
        XCTAssertEqual(session.state, .loading(first))
        await fulfillment(of: [started], timeout: 3)
        session.select(second) { _ in await withCheckedContinuation { secondReply = $0; secondStarted.fulfill() } }
        XCTAssertEqual(session.state, .loading(second))
        await fulfillment(of: [secondStarted], timeout: 3)
        firstReply?.resume()
        await Task.yield()
        XCTAssertEqual(session.state, .loading(second))
        secondReply?.resume()
        for _ in 0..<100 where session.state != .ready(second) { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertEqual(session.state, .ready(second))
        session.select(first) { _ in throw ThinkingClient.Failure.server("Model drive disconnected") }
        for _ in 0..<100 where session.state == .loading(first) { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertEqual(session.state, .failed(first, "Model drive disconnected"))
    }

    @MainActor
    func testPolicyRequestWaitsForStartupLoad() async throws {
        let session = ThinkingModelSession(isEnabled: true)
        XCTAssertEqual(session.model, .gemma)
        let started = expectation(description: "startup request")
        var finish: CheckedContinuation<Void, Never>?
        session.select(.startup) { _ in await withCheckedContinuation { finish = $0; started.fulfill() } }
        await fulfillment(of: [started], timeout: 3)
        let policy = Task { try await session.waitUntilReady(for: .gemma) }
        XCTAssertEqual(session.state, .loading(.gemma))
        finish?.resume()
        try await policy.value
        XCTAssertEqual(session.state, .ready(.gemma))
        let context = ThinkingContext(evidence: "Current goal: Find more unmapped territory for the Atlas", image: nil)
        let request = try ThinkingClient.request(model: .gemma, context: context)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? [String: Any])
        let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
        XCTAssertFalse(messages.contains { $0["images"] != nil })
        XCTAssertTrue((messages.first?["content"] as? String)?.contains("complete the Atlas") == true)
        XCTAssertEqual(messages.last?["content"] as? String, context.user)
    }

    @MainActor
    func testPendingPolicyFollowsReplacementLoadForSameModel() async throws {
        let session = ThinkingModelSession(isEnabled: true)
        let firstStarted = expectation(description: "first load")
        let secondStarted = expectation(description: "replacement load")
        var first: CheckedContinuation<Void, Never>?
        var second: CheckedContinuation<Void, Never>?
        session.select(.gemma) { _ in
            await withCheckedContinuation { first = $0; firstStarted.fulfill() }
        }
        await fulfillment(of: [firstStarted], timeout: 3)
        let waiting = Task { try await session.waitUntilReady(for: .gemma) }
        await Task.yield()
        session.select(.gemma, force: true) { _ in
            await withCheckedContinuation { second = $0; secondStarted.fulfill() }
        }
        await fulfillment(of: [secondStarted], timeout: 3)
        first?.resume()
        try await Task.sleep(for: .milliseconds(15))
        second?.resume()
        try await waiting.value
        XCTAssertTrue(session.isReady)
    }

    func testLoadProgressOnlyReportsTheRequestedRunnerAndHandlesPartialLines() {
        var log = OllamaLoadLog(blob: "sha256-test")
        XCTAssertTrue(log.consume("load_tensors: loading model tensors\n").isEmpty)
        XCTAssertEqual(log.consume("srv    load_model: loading model 'sha256-test'\nload_tensors: loa"), ["Opening model weights"])
        XCTAssertEqual(log.consume("ding model tensors\n"), ["Mapping model weights into memory"])
        let events = log.consume("""
            load_tensors: offloaded 35/35 layers to GPU
            load_tensors: CPU_Mapped model buffer size = 512.00 MiB
            load_tensors: MTL0_Mapped model buffer size = 1024.00 MiB
            get_dummy_batch: warmup with image size = 896 x 896

            """)
        XCTAssertEqual(events, ["Offloaded 35/35 layers to GPU", "Warming up the vision encoder",
                                "Mapped buffers · 1.00 GiB Metal · 0.50 GiB CPU"])
        XCTAssertTrue(log.consume("slot prompt processing progress = 0.82\n").isEmpty)
        XCTAssertTrue(log.consume("srv    load_model: loading model 'unrelated'\nload_tensors: loading model tensors\n").isEmpty)
    }

    func testEngineeredRequestSendsExactlyTheDisplayedMessagesAndOneFrame() throws {
        let frame = try testFrame()
        let sight = SkillSight(context: "King's Pass", capturedAt: 1, generation: 1, gameplay: true,
                              menu: nil, selected: nil, lines: [], hasAbilityControls: true)
        let context = ThinkingContext(evidence: "Live decision evidence\nVisible OCR: INSPECT", image: frame,
                                      sight: sight, visible: ["INSPECT"])
        for model in ThinkingModel.allCases {
            let request = try ThinkingClient.request(model: model, context: context)
            XCTAssertEqual(request.url?.absoluteString, "http://127.0.0.1:11434/api/chat")
            let configured = try ThinkingClient.request(model: model, context: context,
                                                         useLlamaCompatibilityServer: true)
            XCTAssertEqual(configured.url?.host, "127.0.0.1")
            XCTAssertEqual(configured.url?.port, model == .llama ? 11435 : 11434)
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? [String: Any])
            XCTAssertEqual(json["model"] as? String, model.rawValue)
            let format = try XCTUnwrap(json["format"] as? NSDictionary)
            let displayed = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(context.schemaText.utf8)) as? NSDictionary)
            XCTAssertEqual(format, displayed)
            let properties = try XCTUnwrap(format["properties"] as? [String: Any])
            XCTAssertEqual(Set(properties.keys), ["policy", "parameters"])
            XCTAssertTrue(context.user.contains(#""objective":"expandAtlas""#))
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            XCTAssertEqual(messages.compactMap { $0["role"] as? String }, ["system", "user"])
            XCTAssertEqual(messages.compactMap { $0["content"] as? String }, [context.system, context.user])
            XCTAssertTrue(context.user.contains(ThinkingPolicyContract.policySchemaText))
            XCTAssertTrue(context.user.contains(ThinkingPolicyContract.menuSchemaText))
            XCTAssertFalse(context.user.contains("wallJumpRight"))
            XCTAssertTrue(context.image === frame)
            XCTAssertFalse(context.system.contains("no attached screenshot"))
            XCTAssertEqual((messages[1]["images"] as? [String])?.count, 1)
            XCTAssertEqual(messages.compactMap { $0["images"] }.count, 1)
            let image = try XCTUnwrap((messages[1]["images"] as? [String])?.first)
            XCTAssertEqual(Data(base64Encoded: image)?.prefix(8), Data([137, 80, 78, 71, 13, 10, 26, 10]))
        }
    }

    @MainActor
    func testOnlyAnOfferedPolicyAndItsExactParametersCanBeSaved() throws {
        let image = try testFrame()
        let sight = SkillSight(context: "Pass", capturedAt: 1, generation: 1, gameplay: true,
                              menu: nil, selected: nil, lines: ["Inspect"], hasAbilityControls: true)
        let context = ThinkingContext(evidence: "", image: image, sight: sight, visible: sight.lines)
        let sample = ThinkingFrame(image: image, profile: UUID(), generation: 1, context: "Pass",
                                   mode: "modded", atlas: "", sight: sight, language: "en", readsText: true)
        let input = ThinkingInspector.Input(frame: sample, model: .gemma, context: context)
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let inspector = ThinkingInspector(directory: directory)
        let reply = #"{"policy":"interact","parameters":{"targetId":0,"verb":"inspect"}}"#
        let url = try inspector.save(.init(input: input, reply: reply))
        let saved = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? NSDictionary)
        XCTAssertEqual(saved, try JSONSerialization.jsonObject(with: Data(reply.utf8)) as? NSDictionary)
        for bad in [#"{"scores":{"c0":95}}"#, #"{"policy":"interact","parameters":{"targetId":false,"verb":"inspect"}}"#,
                    #"{"policy":"interact","parameters":{"targetId":1,"verb":"inspect"}}"#,
                    #"{"policy":"observe","parameters":{}}"#, #"{"policy":"interact","parameters":{},"reason":"go"}"#] {
            XCTAssertThrowsError(try inspector.save(.init(input: input, reply: bad)), bad)
        }
        XCTAssertEqual(saved, try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? NSDictionary)
    }

    func testModelCanPickOnlyCapturedInteractionsAndVisionChecksThemAgain() throws {
        func sight(gameplay: Bool, menu: LabelingContext? = nil, selection: String? = nil, lines: [String] = []) -> SkillSight {
            .init(context: "King's Pass", capturedAt: 1, generation: 1, gameplay: gameplay,
                  menu: menu, selected: selection, lines: lines, hasAbilityControls: false)
        }
        let reading = Data(#"{"policy":"interact","parameters":{"verb":"inspect","targetId":0}}"#.utf8)
        let page = sight(gameplay: true, lines: ["INSPECT"])
        let read = try ThinkingPolicyContract(sight: page, visible: ["INSPECT"]).resolve(reading)
        XCTAssertEqual(read.choose(page)?.control, .inspect)
        XCTAssertNil(read.choose(sight(gameplay: true)))
        var differentPrompt = sight(gameplay: true, lines: ["INSPECT"])
        differentPrompt.reading = .init(context: page.context, capturedAt: 15, visible: ["INSPECT"],
            promptStarts: [RememberedGameText.key("Inspect"): 12])
        XCTAssertNil(read.choose(differentPrompt))

        let unavailable = ThinkingPolicyContract(sight: nil, visible: ["INSPECT"])
        XCTAssertFalse(unavailable.hasChoices)
        XCTAssertThrowsError(try unavailable.resolve(reading))
        for retired in ["observe", "menu", "fight", "recover"] {
            XCTAssertThrowsError(try unavailable.resolve(JSONSerialization.data(withJSONObject: ["policy": retired, "parameters": [:]])))
        }

    }

    func testSelectedWaypointRunsAndAnExpiredSelectionCannotFallBack() throws {
        func route(_ x: Int, frontier: Bool) -> BotRouteTarget {
            .init(chart: "Pass", goal: .init(x * 2, 0), waypoint: .init(x, 0),
                  hint: .init(dx: x < 0 ? -0.4 : 0.4, dy: 0, clearance: 0.1, distance: 0.4, frontier: frontier))
        }
        let left = route(-20, frontier: true), right = route(30, frontier: false)
        var sight = SkillSight(context: "Pass", capturedAt: 10, generation: 7, gameplay: true, menu: nil,
            selected: nil, lines: [], hasAbilityControls: false, scene: .init(room: 8, grounded: true,
            recovery: nil, enemies: [], cocoons: [], routes: [left, right]))
        let contract = ThinkingPolicyContract(sight: sight, visible: [])
        XCTAssertEqual(Set(contract.rows().map(\.policy)), ["traverse", "explore"])
        let explore = try contract.resolve(Data(#"{"policy":"explore","parameters":{}}"#.utf8))
        XCTAssertEqual(explore.choose(sight)?.policy, .explore)
        XCTAssertEqual(explore.choose(sight)?.binding, .init(room: 8, target: .traverse(left)))
        let selectedRight = try contract.resolve(Data(#"{"policy":"traverse","parameters":{"waypointId":1}}"#.utf8))
        XCTAssertEqual(contract.rows(selectedBy: selectedRight).map(\.selected), [false, true, false])
        XCTAssertTrue(contract.rows()[1].target.contains("(30, 0)"))
        for (index, expectedID, expectedTarget, expectedButtons) in [
            (1, "c1", right, InputBridgeButtons.right), (0, "c0", left, InputBridgeButtons.left)
        ] {
            let plan = try contract.resolve(JSONSerialization.data(withJSONObject:
                ["policy": "traverse", "parameters": ["waypointId": index]]))
            sight.scene?.routes = [left, right]
            let bot = BotPlaybackController(loadModel: false, now: { 10 }, hasFocus: { true })
            bot.skillSight = { sight }
            var selected: BotSkillChoice?, pressed: [InputBridgeButtons] = []
            bot.onSkillEvent = { _, choice, _, event, _ in if event == "selected" { selected = choice } }
            bot.configure(canContinue: { true }, begin: { UUID() }, apply: { action, _, _ in
                pressed.append(action.buttons); return true
            }, neutralize: { _ in }, end: { _ in })
            let handoff = bot.dispatch(plan, context: sight.context, generation: 7, domain: .gameplay)
            XCTAssertTrue(handoff.contains("Dispatched · " + expectedID), handoff)
            XCTAssertEqual(selected?.candidateID, expectedID)
            XCTAssertEqual(selected?.policy, .traverse)
            XCTAssertEqual(selected?.binding, .init(room: 8, target: .traverse(expectedTarget)))
            var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
            features[0] = 0.5; features[4] = 1
            var obs = BotObservation(type: "observation", captureTimestamp: 10, capturedAt: 10, availableAt: 10,
                generation: 7, roomID: 8, roomRevision: 1, detectionTimestamp: 10, detectionFrameID: nil,
                modelVersion: nil, bestKnightConfidence: 1, features: features, issues: [], terrainSource: "detected")
            obs.roadHint = expectedID == "c1" ? left.hint : right.hint
            obs.hackerPathVersion = 1
            obs.policyScene = sight.scene
            bot.observe(obs)
            let applied = expectation(description: "Playback consumes target observation")
            DispatchQueue.main.async { applied.fulfill() }
            wait(for: [applied], timeout: 2)
            XCTAssertEqual(pressed.last, expectedButtons)
            bot.stop()
        }
        sight.scene?.routes = [left]
        XCTAssertNil(selectedRight.choose(sight))
        sight.scene?.routes = [route(99, frontier: false)]
        XCTAssertNil(selectedRight.choose(sight))

    }

    @MainActor
    func testOnlyEventsCaptureScreenshotsWhileOneRequestIsPending() async throws {
        let inspector = ThinkingInspector()
        let session = ThinkingModelSession(isEnabled: true)
        let reader = GameTextReader()
        let frame = try testFrame()
        let loading = expectation(description: "model loading")
        var finish: CheckedContinuation<Void, Never>?
        session.select(.gemma) { _ in await withCheckedContinuation { finish = $0; loading.fulfill() } }
        await fulfillment(of: [loading], timeout: 3)

        let secondCapture = expectation(description: "next scheduled frame")
        var ids = [UUID]()
        let sight = SkillSight(context: "Pass", capturedAt: 1, generation: 0, gameplay: true, menu: nil,
            selected: nil, lines: [], hasAbilityControls: false)
        var polls = 0
        let monitor = Task {
            await inspector.monitor(reader: reader, session: session, interval: .milliseconds(10), signals: {
                polls += 1
                return .init(profile: nil, generation: 0, room: "Pass", mode: "modded", npc: polls >= 5)
            }) {
                let sample = ThinkingFrame(image: frame, profile: nil, generation: 0, context: "Pass",
                    mode: "modded", atlas: "", sight: sight, language: "en", readsText: false)
                ids.append(sample.id)
                if ids.count == 2 { secondCapture.fulfill() }
                return sample
            }
        }
        await fulfillment(of: [secondCapture], timeout: 3)
        XCTAssertEqual(ids.count, 2)
        XCTAssertGreaterThanOrEqual(polls, 5)
        XCTAssertEqual(inspector.frame?.event, "NPC visible")
        XCTAssertEqual(inspector.frame?.id, ids.last)
        XCTAssertTrue(session.answering)
        XCTAssertNil(inspector.output)
        monitor.cancel()
        await monitor.value
        finish?.resume()
        XCTAssertFalse(session.answering)
    }

    func testBackloggedScreenshotKeepsItsIdentityAndReleasesItsLocalFile() throws {
        let frame = ThinkingFrame(image: try testFrame(), profile: nil, generation: 6, context: "Pass",
            mode: "modded", atlas: "", sight: nil, language: "en", readsText: false, event: "NPC visible")
        var queued: ThinkingFrame? = try frame.diskBacked()
        let file = try XCTUnwrap(queued?.imageFile?.url)
        XCTAssertTrue(FileManager.default.fileExists(atPath: file.path))
        XCTAssertEqual(queued?.id, frame.id)
        XCTAssertEqual(queued?.event, frame.event)
        XCTAssertEqual(queued?.image.width, frame.image.width)
        XCTAssertNotNil(queued?.image.dataProvider?.data)
        queued = nil
        XCTAssertFalse(FileManager.default.fileExists(atPath: file.path))
    }

    @MainActor
    func testInvalidJSONRemainsInspectableWithoutCreatingAPlan() throws {
        let frame = ThinkingFrame(image: try testFrame(), profile: nil, generation: 0, context: "King's Pass",
            mode: "modded", atlas: "", sight: nil, language: "en", readsText: false)
        let context = ThinkingContext(evidence: frame.decisionEvidence(visible: []), image: frame.image)
        let input = ThinkingInspector.Input(frame: frame, model: .gemma, context: context)
        let output = ThinkingInspector.Output(input: input, reply: #"{"choices":[]}"#)
        XCTAssertNil(output.plan)
        XCTAssertNotNil(output.issue)
        XCTAssertEqual(output.input.frame.id, frame.id)
        XCTAssertTrue(output.text.contains(#""choices""#))
    }

    @MainActor
    func testMenuCommandsFollowVerifiedFocusAndRequestFreshPixelsOnlyAfterAdvance() async throws {
        let ids = ["pause.continue", "shared.options", "quit-to-menu.quit-to-menu"]
        let elements = ids.enumerated().map { ThinkingMenuPage.Element(id: $0.element, center: CGPoint(x: 50, y: 200 - 50 * $0.offset)) }
        func page(_ selected: Int) -> ThinkingMenuPage {
            ThinkingMenuPage(stateID: "pause", selected: ids[selected], elements: elements)
        }
        let image = try testFrame()
        let sight = SkillSight(context: "Pause", capturedAt: 0, generation: 1, gameplay: false, menu: .pause,
            selected: ids[0], lines: [], hasAbilityControls: false, menuPage: page(0))
        let context = ThinkingContext(evidence: "", image: image, sight: sight)
        let contract = context.contract
        let frame = ThinkingFrame(image: image, profile: nil, generation: 1, context: "Pause", mode: "modded",
            atlas: "", sight: sight, language: "en", readsText: false)
        let reply = #"{"menu":"select","elementId":"quit-to-menu.quit-to-menu"}"#
        let selection = ThinkingInspector.Output(input: .init(frame: frame, model: .gemma, context: context), reply: reply)
        let select = try XCTUnwrap(selection.script)
        XCTAssertNil(selection.plan)
        XCTAssertThrowsError(try contract.resolve(Data(reply.utf8)))
        let more = try contract.menuScript(Data(#"{"menu":"more"}"#.utf8))
        var index = 0, pressed = [ThinkingMenuInput]()
        func press(_ button: ThinkingMenuInput) -> Bool {
            pressed.append(button)
            if button == .down { index += 1 }
            return true
        }
        let advanced = await more.run(current: { page(index) }, press: press, wait: {})
        XCTAssertEqual(advanced, .menuAdvanced(ids[1]))
        XCTAssertEqual(pressed, [.down])
        let stale = await select.run(current: { page(index) }, press: press, wait: {})
        XCTAssertTrue(stale.text.contains("Skipped"))
        XCTAssertEqual(pressed, [.down])
        index = 0; pressed = []
        let confirmed = await select.run(current: { page(index) }, press: press, wait: {})
        XCTAssertTrue(confirmed.text.contains(ids[2]))
        XCTAssertEqual(pressed, [.down, .down, .confirm])
        let stuck = await more.run(current: { page(0) }, press: { _ in true }, wait: {})
        XCTAssertTrue(stuck.text.contains("no menu advance"))
        for bad in [#"{"menu":"select","elementId":"unseen"}"#, #"{"menu":"more","elementId":"shared.options"}"#,
                    #"{"policy":"menu","parameters":{}}"#, #"{"policy":"explore","parameters":{}}"#] {
            XCTAssertThrowsError(try contract.menuScript(Data(bad.utf8)))
        }
        XCTAssertThrowsError(try ThinkingPolicyContract(sight: sight, visible: [], allowsMore: false)
            .menuScript(Data(#"{"menu":"more"}"#.utf8)))
    }

    @MainActor
    func testRecordedPauseMenuThroughLocalGemmaWhenRequested() async throws {
        guard ProcessInfo.processInfo.environment["HKV_THINKING_PAUSE_TEST"] == "1" else {
            throw XCTSkip("Requires a local labelled pause image and Ollama")
        }
        let store = LabelingExampleStore()
        let fixture = try XCTUnwrap(try store.loadExamples().first {
            $0.manifest.contextIdentifier == "pause" && $0.manifest.languageIdentifier == "en"
        })
        let image = try store.loadImage(for: fixture)
        let catalog = try MenuStencilCatalog(examplesRootURL: store.rootURL, calibration: .loadDefault(), includedContexts: [.pause])
        let stencil = MenuStencilTracker(catalog: catalog).observe(image, timestamp: 1)
        let page = try XCTUnwrap(ThinkingMenuPage(stencil))
        XCTAssertEqual(Set(page.elements.map(\.id)), ["pause.continue", LabelingClassIdentity.options, LabelingClassIdentity.quitToMenu])
        let sight = SkillSight(context: "Pause", capturedAt: 1, generation: 1, gameplay: false,
            menu: .pause, selected: page.selected, lines: [], hasAbilityControls: false, menuPage: page)
        let context = ThinkingContext(evidence: "Current game menu: Pause", image: image, sight: sight)
        let reply = try await ThinkingClient.answer(model: .gemma, context: context)
        let script = try context.contract.menuScript(Data(reply.utf8))
        XCTAssertEqual(script.page.selected, page.selected)
        print("Recorded pause screenshot · highlighted \(page.selected ?? "unknown") · local Gemma reply \(reply)")
    }

    @MainActor
    func testMoreResubmitsTheAdvancedScreenshotAndItsOwnMenuOffer() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let images = [try testFrame(), try testFrame()]
        let elements = [ThinkingMenuPage.Element(id: "pause.continue", center: .init(x: 4, y: 40)),
                        .init(id: "shared.options", center: .init(x: 4, y: 10))]
        var index = 0, requests = 0
        func page() -> ThinkingMenuPage {
            ThinkingMenuPage(stateID: "pause", selected: elements[index].id, elements: elements)
        }
        let completed = expectation(description: "New menu screenshot received and verified")
        let inspector = ThinkingInspector(directory: dir) { _, _, context in
            requests += 1
            XCTAssertTrue(context.image === images[index])
            XCTAssertEqual(context.contract.menuPage?.selected, elements[index].id)
            XCTAssertTrue(context.user.contains(ThinkingPolicyContract.policySchemaText))
            XCTAssertTrue(context.user.contains(ThinkingPolicyContract.menuSchemaText))
            if requests == 1 { return #"{"menu":"more"}"# }
            XCTAssertTrue(context.user.contains("Menu advanced"))
            return #"{"menu":"select","elementId":"shared.options"}"#
        }
        let session = ThinkingModelSession(isEnabled: true)
        session.select(.gemma) { _ in }
        let monitor = Task {
            await inspector.monitor(reader: GameTextReader(), session: session, interval: .milliseconds(5), signals: {
                ThinkingSignals(profile: nil, generation: 1, room: "Pause", mode: "modded",
                    selectedMenu: page().selected, menuImage: images[index])
            }, dispatch: { output in
                guard let action = output.script else { XCTFail("Expected a validated menu command"); return nil }
                let result = await action.run(current: page, press: { button in
                    if button == .down { index = 1 }
                    return true
                }, wait: {})
                if requests == 2 { completed.fulfill() }
                return result
            }, snapshot: {
                let sight = SkillSight(context: "Pause", capturedAt: 1, generation: 1, gameplay: false,
                    menu: .pause, selected: page().selected, lines: [], hasAbilityControls: false, menuPage: page())
                return ThinkingFrame(image: images[0], profile: nil, generation: 1, context: "Pause",
                    mode: "modded", atlas: "", sight: sight, language: "en", readsText: false)
            })
        }
        await fulfillment(of: [completed], timeout: 5)
        XCTAssertEqual(requests, 2)
        monitor.cancel()
        await monitor.value
    }

    func testTrackedSignUsesDetectorPixelsAndReportsBoundsWithoutAnInspectPrompt() throws {
        let newer = try testFrame(), detected = try testFrame()
        let sight = SkillSight(context: "Pass", capturedAt: 0, generation: 3, gameplay: true, menu: nil,
            selected: nil, lines: [], hasAbilityControls: true)
        let state = ThinkingSignals(profile: nil, generation: 3, room: "Pass", mode: "modded", signTracks: [5],
            signImage: detected, signBounds: [CGRect(x: 0.2, y: 0.4, width: 0.15, height: 0.2)], signSourceID: 87)
        var sample = ThinkingFrame(image: newer, profile: nil, generation: 3, context: "Pass", mode: "modded",
            atlas: "", sight: sight, language: "en", readsText: false)
        var detector = ThinkingEventDetector()
        let reasons = detector.events(state, at: 1)
        XCTAssertTrue(reasons.contains("Sign visible"))
        state.attachEvidence(to: &sample, trackedSign: detector.newSignTrack)
        let input = ThinkingContext(evidence: sample.decisionEvidence(visible: []), image: sample.image, sight: sight)
        XCTAssertTrue(input.image === detected)
        XCTAssertTrue(input.user.contains("world.sign, frame 87"))
        XCTAssertTrue(input.user.contains(ThinkingPolicyContract.menuSchemaText))
        XCTAssertEqual(input.contract.rows().map(\.policy), ["explore"])
        XCTAssertThrowsError(try input.contract.resolve(Data(#"{"policy":"interact","parameters":{"verb":"inspect","targetId":0}}"#.utf8)))
        XCTAssertEqual(detector.events(state, at: 2), [])
    }

    private func testFrame() throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8,
            bytesPerRow: 8, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        return try XCTUnwrap(context.makeImage())
    }
}
