import CoreGraphics
import CryptoKit
import Foundation
import XCTest
@testable import HollowKnightVision

/// Pixel-only detector evidence. Camera truth and human lines are joined by a
/// separate scorer after detection, never supplied to the implementation.
final class GroundDetectionReplayAuditTests: XCTestCase {
    func testPlatformPatchTrackingOnRecordedGameplayWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_PATCH_REPLAY_INPUT"], let output = env["HKV_PATCH_REPLAY_OUTPUT"] else {
            throw XCTSkip("Set the platform patch replay input and output paths")
        }
        let report = try VisualProductionReplay.run(arguments: [
            "--replay-visual-archive", input, "--visual-replay-output", output])
        XCTAssertEqual(report.frames, report.archiveFrameCount)
        XCTAssertTrue(report.missingFrameIndices.isEmpty)
    }

    func testRecordedGroundDetectionsWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let inputPath = env["HKV_GROUND_DETECTION_RECORDING"],
              let outputPath = env["HKV_GROUND_DETECTION_OUTPUT"] else {
            throw XCTSkip("Set ground detection recording and output paths")
        }
        let frameStride = try XCTUnwrap(Int(env["HKV_GROUND_DETECTION_STRIDE"] ?? "6"))
        XCTAssertTrue((1...60).contains(frameStride))
        guard (1...60).contains(frameStride) else { return }
        let outputURL = URL(fileURLWithPath: outputPath)
        XCTAssertFalse(FileManager.default.fileExists(atPath: outputPath),
            "Use a fresh output file so earlier evidence stays reproducible")
        guard !FileManager.default.fileExists(atPath: outputPath) else { return }

        struct Source: Decodable {
            let id: UUID
            let trackingSamples: [RecordedTrackingSample]?
            let coarseMotionSamples: [RecordedCoarseMotionSample]?
            let visualFrameArchive: RecordedVisualFrameArchive
        }
        let recordingURL = URL(fileURLWithPath: inputPath)
        let recordingData = try Data(contentsOf: recordingURL)
        let source = try JSONDecoder().decode(Source.self, from: recordingData)
        let reader = try VisualReplayArchiveReader(rootURL: recordingURL.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id, requireComplete: true)
        let inputs = reader.manifest.groundInputs.sorted { $0.captureTimestamp < $1.captureTimestamp }
        let requestedIndices = try env["HKV_GROUND_DETECTION_INDICES"].map { value in
            Set(try value.split(separator: ",").map { try XCTUnwrap(Int($0)) })
        }
        let selected = reader.manifest.frames.filter {
            requestedIndices?.contains($0.index) ?? $0.index.isMultiple(of: frameStride)
        }
        if let requestedIndices { XCTAssertEqual(Set(selected.map(\.index)), requestedIndices) }
        XCTAssertLessThanOrEqual(selected.count, 5_000)
        var inputIndex = 0
        var recentInput: VisualReplayGroundInput?
        let poses = Dictionary(grouping: source.trackingSamples ?? [], by: \.captureTimestamp)
        let intents = Dictionary(grouping: source.coarseMotionSamples ?? [], by: \.captureTimestamp)
        var tracker = KnightTerrainContactTracker(), locator = KnightFootLocator(), lastIsland: Int?
        var rows = [[String: Any]]()
        var missingMasks = 0
        var exactMasks = 0
        let began = ProcessInfo.processInfo.systemUptime

        func rect(_ rect: CGRect) -> [Double] {
            [rect.minX, rect.minY, rect.width, rect.height].map(Double.init)
        }
        func lines(_ lines: [CleanFloorLine]) -> [[String: Any]] {
            lines.map { ["row": $0.row, "x0": $0.xRange.lowerBound,
                        "x1": $0.xRange.upperBound,
                        "evidence": $0.evidenceRanges.map { [$0.lowerBound, $0.upperBound] }] }
        }
        func tuning(_ tuning: GroundTheoryTuning) -> [String: Int] {
            ["groundThreshold": tuning.groundThreshold,
             "minimumSegmentLength": tuning.minimumSegmentLength,
             "lineSeparation": tuning.lineSeparation, "occlusionMergeGap": tuning.occlusionMergeGap]
        }
        for frame in selected {
            while inputIndex < inputs.count,
                  inputs[inputIndex].captureTimestamp <= frame.captureTimestamp {
                recentInput = inputs[inputIndex]
                inputIndex += 1
            }
            guard let input = recentInput,
                  frame.captureTimestamp - input.captureTimestamp <= 0.1 else {
                missingMasks += 1
                continue
            }
            let exact = frame.captureTimestamp == input.captureTimestamp
            if exact { exactMasks += 1 }
            try autoreleasepool {
                let image = try reader.image(for: frame)
                let started = ProcessInfo.processInfo.systemUptime
                let analysis = try XCTUnwrap(GroundLineDetector.analyze(image, excluding: input.excludingRects))
                let comparison = GroundLineDetector.compare(analysis)
                let semantic = GroundLineDetector.semanticFloorLines(from: comparison, tuning: input.semanticTuning)
                let semanticLines = GroundLineDetector.gameplayFloors(semantic, imageHeight: image.height,
                    foregroundRects: input.excludingRects, knightRect: input.knightRect)
                let tracking = GroundLineDetector.cameraFloors(from: comparison, tuning: input.tuning,
                    visibleFloors: semanticLines, imageHeight: image.height, foregroundRects: input.excludingRects,
                    knightRect: input.knightRect)
                let wallStart = ProcessInfo.processInfo.systemUptime
                var terrain = TerrainWallDetector.detect(analysis, floors: semanticLines)
                let rawWalls = terrain.walls.map { [$0.x, $0.top, $0.bottom] }
                let trace = poses[frame.captureTimestamp]?.last
                let coarse = intents[frame.captureTimestamp]?.last
                let actor = exact ? locator.locate(analysis, near: input.observedPlayerBounds ?? input.knightRect,
                    direction: coarse?.input?.horizontalInput ?? 0, time: frame.captureTimestamp,
                    room: coarse?.roomID ?? -1, generation: frame.captureGeneration) : nil
                let foot = actor?.foot
                let pose: CGPoint?
                if let t = trace, t.island?.measured == true, let x = t.publishedCameraX, let y = t.publishedCameraY {
                    pose = CGPoint(x: x * Double(image.width) / frame.solveWidth, y: y * Double(image.width) / frame.solveWidth)
                } else { pose = nil }
                if let id = trace?.island?.id {
                    if lastIsland != id { tracker = .init() }
                    lastIsland = id
                }
                if coarse?.isTransitioning == true { tracker = .init() }
                let contact = tracker.observe(foot: foot, camera: pose, direction: coarse?.input?.horizontalInput ?? 0,
                    time: frame.captureTimestamp, room: coarse?.roomID ?? trace?.roomID ?? -1, generation: frame.captureGeneration,
                    width: image.width, height: image.height, walls: terrain.walls, partial: actor?.partial == true)
                terrain.apply(contact, floors: semanticLines, analysis: analysis)
                let wallMilliseconds = (ProcessInfo.processInfo.systemUptime - wallStart) * 1_000
                rows.append([
                    "index": frame.index, "captureTimestamp": frame.captureTimestamp,
                    "renderedGameFrame": frame.renderedGameFrame.map { $0 as Any } ?? NSNull(),
                    "imageSHA256": frame.sha256 ?? "", "width": image.width, "height": image.height,
                    "inputTimestamp": input.captureTimestamp, "exactMaskInput": exact,
                    "exclusions": input.excludingRects.map {
                        rect(CGRect(x: $0.minX, y: CGFloat(image.height) - $0.maxY,
                                    width: $0.width, height: $0.height))
                    },
                    "trackingTuning": tuning(input.tuning), "semanticTuning": tuning(input.semanticTuning),
                    "walls": terrain.walls.map { [$0.x, $0.top, $0.bottom] },
                    "corners": terrain.corners.map { [$0.x, $0.y] },
                    "rawWalls": rawWalls, "foot": foot.map { [Double($0.x),Double($0.y)] } ?? [],
                    "contactFloor": contact.floor.map { [$0.row,$0.xRange.lowerBound,$0.xRange.upperBound] } ?? [],
                    "contactWall": contact.wall.map { [$0.x,$0.top,$0.bottom] } ?? [],
                    "supportFloor": terrain.supportedFloor.map { [$0.row,$0.xRange.lowerBound,$0.xRange.upperBound] } ?? [],
                    "darkFraction": analysis.sourceLuma.map { Double($0.filter { $0 <= 10 }.count) / Double($0.count) } ?? 0,
                    "wallMilliseconds": wallMilliseconds,
                    "trackingLines": lines(tracking), "semanticLines": lines(semanticLines),
                    "detectorMilliseconds": (ProcessInfo.processInfo.systemUptime - started) * 1_000,
                ])
            }
            if rows.count.isMultiple(of: 100) {
                fputs("ground detector audit: \(rows.count)/\(selected.count) images\n", stderr)
            }
        }
#if DEBUG
        let buildConfiguration = "debug"
#else
        let buildConfiguration = "release"
#endif
        let report: [String: Any] = [
            "schemaVersion": 1, "sourcePathID": source.id.uuidString,
            "recordingSHA256": SHA256.hash(data: recordingData).map { String(format: "%02x", $0) }.joined(),
            "frameStride": frameStride, "archiveFrames": reader.manifest.frames.count,
            "requestedIndices": requestedIndices.map { $0.sorted() as Any } ?? NSNull(),
            "selectedFrames": selected.count, "processedFrames": rows.count,
            "missingFreshMaskInputs": missingMasks, "exactMaskInputs": exactMasks,
            "elapsedSeconds": ProcessInfo.processInfo.systemUptime - began,
            "buildConfiguration": buildConfiguration,
            "method": "Pixel terrain detection uses archive images, masks and tuning. Knight contact uses the recorded visual camera solve and input. Unity telemetry and human labels enter only during scoring.",
            "frames": rows,
        ]
        try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys])
            .write(to: outputURL, options: .atomic)
        XCTAssertEqual(rows.count + missingMasks, selected.count)
        print("Ground detection audit: \(rows.count) images, \(missingMasks) missing fresh masks")
    }
}
