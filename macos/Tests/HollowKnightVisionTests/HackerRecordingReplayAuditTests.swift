import CoreImage
import Darwin
import Foundation
import XCTest
@testable import HollowKnightVision

/// Hacker-only replay: exact game-camera telemetry is intentionally used here.
/// This does not share observations or poses with the Gameplay visual tracker.
final class HackerRecordingReplayAuditTests: XCTestCase {
    func testRecordedHackerRoomLayoutWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_HACKER_RECORDING_AUDIT_INPUT"],
              let output = env["HKV_HACKER_RECORDING_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set Hacker recording audit input and output")
        }
        struct Source: Decodable {
            let id: UUID
            let visualFrameArchive: RecordedVisualFrameArchive
            let groundTruthSamples: [RecordedGroundTruthSample]
        }
        let path = URL(fileURLWithPath: input)
        let source = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
        let reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id,
            requireComplete: env["HKV_HACKER_RECORDING_AUDIT_ALLOW_INCOMPLETE"] != "1")
        let samples = Dictionary(grouping: source.groundTruthSamples.map(\.sample),
            by: { HackerFrameSynchronizer.key(for: $0.unityFrame) })
        let preparer = HackerFramePreparer()
        let pipeline = HackerAtlasPipeline(context: CIContext(options: [.cacheIntermediates: false]))
        var last: HackerAtlasOutput?
        var lastIntegrated = -Double.infinity
        var priorScene: String?, processed = 0, joined = 0
        var peakBytes: UInt64 = 0
        for frame in reader.manifest.frames where frame.fileName != nil {
            guard let marker = frame.renderedGameFrame, let values = samples[marker],
                  let sample = values.first,
                  values.allSatisfy({ $0 == sample }) else { continue }
            joined += 1
            let sceneChanged = priorScene != sample.sceneName
            let integrates = sceneChanged || frame.captureTimestamp - lastIntegrated >= 1
            priorScene = sample.sceneName
            try autoreleasepool {
                let image = try reader.image(for: frame)
                let matched = HackerMatchedFrame(capture: .init(unityFrame: marker, image: image,
                    capturedAt: frame.captureTimestamp, integratesAtlas: integrates),
                    sample: sample, sampleObservedAt: frame.captureTimestamp)
                guard let prepared = preparer.process(matched) else { return }
                if let result = pipeline.process(prepared) {
                    last = result
                    lastIntegrated = frame.captureTimestamp
                }
            }
            processed += 1
            if processed.isMultiple(of: 100) {
                let memory = try physicalFootprint()
                peakBytes = max(peakBytes, memory)
                guard memory < 2 * 1_024 * 1_024 * 1_024 else {
                    throw NSError(domain: "HackerReplayMemoryGuard", code: 1,
                        userInfo: [NSLocalizedDescriptionKey: "Hacker audit reached 2 GiB; stopped before processing more images"])
                }
            }
        }
        let finalRaster = try XCTUnwrap(last)
        let result = pipeline.output(sceneName: finalRaster.sceneName, unityFrame: finalRaster.unityFrame)
        let archive = pipeline.archive()
        let layout = HackerRoomLayout.make(rooms: result.rooms, connections: result.connections,
            floorAlignedConnectionIDs: result.floorAlignedConnectionIDs)
        let names = Dictionary(uniqueKeysWithValues: result.rooms.map { ($0.id, $0.sceneName) })
        let edges = result.connections.map { edge -> [String: Any] in
            let from = layout.positions[edge.fromRoomID] ?? .zero
            let to = layout.positions[edge.toRoomID] ?? .zero
            let horizontal = edge.fromSide.isHorizontal
            return ["id": edge.id.uuidString, "fromScene": names[edge.fromRoomID] ?? "unknown",
                "toScene": names[edge.toRoomID] ?? "unknown", "fromSide": edge.fromSide.rawValue,
                "fromCoordinate": edge.fromCoordinate, "toCoordinate": edge.toCoordinate,
                "floorAligned": archive.floorAlignedConnectionIDs.contains(edge.id),
                "freeform": layout.freeformConnectionIDs.contains(edge.id),
                "connectorMismatchPixels": horizontal
                    ? abs(from.y + edge.fromCoordinate - to.y - edge.toCoordinate)
                    : abs(from.x + edge.fromCoordinate - to.x - edge.toCoordinate)]
        }
        let report: [String: Any] = ["sourcePathID": source.id.uuidString,
            "exactJoinedImages": joined, "processedImages": processed,
            "sampling": "Every exact-joined image supplies geometry; one-second pixel integration cadence plus scene changes",
            "peakSampledPhysicalBytes": peakBytes,
            "rooms": result.rooms.map { room -> [String: Any] in
                let position = layout.positions[room.id] ?? .zero
                return ["scene": room.sceneName, "position": [position.x, position.y],
                        "bounds": [room.bounds.minX, room.bounds.minY, room.bounds.width, room.bounds.height]]
            }, "connections": edges,
            "limits": "Independent Hacker geometry audit using exact mod camera data. Raster cadence is reduced, so this is not a full-rate quilt-quality or live-throughput measurement."]
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
            .write(to: URL(fileURLWithPath: output), options: .atomic)
        print("Hacker recorded layout: \(result.rooms.count) rooms, \(edges.count) connections, peak \(peakBytes / 1_024 / 1_024) MiB")
    }

    private func physicalFootprint() throws -> UInt64 {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        guard result == KERN_SUCCESS else { throw NSError(domain: NSMachErrorDomain, code: Int(result)) }
        return info.phys_footprint
    }
}
