import Foundation
import XCTest
import HollowKnightVisionTrainingRuntime

final class TrainingEnvironmentTests: XCTestCase {
    private var root: URL!
    private var runtime: TrainingEnvironment!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("hkv-training-runtime-\(UUID())")
        runtime = TrainingEnvironment(rootURL: root)
    }

    override func tearDownWithError() throws { if FileManager.default.fileExists(atPath: root.path) { try FileManager.default.removeItem(at: root) } }

    func testFreshInstallUsesUserStorageWithoutCheckoutOrWorkingDirectory() throws {
        XCTAssertEqual(try runtime.pythonURL(environment: [:]), root.appendingPathComponent("runtime/.training-venv/bin/python"))
        XCTAssertEqual(try runtime.pythonURL(environment: ["HKV_TRAINER_PYTHON": " \n"]), runtime.defaultPythonURL)
        XCTAssertTrue(TrainingEnvironment().defaultPythonURL.path.contains("Library/Application Support/HollowKnightVision/training-v1/runtime/"))
    }

    func testRegistrationSurvivesNewLauncherAndInstallerWithoutOverride() throws {
        let python = try executable("environment with spaces/python")
        try runtime.register(pythonURL: python)
        let relaunched = TrainingEnvironment(rootURL: root)
        XCTAssertEqual(try relaunched.pythonURL(environment: [:]), python)
        try relaunched.register(pythonURL: try executable("new checkout/python"), onlyIfUnset: true)
        XCTAssertEqual(try relaunched.pythonURL(environment: [:]), python)
        let registered = try JSONSerialization.jsonObject(with: Data(contentsOf: runtime.settingsURL)) as? [String: String]
        XCTAssertEqual(registered?["pythonPath"], python.path)
    }

    func testExplicitOverridesWinWithoutChangingRegistration() throws {
        let python = try executable("registered/python")
        try runtime.register(pythonURL: python, onlyIfUnset: true)
        let override = try executable("override/python")
        XCTAssertEqual(try runtime.pythonURL(environment: ["HKV_TRAINER_PYTHON": override.path]), override)
        XCTAssertEqual(try runtime.pythonURL(environment: [:]), python)
        try runtime.register(pythonURL: override)
        XCTAssertEqual(try runtime.pythonURL(environment: [:]), override)
    }

    func testMissingInterpreterCannotReplaceWorkingRegistration() throws {
        let python = try executable("working/python")
        try runtime.register(pythonURL: python)
        XCTAssertThrowsError(try runtime.register(pythonURL: root.appendingPathComponent("missing")))
        XCTAssertEqual(try runtime.pythonURL(environment: [:]), python)
    }

    func testMalformedSettingsAreReportedAndExplicitOverrideCanRecover() throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        for data in [Data("broken".utf8), Data("{\"pythonPath\":\"relative/python\"}".utf8), Data("{\"pythonPath\":\"\"}".utf8)] {
            try data.write(to: runtime.settingsURL)
            XCTAssertThrowsError(try runtime.pythonURL(environment: [:]))
        }
        let python = try executable("repaired/python")
        XCTAssertEqual(try runtime.pythonURL(environment: ["HKV_TRAINER_PYTHON": python.path]), python)
        try runtime.register(pythonURL: python)
        XCTAssertEqual(try runtime.pythonURL(environment: [:]), python)
    }

    private func executable(_ path: String) throws -> URL {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }
}
