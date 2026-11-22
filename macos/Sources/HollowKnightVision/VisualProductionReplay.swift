import CoreGraphics
import CryptoKit
import Foundation

struct VisualProductionReplayReport: Codable {
    let schemaVersion: Int
    let sourcePathID: UUID
    let frames: Int
    let archiveFrameCount: Int
    let selectedFrameCount: Int
    let missingFrameIndices: [Int]
    let archiveComplete: Bool
    let maskInputs: Int
    let elapsedSeconds: Double
    let framesPerSecond: Double
    let visualDigest: String
    let coarseVisualDigest: String
    let worldDigest: String
    let worldDirectory: String
    let outputPath: String
    let worldSnapshotPath: String
    let notes: [String]
}

enum VisualProductionReplayError: Error {
    case invalidArguments
    case missingArchive
    case outputAlreadyExists
    case missingOutput
}

/// Offline adapter for the production LiveCaptureModel, not another odometry
/// implementation. Only pixels, visual masks/tuning and the user's recorded
/// inputs enter the model. Reference telemetry is deliberately never copied to
/// the output; independent scoring joins it after tracking has finished.
enum VisualProductionReplay {
    struct FrameSelection {
        let available: [VisualReplayFrame]
        let missingIndices: [Int]
        let selectedCount: Int
    }

    static func selectFrames(_ frames: [VisualReplayFrame], limit: Int?, allowIncomplete: Bool) throws -> FrameSelection {
        let selected = Array(frames.prefix(limit ?? frames.count))
        let missing = selected.filter { $0.fileName == nil }.map(\.index)
        guard allowIncomplete || missing.isEmpty else { throw VisualReplayArchiveError.incompleteArchive }
        let available = selected.filter { $0.fileName != nil }
        guard !available.isEmpty else { throw VisualProductionReplayError.missingArchive }
        return .init(available: available, missingIndices: missing, selectedCount: selected.count)
    }

    private struct DigestSample: Codable {
        let captureTimestamp: Double
        let x: Double?
        let y: Double?
        let source: String?
        let measured: Bool
        let roomID: Int?
    }

    static func run(arguments: [String]) throws -> VisualProductionReplayReport {
        var pathURL: URL?, outputURL: URL?
        var limit: Int?
        var allowIncomplete = false
        var index = 0
        while index < arguments.count {
            if arguments[index] == "--visual-replay-allow-incomplete" {
                allowIncomplete = true
                index += 1
                continue
            }
            guard index + 1 < arguments.count else { throw VisualProductionReplayError.invalidArguments }
            let value = arguments[index + 1]
            switch arguments[index] {
            case "--replay-visual-archive": pathURL = URL(fileURLWithPath: value)
            case "--visual-replay-output": outputURL = URL(fileURLWithPath: value, isDirectory: true)
            case "--visual-replay-frame-limit":
                guard let parsed = Int(value), parsed > 0 else { throw VisualProductionReplayError.invalidArguments }
                limit = parsed
            default: throw VisualProductionReplayError.invalidArguments
            }
            index += 2
        }
        guard let pathURL, let outputURL else { throw VisualProductionReplayError.invalidArguments }
        let source = try InputPathStore().load(from: pathURL)
        guard let summary = source.visualFrameArchive else { throw VisualProductionReplayError.missingArchive }
        let reader = try VisualReplayArchiveReader(rootURL: pathURL.deletingLastPathComponent(),
            summary: summary, expectedPathID: source.id, requireComplete: !allowIncomplete)
        // Input-event offsets use receipt uptime, not screenshot presentation
        // time. The first saved capture supplies both clocks; no pose is read.
        guard let first = reader.manifest.frames.first,
              let firstCoarse = source.coarseMotionTrace.first else { throw VisualProductionReplayError.missingArchive }
        let startTimestamp = first.capturedAt - firstCoarse.offset
        return try evaluate(reader: reader, events: source.events, createdAt: source.createdAt,
                            startTimestamp: startTimestamp, outputURL: outputURL, frameLimit: limit,
                            allowIncomplete: allowIncomplete)
    }

    static func evaluate(reader: VisualReplayArchiveReader, events: [RecordedInputPathEvent],
                         createdAt: Date, startTimestamp: Double, outputURL: URL,
                         frameLimit: Int? = nil, allowIncomplete: Bool = false) throws -> VisualProductionReplayReport {
        let selection = try selectFrames(reader.manifest.frames, limit: frameLimit, allowIncomplete: allowIncomplete)
        guard !FileManager.default.fileExists(atPath: outputURL.path) else {
            throw VisualProductionReplayError.outputAlreadyExists
        }
        try FileManager.default.createDirectory(at: outputURL, withIntermediateDirectories: true)
        let worldURL = outputURL.appendingPathComponent("world", isDirectory: true)
        let model = LiveCaptureModel(visualReplayMode: true, worldRootOverride: worldURL)
        let frames = selection.available
        let inputByTimestamp = Dictionary(reader.manifest.groundInputs.map { ($0.captureTimestamp, $0) },
            uniquingKeysWith: { _, latest in latest })
        let orderedEvents = events.enumerated().sorted {
            $0.element.offset == $1.element.offset ? $0.offset < $1.offset : $0.element.offset < $1.element.offset
        }.map(\.element)
        let resultLock = NSLock()
        var outcome: Result<(RecordedInputPath, LiveWorldSnapshot, Double), Error>?
        // The real model publishes onto main, including completion callbacks.
        // Keep the run loop live while the bounded worker feeds its queues.
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                let began = ProcessInfo.processInfo.systemUptime
                model.prepareVisualReplay(startTimestamp: startTimestamp, createdAt: createdAt,
                    sourcePathID: reader.manifest.pathID)
                var eventIndex = 0
                var held = Set<RecordedGameButton>()
                var recentInput: VisualReplayGroundInput?
                for (index, frame) in frames.enumerated() {
                    try autoreleasepool {
                        let offset = frame.capturedAt - startTimestamp
                        var jumpDuringFrame = held.contains(.z)
                        while eventIndex < orderedEvents.count, orderedEvents[eventIndex].offset <= offset {
                            let event = orderedEvents[eventIndex]
                            if event.button == .z && event.transition == .pressed { jumpDuringFrame = true }
                            if event.transition == .pressed { held.insert(event.button) }
                            else { held.remove(event.button) }
                            eventIndex += 1
                        }
                        let direction: VisualRoomDirection? = held.contains(.left) == held.contains(.right)
                            ? nil : (held.contains(.left) ? .left : .right)
                        if let input = inputByTimestamp[frame.captureTimestamp] { recentInput = input }
                        // Never extend a stale actor mask across a blackout.
                        let input = recentInput.flatMap {
                            frame.captureTimestamp - $0.captureTimestamp <= 0.1 ? $0 : nil
                        }
                        let image = try reader.image(for: frame)
                        model.consumeVisualReplayFrame(image, record: frame, input: input,
                            direction: direction, jumpInput: jumpDuringFrame,
                            worldTimestamp: createdAt.timeIntervalSinceReferenceDate
                                + frame.captureTimestamp - startTimestamp)
                    }
                    if (index + 1).isMultiple(of: 500) {
                        fputs("visual replay: \(index + 1)/\(frames.count) frames\n", stderr)
                    }
                }
                let (path, snapshot) = model.finishVisualReplay(
                    at: frames.last?.capturedAt ?? startTimestamp)
                guard let path else { throw VisualProductionReplayError.missingOutput }
                let result = (path, snapshot, ProcessInfo.processInfo.systemUptime - began)
                resultLock.withLock { outcome = .success(result) }
            } catch { resultLock.withLock { outcome = .failure(error) } }
        }
        while resultLock.withLock({ outcome == nil }) {
            RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.01))
        }
        let (path, snapshot, elapsed) = try resultLock.withLock { try outcome!.get() }
        let pathURL = try InputPathStore(rootURL: outputURL).save(path)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let snapshotURL = outputURL.appendingPathComponent("world-snapshot.json")
        let snapshotData = try encoder.encode(snapshot)
        try snapshotData.write(to: snapshotURL, options: .atomic)
        let samples = path.trackingSamples.map {
            DigestSample(captureTimestamp: $0.captureTimestamp, x: $0.publishedCameraX,
                y: $0.publishedCameraY, source: $0.poseSource,
                measured: GroundMotionEvidence.isMeasured($0), roomID: $0.roomID)
        }
        let digest = SHA256.hash(data: try encoder.encode(samples)).map { String(format: "%02x", $0) }.joined()
        let diagnosticEncoder = InputPathStore.diagnosticEncoder()
        diagnosticEncoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let coarseDigest = SHA256.hash(data: try diagnosticEncoder.encode(path.coarseMotionTrace))
            .map { String(format: "%02x", $0) }.joined()
        let worldDigest = SHA256.hash(data: snapshotData).map { String(format: "%02x", $0) }.joined()
        let report = VisualProductionReplayReport(schemaVersion: 3, sourcePathID: reader.manifest.pathID,
            frames: frames.count, archiveFrameCount: reader.manifest.frames.count,
            selectedFrameCount: selection.selectedCount, missingFrameIndices: selection.missingIndices,
            archiveComplete: reader.manifest.summary.complete,
            maskInputs: frames.filter { inputByTimestamp[$0.captureTimestamp] != nil }.count,
            elapsedSeconds: elapsed,
            framesPerSecond: Double(frames.count) / max(0.001, elapsed), visualDigest: digest,
            coarseVisualDigest: coarseDigest, worldDigest: worldDigest,
            worldDirectory: worldURL.path, outputPath: pathURL.path, worldSnapshotPath: snapshotURL.path,
            notes: ["Production capture, vision, ground, fallback and atlas implementations; serial replay schedule.",
                    "Global ground searches complete synchronously; reference telemetry and recorded poses are never inputs.",
                    "Gameplay-only route; archived visual masks/tuning replace mutable local stencil/model assets.",
                    "Performance excludes display rendering; compare with live validation separately.",
                    "Missing selected images: \(selection.missingIndices.count). Original timestamps and frame indices are retained; no image or pose is synthesized for a missing capture."])
        try encoder.encode(report).write(to: outputURL.appendingPathComponent("report.json"), options: .atomic)
        return report
    }
}
