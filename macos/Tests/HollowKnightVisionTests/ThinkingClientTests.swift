import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class ThinkingClientTests: XCTestCase {
    @MainActor
    func testModelSwitchStartsLoadingImmediatelyAndDiscardsSupersededReply() async throws {
        let session = ThinkingModelSession()
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
        let session = ThinkingModelSession()
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
        let session = ThinkingModelSession()
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
            let choices = try XCTUnwrap(properties["choices"] as? [String: Any])
            let items = try XCTUnwrap(choices["items"] as? [String: Any])
            let fields = try XCTUnwrap(items["properties"] as? [String: [String: Any]])
            XCTAssertEqual(Set(fields.keys), ["option", "points"])
            XCTAssertEqual(fields["option"]?["enum"] as? [String],
                           ["explore", "traverse", "fight", "recover", "progress", "readPrompt"])
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
        let sight = SkillSight(context: "King's Pass", capturedAt: 1, generation: 1, gameplay: true,
                              menu: nil, selected: nil, lines: [], hasAbilityControls: false)
        let contract = ThinkingPolicyContract(sight: sight, visible: [])
        let reply = #"{"choices":[{"option":"explore","points":40},{"option":"traverse","points":92}],"shopping":[]}"#
        let plan = try contract.resolve(Data(reply.utf8), goal: "Find new ground")
        XCTAssertEqual(plan.objective, "Find new ground")
        XCTAssertEqual(plan.choose(sight), .init(policy: .traverse, control: .native, points: 92, cue: ""))
        let invalid = [
            #"{"choices":[{"control":"wallJumpRight","cue":"Wall Jump Right","points":92,"policy":"traverse"}],"objective":"Wall Jump Right","shopping":[],"success":"Reach the next area"}"#,
            #"{"choices":[{"option":"wallJumpRight","points":92}],"shopping":[]}"#,
            #"{"choices":[{"option":"traverse","points":92,"control":"wallJumpRight"}],"shopping":[]}"#,
            #"{"choices":[{"option":"traverse","points":101}],"shopping":[]}"#,
            #"{"choices":[{"option":"traverse","points":92},{"option":"traverse","points":40}],"shopping":[]}"#,
            #"{"choices":[{"option":"readPrompt","points":90}],"shopping":[]}"#
        ]
        for reply in invalid {
            XCTAssertThrowsError(try contract.resolve(Data(reply.utf8), goal: "Find new ground"), reply)
        }
    }

    func testModelCanRankOnlyCapturedInteractionsAndVisionChecksThemAgain() throws {
        func sight(gameplay: Bool, menu: LabelingContext? = nil, selection: String? = nil, lines: [String] = []) -> SkillSight {
            .init(context: "King's Pass", capturedAt: 1, generation: 1, gameplay: gameplay,
                  menu: menu, selected: selection, lines: lines, hasAbilityControls: false)
        }
        let reading = Data(#"{"choices":[{"option":"readPrompt","points":93}],"shopping":[]}"#.utf8)
        let page = sight(gameplay: true, lines: ["INSPECT"])
        let read = try ThinkingPolicyContract(sight: page, visible: ["INSPECT"]).resolve(reading, goal: "Read")
        XCTAssertEqual(read.choose(page)?.control, .inspect)
        XCTAssertNil(read.choose(sight(gameplay: true)))

        let resume = Data(#"{"choices":[{"option":"resumeGame","points":80}],"shopping":[]}"#.utf8)
        let paused = sight(gameplay: false, menu: .pause, selection: "pause.continue")
        let options = sight(gameplay: false, menu: .pause, selection: "pause.options")
        let plan = try ThinkingPolicyContract(sight: paused, visible: []).resolve(resume, goal: "Resume")
        XCTAssertEqual(plan.choose(paused)?.control, .confirm)
        XCTAssertNil(plan.choose(options))
        XCTAssertThrowsError(try ThinkingPolicyContract(sight: options, visible: []).resolve(resume, goal: "Resume"))

        let unavailable = ThinkingPolicyContract(sight: nil, visible: ["INSPECT"])
        XCTAssertThrowsError(try unavailable.resolve(reading, goal: "Read"))
        let idle = try unavailable.resolve(Data(#"{"choices":[],"shopping":[]}"#.utf8), goal: "Read")
        XCTAssertTrue(idle.choices.isEmpty)
    }

    @MainActor
    func testMinuteSamplerKeepsCapturingWithOneModelRequestPending() async throws {
        let inspector = ThinkingInspector()
        let session = ThinkingModelSession()
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
                    goal: BotSkillPlan.defaultObjective, mode: "modded", atlas: "", sight: nil, language: "en", readsText: false)
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
            goal: BotSkillPlan.defaultObjective, mode: "modded", atlas: "", sight: nil, language: "en", readsText: false)
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
