import XCTest
@testable import HollowKnightVision

final class SourceWindowIdentityTests: XCTestCase {
    func testRecognizesTheRunningGameApplication() {
        XCTAssertTrue(SourceWindowIdentity.isHollowKnight(
            applicationName: "Hollow Knight",
            bundleIdentifier: "unity.Team Cherry.Hollow Knight"
        ))
    }

    func testNormalizesCaseWhitespaceAndPunctuationForBothIdentifiers() {
        XCTAssertTrue(SourceWindowIdentity.isHollowKnight(
            applicationName: "  HOLLOW---knight  ",
            bundleIdentifier: "UNITY_team-cherry / hollow.knight"
        ))
    }

    func testRejectsBrowsersRegardlessOfGameNamedTabOrWindowTitle() {
        XCTAssertFalse(SourceWindowIdentity.isHollowKnight(
            applicationName: "Safari",
            bundleIdentifier: "com.apple.Safari"
        ))
        XCTAssertFalse(SourceWindowIdentity.isHollowKnight(
            applicationName: "Google Chrome",
            bundleIdentifier: "com.google.Chrome"
        ))
    }

    func testRejectsAnUnrelatedApplication() {
        XCTAssertFalse(SourceWindowIdentity.isHollowKnight(
            applicationName: "Steam",
            bundleIdentifier: "com.valvesoftware.steam"
        ))
    }

    func testCaptureWindowEligibilityDoesNotRequireGameFocusOrVisibility() {
        XCTAssertTrue(SourceWindowIdentity.isCaptureWindow(
            applicationName: "Hollow Knight",
            bundleIdentifier: "unity.Team Cherry.Hollow Knight",
            windowLayer: 0
        ))
    }

    func testCaptureWindowRejectsNonstandardWindowLayers() {
        XCTAssertFalse(SourceWindowIdentity.isCaptureWindow(
            applicationName: "Hollow Knight",
            bundleIdentifier: "unity.Team Cherry.Hollow Knight",
            windowLayer: 1
        ))
    }
}
