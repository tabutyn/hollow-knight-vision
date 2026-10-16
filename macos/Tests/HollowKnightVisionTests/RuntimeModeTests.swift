import XCTest
@testable import HollowKnightVision

final class RuntimeModeTests: XCTestCase {
    func testOrdinaryLaunchAlwaysRequiresModeChoice() {
        XCTAssertTrue(HollowKnightRuntimeModeLaunchPolicy.requiresChoice(argumentMode: nil))
        XCTAssertFalse(HollowKnightRuntimeModeLaunchPolicy.requiresChoice(argumentMode: .normal))
        XCTAssertFalse(HollowKnightRuntimeModeLaunchPolicy.requiresChoice(argumentMode: .modded))
        XCTAssertEqual(HollowKnightRuntimeModeLaunchPolicy.initialMode(argumentMode: nil), .normal)
        XCTAssertEqual(HollowKnightRuntimeModeLaunchPolicy.initialMode(argumentMode: .modded), .modded)
    }

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

        XCTAssertEqual(
            HollowKnightInstallationPolicy.issue(gameURL: root, mode: .normal),
            .notHollowKnightInstallation
        )
        let gameAssembly = managed.appendingPathComponent("Assembly-CSharp.dll")
        XCTAssertTrue(FileManager.default.createFile(atPath: gameAssembly.path, contents: Data()))
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

    func testDiscoveryOnlyEnablesCompleteCompatibleInstallations() throws {
        let workspace = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        let normal = workspace.appendingPathComponent("Normal.app", isDirectory: true)
        let modded = workspace.appendingPathComponent("Modded.app", isDirectory: true)
        let shortcut = workspace.appendingPathComponent("Shortcut.app", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: workspace) }

        for game in [normal, modded] {
            let managed = game.appendingPathComponent(
                "Contents/Resources/Data/Managed",
                isDirectory: true
            )
            try FileManager.default.createDirectory(at: managed, withIntermediateDirectories: true)
            XCTAssertTrue(FileManager.default.createFile(
                atPath: managed.appendingPathComponent("Assembly-CSharp.dll").path,
                contents: Data()
            ))
        }
        let moddedManaged = modded.appendingPathComponent(
            "Contents/Resources/Data/Managed",
            isDirectory: true
        )
        XCTAssertTrue(FileManager.default.createFile(
            atPath: moddedManaged.appendingPathComponent("MMHOOK_Assembly-CSharp.dll").path,
            contents: Data()
        ))
        let receiver = moddedManaged.appendingPathComponent(
            "Mods/HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll"
        )
        try FileManager.default.createDirectory(
            at: receiver.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        XCTAssertTrue(FileManager.default.createFile(atPath: receiver.path, contents: Data()))
        try FileManager.default.createDirectory(at: shortcut, withIntermediateDirectories: true)

        let catalog = HollowKnightInstallationDiscovery.catalog(
            candidateURLs: [shortcut, modded, normal]
        )
        XCTAssertEqual(catalog.normal, normal.standardizedFileURL)
        XCTAssertEqual(catalog.modded, modded.standardizedFileURL)
    }
}
