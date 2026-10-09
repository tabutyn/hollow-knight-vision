import CoreGraphics
import Foundation

/// Opt-in raw evidence for deterministic offline solver comparisons. One
/// pending disk write at most; capture never waits for disk or image encoding.
final class RawGroundFrameAudit {
    private let directory: URL
    private let queue = DispatchQueue(label: "vision.raw-ground-audit", qos: .utility)
    private let lock = NSLock()
    private var writing = false
    private var sequence = 0
    private var skipped = 0
    private var lastBankTimestamp: Double?

    static func launch(arguments: [String] = ProcessInfo.processInfo.arguments) -> RawGroundFrameAudit? {
        let prefix = "--ground-raw-audit-directory="
        guard let argument = arguments.last(where: { $0.hasPrefix(prefix) }) else { return nil }
        let path = String(argument.dropFirst(prefix.count))
        guard path.hasPrefix("/") else { return nil }
        return RawGroundFrameAudit(directory: URL(fileURLWithPath: path, isDirectory: true))
    }

    private init?(directory: URL) {
        guard (try? FileManager.default.createDirectory(at: directory,
            withIntermediateDirectories: true)) != nil else { return nil }
        self.directory = directory
    }

    func record(pixels: [UInt8], width: Int, height: Int, timestamp: Double,
                lines: [CleanFloorLine], exclusions: [CGRect],
                tracking: GroundHypothesisTrackingResult,
                featureBank: @autoclosure () -> [String: Any]) {
        guard pixels.count == width * height, sequence < 6_000 else { return }
        lock.lock()
        guard !writing else { skipped += 1; lock.unlock(); return }
        writing = true
        let omitted = skipped
        lock.unlock()
        sequence += 1
        let name = String(format: "%08d", sequence)
        let bank: [String: Any]?
        if lastBankTimestamp.map({ timestamp - $0 >= 1 }) ?? true {
            bank = featureBank()
            lastBankTimestamp = timestamp
        } else {
            bank = nil
        }
        queue.async { [self] in
            defer { lock.lock(); writing = false; lock.unlock() }
            var data: [String: Any] = [
                "timestamp": timestamp, "width": width, "height": height,
                "skippedWrites": omitted, "poseVerified": tracking.poseVerified,
                "camera": tracking.cameraPosition.map { [Double($0.x), Double($0.y)] } ?? [],
                "delta": tracking.cameraTranslation.map { [Double($0.dx), Double($0.dy)] } ?? [],
                "correction": tracking.globalCorrection.map { [Double($0.dx), Double($0.dy)] } ?? [],
                "exclusions": exclusions.map { [Double($0.minX), Double($0.minY), Double($0.width), Double($0.height)] },
                "lines": lines.map { ["row": $0.row, "x0": $0.xRange.lowerBound,
                    "x1": $0.xRange.upperBound,
                    "evidence": $0.evidenceRanges.map { [$0.lowerBound, $0.upperBound] }] as [String: Any] }
            ]
            do {
                if let bank {
                    let bankName = name + ".bank"
                    let encoded = try JSONSerialization.data(withJSONObject: bank, options: [.sortedKeys])
                    try encoded.write(to: directory.appendingPathComponent(bankName))
                    data["featureBank"] = bankName
                }
                let json = try JSONSerialization.data(withJSONObject: data, options: [.sortedKeys])
                try Data(pixels).write(to: directory.appendingPathComponent(name + ".luma"))
                try json.write(to: directory.appendingPathComponent(name + ".json"), options: .atomic)
            } catch {
                // Missing metadata makes incomplete samples unambiguously absent.
                try? FileManager.default.removeItem(at: directory.appendingPathComponent(name + ".luma"))
            }
        }
    }
}
