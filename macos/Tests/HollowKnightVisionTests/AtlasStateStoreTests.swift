import CoreImage
import Foundation
import XCTest
@testable import HollowKnightVision

final class AtlasStateStoreTests: XCTestCase {
    func testSaveListLoadAndPermanentDelete() throws {
        let workspace = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: workspace) }
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let activeURL = workspace.appendingPathComponent("active")
        let activeStore = try LiveWorldSessionStore(rootURL: activeURL)
        let store = AtlasStateStore(rootURL: workspace.appendingPathComponent("states"))

        let saved = try store.save(name: "First atlas") { destination in
            try FileManager.default.copyItem(at: activeStore.rootURL, to: destination)
        }
        XCTAssertEqual(try store.list().map(\.id), [saved.id])
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: store.worldURL(for: saved)).load().sceneRevision, 0)

        try store.delete(saved)
        XCTAssertTrue(try store.list().isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: store.rootURL.appendingPathComponent(saved.id.uuidString).path
        ))
    }

    func testNewArchiveCanBecomeLoadableWithoutDuplicatingWorld() throws {
        let workspace = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: workspace) }
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let activeStore = try LiveWorldSessionStore(rootURL: workspace.appendingPathComponent("active"))
        let archive = try activeStore.archiveAndReset()
        let states = AtlasStateStore(rootURL: workspace.appendingPathComponent("states"))

        let imported = try states.importArchive(at: archive.archiveURL, name: "Before New")
        XCTAssertFalse(FileManager.default.fileExists(atPath: archive.archiveURL.path))
        XCTAssertEqual(try states.list().map(\.id), [imported.id])
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: states.worldURL(for: imported)).load().sceneRevision, 0)
        XCTAssertEqual(try archive.freshStore.load().sceneRevision, 0)
    }

    func testDeletingAutoSaveArchivePermanentlyRemovesIt() throws {
        let workspace = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: workspace) }
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let activeStore = try LiveWorldSessionStore(rootURL: workspace.appendingPathComponent("active"))
        let archive = try activeStore.archiveAndReset()
        let states = AtlasStateStore(rootURL: workspace.appendingPathComponent("states"))

        try states.deleteActiveArchive(at: archive.archiveURL)

        XCTAssertFalse(FileManager.default.fileExists(atPath: archive.archiveURL.path))
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: states.rootURL.appendingPathComponent("Trash").path
        ))
        XCTAssertEqual(try archive.freshStore.load().snapshot.observations.count, 0)
    }

    func testPurgingLegacyTrashDoesNotTouchActiveOrNamedStates() throws {
        let workspace = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: workspace) }
        let states = AtlasStateStore(rootURL: workspace.appendingPathComponent("states"))
        let trash = states.rootURL.appendingPathComponent("Trash", isDirectory: true)
        try FileManager.default.createDirectory(at: trash, withIntermediateDirectories: true)
        try Data(repeating: 1, count: 64).write(to: trash.appendingPathComponent("old-atlas"))
        let active = workspace.appendingPathComponent("active", isDirectory: true)
        try FileManager.default.createDirectory(at: active, withIntermediateDirectories: true)
        let named = states.rootURL.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: named, withIntermediateDirectories: true)

        try states.purgeLegacyTrash()

        XCTAssertFalse(FileManager.default.fileExists(atPath: trash.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: active.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: named.path))
    }

    func testAtlasDiskUsageCountsRegisteredImageStorage() throws {
        let workspace = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: workspace) }
        let frames = workspace.appendingPathComponent("frames")
        try FileManager.default.createDirectory(at: frames, withIntermediateDirectories: true)
        try Data(repeating: 7, count: 8_192).write(to: frames.appendingPathComponent("one.png"))
        try Data(repeating: 9, count: 4_096).write(to: frames.appendingPathComponent("two.png"))

        XCTAssertGreaterThanOrEqual(try AtlasDiskUsage.allocatedByteCount(at: workspace), 12_288)
    }

    func testPipelineLoadRestoresSavedSnapshotAndLeavesStateImmutable() throws {
        let workspace = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: workspace) }
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let activeURL = workspace.appendingPathComponent("active")
        let writer = try LiveWorldSessionStore(rootURL: activeURL)
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 32, height: 18)
        let image = try XCTUnwrap(context.createCGImage(
            CIImage(color: .green).cropped(to: extent), from: extent
        ))
        let source = try writer.append(
            maskedImage: image, timestamp: 1, rawCameraPose: .zero, solveWidth: 32
        )
        let snapshot = try LiveWorldSnapshot(
            mapRevision: 1,
            anchoredObservationID: 10,
            observations: [LiveWorldObservation(
                id: 10, sourceObservationID: source.id, timestamp: source.timestamp,
                sourceWidth: 32, sourceHeight: 18, solveWidth: source.solveWidth,
                sampleWidth: 16, excludedRects: source.excludedRects,
                rawPose: source.cameraPosition, optimizedPose: .zero
            )]
        )
        XCTAssertTrue(try writer.commit(snapshot, expectedRevision: 0, newRevision: 1))
        let pipeline = LiveAtlasPipeline(context: context, worldRootURL: activeURL)
        let savedURL = workspace.appendingPathComponent("saved")

        try pipeline.copyActiveWorld(to: savedURL)
        XCTAssertTrue(pipeline.reset())
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: activeURL).load().sceneRevision, 0)
        let loaded = try pipeline.loadSavedWorld(from: savedURL)
        XCTAssertEqual(loaded.snapshot, snapshot)
        XCTAssertFalse(loaded.atlasTiles.isEmpty)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: activeURL).load().snapshot, snapshot)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: savedURL).load().snapshot, snapshot)
    }
}
