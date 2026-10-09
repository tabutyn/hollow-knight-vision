import XCTest
@testable import HollowKnightVision

final class BoundedBackgroundSearchTests: XCTestCase {
    func testBusySearchDoesNotQueueAndInvalidatedResultCannotPublish() {
        let worker = BoundedBackgroundSearch<Int>()
        let started = expectation(description: "started")
        let finished = expectation(description: "finished")
        let gate = DispatchSemaphore(value: 0)
        XCTAssertTrue(worker.submit {
            started.fulfill()
            _ = gate.wait(timeout: .now() + 2)
            finished.fulfill()
            return 41
        })
        wait(for: [started], timeout: 2)
        XCTAssertFalse(worker.submit { 99 })
        worker.invalidate()
        gate.signal()
        wait(for: [finished], timeout: 2)
        // Wait until the worker's completion transaction has finished by
        // submitting the next job; do not infer completion from its closure.
        let deadline = Date().addingTimeInterval(2)
        while !worker.submit({ 42 }), Date() < deadline { Thread.sleep(forTimeInterval: 0.001) }
        var value: Int?
        while value == nil, Date() < deadline {
            value = worker.take()
            if value == nil { Thread.sleep(forTimeInterval: 0.001) }
        }
        XCTAssertEqual(value, 42)
        XCTAssertNil(worker.take())
    }

    func testMaterialCorrectionRequiresIndependentConsistentObservations() {
        var gate = GroundCorrectionConfirmation()
        XCTAssertFalse(gate.accepts(.init(dx: 80, dy: 0), timestamp: 1))
        XCTAssertFalse(gate.accepts(.init(dx: -80, dy: 0), timestamp: 1.3))
        XCTAssertTrue(gate.accepts(.init(dx: -79, dy: 1), timestamp: 1.6))
        gate.reset()
        XCTAssertFalse(gate.accepts(.init(dx: 80, dy: 0), timestamp: 2))
        XCTAssertFalse(gate.accepts(.init(dx: 80, dy: 0), timestamp: 4))
        XCTAssertFalse(gate.accepts(.init(dx: 3, dy: 1), timestamp: 4.1))
        XCTAssertTrue(gate.accepts(.init(dx: 3.2, dy: 1.1), timestamp: 4.35))
    }

    func testCorrectionNoiseFloorNeverMovesCamera() {
        var gate = GroundCorrectionConfirmation()
        XCTAssertFalse(gate.accepts(.init(dx: 1.9, dy: 0), timestamp: 1))
        XCTAssertFalse(gate.accepts(.init(dx: 1.8, dy: 0.1), timestamp: 1.3))
        XCTAssertFalse(gate.needsConfirmation)
    }

    func testMidSizedCorrectionsMustAgreeClosely() {
        var gate = GroundCorrectionConfirmation()
        XCTAssertFalse(gate.accepts(.init(dx: 4, dy: 0), timestamp: 1))
        XCTAssertFalse(gate.accepts(.init(dx: 2.5, dy: 0), timestamp: 1.3))
        XCTAssertTrue(gate.accepts(.init(dx: 2.6, dy: 0.1), timestamp: 1.6))
        gate.reset()
        XCTAssertFalse(gate.accepts(.init(dx: 3, dy: 0), timestamp: 2))
        XCTAssertFalse(gate.accepts(.init(dx: 4.5, dy: 0), timestamp: 2.3))
        XCTAssertTrue(gate.accepts(.init(dx: 4.4, dy: 0.1), timestamp: 2.6))
    }
}
