import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Opt-in local diagnostic. Each line describes a completed scan and the actual notebook matches.
final class GameTextScanTrace {
    struct Match: Codable {
        let input, entryID, result: String
        let absorbedEntryIDs: [String]?
    }
    private let directory: URL
    private var sequence = 0

    init?(arguments: [String] = ProcessInfo.processInfo.arguments) {
        guard let flag = arguments.firstIndex(of: "--notebook-trace"),
              arguments.indices.contains(flag + 1), arguments[flag + 1].hasPrefix("/") else { return nil }
        directory = URL(fileURLWithPath: arguments[flag + 1], isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
    }

    // Each store owns its decisions, including when a new profile cancels the previous monitor.
    final class Decisions {
        private(set) var matches = [Match]()
        func matched(input: String, entryID: String, inserted: Bool, changed: Bool, absorbed: [String] = []) {
            matches.append(.init(input: input, entryID: entryID,
                result: inserted ? "inserted" : changed ? "duplicate-updated" : "duplicate", absorbedEntryIDs: absorbed.isEmpty ? nil : absorbed))
        }
    }

    func write(scan: GameTextScan, profile: SaveProfileAtlas?, rows: [RememberedGameText], decisions: Decisions) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        sequence += 1
        let name = String(format: "%06d", sequence)
        struct Raw: Codable { let id: Int; let text: String; let confidence: Float; let rect: [CGFloat] }
        struct Record: Codable {
            let scan: Int, timestamp: Double, recognitionMs: Double, context: String, language: String
            let profile: SaveProfileAtlas?, image: String?, lines: [Raw], decisions: [Match], notebook: [RememberedGameText]
        }
        // Keep source pixels only for the requested game window. Other UI is never captured here.
        let imageName = name + ".jpg"
        let url = directory.appendingPathComponent(imageName)
        guard let target = CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil)
            else { throw CocoaError(.fileWriteUnknown) }
        CGImageDestinationAddImage(target, scan.image, [kCGImageDestinationLossyCompressionQuality: 0.90] as CFDictionary)
        guard CGImageDestinationFinalize(target) else { throw CocoaError(.fileWriteUnknown) }
        let record = Record(scan: sequence, timestamp: scan.capturedAt, recognitionMs: scan.milliseconds,
            context: scan.context, language: scan.language, profile: profile, image: imageName,
            lines: scan.lines.map { Raw(id: $0.id, text: $0.text, confidence: $0.confidence,
                rect: [$0.bounds.minX, $0.bounds.minY, $0.bounds.width, $0.bounds.height]) }, decisions: decisions.matches, notebook: rows)
        try JSONEncoder().encode(record).write(to: directory.appendingPathComponent(name + ".json"), options: .atomic)
    }
}
