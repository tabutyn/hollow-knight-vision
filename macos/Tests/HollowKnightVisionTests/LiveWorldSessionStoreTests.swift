import CoreImage
import Foundation
import XCTest
@testable import HollowKnightVision

final class LiveWorldSessionStoreTests: XCTestCase {
    private enum InjectedFailure: Error { case freshStore }
    private let context = CIContext(options: [.useSoftwareRenderer: true])

    func testAppendSnapshotAndRestartPreserveDescriptorsAndOptimizedPoses() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let writer = try LiveWorldSessionStore(rootURL: root)
        let first = try writer.append(maskedImage: image(.red), timestamp: 1, rawCameraPose: .zero, solveWidth: 32, excludedRects: [CGRect(x: 1, y: 1, width: 2, height: 2)])
        let second = try writer.append(maskedImage: image(.blue), timestamp: 2, rawCameraPose: CGPoint(x: 4, y: 0), solveWidth: 32)
        let snapshot = try world(first: first, second: second, revision: 1)
        XCTAssertTrue(try writer.commit(snapshot, expectedRevision: 0, newRevision: 1))

        let reopened = try LiveWorldSessionStore(rootURL: root)
        let loaded = try reopened.load()
        XCTAssertEqual(loaded.snapshot, snapshot)
        XCTAssertEqual(loaded.sceneRevision, 1)
        XCTAssertEqual(try reopened.source(for: first.id).width, 32)
    }

    func testStaleDiskRevisionIsRejected() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let writer = try LiveWorldSessionStore(rootURL: root)
        let source = try writer.append(maskedImage: image(.white), timestamp: 1, rawCameraPose: .zero, solveWidth: 32)
        let stale = try LiveWorldSessionStore(rootURL: root)
        let first = try oneObservationWorld(source: source, revision: 1)
        XCTAssertTrue(try writer.commit(first, expectedRevision: 0, newRevision: 1))
        let second = try oneObservationWorld(source: source, revision: 2, optimized: CGPoint(x: 3, y: 0))
        XCTAssertFalse(try stale.commit(second, expectedRevision: 0, newRevision: 2))
        XCTAssertEqual(try writer.load().sceneRevision, 1)
    }

    func testRoomPlacementTranslationPreservesManifestEvidenceAndCommits() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try LiveWorldSessionStore(rootURL: root)
        let rightSource = try store.append(
            maskedImage: image(.red), timestamp: 1,
            rawCameraPose: CGPoint(x: -180, y: 0), solveWidth: 32
        )
        let leftSource = try store.append(
            maskedImage: image(.blue), timestamp: 2,
            rawCameraPose: CGPoint(x: -820, y: 0), solveWidth: 32
        )
        let original = try LiveWorldSnapshot(
            mapRevision: 1,
            anchoredObservationID: 1,
            observations: [
                LiveWorldObservation(
                    id: 1, sourceObservationID: rightSource.id,
                    timestamp: rightSource.timestamp,
                    sourceWidth: 32, sourceHeight: 18, solveWidth: 32,
                    sampleWidth: 16, excludedRects: [], roomID: 0,
                    rawPose: rightSource.cameraPosition,
                    optimizedPose: rightSource.cameraPosition
                ),
                LiveWorldObservation(
                    id: 2, sourceObservationID: leftSource.id,
                    timestamp: leftSource.timestamp,
                    sourceWidth: 32, sourceHeight: 18, solveWidth: 32,
                    sampleWidth: 16, excludedRects: [], roomID: 1,
                    rawPose: leftSource.cameraPosition,
                    optimizedPose: leftSource.cameraPosition
                )
            ]
        )
        XCTAssertTrue(try store.commit(
            original, expectedRevision: 0, newRevision: 1
        ))

        let translated = try original.translatingRoom(
            1, by: CGVector(dx: 320, dy: 5)
        )
        XCTAssertTrue(try store.commit(
            translated, expectedRevision: 1, newRevision: 2
        ))

        let reopened = try LiveWorldSessionStore(rootURL: root).load().snapshot
        let left = try XCTUnwrap(reopened.observations.first { $0.roomID == 1 })
        XCTAssertEqual(left.rawPose, leftSource.cameraPosition)
        XCTAssertEqual(left.optimizedPose, CGPoint(x: -500, y: 5))
    }

    func testCorruptSnapshotIsSurfacedWithoutOverwrite() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        XCTAssertTrue(try store.saveScene(data: Data("not a live world".utf8), expectedRevision: 0, newRevision: 1))
        let before = try Data(contentsOf: root.appendingPathComponent("scene.snapshot"))
        XCTAssertThrowsError(try LiveWorldSessionStore(rootURL: root)) { error in
            XCTAssertEqual(error as? LiveWorldSessionStoreError, .corruptSnapshot)
        }
        XCTAssertEqual(try Data(contentsOf: root.appendingPathComponent("scene.snapshot")), before)
    }

    func testArchivePreservesOldWorldAndFreshStoreIsEmpty() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try LiveWorldSessionStore(rootURL: root)
        let source = try store.append(maskedImage: image(.green), timestamp: 1, rawCameraPose: .zero, solveWidth: 32)
        let world = try oneObservationWorld(source: source, revision: 1)
        XCTAssertTrue(try store.commit(world, expectedRevision: 0, newRevision: 1))

        let archived = try store.archiveAndReset(now: Date(timeIntervalSince1970: 123))
        XCTAssertEqual(try archived.freshStore.load(), LiveWorldSessionState(snapshot: try LiveWorldSnapshot(), sceneRevision: 0))
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: archived.archiveURL).load().snapshot, world)
        XCTAssertTrue(FileManager.default.fileExists(atPath: archived.archiveURL.appendingPathComponent(source.imagePath).path))
    }

    func testFailedFreshStoreInitializationRestoresOriginalWorldOverPartialRoot() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try LiveWorldSessionStore(rootURL: root)
        let source = try store.append(maskedImage: image(.green), timestamp: 1, rawCameraPose: .zero, solveWidth: 32)
        let world = try oneObservationWorld(source: source, revision: 1)
        XCTAssertTrue(try store.commit(world, expectedRevision: 0, newRevision: 1))

        XCTAssertThrowsError(try store.archiveAndReset { destination in
            try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
            try Data("partial".utf8).write(to: destination.appendingPathComponent("partial"))
            throw InjectedFailure.freshStore
        }) { error in
            XCTAssertTrue(error is InjectedFailure)
        }

        let reopened = try LiveWorldSessionStore(rootURL: root)
        XCTAssertEqual(try reopened.load().snapshot, world)
        XCTAssertFalse(FileManager.default.fileExists(atPath: root.appendingPathComponent("partial").path))
    }

    private func oneObservationWorld(source: StoredFrameObservation, revision: Int, optimized: CGPoint = .zero) throws -> LiveWorldSnapshot {
        try LiveWorldSnapshot(
            mapRevision: revision,
            anchoredObservationID: 10,
            observations: [LiveWorldObservation(
                id: 10, sourceObservationID: source.id, timestamp: source.timestamp,
                sourceWidth: 32, sourceHeight: 18, solveWidth: source.solveWidth, sampleWidth: 16,
                excludedRects: source.excludedRects, rawPose: source.cameraPosition, optimizedPose: optimized
            )]
        )
    }

    private func world(first: StoredFrameObservation, second: StoredFrameObservation, revision: Int) throws -> LiveWorldSnapshot {
        let firstObservation = LiveWorldObservation(id: 10, sourceObservationID: first.id, timestamp: first.timestamp, sourceWidth: 32, sourceHeight: 18, solveWidth: first.solveWidth, sampleWidth: 16, excludedRects: first.excludedRects, rawPose: first.cameraPosition, optimizedPose: .zero)
        let secondObservation = LiveWorldObservation(id: 11, sourceObservationID: second.id, timestamp: second.timestamp, sourceWidth: 32, sourceHeight: 18, solveWidth: second.solveWidth, sampleWidth: 16, excludedRects: second.excludedRects, rawPose: second.cameraPosition, optimizedPose: CGPoint(x: 4.5, y: 0))
        return try LiveWorldSnapshot(
            mapRevision: revision,
            anchoredObservationID: 10,
            observations: [firstObservation, secondObservation],
            keyframes: [
                LiveWorldKeyframe(id: 20, observationID: 10, sourceObservationID: first.id),
                LiveWorldKeyframe(id: 21, observationID: 11, sourceObservationID: second.id)
            ],
            landmarks: [
                LiveWorldLandmark(id: 30, keyframeID: 20, sourceObservationID: first.id, samplePoint: CGPoint(x: 2, y: 3), worldPoint: CGPoint(x: 2, y: 3), descriptor: [0.1, 0.2], depth: 1, depthConfidence: 0.8),
                LiveWorldLandmark(id: 31, keyframeID: 20, sourceObservationID: first.id, samplePoint: CGPoint(x: 4, y: 3), worldPoint: CGPoint(x: 4, y: 3), descriptor: [0.3, 0.4], depth: 1, depthConfidence: 0.8),
                LiveWorldLandmark(id: 32, keyframeID: 20, sourceObservationID: first.id, samplePoint: CGPoint(x: 6, y: 3), worldPoint: CGPoint(x: 6, y: 3), descriptor: [0.5, 0.6], depth: 1, depthConfidence: 0.8)
            ],
            relativeMotionEdges: [LiveWorldRelativeMotionEdge(id: 40, fromObservationID: 10, toObservationID: 11, deltaX: 4, deltaY: 0, support: 8)],
            loopClosureEdges: [LiveWorldLoopClosureEdge(id: 50, fromKeyframeID: 20, toKeyframeID: 21, landmarkIDs: [30, 31, 32], deltaX: 4, deltaY: 0, support: 3, ambiguity: 0.1)]
        )
    }

    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private func image(_ color: CIColor) -> CGImage {
        let extent = CGRect(x: 0, y: 0, width: 32, height: 18)
        return context.createCGImage(CIImage(color: color).cropped(to: extent), from: extent)!
    }
}
