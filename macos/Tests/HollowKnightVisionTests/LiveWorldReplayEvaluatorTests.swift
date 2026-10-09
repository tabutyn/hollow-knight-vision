import CoreGraphics
import Foundation
import XCTest

@testable import HollowKnightVision

final class LiveWorldReplayEvaluatorTests: XCTestCase {
    func testThreeReturningPassesCloseAfterReopeningWorldFromDisk() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        let route: [(CGImage, CGPoint)] = [
            (anchorTexture(), .zero),
            // These second samples make the cross-visit relocalization
            // confirmation explicit. The first pass still has to travel away
            // and return before it can close against its own anchor.
            (anchorTexture(), .zero),
            (unrelatedTexture(1), CGPoint(x: 100, y: 0)),
            (unrelatedTexture(2), CGPoint(x: 100, y: 100)),
            (unrelatedTexture(3), CGPoint(x: 0, y: 100)),
            (anchorTexture(), CGPoint(x: 8, y: 0)),
            (anchorTexture(), CGPoint(x: 8, y: 0)),
        ]
        try append(route: route, to: store)

        let outputURL = root.appendingPathComponent("replay-report.json")
        let report = try LiveWorldReplayEvaluator(store: store).evaluate(
            options: LiveWorldReplayOptions(
                repeatCount: 3,
                reopenSnapshotBetweenPasses: true,
                routeMode: .returning
            ),
            outputURL: outputURL
        )

        XCTAssertTrue(report.passed, "\(report)")
        XCTAssertEqual(report.passes.count, 3)
        XCTAssertTrue(report.reopenedSnapshotBetweenPasses)
        XCTAssertTrue(report.passes.allSatisfy(\.recordingReturnsToStart))
        XCTAssertGreaterThanOrEqual(report.passes[0].newLoopClosures, 1)
        XCTAssertTrue(report.passes.allSatisfy(\.closureRequirementMet))
        XCTAssertTrue(report.passes.allSatisfy(\.passed))
        XCTAssertGreaterThan(report.passes[1].savedKeyframeCount, report.passes[0].savedKeyframeCount)
        XCTAssertGreaterThan(report.passes[2].savedKeyframeCount, report.passes[1].savedKeyframeCount)
        XCTAssertGreaterThan(report.passes[1].savedLandmarkCount, report.passes[0].savedLandmarkCount)
        XCTAssertGreaterThan(report.passes[2].savedRevision, report.passes[1].savedRevision)
        XCTAssertTrue(report.failureReasons.isEmpty)
        XCTAssertTrue(report.passes.allSatisfy { $0.globalSearchCount <= $0.observationCount })
        XCTAssertEqual(try JSONDecoder().decode(LiveWorldReplayReport.self, from: Data(contentsOf: outputURL)), report)
    }

    func testCommandArgumentsWriteReport() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        try append(route: [
            (anchorTexture(), .zero),
            (unrelatedTexture(1), CGPoint(x: 100, y: 0)),
        ], to: store)
        let reportURL = root.appendingPathComponent("command-report.json")

        let report = try LiveWorldReplayEvaluator.run(arguments: [
            "--world-replay-session", root.path,
            "--world-replay-report", reportURL.path,
            "--world-replay-repeats", "1",
            "--world-replay-reopen",
        ])

        XCTAssertEqual(report.repeatCount, 1)
        XCTAssertTrue(report.reopenedSnapshotBetweenPasses)
        XCTAssertEqual(report.routeMode, .oneWay)
        XCTAssertEqual(
            try JSONDecoder().decode(LiveWorldReplayReport.self, from: Data(contentsOf: reportURL)),
            report
        )
    }

    func testReturningRouteMustBeExplicitOnCommandLine() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        try append(route: [
            (anchorTexture(), .zero),
            (anchorTexture(), .zero),
        ], to: store)

        let report = try LiveWorldReplayEvaluator.run(arguments: [
            "--world-replay-session", root.path,
            "--world-replay-repeats", "1",
            "--world-replay-returning",
        ])

        XCTAssertEqual(report.routeMode, .returning)
        XCTAssertTrue(report.passes[0].recordingReturnsToStart)
        XCTAssertTrue(report.passes[0].closureRequired)
    }

    func testSerializedSnapshotRelocalizesStoredPNGEvidence() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        try append(route: [
            (anchorTexture(), .zero),
            (unrelatedTexture(1), CGPoint(x: 100, y: 0)),
            (unrelatedTexture(2), CGPoint(x: 100, y: 100)),
            (unrelatedTexture(3), CGPoint(x: 0, y: 100)),
        ], to: store)
        let source = store.manifest.observations
        let frames = try source.map(store.source(for:))
        let original = LiveWorldTracker()
        for index in source.indices {
            _ = try original.ingest(
                observationID: index,
                sourceObservationID: source[index].id,
                timestamp: source[index].timestamp,
                frame: frames[index],
                proposedCameraPose: source[index].cameraPosition,
                solveWidth: source[index].solveWidth
            )
        }

        let persisted = try JSONDecoder().decode(LiveWorldSnapshot.self, from: JSONEncoder().encode(original.snapshot))
        let reopened = try LiveWorldTracker(snapshot: persisted)
        let first = try reopened.ingest(
            observationID: 4, sourceObservationID: source[0].id, timestamp: 10,
            frame: frames[0], proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )
        let closed = try reopened.ingest(
            observationID: 5, sourceObservationID: source[0].id, timestamp: 10.5,
            frame: frames[0], proposedCameraPose: CGPoint(x: 8, y: 0), solveWidth: 320
        )

        XCTAssertTrue(first.review.rejections.contains(.confirmationRequired))
        XCTAssertNotNil(closed.acceptedClosure)
    }

    func testNonReturningRouteReportsNoClosureRequirement() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        try append(route: [
            (anchorTexture(), .zero),
            (unrelatedTexture(1), CGPoint(x: 100, y: 0)),
            (unrelatedTexture(2), CGPoint(x: 220, y: 0)),
        ], to: store)

        let report = try LiveWorldReplayEvaluator(store: store).evaluate()

        XCTAssertTrue(report.passed)
        XCTAssertTrue(report.passes.allSatisfy { !$0.recordingReturnsToStart })
        XCTAssertTrue(report.passes.allSatisfy { !$0.closureRequired })
        XCTAssertTrue(report.passes.allSatisfy { $0.newLoopClosures == 0 && !$0.unexpectedClosure })
    }

    func testInternalRevisitDoesNotMakeOneWayRouteAClosedLoop() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        try append(route: [
            (anchorTexture(), .zero),
            (unrelatedTexture(1), CGPoint(x: 100, y: 0)),
            (unrelatedTexture(2), CGPoint(x: 100, y: 100)),
            (unrelatedTexture(3), CGPoint(x: 0, y: 100)),
            // This local return confirms an ordinary same-room place match.
            (anchorTexture(), CGPoint(x: 8, y: 0)),
            (anchorTexture(), CGPoint(x: 8, y: 0)),
            // The recording then continues to a different endpoint.
            (unrelatedTexture(4), CGPoint(x: 220, y: 0)),
        ], to: store)

        let report = try LiveWorldReplayEvaluator(store: store).evaluate(
            options: LiveWorldReplayOptions(repeatCount: 1)
        )

        XCTAssertFalse(report.passes[0].recordingReturnsToStart)
        XCTAssertFalse(report.passes[0].closureRequired)
        XCTAssertEqual(report.passes[0].newLoopClosures, 1)
        XCTAssertFalse(report.passes[0].unexpectedClosure)
        XCTAssertTrue(report.passes[0].passed)
    }

    func testMatchingEndpointAppearanceAcrossRoomsDoesNotCreateLoopIntent() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        let startRoom = UUID()
        let endRoom = UUID()
        let visitID = UUID()
        let route = [
            (anchorTexture(), startRoom, CGPoint.zero),
            (unrelatedTexture(1), startRoom, CGPoint(x: 100, y: 0)),
            (anchorTexture(), endRoom, CGPoint(x: 220, y: 0)),
            // Two matching endpoint frames would satisfy the matcher's
            // confirmation rule if the evaluator flattened both rooms.
            (anchorTexture(), endRoom, CGPoint(x: 220, y: 0)),
        ]
        for (index, entry) in route.enumerated() {
            _ = try store.append(
                frame: entry.0,
                roomID: entry.1,
                visitID: visitID,
                timestamp: Double(index) * 0.5,
                cameraPosition: entry.2,
                solveWidth: 320,
                excluding: []
            )
        }

        let report = try LiveWorldReplayEvaluator(store: store).evaluate(
            options: LiveWorldReplayOptions(repeatCount: 1)
        )

        XCTAssertTrue(report.passes[0].endpointFeatureMatchFound)
        XCTAssertFalse(report.passes[0].recordingReturnsToStart)
        XCTAssertFalse(report.passes[0].closureRequired)
        XCTAssertEqual(report.passes[0].newLoopClosures, 0)
        XCTAssertFalse(report.passes[0].unexpectedClosure)
    }

    func testDefaultOneWayRouteCannotBeReclassifiedFromVisionCoordinates() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        // Simulate the failure under diagnosis: Vision publishes the terminal
        // frame near its start even though route metadata says it is one-way.
        try append(route: [
            (anchorTexture(), .zero),
            (unrelatedTexture(1), CGPoint(x: 100, y: 0)),
            (unrelatedTexture(2), CGPoint(x: 4, y: 0)),
        ], to: store)

        let report = try LiveWorldReplayEvaluator(store: store).evaluate(
            options: LiveWorldReplayOptions(repeatCount: 1)
        )

        XCTAssertEqual(report.routeMode, .oneWay)
        XCTAssertFalse(report.passes[0].recordingReturnsToStart)
        XCTAssertFalse(report.passes[0].closureRequired)
    }

    func testLoopIntentUsesWiderDetectionBoundButMustMeetTightOptimizedBound() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        try append(route: [
            (anchorTexture(), .zero),
            (unrelatedTexture(1), CGPoint(x: 100, y: 0)),
            (unrelatedTexture(2), CGPoint(x: 100, y: 100)),
            (unrelatedTexture(3), CGPoint(x: 0, y: 100)),
            (anchorTexture(), CGPoint(x: 18, y: 0)),
            (anchorTexture(), CGPoint(x: 18, y: 0)),
        ], to: store)

        let report = try LiveWorldReplayEvaluator(store: store).evaluate(
            options: LiveWorldReplayOptions(
                repeatCount: 1,
                returnThresholdPixels: 12,
                loopDetectionThresholdPixels: 24,
                routeMode: .returning
            )
        )

        XCTAssertTrue(report.passes[0].recordingReturnsToStart)
        XCTAssertTrue(report.passes[0].closureRequired)
        XCTAssertEqual(report.loopDetectionThresholdPixels, 24)
        XCTAssertTrue(report.passes[0].passed)
        XCTAssertLessThanOrEqual(report.passes[0].optimizedReturnError, 12)
    }

    private func append(route: [(CGImage, CGPoint)], to store: SceneSessionStore) throws {
        let roomID = UUID()
        let visitID = UUID()
        for (index, entry) in route.enumerated() {
            _ = try store.append(
                frame: entry.0,
                roomID: roomID,
                visitID: visitID,
                timestamp: Double(index) * 0.5,
                cameraPosition: entry.1,
                solveWidth: 320,
                excluding: []
            )
        }
    }

    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private func anchorTexture() -> CGImage {
        image { x, y in
            let value = (x * 17 + y * 31 + (x * y) % 89) & 255
            return (value, 255 - value, value / 3, 255)
        }
    }

    private func unrelatedTexture(_ seed: Int) -> CGImage {
        image { x, y in
            let value = ((x * (7 + seed) ^ y * (53 + seed * 3)) + seed * 41) & 255
            return (255 - value, value / 5, value, 255)
        }
    }

    private func image(_ color: (Int, Int) -> (Int, Int, Int, Int)) -> CGImage {
        let width = 320, height = 180
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let index = (y * width + x) * 4
                let value = color(x, y)
                bytes[index] = UInt8(value.0)
                bytes[index + 1] = UInt8(value.1)
                bytes[index + 2] = UInt8(value.2)
                bytes[index + 3] = UInt8(value.3)
            }
        }
        return CGContext(
            data: &bytes, width: width, height: height, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!.makeImage()!
    }
}
