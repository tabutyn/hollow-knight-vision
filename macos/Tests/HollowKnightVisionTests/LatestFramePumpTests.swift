import XCTest
@testable import HollowKnightVision

final class LatestFramePumpTests: XCTestCase {
    func testTelemetrySeparatesOverloadFromCancelledAndStaleWork() throws {
        let pump = LatestFramePump<String>(epoch: 1)
        let active = try XCTUnwrap(pump.submit("active", epoch: 1))
        _ = pump.submit("overloaded", epoch: 1)
        _ = pump.submit("cancelled on restart", epoch: 1)
        pump.invalidate(epoch: 2)
        _ = pump.submit("stale producer", epoch: 1)
        _ = pump.submit("new generation", epoch: 2)
        let current = try XCTUnwrap(pump.complete(active).next)
        _ = pump.complete(current)
        _ = pump.complete(current) // A duplicate callback is not completed work.
        XCTAssertEqual(pump.statistics, .init(submitted: 4, started: 2,
            completed: 2, superseded: 1, cancelled: 1,
            staleSubmissions: 1, staleCompletions: 1))
    }

    func testFirstSubmissionStartsExactlyOneActiveTicket() throws {
        let pump = LatestFramePump<String>(epoch: 4)

        let ticket = pump.submit("first", epoch: 4)

        XCTAssertEqual(ticket?.item, "first")
        XCTAssertTrue(pump.isProcessing)
        XCTAssertEqual(pump.pendingCount, 0)
        let completion = pump.complete(try XCTUnwrap(ticket))
        XCTAssertTrue(completion.shouldAcceptResult)
        XCTAssertNil(completion.next)
        XCTAssertFalse(pump.isProcessing)
    }

    func testBusySubmissionsKeepOnlyLatestPendingTicket() throws {
        let pump = LatestFramePump<String>(epoch: 1)
        let first = try XCTUnwrap(pump.submit("first", epoch: 1))

        XCTAssertNil(pump.submit("obsolete", epoch: 1))
        XCTAssertNil(pump.submit("latest", epoch: 1))
        XCTAssertEqual(pump.pendingCount, 1)

        let firstCompletion = pump.complete(first)
        XCTAssertTrue(firstCompletion.shouldAcceptResult)
        let latest = try XCTUnwrap(firstCompletion.next)
        XCTAssertEqual(latest.item, "latest")
        XCTAssertTrue(pump.isProcessing)

        let latestCompletion = pump.complete(latest)
        XCTAssertTrue(latestCompletion.shouldAcceptResult)
        XCTAssertNil(latestCompletion.next)
        XCTAssertFalse(pump.isProcessing)
    }

    func testInvalidationDropsStalePendingAndRejectsOldActiveResult() throws {
        let pump = LatestFramePump<String>(epoch: 7)
        let old = try XCTUnwrap(pump.submit("old", epoch: 7))
        XCTAssertNil(pump.submit("stale pending", epoch: 7))

        pump.invalidate(epoch: 8)
        XCTAssertEqual(pump.pendingCount, 0)
        XCTAssertNil(pump.submit("obsolete epoch", epoch: 7))
        XCTAssertNil(pump.submit("current", epoch: 8))

        let oldCompletion = pump.complete(old)
        XCTAssertFalse(oldCompletion.shouldAcceptResult)
        let current = try XCTUnwrap(oldCompletion.next)
        XCTAssertEqual(current.item, "current")
        XCTAssertEqual(current.epoch, 8)

        let currentCompletion = pump.complete(current)
        XCTAssertTrue(currentCompletion.shouldAcceptResult)
        XCTAssertNil(currentCompletion.next)
        XCTAssertFalse(pump.isProcessing)
    }

    func testClearPendingLetsActiveTicketFinishAndReturnsIdle() throws {
        let pump = LatestFramePump<String>()
        let active = try XCTUnwrap(pump.submit("active", epoch: 0))
        XCTAssertNil(pump.submit("pending", epoch: 0))

        pump.clearPending()
        let completion = pump.complete(active)

        XCTAssertTrue(completion.shouldAcceptResult)
        XCTAssertNil(completion.next)
        XCTAssertFalse(pump.isProcessing)
        XCTAssertEqual(pump.pendingCount, 0)
    }

    func testReplacingPendingDiscardsItsOwnedResourceImmediately() throws {
        var discarded: [String] = []
        let pump = LatestFramePump<String>(discard: { discarded.append($0) })
        _ = try XCTUnwrap(pump.submit("active", epoch: 0))
        XCTAssertNil(pump.submit("old-buffer", epoch: 0))
        XCTAssertNil(pump.submit("latest-buffer", epoch: 0))

        XCTAssertEqual(discarded, ["old-buffer"])
    }
}
