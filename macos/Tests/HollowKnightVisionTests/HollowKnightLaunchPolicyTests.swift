import Foundation
import XCTest
@testable import HollowKnightVision

final class HollowKnightLaunchPolicyTests: XCTestCase {
    func testSteamInstallationUsesSteamInsteadOfStartingBootstrapProcess() throws {
        let root = try workspace()
        defer { try? FileManager.default.removeItem(at: root) }
        let app = try makeSteamInstallation(at: root)
        XCTAssertEqual(HollowKnightLaunchPolicy.route(for: app), .steam)
    }

    func testSeparateCopyDoesNotLaunchSteamLibraryVersion() throws {
        let root = try workspace()
        defer { try? FileManager.default.removeItem(at: root) }
        _ = try makeSteamInstallation(at: root)
        for name in ["Normal", "Modded"] {
            let app = root.appendingPathComponent("\(name)/hollow_knight.app")
            try FileManager.default.createDirectory(at: app, withIntermediateDirectories: true)
            XCTAssertEqual(HollowKnightLaunchPolicy.route(for: app), .application(app.resolvingSymlinksInPath().standardizedFileURL))
        }
    }

    func testSteamRouteRequiresManifestForExactInstalledDirectoryAndAppID() throws {
        let root = try workspace()
        defer { try? FileManager.default.removeItem(at: root) }
        let app = try makeSteamInstallation(at: root)
        let manifest = root.appendingPathComponent("steamapps/appmanifest_367520.acf")
        for contents in [
            "\"appid\" \"367520\"\n\"installdir\" \"Hollow Knight Modded\"",
            "\"appid\" \"999999\"\n\"installdir\" \"Hollow Knight\"",
            "invalid manifest",
        ] {
            try contents.write(to: manifest, atomically: true, encoding: .utf8)
            XCTAssertEqual(HollowKnightLaunchPolicy.route(for: app), .application(app.resolvingSymlinksInPath().standardizedFileURL))
        }
        try FileManager.default.removeItem(at: manifest)
        XCTAssertEqual(HollowKnightLaunchPolicy.route(for: app), .application(app.resolvingSymlinksInPath().standardizedFileURL))
    }

    func testSymlinkToSteamBundleUsesSameLaunchRoute() throws {
        let root = try workspace()
        defer { try? FileManager.default.removeItem(at: root) }
        let app = try makeSteamInstallation(at: root)
        let alias = root.appendingPathComponent("Hollow Knight.app")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: app)
        XCTAssertEqual(HollowKnightLaunchPolicy.route(for: alias), .steam)
    }

    private func workspace() throws -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .resolvingSymlinksInPath()
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private func makeSteamInstallation(at root: URL) throws -> URL {
        let app = root.appendingPathComponent("steamapps/common/Hollow Knight/hollow_knight.app")
        try FileManager.default.createDirectory(at: app, withIntermediateDirectories: true)
        try "\"AppState\" {\n\"appid\" \"367520\"\n\"installdir\" \"Hollow Knight\"\n}"
            .write(to: root.appendingPathComponent("steamapps/appmanifest_367520.acf"),
                   atomically: true, encoding: .utf8)
        return app
    }
}
