import Foundation

struct AtlasSavedState: Codable, Identifiable, Equatable {
    let id: UUID
    let name: String
    let createdAt: Date
}

struct AtlasAutoSaveSummary: Identifiable, Equatable {
    static let activeID = UUID(uuidString: "A0705A5E-0000-4000-8000-000000000001")!
    let id = activeID
    let registrationCount: Int
    let createdAt: Date
    let totalByteCount: Int64?

    init(registrationCount: Int, createdAt: Date, totalByteCount: Int64? = nil) {
        self.registrationCount = registrationCount
        self.createdAt = createdAt
        self.totalByteCount = totalByteCount
    }

    func withTotalByteCount(_ byteCount: Int64) -> AtlasAutoSaveSummary {
        AtlasAutoSaveSummary(
            registrationCount: registrationCount,
            createdAt: createdAt,
            totalByteCount: byteCount
        )
    }
}

enum AtlasDiskUsage {
    static func allocatedByteCount(
        at rootURL: URL,
        fileManager: FileManager = .default
    ) throws -> Int64 {
        let keys: [URLResourceKey] = [
            .isRegularFileKey, .totalFileAllocatedSizeKey, .fileAllocatedSizeKey
        ]
        guard let enumerator = fileManager.enumerator(
            at: rootURL,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        ) else { throw AtlasStateStoreError.missingState }
        var total: Int64 = 0
        for case let url as URL in enumerator {
            let values = try url.resourceValues(forKeys: Set(keys))
            guard values.isRegularFile == true else { continue }
            total += Int64(values.totalFileAllocatedSize ?? values.fileAllocatedSize ?? 0)
        }
        return total
    }
}

enum AtlasStateStoreError: LocalizedError {
    case emptyName
    case missingState
    case archiveImportRollbackFailed

    var errorDescription: String? {
        switch self {
        case .emptyName: "Give the atlas state a name."
        case .missingState: "That atlas state is no longer available."
        case .archiveImportRollbackFailed: "The archived atlas remains in the state-library staging folder."
        }
    }
}

/// Named, immutable restore points. The active atlas remains auto-saved at its
/// existing root; loading copies a saved state into that root, never edits it.
final class AtlasStateStore {
    let rootURL: URL
    private let fileManager: FileManager

    init(
        rootURL: URL = LiveWorldSessionStore.defaultRootURL()
            .deletingLastPathComponent()
            .appendingPathComponent("atlas-states-v1", isDirectory: true),
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL.standardizedFileURL
        self.fileManager = fileManager
    }

    func list() throws -> [AtlasSavedState] {
        guard fileManager.fileExists(atPath: rootURL.path) else { return [] }
        return try fileManager.contentsOfDirectory(
            at: rootURL, includingPropertiesForKeys: [.isDirectoryKey]
        ).compactMap { directory in
            guard UUID(uuidString: directory.lastPathComponent) != nil else { return nil }
            let metadataURL = directory.appendingPathComponent("state.json")
            guard let data = try? Data(contentsOf: metadataURL),
                  let state = try? JSONDecoder().decode(AtlasSavedState.self, from: data),
                  directory.lastPathComponent == state.id.uuidString
            else { return nil }
            return state
        }.sorted { $0.createdAt > $1.createdAt }
    }

    func save(
        name: String,
        copyActiveWorld: (URL) throws -> Void
    ) throws -> AtlasSavedState {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw AtlasStateStoreError.emptyName }
        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        let state = AtlasSavedState(id: UUID(), name: trimmed, createdAt: Date())
        let stagingURL = rootURL.appendingPathComponent("staging-\(state.id.uuidString)")
        let stateURL = rootURL.appendingPathComponent(state.id.uuidString, isDirectory: true)
        try fileManager.createDirectory(at: stagingURL, withIntermediateDirectories: false)
        do {
            try copyActiveWorld(stagingURL.appendingPathComponent("world", isDirectory: true))
            let data = try JSONEncoder().encode(state)
            try data.write(to: stagingURL.appendingPathComponent("state.json"), options: .atomic)
            try fileManager.moveItem(at: stagingURL, to: stateURL)
        } catch {
            try? fileManager.removeItem(at: stagingURL)
            throw error
        }
        return state
    }

    func worldURL(for state: AtlasSavedState) throws -> URL {
        let stateURL = rootURL.appendingPathComponent(state.id.uuidString, isDirectory: true)
        let metadataURL = stateURL.appendingPathComponent("state.json")
        guard let data = try? Data(contentsOf: metadataURL),
              let stored = try? JSONDecoder().decode(AtlasSavedState.self, from: data),
              stored.id == state.id else { throw AtlasStateStoreError.missingState }
        let worldURL = stateURL.appendingPathComponent("world", isDirectory: true)
        guard fileManager.fileExists(atPath: worldURL.path) else {
            throw AtlasStateStoreError.missingState
        }
        return worldURL
    }

    /// Turns New's archived active world into a loadable named state without
    /// copying all frame evidence a second time. The archive came from the
    /// already-open active writer; loading it later performs full validation.
    /// Avoid decoding a potentially multi-gigabyte world on this UI operation.
    func importArchive(at archiveURL: URL, name: String) throws -> AtlasSavedState {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw AtlasStateStoreError.emptyName }
        guard fileManager.fileExists(atPath: archiveURL.path) else {
            throw AtlasStateStoreError.missingState
        }
        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        let state = AtlasSavedState(id: UUID(), name: trimmed, createdAt: Date())
        let stagingURL = rootURL.appendingPathComponent("staging-\(state.id.uuidString)")
        let stateURL = rootURL.appendingPathComponent(state.id.uuidString, isDirectory: true)
        let stagedWorldURL = stagingURL.appendingPathComponent("world", isDirectory: true)
        try fileManager.createDirectory(at: stagingURL, withIntermediateDirectories: false)
        do {
            try fileManager.moveItem(at: archiveURL, to: stagedWorldURL)
            try JSONEncoder().encode(state).write(
                to: stagingURL.appendingPathComponent("state.json"), options: .atomic
            )
            try fileManager.moveItem(at: stagingURL, to: stateURL)
        } catch {
            if fileManager.fileExists(atPath: stagedWorldURL.path) {
                do {
                    try fileManager.moveItem(at: stagedWorldURL, to: archiveURL)
                } catch {
                    throw AtlasStateStoreError.archiveImportRollbackFailed
                }
            }
            try? fileManager.removeItem(at: stagingURL)
            throw error
        }
        return state
    }

    /// Delete is permanent so the selected state's frame evidence releases its
    /// disk space rather than accumulating in an application-private Trash.
    func delete(_ state: AtlasSavedState) throws {
        _ = try worldURL(for: state)
        let sourceURL = rootURL.appendingPathComponent(state.id.uuidString, isDirectory: true)
        try fileManager.removeItem(at: sourceURL)
    }

    /// Reset first moves the active world aside atomically; this permanently
    /// removes that exact archive after the fresh Auto Save is open.
    func deleteActiveArchive(at archiveURL: URL) throws {
        guard fileManager.fileExists(atPath: archiveURL.path) else {
            throw AtlasStateStoreError.missingState
        }
        try fileManager.removeItem(at: archiveURL)
    }

    /// One-time cleanup for atlases deleted by builds that used a recoverable
    /// application-private Trash. Only that exact state-library child is
    /// eligible; active and named atlas directories are never traversed here.
    func purgeLegacyTrash() throws {
        let trashURL = rootURL.appendingPathComponent("Trash", isDirectory: true)
        guard fileManager.fileExists(atPath: trashURL.path) else { return }
        try fileManager.removeItem(at: trashURL)
    }
}
