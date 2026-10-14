import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class LiveMenuStencilWorkerTests: XCTestCase {
    private func image() -> CGImage {
        CGImage(width: 1, height: 1, bitsPerComponent: 8, bitsPerPixel: 8,
                bytesPerRow: 1, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: [],
                provider: CGDataProvider(data: Data([255]) as CFData)!,
                decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
    }

    func testHackerAdmissionDoesNotWaitAndOnlyLatestPendingFrameIsScanned() throws {
        let firstStarted = expectation(description: "slow scan started")
        let latestStarted = expectation(description: "latest scan started")
        let release = DispatchSemaphore(value: 0)
        let seenLock = NSLock()
        var seen = [TimeInterval]()
        let worker = LiveMenuStencilWorker { _, timestamp in
            seenLock.lock(); seen.append(timestamp); seenLock.unlock()
            if timestamp == 1 {
                firstStarted.fulfill()
                _ = release.wait(timeout: .now() + 5)
            }
            if timestamp == 4 { latestStarted.fulfill() }
            return .init(result: nil, diagnostic: String(timestamp))
        }
        defer { release.signal() }
        let frame = image()
        // This must return while recognition is still blocked.
        XCTAssertNil(worker.observe(frame, timestamp: 1, asynchronously: true))
        wait(for: [firstStarted], timeout: 2)
        for timestamp in [2.0, 3.0, 4.0] {
            XCTAssertNil(worker.observe(frame, timestamp: timestamp, asynchronously: true))
        }
        release.signal()
        wait(for: [latestStarted], timeout: 2)
        // Joining the worker after the latest scan starts also exercises the
        // Hacker-to-Gameplay handoff without rescanning the same timestamp.
        XCTAssertNil(worker.observe(frame, timestamp: 4, asynchronously: false))
        XCTAssertEqual(worker.snapshot(at: 4)?.observation.diagnostic, "4.0")
        seenLock.lock(); let processed = seen; seenLock.unlock()
        XCTAssertEqual(processed, [1, 4])
    }

    func testGameplayReturnsTheExactSubmittedFrame() {
        let worker = LiveMenuStencilWorker { _, timestamp in
            .init(result: nil, diagnostic: String(timestamp))
        }
        let first = worker.observe(image(), timestamp: 10, asynchronously: false)
        let second = worker.observe(image(), timestamp: 11, asynchronously: false)
        XCTAssertEqual(first?.timestamp, 10)
        XCTAssertEqual(first?.observation.diagnostic, "10.0")
        XCTAssertEqual(second?.timestamp, 11)
        XCTAssertEqual(second?.observation.diagnostic, "11.0")
    }

    func testHackerRejectsExpiredAndFutureMenuEvidence() {
        let worker = LiveMenuStencilWorker { _, _ in .init(result: nil, diagnostic: "menu") }
        _ = worker.observe(image(), timestamp: 10, asynchronously: false)
        XCTAssertNotNil(worker.snapshot(at: 10.25))
        XCTAssertNil(worker.snapshot(at: 10.251))
        XCTAssertNil(worker.snapshot(at: 9.99))
        XCTAssertNil(worker.snapshot(at: .nan))
    }

    func testOlderFrameCannotRewindRecognition() {
        var observed = [TimeInterval]()
        let worker = LiveMenuStencilWorker { _, timestamp in
            observed.append(timestamp)
            return .init(result: nil, diagnostic: String(timestamp))
        }
        _ = worker.observe(image(), timestamp: 10, asynchronously: false)
        XCTAssertNil(worker.observe(image(), timestamp: 9, asynchronously: false))
        XCTAssertEqual(worker.snapshot(at: 10)?.timestamp, 10)
        XCTAssertEqual(observed, [10])
    }

    func testResetClearsEvidenceAndAcceptsRestartedCaptureClock() {
        var resets = 0
        let worker = LiveMenuStencilWorker(
            recognize: { _, _ in .init(result: nil, diagnostic: "menu") },
            reset: { resets += 1 }
        )
        _ = worker.observe(image(), timestamp: 10, asynchronously: false)
        worker.reset()
        XCTAssertEqual(resets, 1)
        XCTAssertNil(worker.snapshot(at: 10))
        XCTAssertEqual(worker.observe(image(), timestamp: 1, asynchronously: false)?.timestamp, 1)
    }

    func testNoMenuObservationReplacesPreviousDiagnostic() {
        let worker = LiveMenuStencilWorker { _, timestamp in
            .init(result: nil, diagnostic: timestamp == 1 ? "menu" : nil)
        }
        _ = worker.observe(image(), timestamp: 1, asynchronously: false)
        _ = worker.observe(image(), timestamp: 2, asynchronously: false)
        XCTAssertEqual(worker.snapshot(at: 2)?.timestamp, 2)
        XCTAssertNil(worker.snapshot(at: 2)?.observation.diagnostic)
    }
}
