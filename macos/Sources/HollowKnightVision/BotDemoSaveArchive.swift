import CryptoKit
import Foundation

enum BotDemoSaveArchiveError: LocalizedError {
    case profileMismatch
    var errorDescription: String? { "The profile stencils and save-file versions do not agree. No save was cleared." }
}

struct BotDemoSaveArchive {
    static var gameSavesURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(
            "Library/Application Support/unity.Team Cherry.Hollow Knight", isDirectory: true)
    }
    static var archivesURL: URL {
        BotDemonstrationRecorder.defaultRootURL.deletingLastPathComponent()
            .appendingPathComponent("save-archives", isDirectory: true)
    }
    let savesURL: URL
    let rootURL: URL
    init(savesURL: URL = Self.gameSavesURL, rootURL: URL = Self.archivesURL) {
        self.savesURL = savesURL; self.rootURL = rootURL
    }
    struct Manifest: Codable {
        let schemaVersion: Int
        let slot: Int
        let createdAt: Date
        let sourceDirectory: String
        let files: [String: String]
        let hadActiveSave: Bool
    }
    struct Result { let url: URL; let hadActiveSave: Bool }

    func slotFiles() throws -> [URL] {
        try FileManager.default.contentsOfDirectory(at: savesURL, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            .filter { $0.lastPathComponent.hasPrefix("user4.") || $0.lastPathComponent.hasPrefix("user4_") }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }
    func hasActiveSave(matching occupiedSlots: Set<Int>? = nil) throws -> Bool {
        guard let occupiedSlots else { return try slotFiles().contains { $0.pathExtension == "dat" } }
        // Different game versions coexist in this directory (user4.dat and
        // user4_1.5.12620.dat, for example). Reconcile whole profile families
        // with the live Clear Save stencils rather than treating all as active.
        let files = try FileManager.default.contentsOfDirectory(at: savesURL, includingPropertiesForKeys: nil)
        var families = [String: Set<Int>]()
        for file in files where file.pathExtension == "dat" {
            let name = file.deletingPathExtension().lastPathComponent
            guard name.hasPrefix("user"), name.count >= 5,
                  let slot = Int(String(name.dropFirst(4).prefix(1))), (1...4).contains(slot) else { continue }
            let family = String(name.dropFirst(5))
            guard family.isEmpty || family.hasPrefix("_") else { continue }
            families[family, default: []].insert(slot)
        }
        if families.isEmpty && occupiedSlots.isEmpty { return false }
        guard families.values.contains(occupiedSlots) else {
            throw BotDemoSaveArchiveError.profileMismatch
        }
        return occupiedSlots.contains(4)
    }

    func verifySourceMatchesArchive(at url: URL) throws {
        let manifest = try JSONDecoder().decode(Manifest.self, from: Data(contentsOf: url.appendingPathComponent("manifest.json")))
        let files = try slotFiles()
        guard manifest.schemaVersion == 1, manifest.slot == 4,
              Set(files.map(\.lastPathComponent)) == Set(manifest.files.keys) else {
            throw BotRuntimeError.incompatible("Slot 4 changed after archiving. No save was cleared.")
        }
        for file in files {
            let name = file.lastPathComponent
            guard BotNativePolicy.sha256(try Data(contentsOf: file)) == manifest.files[name],
                  BotNativePolicy.sha256(try Data(contentsOf: url.appendingPathComponent(name))) == manifest.files[name] else {
                throw BotRuntimeError.incompatible("Slot 4 or its archive changed. No save was cleared.")
            }
        }
    }

    /// Copies and verifies every slot-4 save/sidecar/backup before authorizing
    /// the in-game Clear Save action. No original is moved, edited, or deleted.
    func create(activeSavePresent: Bool? = nil) throws -> Result {
        let files = try slotFiles()
        let active = activeSavePresent ?? files.contains { $0.pathExtension == "dat" }
        let url = rootURL.appendingPathComponent("slot-4-\(UUID().uuidString.lowercased())", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        var hashes = [String: String]()
        for source in files {
            let info = try source.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard info.isRegularFile == true, info.isSymbolicLink != true else {
                throw BotRuntimeError.incompatible("Slot 4 contains an unexpected file; archive was not authorized.")
            }
            let data = try Data(contentsOf: source)
            let destination = url.appendingPathComponent(source.lastPathComponent)
            try data.write(to: destination, options: .atomic)
            let hash = BotNativePolicy.sha256(data)
            guard BotNativePolicy.sha256(try Data(contentsOf: destination)) == hash else {
                throw BotRuntimeError.incompatible("Save archive verification failed.")
            }
            hashes[source.lastPathComponent] = hash
        }
        // If an autosave raced the copy, the archive must not authorize a clear.
        guard try slotFiles().map(\.lastPathComponent) == files.map(\.lastPathComponent) else {
            throw BotRuntimeError.incompatible("Slot 4 changed while archiving. Start Demo again from Select Profile.")
        }
        for source in files {
            guard BotNativePolicy.sha256(try Data(contentsOf: source)) == hashes[source.lastPathComponent] else {
                throw BotRuntimeError.incompatible("Slot 4 changed while archiving. No save was cleared.")
            }
        }
        let manifest = Manifest(schemaVersion: 1, slot: 4, createdAt: Date(), sourceDirectory: savesURL.path,
                                files: hashes, hadActiveSave: active)
        try JSONEncoder().encode(manifest).write(to: url.appendingPathComponent("manifest.json"), options: .atomic)
        return Result(url: url, hadActiveSave: active)
    }
}
