import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

/// Inspect measured floor candidates around the loading cut, without using
/// poses or reference telemetry to choose a floor. This is not a floor-connection
/// acceptance test: it exposes absent/ambiguous evidence before adding constraints.
final class DoorwayFloorReplayAuditTests: XCTestCase {
    func testArchivedDoorwayFloorCandidates() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let inputPath = environment["HKV_DOOR_FLOOR_AUDIT_INPUT"],
              let outputPath = environment["HKV_DOOR_FLOOR_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set HKV_DOOR_FLOOR_AUDIT_INPUT/OUTPUT to inspect captured doorway floors")
        }
        let pathURL = URL(fileURLWithPath: inputPath)
        struct Source: Decodable { let id: UUID; let visualFrameArchive: RecordedVisualFrameArchive }
        let source = try JSONDecoder().decode(Source.self, from: Data(contentsOf: pathURL))
        let reader = try VisualReplayArchiveReader(rootURL: pathURL.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id)
        let start = try XCTUnwrap(reader.manifest.frames.first?.captureTimestamp)
        let inputs = Dictionary(reader.manifest.groundInputs.map { ($0.captureTimestamp, $0) },
                                uniquingKeysWith: { _, latest in latest })
        var rows = [[String: Any]]()
        var lastSample = -Double.infinity
        for frame in reader.manifest.frames {
            let offset = frame.captureTimestamp - start
            guard (142...157).contains(offset), offset - lastSample >= 0.10,
                  let input = inputs[frame.captureTimestamp] else { continue }
            lastSample = offset
            try autoreleasepool {
                let image = try reader.image(for: frame)
                let analysis = GroundLineDetector.analyze(image, excluding: input.excludingRects)
                let theory = analysis.map(GroundLineDetector.compare).flatMap {
                    GroundLineDetector.theory(from: $0, tuning: input.tuning)
                }
                let lines = GroundLineDetector.rejectingKnownForegroundLines(
                    theory.map(GroundLineDetector.cleanFloorLines) ?? [], imageHeight: image.height,
                    foregroundRects: input.excludingRects, knightRect: input.knightRect)
                let bands = FrameRegionRenderer.signalProfile(in: image).edgeBlackBands
                rows.append(["captureTimestamp": frame.captureTimestamp, "offset": offset,
                    "imageIndex": frame.index,
                    "floorLines": lines.map { ["row": $0.row, "minimumX": $0.xRange.lowerBound,
                                                "maximumX": $0.xRange.upperBound] },
                    "leftBlackFraction": bands?.left.map { Double($0.widthFraction) as Any } ?? NSNull(),
                    "rightBlackFraction": bands?.right.map { Double($0.widthFraction) as Any } ?? NSNull()])
            }
        }
        XCTAssertFalse(rows.isEmpty)
        let data = try JSONSerialization.data(withJSONObject: rows, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: URL(fileURLWithPath: outputPath), options: .atomic)
        print("Doorway floor image audit: \(rows.count) frames")
    }
}
