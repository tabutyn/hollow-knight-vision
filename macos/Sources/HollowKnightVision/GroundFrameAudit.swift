import CoreGraphics
import Foundation

/// Opt-in, bounded evidence capture for detector/feature tuning. Disabled in
/// ordinary launches; slow disk writes never queue or block the motion worker.
final class GroundFrameAudit {
    private let directory: URL
    private let queue = DispatchQueue(label: "vision.ground-audit", qos: .utility)
    private let lock = NSLock()
    private var writing = false
    private var lastTimestamp: Double?
    private var sequence = 0

    static func launch(arguments: [String] = ProcessInfo.processInfo.arguments) -> GroundFrameAudit? {
        let prefix = "--ground-audit-directory="
        guard let argument = arguments.last(where: { $0.hasPrefix(prefix) }) else { return nil }
        let path = String(argument.dropFirst(prefix.count))
        guard path.hasPrefix("/") else { return nil }
        return GroundFrameAudit(directory: URL(fileURLWithPath: path, isDirectory: true))
    }

    private init?(directory: URL) {
        guard (try? FileManager.default.createDirectory(at: directory,
            withIntermediateDirectories: true)) != nil else { return nil }
        self.directory = directory
    }

    func record(frame: CGImage, timestamp: Double, lines: [CleanFloorLine],
                tracking: GroundHypothesisTrackingResult, tuning: GroundTheoryTuning,
                exclusions: [CGRect]) {
        guard lastTimestamp.map({ timestamp - $0 >= 0.1 }) ?? true else { return }
        lock.lock()
        guard !writing else { lock.unlock(); return }
        writing = true
        lock.unlock()
        lastTimestamp = timestamp
        sequence += 1
        let name = String(format: "%08d", sequence)
        queue.async { [self] in
            defer { lock.lock(); writing = false; lock.unlock() }
            let imageURL = directory.appendingPathComponent(name + ".png")
            guard ImageFileIO.writePNG(frame, to: imageURL) else { return }
            func rect(_ r: CGRect) -> [Double] {
                [Double(r.minX), Double(r.minY), Double(r.width), Double(r.height)]
            }
            let data: [String: Any] = [
                "timestamp": timestamp, "width": frame.width, "height": frame.height,
                "poseVerified": tracking.poseVerified,
                "camera": tracking.cameraPosition.map { [Double($0.x),Double($0.y)] } ?? [],
                "tuning": [tuning.groundThreshold, tuning.minimumSegmentLength,
                           tuning.lineSeparation, tuning.occlusionMergeGap],
                "surfaceArchitecture": GroundLabelParameters.currentSurfaceAlgorithm,
                "exclusions": exclusions.map(rect),
                "lines": lines.map { ["row": $0.row, "x0": $0.xRange.lowerBound,
                    "x1": $0.xRange.upperBound,
                    "evidence": $0.evidenceRanges.map { [$0.lowerBound,$0.upperBound] }] as [String: Any] },
                "reviews": tracking.lineReviews.map { ["id": $0.id, "row": $0.line.row,
                    "x0": $0.line.xRange.lowerBound, "x1": $0.line.xRange.upperBound,
                    "state": $0.state.rawValue, "visibleSeconds": $0.visibleSeconds,
                    "detectedFraction": $0.detectedFraction] as [String: Any] },
                "features": tracking.features.map { ["id": $0.id, "segment": $0.segmentID,
                    "index": $0.sequenceIndex, "rect": rect($0.imageRect),
                    "classification": String(describing: $0.classification)] as [String: Any] },
                "atlasLines": tracking.atlasLines.map { ["id": $0.segmentID,
                    "x0": Double($0.atlasStart.x), "x1": Double($0.atlasEnd.x),
                    "y": Double($0.atlasStart.y)] as [String: Any] },
                "atlasFeatures": tracking.atlasFeatures.map { ["segment": $0.segmentID,
                    "index": $0.sequenceIndex, "x": Double($0.atlasPosition.x),
                    "y": Double($0.atlasPosition.y),
                    "supportedPixels": $0.referenceOpacity.filter { $0 > 0 }.count] as [String: Any] },
            ]
            guard let json = try? JSONSerialization.data(withJSONObject: data, options: [.sortedKeys]) else { return }
            try? json.write(to: directory.appendingPathComponent(name + ".json"), options: .atomic)
        }
    }
}
