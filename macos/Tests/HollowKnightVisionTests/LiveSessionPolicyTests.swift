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

    func testSteamBootstrapExitWaitsForReplacementAndTracksNewPID() {
        var session = GameProcessLifetime()
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 0), .waitingForGame)
        XCTAssertEqual(session.observe(processID: 100, captureReady: true, quitConfirmed: false, at: 1), .watching)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 5), .waitingForRelaunch)
        // Steam startup can exceed the ordinary five-second exit countdown.
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 20), .waitingForRelaunch)
        XCTAssertEqual(session.observe(processID: 200, captureReady: true, quitConfirmed: false, at: 25), .watching)
        XCTAssertEqual(session.processID, 200)
        XCTAssertEqual(session.observe(processID: 200, captureReady: true, quitConfirmed: false, at: 45), .watching)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 46), .exited)
    }

    func testStableGameExitAndConfirmedEarlyQuitKeepNormalCloseBehavior() {
        var session = GameProcessLifetime()
        _ = session.observe(processID: 100, captureReady: true, quitConfirmed: false, at: 0)
        _ = session.observe(processID: 100, captureReady: true, quitConfirmed: false, at: 20)
        // Stream loss immediately before termination must not reset readiness.
        _ = session.observe(processID: 100, captureReady: false, quitConfirmed: false, at: 21)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 22), .exited)

        var earlyQuit = GameProcessLifetime()
        _ = earlyQuit.observe(processID: 100, captureReady: false, quitConfirmed: false, at: 0)
        XCTAssertEqual(earlyQuit.observe(processID: nil, captureReady: false, quitConfirmed: true, at: 1), .exited)
    }

    func testMissingReplacementHasBoundedGraceEvenAcrossDuplicateExitEvents() {
        var session = GameProcessLifetime()
        _ = session.observe(processID: 100, captureReady: false, quitConfirmed: false, at: 0)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 1), .waitingForRelaunch)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 1.1), .waitingForRelaunch)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 60), .waitingForRelaunch)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 61), .exited)
        XCTAssertEqual(session.observe(processID: 200, captureReady: true, quitConfirmed: false, at: 62), .exited)
    }

    func testRepeatedBootstrapProcessesDoNotExtendRelaunchDeadline() {
        var session = GameProcessLifetime()
        _ = session.observe(processID: 100, captureReady: false, quitConfirmed: false, at: 0)
        _ = session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 1)
        _ = session.observe(processID: 200, captureReady: true, quitConfirmed: false, at: 30)
        _ = session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 32)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 61), .exited)
    }

    func testUncapturedBootstrapDoesNotBecomeStableJustByWaiting() {
        var session = GameProcessLifetime()
        _ = session.observe(processID: 100, captureReady: false, quitConfirmed: false, at: 0)
        _ = session.observe(processID: 100, captureReady: false, quitConfirmed: false, at: 120)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 121), .waitingForRelaunch)
    }

    func testReplacementAlreadyRunningIsAdoptedWithoutLossCountdown() {
        var session = GameProcessLifetime()
        _ = session.observe(processID: 100, captureReady: true, quitConfirmed: false, at: 0)
        XCTAssertEqual(session.observe(processID: 200, captureReady: false, quitConfirmed: false, at: 2), .watching)
        XCTAssertEqual(session.processID, 200)
        XCTAssertEqual(session.observe(processID: nil, captureReady: false, quitConfirmed: false, at: 3), .waitingForRelaunch)
    }
}
