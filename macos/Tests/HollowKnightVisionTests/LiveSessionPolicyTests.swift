import Foundation
import XCTest
@testable import HollowKnightVision

final class LiveSessionPolicyTests: XCTestCase {
    func testCaptureRestartRequiresDesiredIdlePipeline() {
        XCTAssertTrue(LiveCaptureRestartPolicy.shouldRestart(
            wantsCapture: true,
            hasStream: false,
            operationInFlight: false,
            isCapturing: false
        ))
    }

    func testCaptureRestartDoesNotReviveIntentionalStopOrRaceNewCapture() {
        XCTAssertFalse(LiveCaptureRestartPolicy.shouldRestart(
            wantsCapture: false,
            hasStream: false,
            operationInFlight: false,
            isCapturing: false
        ))
        XCTAssertFalse(LiveCaptureRestartPolicy.shouldRestart(
            wantsCapture: true,
            hasStream: true,
            operationInFlight: false,
            isCapturing: false
        ))
        XCTAssertFalse(LiveCaptureRestartPolicy.shouldRestart(
            wantsCapture: true,
            hasStream: false,
            operationInFlight: true,
            isCapturing: false
        ))
        XCTAssertFalse(LiveCaptureRestartPolicy.shouldRestart(
            wantsCapture: true,
            hasStream: false,
            operationInFlight: false,
            isCapturing: true
        ))
    }
}
