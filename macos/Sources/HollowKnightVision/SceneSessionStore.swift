import CoreGraphics
import Darwin
import Foundation

struct StoredFrameObservation: Codable, Identifiable, Equatable {
    let id: Int
    let roomID: UUID
    let visitID: UUID
    let timestamp: Double
    let imagePath: String
    let cameraPosition: CGPoint
    let solveWidth: Double
    let excludedRects: [CGRect]
    let poseRevision: Int
}

struct SessionManifest: Codable, Equatable {
    static let currentSchemaVersion = 1

    var schemaVersion: Int
    var sessionID: UUID
    var observations: [StoredFrameObservation]
    var nextObservationID: Int

    init(
        schemaVersion: Int = SessionManifest.currentSchemaVersion,
        sessionID: UUID = UUID(),
        observations: [StoredFrameObservation] = [],
        nextObservationID: Int = 0
    ) {
        self.schemaVersion = schemaVersion
        self.sessionID = sessionID
        self.observations = observations
        self.nextObservationID = nextObservationID
    }
}

enum SceneSessionStoreError: Error, Equatable {
    case unsupportedSchema(Int)
    case unsupportedSnapshotSchema(Int)
    case unsafeImagePath(String)
    case missingSource(String)
    case corruptSource(String)
    case invalidObservation
    case invalidRevision
}

final class SceneSessionStore {
    let rootURL: URL
    private(set) var manifest: SessionManifest
    private(set) var sceneRevision: Int
    private var sceneData: Data?

    private let fileManager = FileManager.default
    private let manifestFileName = "manifest.json"
    private let framesDirectoryName = "frames"
    private let sceneFileName = "scene.snapshot"
    private let sceneLockFileName = "scene.snapshot.lock"

    init(rootURL: URL) throws {
        // `/tmp` is commonly a symlink to `/private/tmp`. Canonicalize once at
        // the boundary so evidence containment compares like with like.
        let resolvedRootURL = rootURL.standardizedFileURL.resolvingSymlinksInPath()
        let manifestURL = resolvedRootURL.appendingPathComponent("manifest.json")
        let fileManager = FileManager.default
        let loadedManifest: SessionManifest
        if fileManager.fileExists(atPath: manifestURL.path) {
            let data = try Data(contentsOf: manifestURL)
            let decoded = try JSONDecoder().decode(SessionManifest.self, from: data)
            guard decoded.schemaVersion == SessionManifest.currentSchemaVersion else {
                throw SceneSessionStoreError.unsupportedSchema(decoded.schemaVersion)
            }
            for observation in decoded.observations {
                _ = try Self.evidenceURL(for: observation.imagePath, rootURL: resolvedRootURL)
            }
            loadedManifest = decoded
        } else {
            try fileManager.createDirectory(at: resolvedRootURL, withIntermediateDirectories: true)
            loadedManifest = SessionManifest()
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(loadedManifest).write(to: manifestURL, options: .atomic)
        }
        self.rootURL = resolvedRootURL
        self.manifest = loadedManifest
        self.sceneRevision = 0
        self.sceneData = nil
        try fileManager.createDirectory(at: framesURL, withIntermediateDirectories: true)
        let scene = try readSceneEnvelope()
        sceneRevision = scene?.revision ?? 0
        sceneData = scene?.data
    }

    @discardableResult
    func append(
        frame: CGImage,
        roomID: UUID,
        visitID: UUID,
        timestamp: Double,
        cameraPosition: CGPoint,
        solveWidth: Double,
        excluding excludedRects: [CGRect],
        poseRevision: Int = 0
    ) throws -> StoredFrameObservation {
        guard timestamp.isFinite,
              cameraPosition.x.isFinite, cameraPosition.y.isFinite,
              solveWidth.isFinite, solveWidth > 0,
              excludedRects.allSatisfy({ $0.origin.x.isFinite && $0.origin.y.isFinite && $0.width.isFinite && $0.height.isFinite }) else {
            throw SceneSessionStoreError.invalidObservation
        }
        let id = manifest.nextObservationID
        let imagePath = "\(framesDirectoryName)/\(String(format: "%08d", id)).png"
        let observation = StoredFrameObservation(
            id: id,
            roomID: roomID,
            visitID: visitID,
            timestamp: timestamp,
            imagePath: imagePath,
            cameraPosition: cameraPosition,
            solveWidth: solveWidth,
            excludedRects: excludedRects,
            poseRevision: poseRevision
        )
        let targetURL = try evidenceURL(for: imagePath)
        try writePNG(frame, to: targetURL)
        let previousManifest = manifest
        manifest.observations.append(observation)
        manifest.nextObservationID += 1
        do {
            try writeManifest()
        } catch {
            manifest = previousManifest
            // The manifest is the commit record. Do not leave an unreferenced
            // PNG behind when that commit fails, or a retry can collide with it.
            try? fileManager.removeItem(at: targetURL)
            throw error
        }
        return observation
    }

    func source(for observation: StoredFrameObservation) throws -> CGImage {
        let url = try evidenceURL(for: observation.imagePath)
        guard fileManager.fileExists(atPath: url.path) else {
            throw SceneSessionStoreError.missingSource(observation.imagePath)
        }
        guard let image = ImageFileIO.load(url) else {
            throw SceneSessionStoreError.corruptSource(observation.imagePath)
        }
        return image
    }

    /// Stores a revision and its complete scene in one atomically replaced envelope.
    func saveScene(data: Data, expectedRevision: Int, newRevision: Int) throws -> Bool {
        try withSceneLock {
            // Refresh while holding the interprocess lock: another store may
            // have committed since this instance last loaded the envelope.
            let current = try readSceneEnvelope()
            let currentRevision = current?.revision ?? 0
            sceneRevision = currentRevision
            sceneData = current?.data
            guard expectedRevision == currentRevision else { return false }
            guard newRevision > expectedRevision else { throw SceneSessionStoreError.invalidRevision }
            let envelope = SceneSnapshotEnvelope(revision: newRevision, data: data)
            let encoded = try JSONEncoder().encode(envelope)
            try encoded.write(to: sceneURL, options: .atomic)
            sceneRevision = newRevision
            sceneData = data
            return true
        }
    }

    func loadScene() throws -> Data? {
        let scene = try readSceneEnvelope()
        sceneRevision = scene?.revision ?? 0
        sceneData = scene?.data
        return sceneData
    }

    private var manifestURL: URL { rootURL.appendingPathComponent(manifestFileName) }
    private var framesURL: URL { rootURL.appendingPathComponent(framesDirectoryName, isDirectory: true) }
    private var sceneURL: URL { rootURL.appendingPathComponent(sceneFileName) }
    private var sceneLockURL: URL { rootURL.appendingPathComponent(sceneLockFileName) }

    private func withSceneLock<T>(_ body: () throws -> T) throws -> T {
        let descriptor = open(sceneLockURL.path, O_CREAT | O_RDWR, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw POSIXError(.init(rawValue: errno) ?? .EIO) }
        defer { close(descriptor) }

        guard flock(descriptor, LOCK_EX) == 0 else {
            throw POSIXError(.init(rawValue: errno) ?? .EIO)
        }
        defer { _ = flock(descriptor, LOCK_UN) }
        return try body()
    }

    private func writeManifest() throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(manifest).write(to: manifestURL, options: .atomic)
    }

    private func writePNG(_ image: CGImage, to targetURL: URL) throws {
        let temporaryURL = framesURL.appendingPathComponent(".\(UUID().uuidString).tmp")
        guard ImageFileIO.writePNG(image, to: temporaryURL) else {
            try? fileManager.removeItem(at: temporaryURL)
            throw SceneSessionStoreError.corruptSource(targetURL.lastPathComponent)
        }
        do {
            try fileManager.moveItem(at: temporaryURL, to: targetURL)
        } catch {
            try? fileManager.removeItem(at: temporaryURL)
            throw error
        }
    }

    private func evidenceURL(for imagePath: String) throws -> URL {
        try Self.evidenceURL(for: imagePath, rootURL: rootURL)
    }

    private static func evidenceURL(for imagePath: String, rootURL: URL) throws -> URL {
        guard !imagePath.isEmpty,
              !imagePath.hasPrefix("/"),
              !imagePath.split(separator: "/").contains("..") else {
            throw SceneSessionStoreError.unsafeImagePath(imagePath)
        }
        let canonicalRoot = rootURL.standardizedFileURL.resolvingSymlinksInPath()
        let url = canonicalRoot.appendingPathComponent(imagePath).standardizedFileURL.resolvingSymlinksInPath()
        let rootPath = canonicalRoot.path.hasSuffix("/") ? canonicalRoot.path : canonicalRoot.path + "/"
        guard url.path.hasPrefix(rootPath) else { throw SceneSessionStoreError.unsafeImagePath(imagePath) }
        return url
    }

    private func readSceneEnvelope() throws -> SceneSnapshotEnvelope? {
        guard fileManager.fileExists(atPath: sceneURL.path) else { return nil }
        let envelope = try JSONDecoder().decode(SceneSnapshotEnvelope.self, from: Data(contentsOf: sceneURL))
        guard envelope.schemaVersion == SceneSnapshotEnvelope.currentSchemaVersion else {
            throw SceneSessionStoreError.unsupportedSnapshotSchema(envelope.schemaVersion)
        }
        return envelope
    }
}

private struct SceneSnapshotEnvelope: Codable {
    static let currentSchemaVersion = 1
    var schemaVersion: Int
    var revision: Int
    var data: Data

    init(schemaVersion: Int = SceneSnapshotEnvelope.currentSchemaVersion, revision: Int, data: Data) {
        self.schemaVersion = schemaVersion
        self.revision = revision
        self.data = data
    }
}
