import XCTest
@testable import HollowKnightVision

final class FramePipelineTelemetryTests: XCTestCase {
    func testBoundedRingRetainsNewestSamplesForP95() throws {
        let telemetry = FramePipelineTelemetry(capacity: 4)
        [0.001, 0.002, 0.003, 0.004, 0.050].forEach {
            telemetry.record(.captureToPresent, seconds: $0)
        }

        let summary = try XCTUnwrap(telemetry.summary(for: .captureToPresent))
        XCTAssertEqual(summary.count, 4)
        XCTAssertEqual(summary.p50Milliseconds, 4, accuracy: 0.001)
        XCTAssertEqual(summary.p95Milliseconds, 50, accuracy: 0.001)
    }

    func testRejectsNegativeAndNonFiniteDurations() {
        let telemetry = FramePipelineTelemetry(capacity: 2)
        telemetry.record(.render, seconds: -0.1)
        telemetry.record(.render, seconds: .infinity)
        XCTAssertNil(telemetry.summary(for: .render))
    }
}
