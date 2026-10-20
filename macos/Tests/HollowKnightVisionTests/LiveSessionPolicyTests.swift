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

    func testGameLossCountdownUsesRequestedMessage() {
        XCTAssertEqual(GameLossCountdown.initialSeconds, 5)
        XCTAssertEqual(
            GameLossCountdown.message(remainingSeconds: 5),
            "Hollow Knight lost. Closing app in 5 seconds"
        )
        XCTAssertEqual(
            GameLossCountdown.message(remainingSeconds: 0),
            "Hollow Knight lost. Closing app in 0 seconds"
        )
    }
}
