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
    }
}
