import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

/// Optional pixel-only counterfactual. Uses archived raw floor rows, not the
/// solver's stabilized reference rows; results are diagnostic, never map poses.
final class CaptureRefinementAuditTests: XCTestCase {
    func testInspectIndependentCaptureRefinementWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let recording = env["HKV_CAPTURE_REFINEMENT_RECORDING"],
              let queries = env["HKV_CAPTURE_REFINEMENT_PAIRS"],
              let output = env["HKV_CAPTURE_REFINEMENT_OUTPUT"] else {
            throw XCTSkip("Set capture refinement recording, pairs and output")
        }
        struct Source: Decodable {
            let id: UUID
            let visualFrameArchive: RecordedVisualFrameArchive
        }
        struct Pair: Decodable {
            let id: Int
            let timestamp: Double
            let referenceTimestamp: Double
            let dx: Int
            let dy: Int
            let source: String
        }
        let path = URL(fileURLWithPath: recording)
        let source = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
        let reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id)
        let frames = Dictionary(uniqueKeysWithValues: reader.manifest.frames.map {
            ($0.captureTimestamp, $0)
        })
        let inputs = Dictionary(uniqueKeysWithValues: reader.manifest.groundInputs.map {
            ($0.captureTimestamp, $0)
        })
        let pairs = try JSONDecoder().decode([Pair].self,
            from: Data(contentsOf: URL(fileURLWithPath: queries)))
        XCTAssertLessThanOrEqual(pairs.count, 4_000)
        struct Pixels {
            let luma: [UInt8]
            let lines: [CleanFloorLine]
            let exclusions: [CGRect]
            let width: Int
            let height: Int
        }
        func load(_ timestamp: Double) throws -> Pixels {
            let frame = try XCTUnwrap(frames[timestamp])
            let input = try XCTUnwrap(inputs[timestamp])
            let image = try reader.image(for: frame)
            let analysis = try XCTUnwrap(GroundLineDetector.analyze(image,
                excluding: input.excludingRects))
            let theory = GroundLineDetector.theory(from: GroundLineDetector.compare(analysis),
                tuning: input.tuning)
            let lines = GroundLineDetector.rejectingKnownForegroundLines(
                theory.map(GroundLineDetector.cleanFloorLines) ?? [], imageHeight: image.height,
                foregroundRects: input.excludingRects, knightRect: input.knightRect)
            return Pixels(luma: try XCTUnwrap(analysis.sourceLuma), lines: lines,
                exclusions: input.excludingRects.map {
                    CGRect(x: $0.minX, y: CGFloat(image.height) - $0.maxY,
                           width: $0.width, height: $0.height)
                }, width: image.width, height: image.height)
        }
        var rows = [[String: Any]]()
        for pair in pairs {
            guard inputs[pair.timestamp] != nil, inputs[pair.referenceTimestamp] != nil,
                  frames[pair.timestamp] != nil, frames[pair.referenceTimestamp] != nil else {
                rows.append(["id": pair.id, "refined": false,
                    "issue": "Archived ground inputs unavailable for this reference pair"])
                continue
            }
            try autoreleasepool {
                let old = try load(pair.referenceTimestamp), current = try load(pair.timestamp)
                XCTAssertEqual(old.width, current.width)
                XCTAssertEqual(old.height, current.height)
                let width = old.width, height = old.height
                var groups = [[GroundSubpixelRegistration.Patch]]()
                for line in old.lines where line.row >= 0 && line.row + 12 <= height {
                    let count = max(0, line.xRange.count / 16)
                    let strideCells = max(1, Int(ceil(Double(count) / 8)))
                    var group = [GroundSubpixelRegistration.Patch]()
                    for cell in stride(from: 0, to: count, by: strideCells) {
                        let x = line.xRange.lowerBound + cell * 16
                        let rect = CGRect(x: x, y: line.row, width: 16, height: 12)
                        guard line.evidenceRanges.contains(where: { $0.overlaps(x...(x + 15)) }),
                              !old.exclusions.contains(where: { $0.intersects(rect) }),
                              let patch = GroundPixels.patch(old.luma, width: width,
                                height: height, x: x, y: line.row),
                              (patch.max() ?? 0) - (patch.min() ?? 0) >= 12 else { continue }
                        group.append(.init(x: x, y: line.row, pixels: patch))
                    }
                    if !group.isEmpty { groups.append(group) }
                }
                var patches = [GroundSubpixelRegistration.Patch]()
                var offset = 0
                while patches.count < 32 {
                    var added = false
                    for group in groups where offset < group.count {
                        patches.append(group[offset]); added = true
                        if patches.count == 32 { break }
                    }
                    if !added { break }
                    offset += 1
                }
                let usable = patches.filter { patch in
                    let x = patch.x + pair.dx, y = patch.y + pair.dy
                    let rect = CGRect(x: x - 5, y: y - 5, width: 26, height: 22)
                    guard rect.minX >= 0, rect.minY >= 0,
                          rect.maxX < CGFloat(width), rect.maxY < CGFloat(height),
                          !current.exclusions.contains(where: { $0.intersects(rect) }),
                          current.lines.isEmpty || current.lines.contains(where: { abs($0.row - y) <= 3 }),
                          let target = GroundPixels.patch(current.luma, width: width,
                            height: height, x: x, y: y) else { return false }
                    return GroundCameraSolver.patchError(patch.pixels, target) <= 18
                }
                let result = GroundSubpixelRegistration.refine(patches: usable,
                    pixels: current.luma, width: width, height: height, dx: pair.dx, dy: pair.dy)
                var row: [String: Any] = ["id": pair.id, "timestamp": pair.timestamp,
                    "referenceTimestamp": pair.referenceTimestamp,
                    "patches": usable.count, "source": pair.source,
                    "dx": pair.dx, "dy": pair.dy, "refined": result != nil]
                if let result {
                    row["offset"] = [result.offset.dx, result.offset.dy]
                    row["validationError"] = result.validationError
                }
                if env["HKV_CAPTURE_CONSENSUS"] == "1",
                   let consensus = independentConsensus(patches: patches,
                       pixels: current.luma, width: width, height: height,
                       dx: pair.dx, dy: pair.dy, exclusions: current.exclusions) {
                    row["independentConsensus"] = consensus
                }
                rows.append(row)
            }
        }
        try JSONSerialization.data(withJSONObject: rows, options: [.prettyPrinted, .sortedKeys])
            .write(to: URL(fileURLWithPath: output))
    }

    /// Diagnostic port of Windows experimental GroundPatchConsensus. This
    /// neither bypasses the production ambiguity gate nor changes saved poses.
    private func independentConsensus(patches: [GroundSubpixelRegistration.Patch],
        pixels: [UInt8], width: Int, height: Int, dx: Int, dy: Int,
        exclusions: [CGRect]) -> [String: Any]? {
        struct Match {
            let patch: GroundSubpixelRegistration.Patch
            let x: Int
            let y: Int
            let error: CGFloat
        }
        var matches = [Match]()
        for patch in patches {
            var scores = [Match]()
            for y in (dy - 4)...(dy + 4) {
                for x in (dx - 4)...(dx + 4) {
                    let rect = CGRect(x: patch.x + x, y: patch.y + y, width: 16, height: 12)
                    guard rect.minX >= 0, rect.minY >= 0,
                          rect.maxX <= CGFloat(width), rect.maxY <= CGFloat(height),
                          !exclusions.contains(where: { $0.intersects(rect) }),
                          let values = GroundPixels.patch(pixels, width: width, height: height,
                              x: patch.x + x, y: patch.y + y) else { continue }
                    scores.append(Match(patch: patch, x: x, y: y,
                        error: GroundCameraSolver.patchError(patch.pixels, values)))
                }
            }
            guard let best = scores.min(by: { $0.error < $1.error }), best.error <= 12 else { continue }
            let rival = scores.filter { abs($0.x - best.x) > 2 || abs($0.y - best.y) > 2 }
                .min(by: { $0.error < $1.error })
            if let rival, rival.error <= best.error * 1.12 + 0.25 { continue }
            matches.append(best)
        }
        guard matches.count >= 4 else { return nil }
        let groups = matches.map { seed in matches.filter {
            abs($0.x - seed.x) <= 1 && abs($0.y - seed.y) <= 1
        } }
        guard let group = groups.enumerated().min(by: { a, b in
            if a.element.count != b.element.count { return a.element.count > b.element.count }
            let ae = a.element.map(\.error).reduce(0, +) / CGFloat(a.element.count)
            let be = b.element.map(\.error).reduce(0, +) / CGFloat(b.element.count)
            return ae == be ? a.offset < b.offset : ae < be
        })?.element, group.count >= 4, group.count * 2 > matches.count,
            group.map({ $0.patch.x }).max()! - group.map({ $0.patch.x }).min()! >= 32 else { return nil }
        let x = group.map(\.x).sorted()[group.count / 2]
        let y = group.map(\.y).sorted()[group.count / 2]
        let inliers = group.filter {
            let rect = CGRect(x: $0.patch.x + x, y: $0.patch.y + y, width: 16, height: 12)
            return rect.minX >= 0 && rect.minY >= 0 && rect.maxX <= CGFloat(width)
                && rect.maxY <= CGFloat(height) && !exclusions.contains { $0.intersects(rect) }
        }
        guard inliers.count >= 4 else { return nil }
        let errors = inliers.compactMap { value in
            GroundPixels.patch(pixels, width: width, height: height,
                x: value.patch.x + x, y: value.patch.y + y).map {
                    GroundCameraSolver.patchError(value.patch.pixels, $0)
                }
        }
        return ["dx": x, "dy": y, "support": inliers.count,
            "error": errors.reduce(0, +) / CGFloat(errors.count),
            "independentMatches": matches.count, "sourcePatches": patches.count]
    }
}
