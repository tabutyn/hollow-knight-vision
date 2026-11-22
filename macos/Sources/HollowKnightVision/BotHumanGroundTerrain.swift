import CoreGraphics
import Foundation

enum BotHumanGroundProjection {
    static func geometry(lines: [GroundTruthWorldLine], transform: GroundTruthCameraTransform,
                         frameSize: CGSize) -> BotScreenTerrain {
        var geometry = BotScreenTerrain(width: frameSize.width, height: frameSize.height)
        geometry.spikes = []; geometry.doors = []
        guard transform.frameSize.width > 0, transform.frameSize.height > 0,
              transform.pixelsPerWorldUnitX.isFinite, transform.pixelsPerWorldUnitY.isFinite,
              transform.pixelsPerWorldUnitX > 0, transform.pixelsPerWorldUnitY > 0,
              transform.cameraWorld.x.isFinite, transform.cameraWorld.y.isFinite,
              frameSize.width.isFinite, frameSize.height.isFinite,
              frameSize.width > 0, frameSize.height > 0 else { return geometry }
        let bounds = CGRect(origin: .zero, size: transform.frameSize)
        let scaleX = frameSize.width / bounds.width, scaleY = frameSize.height / bounds.height
        for line in lines where !line.seededFromDetector && line.sceneName == transform.sceneName {
            let a = transform.worldToAtlas(line.start, liveBounds: bounds)
            let b = transform.worldToAtlas(line.end, liveBounds: bounds)
            guard [a.x, a.y, b.x, b.y].allSatisfy(\.isFinite) else { continue }
            switch line.kind {
            case .positive:
                let y = a.y * scaleY
                let left = max(0, min(a.x, b.x) * scaleX), right = min(frameSize.width, max(a.x, b.x) * scaleX)
                if (0...frameSize.height).contains(y), left < right {
                    geometry.floors.append(.init(minimumX: left, maximumX: right, y: y, confidence: 1))
                }
            case .wall:
                let x = a.x * scaleX
                let low = max(0, min(a.y, b.y) * scaleY), high = min(frameSize.height, max(a.y, b.y) * scaleY)
                if (0...frameSize.width).contains(x), low < high {
                    // The editor marks a face without declaring which side is solid.
                    geometry.walls.append(.init(x: x, bottomY: low, topY: high, solidSide: 0))
                }
            case .door:
                let y = a.y * scaleY
                let left = max(0, min(a.x, b.x) * scaleX), right = min(frameSize.width, max(a.x, b.x) * scaleX)
                if (0...frameSize.height).contains(y), left < right {
                    geometry.doors = (geometry.doors ?? []) + [.init(x1: left, y1: y, x2: right, y2: y)]
                }
            case .doorVertical:
                let x = a.x * scaleX
                let low = max(0, min(a.y, b.y) * scaleY), high = min(frameSize.height, max(a.y, b.y) * scaleY)
                if (0...frameSize.width).contains(x), low < high {
                    geometry.doors = (geometry.doors ?? []) + [.init(x1: x, y1: low, x2: x, y2: high)]
                }
            case .spike:
                let y = a.y * scaleY
                let left = max(0, min(a.x, b.x) * scaleX), right = min(frameSize.width, max(a.x, b.x) * scaleX)
                if (0...frameSize.height).contains(y), left < right {
                    geometry.spikes?.append(.init(x1: left, y1: y, x2: right, y2: y))
                }
            case .negative: continue
            }
        }
        return geometry
    }

    static func segments(lines: [GroundTruthWorldLine], transform: GroundTruthCameraTransform,
                         frameSize: CGSize) -> [BotTerrainSegment] {
        geometry(lines: lines, transform: transform, frameSize: frameSize).floors
    }
}

/// Frame-matched cameras prevent a later Hacker frame or another capture
/// generation from projecting labels onto an earlier policy observation.
final class BotHumanGroundTerrainCache: @unchecked Sendable {
    private struct Camera {
        let timestamp: Double
        let generation: UInt64
        let transform: GroundTruthCameraTransform
    }
    private let lock = NSLock()
    private var linesByScene = [String: [GroundTruthWorldLine]]()
    private var doorsByScene = [String: [GroundTruthWorldLine]]()
    private var wallsByScene = [String: [GroundTruthWorldLine]]()
    private var spikesByScene = [String: [GroundTruthWorldLine]]()
    private var document = GroundTruthLineDocument()
    var snapshot: GroundTruthLineDocument { lock.lock(); defer { lock.unlock() }; return document }
    private var cameras = [Camera]()
    private var lastQueryTimestamp: Double?
    private var lastQueryGeneration: UInt64?
    private var lastQueryMatched = false

    var diagnostic: [String: Any] {
        lock.lock(); defer { lock.unlock() }
        var result: [String: Any] = ["cameraCount": cameras.count, "matched": lastQueryMatched]
        if let camera = cameras.last {
            result["latestCameraScene"] = camera.transform.sceneName
            result["latestCameraTimestamp"] = camera.timestamp
            result["latestCameraWorld"] = [Double(camera.transform.cameraWorld.x), Double(camera.transform.cameraWorld.y)]
        }
        if let timestamp = lastQueryTimestamp {
            result["queryTimestamp"] = timestamp
            if let generation = lastQueryGeneration { result["queryGeneration"] = generation }
            if let delta = cameras.map({ abs($0.timestamp - timestamp) }).min() { result["nearestCameraDelta"] = delta }
        }
        return result
    }

    var lineCount: Int {
        lock.lock(); defer { lock.unlock() }
        return linesByScene.values.reduce(0) { $0 + $1.count }
    }
    func update(document: GroundTruthLineDocument) {
        let lines = document.lines.filter { $0.kind == .positive && !$0.seededFromDetector }
        let walls = (document.walls ?? []).filter { $0.kind == .wall && !$0.seededFromDetector }
        lock.lock(); defer { lock.unlock() }
        self.document = document
        linesByScene = Dictionary(grouping: lines, by: \.sceneName)
        wallsByScene = Dictionary(grouping: walls, by: \.sceneName)
        spikesByScene = Dictionary(grouping: (document.spikes ?? []).filter { !$0.seededFromDetector }, by: \.sceneName)
        doorsByScene = Dictionary(grouping: (document.doors ?? []).filter { !$0.seededFromDetector }, by: \.sceneName)
    }
    func matchedCamera(timestamp: Double, generation: UInt64) -> GroundTruthCameraTransform? {
        lock.lock(); defer { lock.unlock() }
        return cameras.last { $0.generation == generation && $0.timestamp == timestamp }?.transform
    }

    func auditProjection(timestamp: Double, generation: UInt64, frameSize: CGSize) -> (lines: [BotTerrainSegment], scene: String?, available: Bool) {
        lock.lock()
        guard let camera = cameras.last(where: { $0.generation == generation && abs($0.timestamp - timestamp) < 0.000_001 }) else {
            lock.unlock(); return ([], nil, false)
        }
        let reference = linesByScene[camera.transform.sceneName]
        lock.unlock()
        return (BotHumanGroundProjection.segments(lines: reference ?? [], transform: camera.transform, frameSize: frameSize),
                camera.transform.sceneName, reference != nil)
    }
    func geometry(transform: GroundTruthCameraTransform, frameSize: CGSize,
                  timestamp: Double, generation: UInt64) -> BotScreenTerrain {
        lock.lock()
        lastQueryTimestamp = timestamp; lastQueryGeneration = generation
        lastQueryMatched = cameras.contains { $0.timestamp == timestamp && $0.generation == generation }
        let lines = (linesByScene[transform.sceneName] ?? []) + (wallsByScene[transform.sceneName] ?? []) + (doorsByScene[transform.sceneName] ?? []) + (spikesByScene[transform.sceneName] ?? [])
        lock.unlock()
        return BotHumanGroundProjection.geometry(lines: lines, transform: transform, frameSize: frameSize)
    }
    func record(transform: GroundTruthCameraTransform, timestamp: Double, generation: UInt64) {
        guard timestamp.isFinite else { return }
        lock.lock(); defer { lock.unlock() }
        cameras.append(Camera(timestamp: timestamp, generation: generation, transform: transform))
        // Audit consumers may arrive after a slow visual solve. Exact capture
        // identity is still required; retaining cameras never relaxes bot freshness.
        if cameras.count > 600 { cameras.removeFirst(cameras.count - 600) }
    }
    func clearCameras() { lock.lock(); cameras = []; lock.unlock() }

    func segments(timestamp: Double, generation: UInt64, frameSize: CGSize) -> [BotTerrainSegment] {
        lock.lock()
        lastQueryTimestamp = timestamp; lastQueryGeneration = generation; lastQueryMatched = false
        guard let camera = cameras.last(where: {
            $0.generation == generation && abs($0.timestamp - timestamp) < 0.000_001
        }) else { lock.unlock(); return [] }
        lastQueryMatched = true
        let lines = linesByScene[camera.transform.sceneName] ?? []
        lock.unlock()
        return BotHumanGroundProjection.segments(lines: lines, transform: camera.transform, frameSize: frameSize)
    }
}
