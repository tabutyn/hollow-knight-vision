import CoreGraphics
import CryptoKit
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Source evidence, never a pose estimate. All coordinates stay in the original
/// capture basis; telemetry lives in the separate input-path JSON.
struct VisualReplayFrame: Codable, Equatable {
    let index: Int
    let captureTimestamp: TimeInterval
    let capturedAt: TimeInterval
    let captureGeneration: UInt64
    let solveWidth: Double
    let width: Int
    let height: Int
    let renderedGameFrame: Int?
    var fileName: String?
    var sha256: String?
    var issue: String?
}

struct VisualReplayStageEvent: Codable, Equatable {
    enum Stage: String, Codable { case visionStarted, visionCompleted }
    let stage: Stage
    let captureTimestamp: TimeInterval
    let captureGeneration: UInt64
}

/// Inputs produced by visual masking, captured before ground extraction.
/// Persist these so detector/model changes cannot silently change a replay.
struct VisualReplayGroundInput: Codable, Equatable {
    let captureTimestamp: TimeInterval
    let captureGeneration: UInt64
    let excludingRects: [CGRect]
    let knightRect: CGRect?
    let tuning: GroundTheoryTuning
    let semanticTuning: GroundTheoryTuning
    /// Unswept, capture-matched detection; absent in older archives.
    let observedPlayerBounds: CGRect?
    var horizontalDrive: Int?
    var recentJump: Bool?

    init(captureTimestamp: TimeInterval, captureGeneration: UInt64,
         excludingRects: [CGRect], knightRect: CGRect?, tuning: GroundTheoryTuning,
         semanticTuning: GroundTheoryTuning, observedPlayerBounds: CGRect? = nil) {
        self.captureTimestamp = captureTimestamp
        self.captureGeneration = captureGeneration
        self.excludingRects = excludingRects
        self.knightRect = knightRect
        self.tuning = tuning
        self.semanticTuning = semanticTuning
        self.observedPlayerBounds = observedPlayerBounds
    }

    func captureMatchedPlayerBounds(at timestamp: TimeInterval) -> CGRect? {
        captureTimestamp == timestamp ? observedPlayerBounds : nil
    }
}

struct RecordedVisualFrameArchive: Codable, Equatable, Sendable {
    let directoryName: String
    let capturedFrameCount: Int
    let savedFrameCount: Int
    let droppedFrameCount: Int
    let failedFrameCount: Int
    let peakPendingImages: Int
    let complete: Bool
    let issue: String?
}

struct VisualReplayManifest: Codable, Equatable {
    static let currentSchemaVersion = 1
    let schemaVersion: Int
    let pathID: UUID
    let configuration: [String: String]
    let frames: [VisualReplayFrame]
    let stageEvents: [VisualReplayStageEvent]
    let groundInputs: [VisualReplayGroundInput]
    let summary: RecordedVisualFrameArchive
}

enum VisualReplayArchiveError: Error, Equatable {
    case invalidDirectory
    case unsupportedSchema(Int)
    case incompleteArchive
    case invalidFrame(Int)
    case corruptImage(Int)
    case imageEncodingFailed
}

/// Bounded concurrent image writer. Capture never waits for
/// compression or disk. Every rejected frame remains an explicit manifest gap;
/// an incomplete archive cannot silently pass as an exact replay.
final class VisualReplayArchiveWriter: @unchecked Sendable {
    typealias ImageEncoder = (CGImage) throws -> Data
    let directoryURL: URL
    private let pathID: UUID
    private let configuration: [String: String]
    private let maximumPendingImages: Int
    private let encodeImage: ImageEncoder
    private let queue = DispatchQueue(label: "hkv.visual-replay-archive", qos: .userInitiated,
                                      attributes: .concurrent)
    private let lock = NSLock()
    private var frames = [VisualReplayFrame]()
    private var events = [VisualReplayStageEvent]()
    private var groundInputs = [VisualReplayGroundInput]()
    private var pending = 0
    private var peakPending = 0
    private var closed = false
    private var finalSummary: RecordedVisualFrameArchive?

    init(rootURL: URL, pathID: UUID, configuration: [String: String],
         maximumPendingImages: Int = 4,
         encodeImage: @escaping ImageEncoder = VisualReplayArchiveWriter.pngData) {
        self.pathID = pathID
        self.configuration = configuration
        self.maximumPendingImages = max(1, maximumPendingImages)
        self.encodeImage = encodeImage
        directoryURL = rootURL.appendingPathComponent(
            "frames-\(pathID.uuidString.lowercased())", isDirectory: true)
    }

    func append(_ image: CGImage, captureTimestamp: TimeInterval,
                capturedAt: TimeInterval, captureGeneration: UInt64,
                solveWidth: Double, renderedGameFrame: Int?) {
        lock.withLock {
            guard !closed else { return }
            let index = frames.count
            var record = VisualReplayFrame(index: index, captureTimestamp: captureTimestamp,
                capturedAt: capturedAt, captureGeneration: captureGeneration,
                solveWidth: solveWidth, width: image.width, height: image.height,
                renderedGameFrame: renderedGameFrame)
            guard captureTimestamp.isFinite, capturedAt.isFinite,
                  solveWidth.isFinite, solveWidth > 0 else {
                // Invalid times cannot be represented by JSON; store finite
                // placeholders and an explicit failure instead of losing the manifest.
                record = VisualReplayFrame(index: index, captureTimestamp: 0,
                    capturedAt: 0, captureGeneration: captureGeneration, solveWidth: 0,
                    width: image.width, height: image.height,
                    renderedGameFrame: renderedGameFrame, issue: "invalidCapture")
                frames.append(record)
                return
            }
            guard pending < maximumPendingImages else {
                record.issue = "writerBackpressure"
                frames.append(record)
                return
            }
            frames.append(record)
            pending += 1
            peakPending = max(peakPending, pending)
            // Enqueue under the admission lock, so finish cannot overtake a
            // frame that has been admitted but not yet submitted to the writer.
            queue.async { [self] in
                autoreleasepool {
                    do {
                        try FileManager.default.createDirectory(at: directoryURL,
                            withIntermediateDirectories: true)
                        let data = try encodeImage(image)
                        let fileName = String(format: "%08d.png", index)
                        try data.write(to: directoryURL.appendingPathComponent(fileName), options: .atomic)
                        let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
                        lock.withLock {
                            frames[index].fileName = fileName
                            frames[index].sha256 = hash
                            pending -= 1
                        }
                    } catch {
                        lock.withLock {
                            frames[index].issue = "imageWriteFailed: \(error.localizedDescription)"
                            pending -= 1
                        }
                    }
                }
            }
        }
    }

    func recordStage(_ stage: VisualReplayStageEvent.Stage,
                     captureTimestamp: TimeInterval, captureGeneration: UInt64) {
        lock.withLock {
            guard !closed, captureTimestamp.isFinite else { return }
            events.append(VisualReplayStageEvent(stage: stage,
                captureTimestamp: captureTimestamp, captureGeneration: captureGeneration))
        }
    }

    func recordGroundInput(_ input: VisualReplayGroundInput) {
        lock.withLock {
            guard !closed else { return }
            groundInputs.append(input)
        }
    }

    /// Only the recording owner finishes a writer. Drains at most the bounded
    /// image queue; repeated calls return the same durable result.
    func finish(cancelled: Bool = false) -> RecordedVisualFrameArchive {
        if let summary = lock.withLock({ finalSummary }) { return summary }
        lock.withLock { closed = true }
        return queue.sync(flags: .barrier) {
            if let summary = lock.withLock({ finalSummary }) { return summary }
            let contents = lock.withLock { (frames, events, peakPending, groundInputs) }
            let saved = contents.0.filter { $0.fileName != nil }.count
            let dropped = contents.0.filter { $0.issue == "writerBackpressure" }.count
            let failed = contents.0.count - saved - dropped
            func summary(issue: String? = nil) -> RecordedVisualFrameArchive {
                RecordedVisualFrameArchive(directoryName: directoryURL.lastPathComponent,
                    capturedFrameCount: contents.0.count, savedFrameCount: saved,
                    droppedFrameCount: dropped, failedFrameCount: failed,
                    peakPendingImages: contents.2,
                    complete: !cancelled && issue == nil && saved > 0 && dropped == 0 && failed == 0,
                    issue: issue ?? (cancelled ? "cancelled" : nil))
            }
            var result = summary()
            do {
                try FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true)
                let manifest = VisualReplayManifest(schemaVersion: VisualReplayManifest.currentSchemaVersion,
                    pathID: pathID, configuration: configuration, frames: contents.0,
                    stageEvents: contents.1, groundInputs: contents.3, summary: result)
                let encoder = JSONEncoder()
                encoder.outputFormatting = [.sortedKeys]
                try encoder.encode(manifest).write(
                    to: directoryURL.appendingPathComponent("manifest.json"), options: .atomic)
            } catch {
                result = summary(issue: "manifestWriteFailed: \(error.localizedDescription)")
            }
            lock.withLock { finalSummary = result }
            return result
        }
    }

    static func pngData(_ image: CGImage) throws -> Data {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil)
        else { throw VisualReplayArchiveError.imageEncodingFailed }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw VisualReplayArchiveError.imageEncodingFailed }
        return data as Data
    }
}

/// Reads only image evidence and capture configuration. No API accepts Hacker
/// camera positions, scene identifiers, or recorded visual poses as inputs.
struct VisualReplayArchiveReader {
    let directoryURL: URL
    let manifest: VisualReplayManifest

    init(rootURL: URL, summary: RecordedVisualFrameArchive, expectedPathID: UUID,
         requireComplete: Bool = true) throws {
        guard summary.directoryName == "frames-\(expectedPathID.uuidString.lowercased())" else {
            throw VisualReplayArchiveError.invalidDirectory
        }
        directoryURL = rootURL.appendingPathComponent(summary.directoryName, isDirectory: true)
        manifest = try JSONDecoder().decode(VisualReplayManifest.self,
            from: Data(contentsOf: directoryURL.appendingPathComponent("manifest.json")))
        guard manifest.schemaVersion == VisualReplayManifest.currentSchemaVersion else {
            throw VisualReplayArchiveError.unsupportedSchema(manifest.schemaVersion)
        }
        guard manifest.pathID == expectedPathID, manifest.summary == summary,
              manifest.frames.count == summary.capturedFrameCount,
              manifest.frames.enumerated().allSatisfy({ $0.offset == $0.element.index }) else {
            throw VisualReplayArchiveError.invalidDirectory
        }
        let saved = manifest.frames.filter { $0.fileName != nil }.count
        let dropped = manifest.frames.filter { $0.issue == "writerBackpressure" }.count
        guard saved == summary.savedFrameCount, dropped == summary.droppedFrameCount,
              manifest.frames.count - saved - dropped == summary.failedFrameCount else {
            throw VisualReplayArchiveError.invalidDirectory
        }
        if requireComplete && (!summary.complete || saved == 0 || dropped > 0 || summary.failedFrameCount > 0) {
            throw VisualReplayArchiveError.incompleteArchive
        }
    }

    func image(for frame: VisualReplayFrame) throws -> CGImage {
        guard manifest.frames.indices.contains(frame.index), manifest.frames[frame.index] == frame,
              frame.fileName == String(format: "%08d.png", frame.index), frame.issue == nil,
              frame.solveWidth.isFinite, frame.solveWidth > 0,
              frame.captureTimestamp.isFinite, frame.capturedAt.isFinite,
              let name = frame.fileName else { throw VisualReplayArchiveError.invalidFrame(frame.index) }
        let data = try Data(contentsOf: directoryURL.appendingPathComponent(name))
        let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard hash == frame.sha256,
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
              image.width == frame.width, image.height == frame.height else {
            throw VisualReplayArchiveError.corruptImage(frame.index)
        }
        return image
    }
}

struct VisualReplayArchiveInspection: Codable {
    let pathID: UUID
    let archiveAvailable: Bool
    let complete: Bool
    let capturedFrames: Int
    let readableFrames: Int
    let droppedFrames: Int
    let failedFrames: Int
    let groundInputs: Int
    let groundInputsWithImages: Int
    let exactFrameMarkers: Int
    let markerCoveragePercent: Double?
    let firstCaptureTimestamp: Double?
    let lastCaptureTimestamp: Double?
}

extension VisualReplayArchiveReader {
    static func inspect(pathURL: URL) throws -> VisualReplayArchiveInspection {
        let path = try InputPathStore().load(from: pathURL)
        guard let summary = path.visualFrameArchive else {
            return VisualReplayArchiveInspection(pathID: path.id, archiveAvailable: false,
                complete: false, capturedFrames: 0, readableFrames: 0, droppedFrames: 0,
                failedFrames: 0, groundInputs: 0, groundInputsWithImages: 0,
                exactFrameMarkers: 0, markerCoveragePercent: nil,
                firstCaptureTimestamp: nil, lastCaptureTimestamp: nil)
        }
        let reader = try VisualReplayArchiveReader(rootURL: pathURL.deletingLastPathComponent(),
            summary: summary, expectedPathID: path.id, requireComplete: false)
        var readable = 0, markers = 0
        var imageKeys = Set<String>()
        func key(_ timestamp: Double, _ generation: UInt64) -> String {
            "\(generation):\(timestamp.bitPattern)"
        }
        for frame in reader.manifest.frames where frame.fileName != nil {
            try autoreleasepool {
                _ = try reader.image(for: frame)
                readable += 1
                if frame.renderedGameFrame != nil { markers += 1 }
                imageKeys.insert(key(frame.captureTimestamp, frame.captureGeneration))
            }
        }
        let inputs = reader.manifest.groundInputs
        let matched = inputs.filter { imageKeys.contains(key($0.captureTimestamp, $0.captureGeneration)) }.count
        return VisualReplayArchiveInspection(pathID: path.id, archiveAvailable: true,
            complete: summary.complete && readable == summary.capturedFrameCount && matched == inputs.count,
            capturedFrames: summary.capturedFrameCount, readableFrames: readable,
            droppedFrames: summary.droppedFrameCount, failedFrames: summary.failedFrameCount,
            groundInputs: inputs.count, groundInputsWithImages: matched, exactFrameMarkers: markers,
            markerCoveragePercent: readable > 0 ? Double(markers) * 100 / Double(readable) : nil,
            firstCaptureTimestamp: reader.manifest.frames.first?.captureTimestamp,
            lastCaptureTimestamp: reader.manifest.frames.last?.captureTimestamp)
    }
}
