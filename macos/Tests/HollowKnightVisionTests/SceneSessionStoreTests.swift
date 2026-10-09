import CoreGraphics
import Dispatch
import Foundation
import ImageIO
import XCTest
@testable import HollowKnightVision

final class SceneSessionStoreTests: XCTestCase {
    func testReopenPreservesObservationIDs() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        let first = try store.append(frame: image(red: 0, green: 0, blue: 0, alpha: 255), roomID: UUID(), visitID: UUID(), timestamp: 1, cameraPosition: .zero, solveWidth: 640, excluding: [])
        let reopened = try SceneSessionStore(rootURL: root)
        let second = try reopened.append(frame: image(red: 1, green: 2, blue: 3, alpha: 255), roomID: UUID(), visitID: UUID(), timestamp: 2, cameraPosition: .zero, solveWidth: 640, excluding: [])
        XCTAssertEqual(first.id, 0)
        XCTAssertEqual(second.id, 1)
        XCTAssertEqual(reopened.manifest.observations.map(\.id), [0, 1])
    }

    func testPNGSourcePreservesBlackAndAlphaPixels() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        let observation = try store.append(frame: image(red: 0, green: 0, blue: 0, alpha: 96), roomID: UUID(), visitID: UUID(), timestamp: 1, cameraPosition: .zero, solveWidth: 640, excluding: [])
        XCTAssertEqual(pixel(in: try store.source(for: observation)), [0, 0, 0, 96])
    }

    func testAtomicSceneRevisionRejectsStaleWriter() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let writer = try SceneSessionStore(rootURL: root)
        let staleWriter = try SceneSessionStore(rootURL: root)
        XCTAssertTrue(try writer.saveScene(data: Data([1, 2]), expectedRevision: 0, newRevision: 1))
        XCTAssertFalse(try staleWriter.saveScene(data: Data([9]), expectedRevision: 0, newRevision: 2))
        XCTAssertEqual(try writer.loadScene(), Data([1, 2]))
        XCTAssertEqual(writer.sceneRevision, 1)
    }

    func testConcurrentSceneCompareAndSwapCommitsExactlyOneWriter() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        _ = try SceneSessionStore(rootURL: root)

        let ready = DispatchGroup()
        let finished = DispatchGroup()
        let start = DispatchSemaphore(value: 0)
        let results = ConcurrentSceneSaveResults()
        let payloads = [Data([1]), Data([2])]

        for index in payloads.indices {
            ready.enter()
            finished.enter()
            DispatchQueue.global(qos: .userInitiated).async {
                ready.leave()
                start.wait()
                defer { finished.leave() }
                do {
                    let store = try SceneSessionStore(rootURL: root)
                    results.record(try store.saveScene(data: payloads[index], expectedRevision: 0, newRevision: 1), at: index)
                } catch {
                    results.record(error, at: index)
                }
            }
        }

        XCTAssertEqual(ready.wait(timeout: .now() + 1), .success)
        start.signal()
        start.signal()
        XCTAssertEqual(finished.wait(timeout: .now() + 5), .success)

        XCTAssertTrue(results.errors.isEmpty)
        XCTAssertEqual(results.values.compactMap { $0 }.filter { $0 }.count, 1)
        XCTAssertEqual(results.values.compactMap { $0 }.filter { !$0 }.count, 1)

        let committedIndex = try XCTUnwrap(results.values.firstIndex(of: true))
        let verifier = try SceneSessionStore(rootURL: root)
        XCTAssertEqual(try verifier.loadScene(), payloads[committedIndex])
        XCTAssertEqual(verifier.sceneRevision, 1)
        XCTAssertThrowsError(try verifier.saveScene(data: Data(), expectedRevision: 1, newRevision: 1)) { error in
            XCTAssertEqual(error as? SceneSessionStoreError, .invalidRevision)
        }
    }

    func testMissingSourceThrowsExplicitError() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        let observation = try store.append(frame: image(red: 0, green: 0, blue: 0, alpha: 255), roomID: UUID(), visitID: UUID(), timestamp: 1, cameraPosition: .zero, solveWidth: 640, excluding: [])
        try FileManager.default.removeItem(at: root.appendingPathComponent(observation.imagePath))
        XCTAssertThrowsError(try store.source(for: observation)) { error in
            XCTAssertEqual(error as? SceneSessionStoreError, .missingSource(observation.imagePath))
        }
    }

    func testUnsupportedManifestAndPathTraversalAreRejected() throws {
        let root = try temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let unsupported = SessionManifest(schemaVersion: 99)
        try JSONEncoder().encode(unsupported).write(to: root.appendingPathComponent("manifest.json"))
        XCTAssertThrowsError(try SceneSessionStore(rootURL: root)) { error in
            XCTAssertEqual(error as? SceneSessionStoreError, .unsupportedSchema(99))
        }
        let unsafe = SessionManifest(observations: [StoredFrameObservation(id: 0, roomID: UUID(), visitID: UUID(), timestamp: 0, imagePath: "../escape.png", cameraPosition: .zero, solveWidth: 1, excludedRects: [], poseRevision: 0)], nextObservationID: 1)
        try JSONEncoder().encode(unsafe).write(to: root.appendingPathComponent("manifest.json"))
        XCTAssertThrowsError(try SceneSessionStore(rootURL: root)) { error in
            XCTAssertEqual(error as? SceneSessionStoreError, .unsafeImagePath("../escape.png"))
        }
    }

    func testTmpSymlinkRootAcceptsCanonicalEvidencePath() throws {
        let root = URL(fileURLWithPath: "/tmp", isDirectory: true)
            .appendingPathComponent("hkv-session-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = try SceneSessionStore(rootURL: root)
        let observation = try store.append(
            frame: image(red: 0, green: 0, blue: 0, alpha: 255), roomID: UUID(), visitID: UUID(),
            timestamp: 1, cameraPosition: .zero, solveWidth: 160, excluding: []
        )
        XCTAssertEqual(observation.imagePath, "frames/00000000.png")
        XCTAssertNotNil(try store.source(for: observation))
    }

    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private func image(red: UInt8, green: UInt8, blue: UInt8, alpha: UInt8) -> CGImage {
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let context = CGContext(data: nil, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4, space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(red: CGFloat(red) / 255, green: CGFloat(green) / 255, blue: CGFloat(blue) / 255, alpha: CGFloat(alpha) / 255)
        context.fill(CGRect(x: 0, y: 0, width: 1, height: 1))
        return context.makeImage()!
    }

    private func pixel(in image: CGImage) -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: 4)
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let context = CGContext(data: &bytes, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4, space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.draw(image, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        return bytes
    }
}

private final class ConcurrentSceneSaveResults: @unchecked Sendable {
    private let lock = NSLock()
    private var storedValues = [Bool?](repeating: nil, count: 2)
    private var storedErrors = [String]()

    var values: [Bool?] {
        lock.withLock { storedValues }
    }

    var errors: [String] {
        lock.withLock { storedErrors }
    }

    func record(_ value: Bool, at index: Int) {
        lock.withLock { storedValues[index] = value }
    }

    func record(_ error: Error, at index: Int) {
        lock.withLock {
            storedErrors.append("writer \(index): \(error)")
        }
    }
}
