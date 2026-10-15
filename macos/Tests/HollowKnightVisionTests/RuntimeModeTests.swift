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

    func testRunningInstallationMustMatchSelectedMode() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension("app")
        let managed = root.appendingPathComponent(
            "Contents/Resources/Data/Managed",
            isDirectory: true
        )
        try FileManager.default.createDirectory(
            at: managed,
            withIntermediateDirectories: true
        )
        defer { try? FileManager.default.removeItem(at: root) }

        XCTAssertNil(HollowKnightInstallationPolicy.issue(gameURL: root, mode: .normal))
        XCTAssertEqual(
            HollowKnightInstallationPolicy.issue(gameURL: root, mode: .modded),
            .incompleteModdedGame
        )

        let receiver = managed.appendingPathComponent(
            "Mods/HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll"
        )
        try FileManager.default.createDirectory(
            at: receiver.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        XCTAssertTrue(FileManager.default.createFile(atPath: receiver.path, contents: Data()))
        XCTAssertEqual(
            HollowKnightInstallationPolicy.issue(gameURL: root, mode: .normal),
            .moddedGameInNormalMode
        )
        XCTAssertEqual(
            HollowKnightInstallationPolicy.issue(gameURL: root, mode: .modded),
            .incompleteModdedGame
        )

        let loader = managed.appendingPathComponent("MMHOOK_Assembly-CSharp.dll")
        XCTAssertTrue(FileManager.default.createFile(atPath: loader.path, contents: Data()))
        XCTAssertNil(HollowKnightInstallationPolicy.issue(gameURL: root, mode: .modded))
        XCTAssertEqual(
            HollowKnightInstallationPolicy.issue(gameURL: root, mode: .normal),
            .moddedGameInNormalMode
        )
    }
}
