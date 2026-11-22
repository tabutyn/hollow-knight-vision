import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

/// Opt-in pixel-only diagnostic. Never reads reference camera or scene telemetry.
final class GroundPairReplayAuditTests: XCTestCase {
    func testArchivedGroundPairsWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let recording = env["HKV_GROUND_PAIR_RECORDING"],
              let output = env["HKV_GROUND_PAIR_OUTPUT"] else {
            throw XCTSkip("Set ground pair recording and output, plus pairs or sequence ranges")
        }
        struct Source: Decodable {
            let id: UUID
            let visualFrameArchive: RecordedVisualFrameArchive
        }
        struct Pair: Decodable {
            let id: Int
            let timestamp: Double
            let referenceTimestamp: Double
            let rows: [Int]?
            let admittedRows: [Int]?
            let proposedPose: CGPoint?
        }
        let path = URL(fileURLWithPath: recording)
        let source = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
        let reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id,
            requireComplete: env["HKV_GROUND_PAIR_ALLOW_INCOMPLETE"] != "1")
        let frames = Dictionary(uniqueKeysWithValues: reader.manifest.frames.map { ($0.captureTimestamp, $0) })
        let inputs = reader.manifest.groundInputs.sorted { $0.captureTimestamp < $1.captureTimestamp }
        struct Pixels {
            let image: CGImage
            let values: [UInt8]
            let lines: [CleanFloorLine]
            let exclusions: [CGRect]
            let width: Int
            let height: Int
            let tuningTimestamp: Double
            let archivedMasksAvailable: Bool
        }
        func load(_ timestamp: Double, rows: [Int]?) throws -> Pixels {
            let frame = try XCTUnwrap(frames[timestamp])
            let input = try XCTUnwrap(inputs.last { $0.captureTimestamp <= timestamp })
            let image = try reader.image(for: frame)
            let fresh = timestamp - input.captureTimestamp <= 0.1
            let exclusions = fresh ? input.excludingRects : SceneRegions.atlasGameplay(
                fromObjectDetections: [], in: CGRect(x: 0, y: 0, width: image.width, height: image.height)).omittedRects
            let analysis = try XCTUnwrap(GroundLineDetector.analyze(image, excluding: exclusions))
            let theory = GroundLineDetector.theory(from: GroundLineDetector.compare(analysis), tuning: input.tuning)
            let detected = GroundLineDetector.rejectingKnownForegroundLines(
                theory.map(GroundLineDetector.cleanFloorLines) ?? [], imageHeight: image.height,
                foregroundRects: exclusions, knightRect: fresh ? input.knightRect : nil)
            return Pixels(image: image, values: try XCTUnwrap(analysis.sourceLuma),
                lines: detected.filter { line in rows.map { $0.contains(line.row) } ?? true },
                exclusions: exclusions.map {
                    CGRect(x: $0.minX, y: CGFloat(image.height) - $0.maxY, width: $0.width, height: $0.height)
                }, width: image.width, height: image.height,
                tuningTimestamp: input.captureTimestamp, archivedMasksAvailable: fresh)
        }
        var results = [[String: Any]]()
        if let rangesPath = env["HKV_GROUND_SEQUENCE_RANGES"] {
            struct Range: Decodable { let start: Double; let end: Double }
            let ranges = try JSONDecoder().decode([Range].self,
                from: Data(contentsOf: URL(fileURLWithPath: rangesPath)))
            XCTAssertLessThanOrEqual(ranges.count, 8)
            for (rangeID, range) in ranges.enumerated() {
                XCTAssertLessThanOrEqual(range.end - range.start, 10)
                for fractional in [false, true] {
                    let solver = GroundCameraSolver(subpixelEnabled: fractional,
                        keyframeSubpixelEnabled: true, independentPatchConsensusEnabled: true,
                        calibratedMotionEnabled: true, matcherBackend: .automatic,
                        maximumLocalSearchRadius: 192)
                    var position = CGVector.zero
                    for frame in reader.manifest.frames where frame.fileName != nil
                        && frame.captureTimestamp >= range.start && frame.captureTimestamp <= range.end {
                        try autoreleasepool {
                            let value = try load(frame.captureTimestamp, rows: nil)
                            let result = solver.solve(pixels: value.values, width: value.width,
                                height: value.height, lines: value.lines,
                                timestamp: frame.captureTimestamp, exclusions: value.exclusions)
                            if let result {
                                position.dx += result.imageTranslation.dx
                                position.dy += result.imageTranslation.dy
                            }
                            var row: [String: Any] = ["range": rangeID,
                                "subpixelEnabled": fractional, "timestamp": frame.captureTimestamp,
                                "position": [position.dx, position.dy], "accepted": result != nil,
                                "rows": value.lines.map(\.row)]
                            if let result {
                                row["support"] = result.support
                                row["error"] = result.error
                                row["source"] = result.matchSource
                                row["displacement"] = result.matchedImageDisplacement.map { [$0.dx, $0.dy] }
                            }
                            results.append(row)
                        }
                    }
                }
            }
            try JSONSerialization.data(withJSONObject: results, options: [.sortedKeys])
                .write(to: URL(fileURLWithPath: output), options: .atomic)
            return
        }
        guard let pairsPath = env["HKV_GROUND_PAIR_PAIRS"] else {
            throw XCTSkip("Set ground pairs or sequence ranges")
        }
        let pairs = try JSONDecoder().decode([Pair].self,
            from: Data(contentsOf: URL(fileURLWithPath: pairsPath)))
        XCTAssertLessThanOrEqual(pairs.count, 100)
        for pair in pairs {
            try autoreleasepool {
                let old = try load(pair.referenceTimestamp, rows: pair.rows)
                let current = try load(pair.timestamp, rows: pair.rows)
                for backend: GroundComputeBackend in [.cpu, .metal] {
                    // Compare integer and fractional measurements on identical
                    // pixels without supplying reference camera telemetry.
                    let subpixel = env["HKV_GROUND_PAIR_SUBPIXEL"] == "1"
                    let solver = GroundCameraSolver(subpixelEnabled: subpixel,
                        keyframeSubpixelEnabled: true,
                        independentPatchConsensusEnabled: true, calibratedMotionEnabled: true,
                        matcherBackend: backend, maximumLocalSearchRadius: 192)
                    _ = solver.solve(pixels: old.values, width: old.width, height: old.height,
                        lines: old.lines, timestamp: pair.referenceTimestamp, exclusions: old.exclusions)
                    if let admittedRows = pair.admittedRows {
                        solver.prioritizeAdmittedReferenceRows(at: pair.referenceTimestamp,
                            lines: old.lines.filter { admittedRows.contains($0.row) })
                    }
                    let result = solver.solve(pixels: current.values, width: current.width, height: current.height,
                        lines: current.lines, timestamp: pair.timestamp, exclusions: current.exclusions)
                    var row: [String: Any] = ["id": pair.id, "backend": backend.rawValue,
                        "subpixelEnabled": subpixel,
                        "timestamp": pair.timestamp, "referenceTimestamp": pair.referenceTimestamp,
                        "referenceRows": old.lines.map(\.row), "currentRows": current.lines.map(\.row),
                        "tuningTimestamps": [old.tuningTimestamp, current.tuningTimestamp],
                        "archivedMasksAvailable": [old.archivedMasksAvailable, current.archivedMasksAvailable],
                        "accepted": result != nil]
                    if let admittedRows = pair.admittedRows { row["admittedReferenceRows"] = admittedRows }
                    if let proposed = pair.proposedPose {
                        func geometry(_ value: Pixels) -> LiveWorldGroundGeometry {
                            LiveWorldGroundGeometry(lines: value.lines.enumerated().flatMap { index, line in
                                line.evidenceRanges.map { span in
                                    LiveGroundLineEvidence(segmentID: index,
                                        start: CGPoint(x: span.lowerBound, y: value.height-line.row),
                                        end: CGPoint(x: span.upperBound+1, y: value.height-line.row))
                                }
                            })
                        }
                        let verification = GroundPlaneClosureVerifier.verify(reference: old.image,
                            referenceGround: geometry(old), referencePose: .zero,
                            current: current.image, currentGround: geometry(current),
                            proposedPose: proposed, solveWidth: CGFloat(old.width))
                        row["proposedFloorVerified"] = verification != nil
                        row["floorEvidenceLimit"] = "Raw detected rows only; no persisted measured-ground ownership is asserted by this diagnostic"
                        if let verification {
                            row["verifiedFloorPose"] = [verification.cameraPose.x, verification.cameraPose.y]
                            row["verifiedFloorPatches"] = verification.supportedPatches
                            row["verifiedFloorCorrelation"] = verification.meanCorrelation
                        }
                    }
                    if let result {
                        row["displacement"] = result.matchedImageDisplacement.map { [$0.dx, $0.dy] }
                        row["translation"] = [result.imageTranslation.dx, result.imageTranslation.dy]
                        row["support"] = result.support
                        row["error"] = result.error
                    }
                    results.append(row)
                }
            }
        }
        try JSONSerialization.data(withJSONObject: results, options: [.prettyPrinted, .sortedKeys])
            .write(to: URL(fileURLWithPath: output), options: .atomic)
    }
}
