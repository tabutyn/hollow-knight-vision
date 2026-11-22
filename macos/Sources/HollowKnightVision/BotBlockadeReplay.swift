import Foundation

/// Reconstruct pre-blockade observations in chronological order; never use a future swing as evidence.
enum BotBlockadeReplay {
    struct Row: Encodable {
        let generation: UInt64
        let captureTimestamp: Double
        let blockade: BotBlockadeEvidence
    }
    private struct VisualTrace: Decodable {
        struct Frame: Decodable { let captureTimestamp: Double; let island: LiveIslandCapture? }
        let id: UUID
        let trackingSamples: [Frame]?
    }
    private struct HackerTrace: Decodable { let id: UUID; let groundTruthSamples: [RecordedGroundTruthSample]? }
    private struct Context: Decodable { let replayRoot: String? }

    static func recover(_ clip: BotSnippet) throws -> (rows: [Row], map: BotBlockadeMap) {
        var map = BotBlockadeMap(), tracker = BotBlockadeTracker(), rows = [Row]()
        let id = clip.manifest.sourceReplayID
        let context = (try? Data(contentsOf: clip.directory.appendingPathComponent("audit-context.json")))
            .flatMap { try? JSONDecoder().decode(Context.self, from: $0) }
        let root = context?.replayRoot.map { URL(fileURLWithPath: $0) } ?? InputPathStore.defaultRootURL()
        let files = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        let matching = files.filter { $0.lastPathComponent.hasPrefix("path-") && $0.lastPathComponent.hasSuffix("-" + id.uuidString.lowercased() + ".json") }
        var islands = [Double: LiveIslandCapture](), actors = [Int: [RecordedGroundTruthSample]]()
        if matching.count == 1, clip.observations.contains(where: { $0.driveDirection == nil }) {
            let data = try Data(contentsOf: matching[0])
            if clip.domain == .hacker {
                let trace = try JSONDecoder().decode(HackerTrace.self, from: data)
                guard trace.id == id else { throw BotRuntimeError.incompatible("Wrong Hacker replay.") }
                actors = Dictionary(grouping: trace.groundTruthSamples ?? []) { HackerFrameSynchronizer.key(for: $0.sample.unityFrame) }
            } else {
                let trace = try JSONDecoder().decode(VisualTrace.self, from: data)
                guard trace.id == id else { throw BotRuntimeError.incompatible("Wrong visual replay.") }
                for frame in trace.trackingSamples ?? [] { islands[frame.captureTimestamp] = frame.island }
            }
        }
        var event = 0, held = Set<String>(), origins = [String: CGPoint]()
        for row in clip.observations {
            while event < clip.inputs.count && clip.inputs[event].timestamp <= row.capturedAt {
                let edge = clip.inputs[event]
                if edge.isPressed { held.insert(edge.button) } else { held.remove(edge.button) }
                event += 1
            }
            let drive = row.driveDirection ?? ((held.contains("right") ? 1 : 0) - (held.contains("left") ? 1 : 0))
            var pose = row.blockadePose
            let island = islands[row.captureTimestamp]
            if row.driveDirection == nil {
                if clip.domain == .hacker, let frame = row.detectionFrameID, let key = Int(exactly: frame) {
                    let matches = actors[key]?.filter { abs($0.receivedTimestamp - row.capturedAt) <= 0.25 }
                    if matches?.count == 1, let sample = matches?.first?.sample { pose = .hacker(sample) }
                } else if clip.domain == .gameplay, let view = row.atlasView {
                    if let island, island.roomID == row.roomID {
                        pose = .visual(island, width: view.width)
                        origins[BotBlockadePose.visualSpace(island)] = island.worldFromLocal
                    } else if island == nil {
                        pose = BotBlockadePose(space: "clip/\(id)/\(row.roomID)", x: view.camera.x, y: view.camera.y, width: view.width)
                    }
                }
            }
            let inferred = tracker.observe(row, pose: pose, direction: drive, map: &map) { space in
                guard let island, space.hasPrefix("visual/\(island.roomID)/"), let origin = origins[space] else { return nil }
                return CGPoint(x: origin.x - island.worldFromLocal.x, y: origin.y - island.worldFromLocal.y)
            }
            if let evidence = row.driveDirection == nil ? inferred : row.blockade {
                rows.append(Row(generation: row.generation, captureTimestamp: row.captureTimestamp, blockade: evidence))
            }
        }
        return (rows, map)
    }
}
