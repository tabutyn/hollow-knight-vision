import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class VisualReplayArchiveTests: XCTestCase {
    func testPartialReplayRequiresExplicitOptInAndRetainsGapIdentityAndTiming() throws {
        let frames = (0..<4).map { index in
            VisualReplayFrame(index: index, captureTimestamp: 10 + Double(index) * 0.05,
                capturedAt: 10 + Double(index) * 0.05, captureGeneration: 1, solveWidth: 640,
                width: 640, height: 360, renderedGameFrame: index,
                fileName: index == 1 ? nil : String(format: "%08d.png", index),
                sha256: index == 1 ? nil : "hash", issue: index == 1 ? "writerBackpressure" : nil)
        }
        XCTAssertThrowsError(try VisualProductionReplay.selectFrames(frames, limit: nil, allowIncomplete: false))
        let partial = try VisualProductionReplay.selectFrames(frames, limit: 3, allowIncomplete: true)
        XCTAssertEqual(partial.selectedCount, 3)
        XCTAssertEqual(partial.missingIndices, [1])
        XCTAssertEqual(partial.available.map(\.index), [0, 2])
        XCTAssertEqual(partial.available.map(\.captureTimestamp), [10, 10.1])
        XCTAssertThrowsError(try VisualProductionReplay.selectFrames([frames[1]], limit: nil, allowIncomplete: true))
    }

    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return root
    }

    private func image() throws -> CGImage {
        let pixels = (0..<32*24).map { UInt8(($0 * 17) % 256) }
        return try XCTUnwrap(CGImage(width: 32, height: 24, bitsPerComponent: 8,
            bitsPerPixel: 8, bytesPerRow: 32, space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: 0),
            provider: CGDataProvider(data: Data(pixels) as CFData)!,
            decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }

    private func append(_ image: CGImage, to writer: VisualReplayArchiveWriter, at time: Double = 100) {
        writer.append(image, captureTimestamp: time, capturedAt: time + 0.01,
            captureGeneration: 7, solveWidth: 1920, renderedGameFrame: 42)
    }

    func testLosslessReplayKeepsSourceBasisAndStageOrder() throws {
        let root = try temporaryRoot(), id = UUID(), frame = try image()
        let writer = VisualReplayArchiveWriter(rootURL: root, pathID: id,
            configuration: ["captureWidth": "640"])
        append(frame, to: writer)
        writer.recordStage(.visionStarted, captureTimestamp: 100, captureGeneration: 7)
        let groundInput = VisualReplayGroundInput(captureTimestamp: 100, captureGeneration: 7,
            excludingRects: [CGRect(x: 1, y: 2, width: 3, height: 4)], knightRect: nil,
            tuning: .default, semanticTuning: .semanticDefault,
            observedPlayerBounds: CGRect(x: 10, y: 3, width: 5, height: 8))
        writer.recordGroundInput(groundInput)
        writer.recordStage(.visionCompleted, captureTimestamp: 100, captureGeneration: 7)
        let summary = writer.finish()
        XCTAssertTrue(summary.complete)
        XCTAssertEqual(summary.savedFrameCount, 1)
        let reader = try VisualReplayArchiveReader(rootURL: root, summary: summary, expectedPathID: id)
        let record = try XCTUnwrap(reader.manifest.frames.first)
        XCTAssertEqual(record.solveWidth, 1920)
        XCTAssertEqual(record.captureTimestamp, 100)
        XCTAssertEqual(record.captureGeneration, 7)
        XCTAssertEqual(record.renderedGameFrame, 42)
        XCTAssertEqual(reader.manifest.stageEvents.map(\.stage), [.visionStarted, .visionCompleted])
        XCTAssertEqual(reader.manifest.configuration, ["captureWidth": "640"])
        XCTAssertEqual(reader.manifest.groundInputs, [groundInput])
        XCTAssertEqual(groundInput.captureMatchedPlayerBounds(at: 100), groundInput.observedPlayerBounds)
        XCTAssertNil(groundInput.captureMatchedPlayerBounds(at: 100.01),
                     "A recently borrowed exclusion mask cannot supply contact for a different capture")
        var legacy = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(groundInput)) as? [String: Any])
        legacy.removeValue(forKey: "observedPlayerBounds")
        XCTAssertNil(try JSONDecoder().decode(VisualReplayGroundInput.self,
            from: JSONSerialization.data(withJSONObject: legacy)).observedPlayerBounds)
        let restored = try reader.image(for: record)
        XCTAssertEqual(GroundPixels.luma(restored, width: 32, height: 24),
                       GroundPixels.luma(frame, width: 32, height: 24))
        // A late capture or delayed worker from the old session must not mutate
        // a recording that has already been handed to the evaluator.
        append(frame, to: writer, at: 101)
        writer.recordStage(.visionStarted, captureTimestamp: 101, captureGeneration: 7)
        XCTAssertEqual(writer.finish(), summary)
        let reopened = try VisualReplayArchiveReader(rootURL: root, summary: summary, expectedPathID: id)
        XCTAssertEqual(reopened.manifest, reader.manifest)
    }

    func testSlowDiskBoundsImagesAndReportsMissingFrames() throws {
        let root = try temporaryRoot(), id = UUID(), frame = try image()
        let started = DispatchSemaphore(value: 0), release = DispatchSemaphore(value: 0)
        let writer = VisualReplayArchiveWriter(rootURL: root, pathID: id, configuration: [:],
            maximumPendingImages: 1) { image in
                started.signal()
                guard release.wait(timeout: .now() + 5) == .success else {
                    throw VisualReplayArchiveError.imageEncodingFailed
                }
                return try VisualReplayArchiveWriter.pngData(image)
            }
        append(frame, to: writer)
        XCTAssertEqual(started.wait(timeout: .now() + 5), .success)
        for i in 1...20 { append(frame, to: writer, at: 100 + Double(i)) }
        release.signal()
        let summary = writer.finish()
        XCTAssertFalse(summary.complete)
        XCTAssertEqual(summary.capturedFrameCount, 21)
        XCTAssertEqual(summary.savedFrameCount, 1)
        XCTAssertEqual(summary.droppedFrameCount, 20)
        XCTAssertEqual(summary.peakPendingImages, 1)
        XCTAssertThrowsError(try VisualReplayArchiveReader(rootURL: root, summary: summary,
            expectedPathID: id)) { XCTAssertEqual($0 as? VisualReplayArchiveError, .incompleteArchive) }
        let reader = try VisualReplayArchiveReader(rootURL: root, summary: summary,
            expectedPathID: id, requireComplete: false)
        XCTAssertEqual(reader.manifest.frames.map(\.index), Array(0...20))
        XCTAssertEqual(reader.manifest.frames.last?.captureTimestamp, 120)
        XCTAssertEqual(reader.manifest.frames.last?.issue, "writerBackpressure")
    }

    func testConcurrentEncodingPreservesCaptureOrderAndFinishWaitsForAllImages() throws {
        let root = try temporaryRoot(), id = UUID(), frame = try image()
        let entered = DispatchSemaphore(value: 0), release = DispatchSemaphore(value: 0)
        let writer = VisualReplayArchiveWriter(rootURL: root, pathID: id, configuration: [:],
            maximumPendingImages: 4) { image in
                entered.signal()
                guard release.wait(timeout: .now() + 5) == .success else {
                    throw VisualReplayArchiveError.imageEncodingFailed
                }
                return try VisualReplayArchiveWriter.pngData(image)
            }
        for i in 0..<4 { append(frame, to: writer, at: 100 + Double(i)) }
        // All four must enter before any encoder is released. A serial writer
        // would stall on the second wait and fail this throughput contract.
        for _ in 0..<4 { XCTAssertEqual(entered.wait(timeout: .now() + 5), .success) }
        for _ in 0..<4 { release.signal() }
        let summary = writer.finish()
        XCTAssertTrue(summary.complete)
        XCTAssertEqual(summary.peakPendingImages, 4)
        let reader = try VisualReplayArchiveReader(rootURL: root, summary: summary, expectedPathID: id)
        XCTAssertEqual(reader.manifest.frames.map(\.captureTimestamp), [100, 101, 102, 103])
        for record in reader.manifest.frames { XCTAssertEqual(try reader.image(for: record).width, 32) }
    }

    func testWriteFailuresAndCancellationCannotLookComplete() throws {
        let root = try temporaryRoot(), frame = try image()
        let failed = VisualReplayArchiveWriter(rootURL: root, pathID: UUID(), configuration: [:]) { _ in
            throw VisualReplayArchiveError.imageEncodingFailed
        }
        append(frame, to: failed)
        let failure = failed.finish()
        XCTAssertFalse(failure.complete)
        XCTAssertEqual(failure.failedFrameCount, 1)
        XCTAssertEqual(failure.savedFrameCount, 0)
        let cancelled = VisualReplayArchiveWriter(rootURL: root, pathID: UUID(), configuration: [:])
        append(frame, to: cancelled)
        let cancellation = cancelled.finish(cancelled: true)
        XCTAssertFalse(cancellation.complete)
        XCTAssertEqual(cancellation.issue, "cancelled")
        let empty = VisualReplayArchiveWriter(rootURL: root, pathID: UUID(), configuration: [:])
        XCTAssertFalse(empty.finish().complete)
    }

    func testCorruptedEvidenceIsRejected() throws {
        let root = try temporaryRoot(), id = UUID()
        let writer = VisualReplayArchiveWriter(rootURL: root, pathID: id, configuration: [:])
        append(try image(), to: writer)
        let summary = writer.finish()
        let reader = try VisualReplayArchiveReader(rootURL: root, summary: summary, expectedPathID: id)
        let frame = try XCTUnwrap(reader.manifest.frames.first)
        try Data([0, 1, 2]).write(to: reader.directoryURL.appendingPathComponent(frame.fileName!))
        XCTAssertThrowsError(try reader.image(for: frame)) {
            XCTAssertEqual($0 as? VisualReplayArchiveError, .corruptImage(0))
        }
        XCTAssertThrowsError(try VisualReplayArchiveReader(rootURL: root, summary: summary, expectedPathID: UUID()))
    }

    func testRecorderLinksArchiveAndKeepsLegacyPathsReadable() throws {
        let root = try temporaryRoot(), frame = try image()
        let recorder = InputPathRecorder()
        XCTAssertTrue(recorder.start(at: 100, visualArchiveRootURL: root))
        recorder.recordVisualFrame(frame, captureTimestamp: 100.1, capturedAt: 100.11,
            captureGeneration: 1, solveWidth: 640, renderedGameFrame: nil)
        let path = try XCTUnwrap(recorder.stop(at: 101))
        let summary = try XCTUnwrap(path.visualFrameArchive)
        XCTAssertTrue(summary.complete)
        let reader = try VisualReplayArchiveReader(rootURL: root, summary: summary, expectedPathID: path.id)
        XCTAssertNil(reader.manifest.frames.first?.renderedGameFrame)
        let encoded = try JSONEncoder().encode(path)
        var legacy = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        legacy.removeValue(forKey: "visualFrameArchive")
        let decoded = try JSONDecoder().decode(RecordedInputPath.self,
            from: JSONSerialization.data(withJSONObject: legacy))
        XCTAssertNil(decoded.visualFrameArchive)
        XCTAssertEqual(decoded.id, path.id)
        XCTAssertTrue(recorder.start(at: 200, visualArchiveRootURL: root))
        recorder.recordVisualFrame(frame, captureTimestamp: 200.1, capturedAt: 200.11,
            captureGeneration: 2, solveWidth: 640, renderedGameFrame: 5)
        let next = try XCTUnwrap(recorder.stop(at: 201))
        XCTAssertNotEqual(next.visualFrameArchive?.directoryName, summary.directoryName)
        XCTAssertEqual(try reader.image(for: reader.manifest.frames[0]).width, 32)
    }

    func testInvalidTimingStillProducesReadableFailureManifest() throws {
        let root = try temporaryRoot(), id = UUID(), frame = try image()
        let writer = VisualReplayArchiveWriter(rootURL: root, pathID: id, configuration: [:])
        append(frame, to: writer, at: .nan)
        let summary = writer.finish()
        XCTAssertFalse(summary.complete)
        XCTAssertEqual(summary.failedFrameCount, 1)
        XCTAssertNil(summary.issue)
        let reader = try VisualReplayArchiveReader(rootURL: root, summary: summary,
            expectedPathID: id, requireComplete: false)
        XCTAssertEqual(reader.manifest.frames.first?.issue, "invalidCapture")
    }
}
