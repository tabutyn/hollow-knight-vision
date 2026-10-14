import Foundation

enum VisionResetController {
    static func resetStencils(
        fileManager: FileManager = .default,
        now: Date = Date(),
        supportURL: URL? = nil
    ) throws -> URL? {
        let support = supportURL ?? supportRoot(fileManager: fileManager)
        let store = LabelingExampleStore(rootURL: support.appendingPathComponent("labeling-v1/examples"))
        let menuContexts = Set(LabelingContext.contractSetCases.filter { $0 != .game }.map(\.storageIdentifier))
        let paths = try store.loadExamples().filter {
            menuContexts.contains($0.manifest.contextIdentifier)
        }.map(\.directoryURL) + [support.appendingPathComponent("menu-stencil-live-v1", isDirectory: true)]
        return try archive(paths: paths, kind: "stencils", fileManager: fileManager,
                           now: now, supportURL: support)
    }

    static func factoryReset(
        defaults: UserDefaults = .standard,
        fileManager: FileManager = .default,
        now: Date = Date()
    ) throws -> URL? {
        let support = supportRoot(fileManager: fileManager)
        let archived = try archive(
            paths: [support],
            kind: "factory",
            fileManager: fileManager,
            now: now
        )
        if let identifier = Bundle.main.bundleIdentifier {
            defaults.removePersistentDomain(forName: identifier)
        } else {
            for key in defaults.dictionaryRepresentation().keys
            where key.hasPrefix("HollowKnightVision") || key.hasPrefix("labeling.") {
                defaults.removeObject(forKey: key)
            }
        }
        return archived
    }

    private static func supportRoot(fileManager: FileManager) -> URL {
        let applicationSupport = fileManager.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first ?? fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support", isDirectory: true)
        return applicationSupport.appendingPathComponent(
            "HollowKnightVision",
            isDirectory: true
        )
    }

    private static func archive(
        paths: [URL],
        kind: String,
        fileManager: FileManager,
        now: Date,
        supportURL: URL? = nil
    ) throws -> URL? {
        let existing = paths.filter { fileManager.fileExists(atPath: $0.path) }
        guard !existing.isEmpty else { return nil }
        let support = supportURL ?? supportRoot(fileManager: fileManager)
        let backupRoot = support.deletingLastPathComponent()
            .appendingPathComponent("HollowKnightVision Backups", isDirectory: true)
        let formatter = ISO8601DateFormatter()
        let stamp = formatter.string(from: now)
            .replacingOccurrences(of: ":", with: "-")
        let destination = backupRoot.appendingPathComponent(
            "\(kind)-\(stamp)-\(UUID().uuidString.lowercased())",
            isDirectory: true
        )
        try fileManager.createDirectory(
            at: destination,
            withIntermediateDirectories: true
        )
        do {
            for path in existing {
                let target = destination.appendingPathComponent(path.lastPathComponent)
                try fileManager.moveItem(at: path, to: target)
            }
        } catch {
            for path in existing {
                let archived = destination.appendingPathComponent(path.lastPathComponent)
                if fileManager.fileExists(atPath: archived.path),
                   !fileManager.fileExists(atPath: path.path) {
                    try? fileManager.moveItem(at: archived, to: path)
                }
            }
            throw error
        }
        return destination
    }
}
