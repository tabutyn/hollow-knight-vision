import XCTest
@testable import HollowKnightVision

final class NormalGameKeyForwarderTests: XCTestCase {
    func testHoldsMultipleKeysWithoutRepeatingDownTransitions() {
        var state = NativeGameKeyState()
        XCTAssertTrue(state.press(124, targetPID: 10, isRepeat: false))
        XCTAssertFalse(state.press(124, targetPID: 10, isRepeat: true))
        XCTAssertFalse(state.press(124, targetPID: 10, isRepeat: false))
        XCTAssertTrue(state.press(6, targetPID: 10, isRepeat: false))
        XCTAssertEqual(state.heldKeyCodes, [124, 6])
        XCTAssertTrue(state.release(124))
        XCTAssertEqual(state.targetPID, 10)
        XCTAssertTrue(state.release(6))
        XCTAssertNil(state.targetPID)
        XCTAssertFalse(state.release(6))
    }

    func testFocusLossReleasesAllAndRequiresFreshDown() {
        var state = NativeGameKeyState()
        XCTAssertTrue(state.press(125, targetPID: 10, isRepeat: false))
        XCTAssertTrue(state.press(7, targetPID: 10, isRepeat: false))
        XCTAssertEqual(state.releaseAll(), [125, 7])
        XCTAssertTrue(state.heldKeyCodes.isEmpty)
        XCTAssertNil(state.targetPID)
        XCTAssertFalse(state.press(125, targetPID: 10, isRepeat: true))
        XCTAssertTrue(state.press(125, targetPID: 10, isRepeat: false))
    }

    func testReplacementGameCannotInheritHeldKeys() {
        var state = NativeGameKeyState()
        XCTAssertTrue(state.press(124, targetPID: 10, isRepeat: false))
        XCTAssertFalse(state.press(6, targetPID: 20, isRepeat: false))
        XCTAssertEqual(state.targetPID, 10)
        XCTAssertEqual(state.releaseAll(), [124])
        XCTAssertTrue(state.press(6, targetPID: 20, isRepeat: false))
        XCTAssertEqual(state.targetPID, 20)
    }

    func testPendingKeyPreservesFirstPressAcrossFocusTransfer() {
        var state = PendingNativeKeyState()
        XCTAssertTrue(state.press(124, flags: [.maskShift], isRepeat: false))
        XCTAssertFalse(state.press(124, flags: [], isRepeat: true))
        XCTAssertTrue(state.press(6, flags: [], isRepeat: false))
        XCTAssertTrue(state.release(124))
        XCTAssertFalse(state.release(123))
        XCTAssertEqual(state.drain(), [
            PendingNativeKeyPress(
                keyCode: 124,
                flags: [.maskShift],
                wasReleased: true
            ),
            PendingNativeKeyPress(
                keyCode: 6,
                flags: [],
                wasReleased: false
            ),
        ])
        XCTAssertTrue(state.presses.isEmpty)
    }

    func testWorkspaceAndSettingsRouting() {
        XCTAssertTrue(NormalGameKeyPolicy.keyCodes(labeling: true, reviewing: false, settings: false).isEmpty)
        XCTAssertTrue(NormalGameKeyPolicy.keyCodes(labeling: false, reviewing: true, settings: false).isEmpty)
        XCTAssertTrue(NormalGameKeyPolicy.keyCodes(labeling: true, reviewing: true, settings: true).isEmpty)
        XCTAssertEqual(NormalGameKeyPolicy.keyCodes(labeling: false, reviewing: false, settings: true), [123, 124, 125, 126])
        let gameplay = NormalGameKeyPolicy.keyCodes(labeling: false, reviewing: false, settings: false)
        XCTAssertTrue(gameplay.isSuperset(of: [123, 124, 125, 126, 6, 7, 36, 53]))
        XCTAssertFalse(gameplay.contains(37), "L belongs to labeling")
        XCTAssertFalse(gameplay.contains(1), "S belongs to stencils")
        XCTAssertEqual(GameKeyRelay.action(type: .keyDown, keyCode: 7,
            modifierFlags: .command, supportedKeyCodes: gameplay), .pass)
        XCTAssertEqual(GameKeyRelay.action(type: .keyUp, keyCode: 7,
            modifierFlags: .command, supportedKeyCodes: gameplay), .forward)
        XCTAssertTrue(NormalGameKeyPolicy.forwardsPointer(
            labeling: false,
            reviewing: false
        ))
        XCTAssertFalse(NormalGameKeyPolicy.forwardsPointer(
            labeling: true,
            reviewing: false
        ))
        XCTAssertFalse(NormalGameKeyPolicy.forwardsPointer(
            labeling: false,
            reviewing: true
        ))
    }

    func testNativeInputAccessAcceptsEitherMacOSTrustSignal() {
        XCTAssertFalse(NativeInputAccessPolicy.isGranted(
            postEvent: false,
            accessibility: false
        ))
        XCTAssertTrue(NativeInputAccessPolicy.isGranted(
            postEvent: true,
            accessibility: false
        ))
        XCTAssertTrue(NativeInputAccessPolicy.isGranted(
            postEvent: false,
            accessibility: true
        ))
    }

    func testFreshKeyboardEventsContainOnlyRequestedKeyTransition() throws {
        let down = try XCTUnwrap(NativeGameEventFactory.keyboard(
            keyCode: 124,
            isDown: true
        ))
        XCTAssertEqual(down.type, .keyDown)
        XCTAssertEqual(
            down.getIntegerValueField(.keyboardEventKeycode),
            124
        )
        XCTAssertEqual(
            down.getIntegerValueField(.keyboardEventAutorepeat),
            0
        )
        let up = try XCTUnwrap(NativeGameEventFactory.keyboard(
            keyCode: 124,
            isDown: false
        ))
        XCTAssertEqual(up.type, .keyUp)
        XCTAssertEqual(up.getIntegerValueField(.keyboardEventKeycode), 124)
    }

    func testPointerProjectionUsesCapturedContentAndInvertsY() throws {
        let geometry = NativeGameWindowGeometry(
            frame: CGRect(x: 100, y: 200, width: 400, height: 228),
            captureSourceRect: CGRect(x: 0, y: 28, width: 400, height: 200)
        )
        XCTAssertEqual(
            try XCTUnwrap(NativeGamePointerProjection.screenPoint(
                normalizedPoint: CGPoint(x: 0.25, y: 0.75),
                geometry: geometry
            )),
            CGPoint(x: 200, y: 278)
        )
        XCTAssertEqual(
            try XCTUnwrap(NativeGamePointerProjection.screenPoint(
                normalizedPoint: CGPoint(x: 0, y: 1),
                geometry: geometry
            )),
            CGPoint(x: 100, y: 228)
        )
        XCTAssertNil(NativeGamePointerProjection.screenPoint(
            normalizedPoint: CGPoint(x: 1.1, y: 0.5),
            geometry: geometry
        ))
    }

    func testFreshPointerEventsUseProjectedLocationAndClickCount() throws {
        let point = CGPoint(x: 350, y: 420)
        let down = try XCTUnwrap(NativeGameEventFactory.pointer(
            kind: .leftDown,
            screenPoint: point,
            clickCount: 2
        ))
        XCTAssertEqual(down.type, .leftMouseDown)
        XCTAssertEqual(down.location, point)
        XCTAssertEqual(down.getIntegerValueField(.mouseEventClickState), 2)
        XCTAssertNil(NativeGameEventFactory.pointer(
            kind: .exited,
            screenPoint: point,
            clickCount: 1
        ))
    }

    func testDashboardDefaultsToFullVisibleFrame() {
        let visible = CGRect(x: -1_200, y: 25, width: 1_200, height: 775)
        XCTAssertEqual(
            DashboardWindowLayout.initialFrame(visibleFrame: visible),
            visible
        )
    }
}
