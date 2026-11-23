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
    func testQuestionCanWaitForStartupLoadWithoutAnotherLoadOrScreenshot() async throws {
        let session = ThinkingModelSession()
        XCTAssertEqual(session.model, .gemma)
        let started = expectation(description: "startup request")
        var finish: CheckedContinuation<Void, Never>?
        session.select(.startup) { _ in await withCheckedContinuation { finish = $0; started.fulfill() } }
        await fulfillment(of: [started], timeout: 3)
        let question = Task { try await session.waitUntilReady(for: .gemma) }
        XCTAssertEqual(session.state, .loading(.gemma))
        finish?.resume()
        try await question.value
        XCTAssertEqual(session.state, .ready(.gemma))
        let request = try ThinkingClient.request(model: .gemma, image: nil, history: [], question: "How do I heal?")
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? [String: Any])
        let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
        XCTAssertFalse(messages.contains { $0["images"] != nil })
        XCTAssertTrue((messages.first?["content"] as? String)?.contains("complete the Atlas") == true)
        XCTAssertEqual(messages.last?["content"] as? String, "How do I heal?")
    }

    @MainActor
    func testQueuedQuestionFollowsReplacementLoadForSameModel() async throws {
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

    func testFollowUpKeepsOneScreenshotAndRecentCompleteExchanges() throws {
        let context = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8,
            bytesPerRow: 8, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue))
        let frame = try XCTUnwrap(context.makeImage())
        let history = [ThinkingTurn(question: "Earlier", answer: String(repeating: "a", count: 3300)),
                       ThinkingTurn(question: "Where am I?", answer: "King's Pass.")]
        for model in ThinkingModel.allCases {
            let request = try ThinkingClient.request(model: model, image: frame, history: history, question: "What next?")
            XCTAssertEqual(request.url?.absoluteString, "http://127.0.0.1:11434/api/chat")
            let configured = try ThinkingClient.request(model: model, image: frame, history: [], question: "Read the sign",
                                                        useLlamaCompatibilityServer: true, evidence: "Earlier game text: Map Shop")
            XCTAssertEqual(configured.url?.host, "127.0.0.1")
            XCTAssertEqual(configured.url?.port, model == .llama ? 11435 : 11434)
            let configuredBody = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(configured.httpBody)) as? [String: Any])
            let turns = try XCTUnwrap(configuredBody["messages"] as? [[String: Any]])
            XCTAssertTrue(try XCTUnwrap(turns.last?["content"] as? String).contains("Map Shop"))
            XCTAssertTrue(try XCTUnwrap(turns.first?["content"] as? String).contains("reachable objective"))
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(request.httpBody)) as? [String: Any])
            XCTAssertEqual(json["model"] as? String, model.rawValue)
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            XCTAssertEqual(messages.dropFirst().compactMap { $0["content"] as? String },
                           ["Where am I?", "King's Pass.", "What next?"])
            XCTAssertEqual(messages.compactMap { $0["images"] }.count, 1)
            let image = try XCTUnwrap((messages[1]["images"] as? [String])?.first)
            XCTAssertEqual(Data(base64Encoded: image)?.prefix(8), Data([137, 80, 78, 71, 13, 10, 26, 10]))
        }
    }
}
