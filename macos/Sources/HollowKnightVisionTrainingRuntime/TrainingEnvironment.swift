import Foundation

/// User-specific paths stay outside the signed, distributable application.
public struct TrainingEnvironment {
    public let rootURL: URL

    private struct Settings: Codable { let pythonPath: String }

    public init(rootURL: URL? = nil) {
        self.rootURL = rootURL ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/HollowKnightVision/training-v1")
    }

    public var settingsURL: URL { rootURL.appendingPathComponent("runtime-settings.json") }
    public var defaultPythonURL: URL {
        rootURL.appendingPathComponent("runtime/.training-venv/bin/python")
    }

    public func pythonURL(environment: [String: String] = ProcessInfo.processInfo.environment) throws -> URL {
        if let override = environment["HKV_TRAINER_PYTHON"], !override.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return URL(fileURLWithPath: override)
        }
        if let settings = try loadSettings() { return URL(fileURLWithPath: settings.pythonPath) }
        return defaultPythonURL
    }

    /// Installation preserves an existing registration unless explicitly overridden.
    public func register(pythonURL: URL, onlyIfUnset: Bool = false) throws {
        if onlyIfUnset, try loadSettings() != nil { return }
        guard FileManager.default.isExecutableFile(atPath: pythonURL.path) else {
            throw NSError(domain: NSCocoaErrorDomain, code: NSFileNoSuchFileError,
                userInfo: [NSLocalizedDescriptionKey: "Training Python is not executable: \(pythonURL.path)"])
        }
        let settings = Settings(pythonPath: pythonURL.standardizedFileURL.path)
        try FileManager.default.createDirectory(at: rootURL, withIntermediateDirectories: true)
        try JSONEncoder().encode(settings).write(to: settingsURL, options: .atomic)
    }

    private func loadSettings() throws -> Settings? {
        guard FileManager.default.fileExists(atPath: settingsURL.path) else { return nil }
        let settings = try JSONDecoder().decode(Settings.self, from: Data(contentsOf: settingsURL))
        guard settings.pythonPath.hasPrefix("/"), !settings.pythonPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "Training Python must be an absolute path."))
        }
        return settings
    }
}
