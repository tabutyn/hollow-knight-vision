import XCTest
@testable import HollowKnightVision

final class RuntimeModeTests: XCTestCase {
    func testGameModeArgumentAcceptsBothForms() throws {
        XCTAssertEqual(
            try HollowKnightRuntimeModeParser.parse(["Vision", "--game-mode", "normal"]),
            .normal
        )
        XCTAssertEqual(
            try HollowKnightRuntimeModeParser.parse(["Vision", "--game-mode=MODDED"]),
            .modded
        )
    }

    func testGameModeArgumentRejectsInvalidAndRepeatedValues() {
        XCTAssertThrowsError(try HollowKnightRuntimeModeParser.parse([
            "Vision", "--game-mode", "vanilla",
        ]))
        XCTAssertThrowsError(try HollowKnightRuntimeModeParser.parse([
            "Vision", "--game-mode=normal", "--game-mode=modded",
        ]))
    }

    func testGamePathArgumentAcceptsManagedLaunchForms() throws {
        XCTAssertEqual(
            try HollowKnightGamePathParser.parse([
                "Vision", "--game-path", "/Games/Stock/hollow_knight.app",
            ]),
            "/Games/Stock/hollow_knight.app"
        )
        XCTAssertEqual(
            try HollowKnightGamePathParser.parse([
                "Vision", "--game-path=/Games/Modded/hollow_knight.app",
            ]),
            "/Games/Modded/hollow_knight.app"
        )
        XCTAssertThrowsError(try HollowKnightGamePathParser.parse([
            "Vision", "--game-path",
        ]))
    }

    func testNormalInitializationOmitsModOnlyMenuLabel() {
        let normal = StencilInitializationPolicy.missingSteps(
            examples: [],
            languageIdentifier: "en",
            mode: .normal
        )
        let modded = StencilInitializationPolicy.missingSteps(
            examples: [],
            languageIdentifier: "en",
            mode: .modded
        )

        XCTAssertFalse(normal.contains { $0.classIdentifier == LabelingClassIdentity.mods })
        XCTAssertTrue(modded.contains { $0.classIdentifier == LabelingClassIdentity.mods })
        XCTAssertEqual(modded.count, normal.count + 6)
        XCTAssertFalse(normal.contains { $0.context == .mods })
    }
}
