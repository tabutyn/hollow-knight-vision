import CoreGraphics
import Foundation

/// Recover only the visual evidence belonging to the actual observation.
enum BotWallReplay {
    struct Row: Encodable {
        let generation: UInt64
        let captureTimestamp: Double
        let features: [Double]
    }
    private struct FrameKey: Hashable {
        let generation: UInt64
        let timestamp: Double
    }
    private struct ArchiveHeader: Decodable { let summary: RecordedVisualFrameArchive }

    static func recover(clip: URL, archive: URL) throws -> [Row] {
        let snippet = try BotSnippet.load(clip)
        if let error = snippet.error { throw BotRuntimeError.incompatible(error) }
        guard snippet.domain == .gameplay else { return [] }
        let header = try JSONDecoder().decode(ArchiveHeader.self,
            from: Data(contentsOf: archive.appendingPathComponent("manifest.json")))
        let reader = try VisualReplayArchiveReader(rootURL: archive.deletingLastPathComponent(),
            summary: header.summary, expectedPathID: snippet.manifest.sourceReplayID, requireComplete: false)
        guard reader.directoryURL.standardizedFileURL == archive.standardizedFileURL else {
            throw VisualReplayArchiveError.invalidDirectory
        }
        let frames = Dictionary(grouping: reader.manifest.frames.filter { $0.fileName != nil }) {
            FrameKey(generation: $0.captureGeneration, timestamp: $0.captureTimestamp)
        }
        let masks = Dictionary(grouping: reader.manifest.groundInputs) {
            FrameKey(generation: $0.captureGeneration, timestamp: $0.captureTimestamp)
        }
        return try snippet.observations.compactMap { obs in
            try autoreleasepool {
                let key = FrameKey(generation: obs.generation, timestamp: obs.captureTimestamp)
                guard obs.isUsable, let matches = frames[key], matches.count == 1, let frame = matches.first else { return nil }
                let f = obs.features
                guard f.prefix(4).allSatisfy({ $0.isFinite && abs($0) <= 4 }) else { return nil }
                guard BotDomain.of(obs) == .gameplay, let inputs = masks[key], inputs.count == 1,
                      let input = inputs.first else { return nil }
                let walls = BotWallEvidence.detect(image: try reader.image(for: frame),
                    excluding: input.excludingRects, knight: input.knightRect, tuning: input.semanticTuning)
                guard let walls else { return nil }
                return Row(generation: obs.generation, captureTimestamp: obs.captureTimestamp,
                    features: BotWallEvidence.features(walls: walls, knightX: f[0], knightY: f[1],
                        width: frame.width, height: frame.height))
            }
        }
    }
}
