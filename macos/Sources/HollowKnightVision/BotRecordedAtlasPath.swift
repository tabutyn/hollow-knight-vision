import CoreGraphics
import Foundation

/// Reconstruct from only the terrain and camera poses recorded at each K frame.
/// These measurements remain in memory and never change the source capture.
enum BotRecordedAtlasPath {
    struct Frame: Encodable {
        let index: Int
        let generation: UInt64
        let captureTimestamp: Double
        let roadHint: BotRoadHint?
        let pathProgress: Double?
    }

    static func recover(_ observations: [BotObservation], domain: BotDomain) -> [Frame] {
        // No present-day authored labels or connectors enter historical observations.
        let unused = FileManager.default.temporaryDirectory.appendingPathComponent("hkv-empty-path-\(UUID().uuidString).json")
        let map = AtlasPathMap(store: AtlasPathConnectionStore(file: unused))
        var frames = [Frame]()
        var prior: (chart: String, room: String, time: Double)?
        for (index, original) in observations.enumerated() {
            var row = original
            row.roadHint = nil
            row.pathProgress = nil
            row.atlasPathVersion = nil
            let terrain = domain == .hacker ? row.authoredTerrain : row.detectedTerrain
            guard let geometry = terrain, let pose = row.blockadePose, pose.valid,
                  geometry.width.isFinite, (1...32768).contains(geometry.width),
                  geometry.height.isFinite, (1...32768).contains(geometry.height),
                  row.features.count >= 7, row.captureTimestamp.isFinite,
                  row.availableAt.isFinite, row.capturedAt.isFinite,
                  (0...0.35).contains(row.availableAt - row.capturedAt),
                  row.issues.isEmpty,
                  row.features[6] == 1, row.features[4] >= 0.5, row.knightPrediction == nil,
                  let detectionTime = row.detectionTimestamp, detectionTime.isFinite,
                  (0...0.2).contains(row.captureTimestamp - detectionTime),
                  (domain == .hacker ? pose.space.hasPrefix("hacker/") : pose.space.hasPrefix("visual/"))
            else {
                // Never bridge progress through an absent or untrusted camera sample.
                if let prior { map.interrupt(room: prior.chart) }
                prior = nil
                continue
            }
            let factor = geometry.width / pose.width
            let camera = CGPoint(x: pose.x * factor, y: pose.y * factor)
            guard factor.isFinite, factor > 0, PathPoint.supports(camera) else {
                if let prior { map.interrupt(room: prior.chart) }
                prior = nil
                continue
            }
            let room = "\(row.generation)/\(row.roomID)/\(pose.space)"
            let logicalRoom = "\(row.generation)/\(row.roomID)"
            if domain == .gameplay, let prior, prior.room == logicalRoom,
               (0...0.45).contains(row.captureTimestamp-prior.time) {
                map.inheritAutomaticDirection(from:prior.chart,into:room)
            }
            if domain == .hacker {
                let scene = String(pose.space.dropFirst("hacker/".count))
                let calibration = AtlasPathCalibration(scene: scene, origin: .zero, scale: CGPoint(x: factor, y: factor))
                map.setAuthored(room: room, calibration: calibration, document: GroundTruthLineDocument())
            }
            map.observe(room: room, camera: camera, width: geometry.width, row: &row,
                        geometry: geometry, space: pose.space, trackPosition: true)
            if domain == .gameplay { map.limitUnplacedCredit(room:logicalRoom,chart:room,row:&row) }
            prior = (room,logicalRoom,row.captureTimestamp)
            if original.atlasPathVersion != 1 && row.atlasPathVersion == 1 {
                frames.append(Frame(index: index, generation: row.generation, captureTimestamp: row.captureTimestamp,
                                    roadHint: row.roadHint, pathProgress: row.pathProgress))
            }
        }
        return frames
    }

    private struct ActorTrace: Decodable {
        let id: UUID
        let groundTruthSamples: [RecordedGroundTruthSample]
    }
    private struct ExitFrame: Encodable {
        let index: Int
        let captureTimestamp: Double
        let generation: UInt64
        let hackerPathVersion = 1
        let roadHint: BotRoadHint?
        let pathProgress, pathAlignment: Double?
    }

    static func exitData(_ args: [String]) throws -> Data {
        guard args.count == 6, let x = Double(args[4]), let y = Double(args[5]), x.isFinite, y.isFinite else {
            throw BotRuntimeError.incompatible("Expected clip, replay, authored ground, connections and exit coordinates.")
        }
        let urls = args.prefix(4).map { URL(fileURLWithPath: $0) }
        let clip = try BotSnippet.load(urls[0])
        let id = clip.manifest.sourceReplayID
        let replay = try JSONDecoder().decode(ActorTrace.self, from: Data(contentsOf: urls[1]))
        let archiveURL = urls[1].deletingLastPathComponent().appendingPathComponent("frames-" + id.uuidString.lowercased())
            .appendingPathComponent("manifest.json")
        let archive = try JSONDecoder().decode(VisualReplayManifest.self, from: Data(contentsOf: archiveURL))
        guard replay.id == id, archive.pathID == id else { throw BotRuntimeError.incompatible("Mismatched replay archive.") }
        let frames = Dictionary(grouping: archive.frames, by: \.captureTimestamp)
        let actors = Dictionary(grouping: replay.groundTruthSamples) { HackerFrameSynchronizer.key(for: $0.sample.unityFrame) }
        let objective = CGPoint(x: x, y: y)
        var guide = try GymHackerPath(labels: urls[2], connections: urls[3], objective: objective)
        var prior: (generation: UInt64, ticks: Int)?
        var results = [ExitFrame]()
        for (index, row) in clip.observations.enumerated() {
            var path: BotObservation?
            let matching = frames[row.captureTimestamp]?.filter { $0.captureGeneration == row.generation }
            if matching?.count == 1, let frame = matching?.first, let key = frame.renderedGameFrame,
               let options = actors[key] {
                let candidates = options.filter { abs($0.receivedTimestamp - row.capturedAt) <= 0.25 }
                if candidates.count == 1, let sample = candidates.first?.sample, sample.sceneName == "Tutorial_01" {
                    let tick = Int((row.captureTimestamp * 60).rounded())
                    if let prior, prior.generation != row.generation || tick <= prior.ticks {
                        guide = try GymHackerPath(labels: urls[2], connections: urls[3], objective: objective)
                    }
                    path = guide.observe(sample, ticks: tick, direction: row.driveDirection ?? 0,
                                         frameSize: CGSize(width: frame.width, height: frame.height))
                    prior = (row.generation, tick)
                }
            }
            if path == nil { guide.interrupt() }
            results.append(.init(index: index, captureTimestamp: row.captureTimestamp, generation: row.generation,
                                 roadHint: path?.roadHint, pathProgress: path?.pathProgress, pathAlignment: path?.pathAlignment))
        }
        return try JSONEncoder().encode(results)
    }

    static func data(directory: URL) throws -> Data {
        let clip = try BotSnippet.load(directory)
        guard clip.error == nil else {
            throw BotRuntimeError.incompatible(clip.error ?? "Invalid demonstration")
        }
        return try JSONEncoder().encode(recover(clip.observations, domain: clip.domain))
    }
}
