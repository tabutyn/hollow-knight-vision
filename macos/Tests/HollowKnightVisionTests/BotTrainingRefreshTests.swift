import XCTest
@testable import HollowKnightVision

@MainActor final class BotTrainingRefreshTests: XCTestCase {
    func testSummaryAndQueuedCaptureUpdateWhileEventDecodeIsBlocked() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let demos = root.appendingPathComponent("demonstrations")
        func saveClip() throws {
            let recorder = BotDemonstrationRecorder(rootURL: demos)
            try recorder.start(gameMode: "modded", detectorVersion: "test", at: 100, domain: .gameplay)
            try recorder.stop(reason: "user-stop", at: 101)
        }
        try saveClip()
        let blocked = DispatchSemaphore(value: 0)
        let release = DispatchSemaphore(value: 0)
        let controller = BotTrainingController(store: BotModelStore(root: root, domain: .gameplay)) { url, manifest in
            blocked.signal()
            release.wait()
            return try BotSnippet.load(url, manifest: manifest)
        }
        defer { release.signal(); release.signal(); release.signal() }
        controller.refresh()
        XCTAssertEqual(blocked.wait(timeout: .now() + 2), .success)
        let first = expectation(description: "metadata appears before event decoding")
        let timer = Timer.scheduledTimer(withTimeInterval: 0.01, repeats: true) { _ in
            if controller.captureManifests.count == 1 { first.fulfill() }
        }
        wait(for: [first], timeout: 2); timer.invalidate()
        XCTAssertTrue(controller.isRefreshing)
        XCTAssertTrue(controller.snippets.isEmpty)
        XCTAssertFalse(controller.canTrain)
        try saveClip()
        controller.refresh()
        let second = expectation(description: "new metadata appears during existing decode")
        let timer2 = Timer.scheduledTimer(withTimeInterval: 0.01, repeats: true) { _ in
            if controller.captureManifests.count == 2 { second.fulfill() }
        }
        wait(for: [second], timeout: 2); timer2.invalidate()
        XCTAssertEqual(controller.newClipCount, 2)
        release.signal()
        // The queued scan must actually load the second clip, not just update counts.
        release.signal(); release.signal()
        let done = expectation(description: "queued validation finishes")
        let timer3 = Timer.scheduledTimer(withTimeInterval: 0.01, repeats: true) { _ in
            if !controller.isRefreshing && controller.snippets.count == 2 { done.fulfill() }
        }
        wait(for: [done], timeout: 2); timer3.invalidate()
        XCTAssertFalse(controller.canTrain) // Empty recordings fail the existing eligibility checks.
    }
}
