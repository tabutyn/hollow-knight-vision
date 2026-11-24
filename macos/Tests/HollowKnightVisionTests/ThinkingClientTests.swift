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
            _ = try await session.answer(model: .gemma, image: frame,
                context: ThinkingContext(evidence: "", hasImage: true))
            XCTFail("Disabled session requested inference")
        } catch is CancellationError {}
        let inspector = ThinkingInspector()
        let finished = expectation(description: "Disabled monitor returns immediately")
        let monitoring = Task {
            await inspector.monitor(reader: GameTextReader(), session: session) {
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
        let context = ThinkingContext(evidence: "Current goal: Find more unmapped territory for the Atlas", hasImage: false)
        let request = try ThinkingClient.request(model: .gemma, image: nil, context: context)
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
        let context = ThinkingContext(evidence: "Live decision evidence\nVisible OCR: INSPECT", hasImage: true,
                                      sight: sight, visible: ["INSPECT"])
        for model in ThinkingModel.allCases {
            let request = try ThinkingClient.request(model: model, image: frame, context: context)
            XCTAssertEqual(request.url?.absoluteString, "http://127.0.0.1:11434/api/chat")
            let configured = try ThinkingClient.request(model: model, image: frame, context: context,
                                                         useLlamaCompatibilityServer: true)
            XCTAssertEqual(configured.url?.host, "127.0.0.1")
            XCTAssertEqual(configured.url?.port, model == .llama ? 11435 : 11434)
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? [String: Any])
            XCTAssertEqual(json["model"] as? String, model.rawValue)
            let format = try XCTUnwrap(json["format"] as? NSDictionary)
            let displayed = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(context.schemaText.utf8)) as? NSDictionary)
            XCTAssertEqual(format, displayed)
            let properties = try XCTUnwrap(format["properties"] as? [String: Any])
            let scores = try XCTUnwrap(properties["scores"] as? [String: Any])
            let fields = try XCTUnwrap(scores["properties"] as? [String: Any])
            XCTAssertEqual(Set(fields.keys), ["c0"])
            XCTAssertTrue(context.user.contains(#""objective":"expandAtlas""#))
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            XCTAssertEqual(messages.compactMap { $0["role"] as? String }, ["system", "user"])
            XCTAssertEqual(messages.compactMap { $0["content"] as? String }, [context.system, context.user])
            XCTAssertFalse(context.user.contains("wallJumpRight"))
            XCTAssertEqual(messages.compactMap { $0["images"] }.count, 1)
            let image = try XCTUnwrap((messages[1]["images"] as? [String])?.first)
            XCTAssertEqual(Data(base64Encoded: image)?.prefix(8), Data([137, 80, 78, 71, 13, 10, 26, 10]))
        }
    }

    func testModelRanksPoliciesWithoutControllingTheirInputs() throws {
        let target = BotRouteTarget(chart: "room", goal: .init(20, 0), waypoint: .init(10, 0),
            hint: .init(dx: 1, dy: 0, clearance: 0.1, distance: 0.3, frontier: true))
        var sight = SkillSight(context: "Room", capturedAt: 1, generation: 1, gameplay: true, menu: nil,
            selected: nil, lines: [], hasAbilityControls: true, scene: .init(room: 7, grounded: true,
            recovery: .init(soul: 33, health: 3, maxHealth: 5, canFocus: true), enemies: [], cocoons: [], routes: [target]))
        let contract = ThinkingPolicyContract(sight: sight, visible: [])
        let plan = try contract.resolve(Data(#"{"scores":{"c0":90,"c1":90}}"#.utf8))
        XCTAssertEqual(plan.objective, "expandAtlas")
        XCTAssertEqual(plan.choose(sight)?.policy, .traverse)
        sight.scene?.routes = []
        XCTAssertEqual(plan.choose(sight)?.control, .focus)
        for soul in [0,32] {
            sight.scene = .init(room: 7, grounded: true, recovery: .init(soul: soul, health: 3, maxHealth: 5,
                canFocus: true), enemies: [], cocoons: [])
            XCTAssertNil(plan.choose(sight))
        }
        sight.scene = nil
        XCTAssertNil(plan.choose(sight))
        for reply in [#"{"choices":[],"shopping":[]}"#, #"{"scores":{"c2":80}}"#, #"{"scores":{"c0":101}}"#,
                      #"{"scores":{"c0":false}}"#, #"{"scores":{"c0":0}}"#, #"{"scores":{"c0":"99"}}"#,
                      #"{"scores":{"c0":1.5}}"#, #"{"scores":{},"reason":"Explore"}"#] {
            XCTAssertThrowsError(try contract.resolve(Data(reply.utf8)), reply)
        }
    }

    func testModelCanRankOnlyCapturedInteractionsAndVisionChecksThemAgain() throws {
        func sight(gameplay: Bool, menu: LabelingContext? = nil, selection: String? = nil, lines: [String] = []) -> SkillSight {
            .init(context: "King's Pass", capturedAt: 1, generation: 1, gameplay: gameplay,
                  menu: menu, selected: selection, lines: lines, hasAbilityControls: false)
        }
        let reading = Data(#"{"scores":{"c0":93}}"#.utf8)
        let page = sight(gameplay: true, lines: ["INSPECT"])
        let read = try ThinkingPolicyContract(sight: page, visible: ["INSPECT"]).resolve(reading)
        XCTAssertEqual(read.choose(page)?.control, .inspect)
        XCTAssertNil(read.choose(sight(gameplay: true)))
        var differentPrompt = sight(gameplay: true, lines: ["INSPECT"])
        differentPrompt.reading = .init(context: page.context, capturedAt: 15, visible: ["INSPECT"],
            promptStarts: [RememberedGameText.key("Inspect"): 12])
        XCTAssertNil(read.choose(differentPrompt))

        let resume = Data(#"{"scores":{"c0":80}}"#.utf8)
        let paused = sight(gameplay: false, menu: .pause, selection: "pause.continue")
        let options = sight(gameplay: false, menu: .pause, selection: "pause.options")
        let plan = try ThinkingPolicyContract(sight: paused, visible: []).resolve(resume)
        XCTAssertEqual(plan.choose(paused)?.control, .confirm)
        XCTAssertNil(plan.choose(options))
        XCTAssertThrowsError(try ThinkingPolicyContract(sight: options, visible: []).resolve(resume))

        let unavailable = ThinkingPolicyContract(sight: nil, visible: ["INSPECT"])
        XCTAssertThrowsError(try unavailable.resolve(reading))
        let idle = try unavailable.resolve(Data(#"{"scores":{}}"#.utf8))
        XCTAssertTrue(idle.choices.isEmpty)
    }

    func testScoredCandidateRunsItsBoundWaypointAndFallsBackOnlyToAnotherScoredTarget() throws {
        func route(_ x: Int, frontier: Bool) -> BotRouteTarget {
            .init(chart: "Pass", goal: .init(x * 2, 0), waypoint: .init(x, 0),
                  hint: .init(dx: x < 0 ? -0.4 : 0.4, dy: 0, clearance: 0.1, distance: 0.4, frontier: frontier))
        }
        let left = route(-20, frontier: true), right = route(30, frontier: false)
        var sight = SkillSight(context: "Pass", capturedAt: 10, generation: 7, gameplay: true, menu: nil,
            selected: nil, lines: [], hasAbilityControls: false, scene: .init(room: 8, grounded: true,
            recovery: nil, enemies: [], cocoons: [], routes: [left, right]))
        let contract = ThinkingPolicyContract(sight: sight, visible: [])
        let plan = try contract.resolve(Data(#"{"scores":{"c1":95,"c0":42}}"#.utf8))
        let rows = contract.rows(scoredBy: plan)
        XCTAssertEqual(rows.map(\.id), ["c0", "c1"])
        XCTAssertEqual(rows.map(\.score), [42, 95])
        XCTAssertTrue(rows[1].target.contains("(30, 0)"))
        XCTAssertEqual(ThinkingPolicyContract(sight: sight, visible: []).rows(scoredBy: plan).map(\.score), [nil, nil])
        let singlePick = try contract.resolve(Data(#"{"scores":{"c1":95}}"#.utf8))
        XCTAssertEqual(contract.rows(scoredBy: singlePick).map(\.score), [nil, 95])
        for (liveRoutes, expectedID, expectedTarget, expectedButtons) in [
            ([left, right], "c1", right, InputBridgeButtons.right),
            ([left], "c0", left, InputBridgeButtons.left)
        ] {
            sight.scene?.routes = liveRoutes
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
        XCTAssertNil(singlePick.choose(sight), "An unscored surviving target must stay unselected")
        sight.scene?.routes = [route(99, frontier: false)]
        XCTAssertNil(plan.choose(sight), "A replacement waypoint cannot inherit the previous score")
    }

    @MainActor
    func testMinuteSamplerKeepsCapturingWithOneModelRequestPending() async throws {
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
        let monitor = Task {
            await inspector.monitor(reader: reader, session: session, interval: .milliseconds(30)) {
                let sample = ThinkingFrame(image: frame, profile: nil, generation: 0, context: "King's Pass",
                    mode: "modded", atlas: "", sight: nil, language: "en", readsText: false)
                ids.append(sample.id)
                if ids.count == 2 { secondCapture.fulfill() }
                return sample
            }
        }
        await fulfillment(of: [secondCapture], timeout: 3)
        XCTAssertEqual(inspector.frame?.id, ids.last)
        XCTAssertTrue(session.answering)
        XCTAssertNil(inspector.output)
        monitor.cancel()
        await monitor.value
        finish?.resume()
        XCTAssertFalse(session.answering)
    }

    @MainActor
    func testInvalidJSONRemainsInspectableWithoutCreatingAPlan() throws {
        let frame = ThinkingFrame(image: try testFrame(), profile: nil, generation: 0, context: "King's Pass",
            mode: "modded", atlas: "", sight: nil, language: "en", readsText: false)
        let context = ThinkingContext(evidence: frame.decisionEvidence(visible: []), hasImage: true)
        let input = ThinkingInspector.Input(frame: frame, model: .gemma, context: context)
        let output = ThinkingInspector.Output(input: input, reply: #"{"choices":[]}"#)
        XCTAssertNil(output.plan)
        XCTAssertNotNil(output.issue)
        XCTAssertEqual(output.input.frame.id, frame.id)
        XCTAssertTrue(output.text.contains(#""choices""#))
    }

    private func testFrame() throws -> CGImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8,
            bytesPerRow: 8, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        return try XCTUnwrap(context.makeImage())
    }
}
