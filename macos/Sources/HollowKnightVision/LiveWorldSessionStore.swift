import CoreGraphics
import Foundation

enum LiveWorldSessionStoreError: Error, Equatable {
    case corruptSnapshot
    case unsupportedSnapshotSchema(Int)
    case snapshotRevisionMismatch(snapshot: Int, disk: Int)
    case missingSourceObservation(Int)
    case archiveRollbackFailed
}

struct LiveWorldSessionState: Equatable {
    let snapshot: LiveWorldSnapshot
    let sceneRevision: Int
}

struct LiveWorldSessionArchive {
    let archiveURL: URL
    let freshStore: LiveWorldSessionStore
}

/// Durable, one-room ownership of masked live captures and their pose graph.
/// `SceneSessionStore` remains the sole writer of PNG evidence and the atomic
/// scene envelope; this wrapper gives that envelope a validated live-world
/// model and stable room/visit IDs for the session lifetime.
final class LiveWorldSessionStore {
    let rootURL: URL
    private let sceneStore: SceneSessionStore

    init(rootURL: URL) throws {
        let sceneStore = try SceneSessionStore(rootURL: rootURL)
        self.rootURL = sceneStore.rootURL
        self.sceneStore = sceneStore
        _ = try load()
    }

    /// A production-only convenience. Tests and callers that need isolation
    /// should always pass an explicit root URL to `init(rootURL:)`.
    static func defaultRootURL(fileManager: FileManager = .default) -> URL {
        // Live replay resets its active atlas. An explicit absolute override
        // lets automated trials own a disposable world without touching saves.
        if let path = ProcessInfo.processInfo.environment["HKV_LIVE_WORLD_ROOT"],
           path.hasPrefix("/"), path != "/" {
            return URL(fileURLWithPath: path, isDirectory: true)
        }
        let applicationSupport = fileManager.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first ?? fileManager.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        return applicationSupport
            .appendingPathComponent("HollowKnightVision", isDirectory: true)
            // v3 starts with clean feature coordinates after source-level
            // title-bar removal. Keep older worlds on disk for inspection.
            .appendingPathComponent("live-world-v3", isDirectory: true)
    }

    /// Reads the disk envelope afresh, then decodes through `LiveWorldSnapshot`
    /// so unsupported schemas and invalid graph references cannot be accepted.
    func load() throws -> LiveWorldSessionState {
        guard let data = try sceneStore.loadScene() else {
            return LiveWorldSessionState(snapshot: try LiveWorldSnapshot(), sceneRevision: sceneStore.sceneRevision)
        }
        let snapshot: LiveWorldSnapshot
        do {
            snapshot = try JSONDecoder().decode(LiveWorldSnapshot.self, from: data)
        } catch let error as LiveWorldModelError {
            if case let .unsupportedSchema(schema) = error {
                throw LiveWorldSessionStoreError.unsupportedSnapshotSchema(schema)
            }
            throw LiveWorldSessionStoreError.corruptSnapshot
        } catch {
            throw LiveWorldSessionStoreError.corruptSnapshot
        }
        guard snapshot.mapRevision == sceneStore.sceneRevision else {
            throw LiveWorldSessionStoreError.snapshotRevisionMismatch(
                snapshot: snapshot.mapRevision,
                disk: sceneStore.sceneRevision
            )
        }
        try validateSourceReferences(in: snapshot)
        return LiveWorldSessionState(snapshot: snapshot, sceneRevision: sceneStore.sceneRevision)
    }

    /// Appends only already-masked, immutable capture evidence.  The
    /// observation ID returned by `SceneSessionStore` is its durable source ID.
    @discardableResult
    func append(
        maskedImage: CGImage,
        timestamp: Double,
        rawCameraPose: CGPoint,
        solveWidth: Double,
        excludedRects: [CGRect] = []
    ) throws -> StoredFrameObservation {
        try sceneStore.append(
            frame: maskedImage,
            roomID: sceneStore.manifest.sessionID,
            visitID: sceneStore.manifest.sessionID,
            timestamp: timestamp,
            cameraPosition: rawCameraPose,
            solveWidth: solveWidth,
            excluding: excludedRects,
            poseRevision: sceneStore.sceneRevision
        )
    }

    func source(for observationID: Int) throws -> CGImage {
        guard let observation = sceneStore.manifest.observations.first(where: { $0.id == observationID }) else {
            throw LiveWorldSessionStoreError.missingSourceObservation(observationID)
        }
        return try sceneStore.source(for: observation)
    }

    /// Persists a whole, self-consistent pose graph with the same compare and
    /// swap revision contract as `SceneSessionStore.saveScene`.
    @discardableResult
    func commit(
        _ snapshot: LiveWorldSnapshot,
        expectedRevision: Int,
        newRevision: Int
    ) throws -> Bool {
        guard snapshot.mapRevision == newRevision else {
            throw LiveWorldSessionStoreError.snapshotRevisionMismatch(
                snapshot: snapshot.mapRevision,
                disk: newRevision
            )
        }
        try validateSourceReferences(in: snapshot)
        let encoded = try JSONEncoder().encode(snapshot)
        return try sceneStore.saveScene(
            data: encoded,
            expectedRevision: expectedRevision,
            newRevision: newRevision
        )
    }

    /// Moves the complete current world aside before opening a newly empty
    /// session at the original root.  The archive is a sibling, never nested
    /// inside the fresh root, so source-path containment remains unambiguous.
    func archiveAndReset(
        now: Date = Date(),
        makeFreshStore: (URL) throws -> LiveWorldSessionStore = { try LiveWorldSessionStore(rootURL: $0) }
    ) throws -> LiveWorldSessionArchive {
        let fileManager = FileManager.default
        let parent = rootURL.deletingLastPathComponent()
        let milliseconds = Int64((now.timeIntervalSince1970 * 1_000).rounded())
        let archiveURL = parent.appendingPathComponent(
            "\(rootURL.lastPathComponent).archive-\(milliseconds)-\(UUID().uuidString)",
            isDirectory: true
        )
        try fileManager.moveItem(at: rootURL, to: archiveURL)
        do {
            return LiveWorldSessionArchive(
                archiveURL: archiveURL,
                freshStore: try makeFreshStore(rootURL)
            )
        } catch let initializationError {
            // Fresh-store initialization may have partially recreated the
            // destination. Remove that partial tree before restoring the
            // complete original world, and never hide a failed restoration.
            if fileManager.fileExists(atPath: rootURL.path) {
                do {
                    try fileManager.removeItem(at: rootURL)
                } catch {
                    throw LiveWorldSessionStoreError.archiveRollbackFailed
                }
            }
            do {
                try fileManager.moveItem(at: archiveURL, to: rootURL)
            } catch {
                throw LiveWorldSessionStoreError.archiveRollbackFailed
            }
            throw initializationError
        }
    }

    private func validateSourceReferences(in snapshot: LiveWorldSnapshot) throws {
        let storedByID = Dictionary(uniqueKeysWithValues: sceneStore.manifest.observations.map { ($0.id, $0) })
        for observation in snapshot.observations {
            guard let stored = storedByID[observation.sourceObservationID],
                  stored.id == observation.sourceObservationID,
                  stored.timestamp == observation.timestamp,
                  stored.cameraPosition == observation.rawPose,
                  stored.solveWidth == observation.solveWidth,
                  stored.excludedRects == observation.excludedRects,
                  stored.imagePath.hasPrefix("frames/")
            else { throw LiveWorldSessionStoreError.missingSourceObservation(observation.sourceObservationID) }
        }
    }
}
