import Combine
import CoreGraphics
import Foundation

/// Connections belong to a room and use the same room-local coordinates as the platform graph.
final class AtlasPathConnectionStore: ObservableObject {
    private struct WorldPoint: Codable { let x, y: Double }
    private struct SceneConnection: Codable {
        let id: UUID
        let first, second: WorldPoint
        let doubleJump: Bool
    }
    private struct Document: Codable {
        var rooms: [String: [PathConnection]]
        var scenes: [String: [SceneConnection]]
    }
    enum Tool: String, CaseIterable, Identifiable {
        case jump = "Jump"
        case doubleJump = "2 Jump"
        case delete = "Delete"
        var id: Self { self }
    }

    struct Selection {
        let room: String
        let floor: PathPlatform
        let point: PathPoint
    }

    @Published private(set) var selection: Selection?
    @Published private(set) var issue: String?
    @Published private(set) var revision = 0
    private var saved = [String: [PathConnection]]()
    private var scenes = [String: [SceneConnection]]()
    private let file: URL
    private var writable = true

    init(file: URL? = nil) {
        self.file = file ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("HollowKnightVision/atlas-path-connections-v1.json")
        reload()
    }

    /// The live renderer and editor hold separate instances; reload after changing Atlases.
    func reload() {
        do {
            if FileManager.default.fileExists(atPath: file.path) {
                let data = try Data(contentsOf: file)
                if let document = try? JSONDecoder().decode(Document.self, from: data) {
                    saved = document.rooms
                    scenes = document.scenes
                } else {
                    saved = try JSONDecoder().decode([String: [PathConnection]].self, from: data)
                    scenes = [:]
                }
            } else {
                saved = [:]
                scenes = [:]
            }
            issue = nil
            writable = true
        } catch {
            issue = "Path connections could not be loaded: \(error.localizedDescription)"
            writable = false
        }
    }

    func connections(room: String) -> [PathConnection] { saved[room] ?? [] }

    /// Another Atlas or Gym episode can use the same authored scene with its own pixel origin.
    func connections(room: String, scene: String, origin: CGPoint, scale: CGPoint) -> [PathConnection] {
        guard origin.x.isFinite, origin.y.isFinite, scale.x.isFinite, scale.y.isFinite,
              scale.x > 0, scale.y > 0 else { return saved[room] ?? [] }
        let projected = (scenes[scene] ?? []).compactMap { line -> PathConnection? in
            let values = [line.first.x, line.first.y, line.second.x, line.second.y]
            guard values.allSatisfy(\.isFinite) else { return nil }
            func project(_ p: WorldPoint) -> PathPoint {
                PathPoint(CGPoint(x: origin.x + CGFloat(p.x) * scale.x, y: origin.y + CGFloat(p.y) * scale.y))
            }
            var connection = PathConnection(first: project(line.first), second: project(line.second), doubleJump: line.doubleJump)
            connection.id = line.id
            return connection
        }
        guard let exact = saved[room] else { return projected }
        let byID = Dictionary(projected.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        return exact.map { byID[$0.id] ?? $0 }
    }

    func cancelSelection() { selection = nil }

    /// Returns the edited connections when a complete edit was saved.
    func click(room: String, point: CGPoint, tolerance: CGFloat, graph: AtlasPlatformGraph, tool: Tool,
               calibration: AtlasPathCalibration? = nil) -> [PathConnection]? {
        guard point.x.isFinite, point.y.isFinite, tolerance.isFinite, tolerance > 0 else { return nil }
        let position = PathPoint(point)
        let radius = max(1, Int(ceil(tolerance)))
        switch tool {
        case .jump, .doubleJump:
            let floor = graph.floors.min { a, b in
                func distance(_ floor: PathPlatform) -> Int {
                    let x = min(floor.right, max(floor.left, position.x))
                    return abs(position.x-x) + min(abs(position.y-floor.y), abs(position.y-floor.y-graph.knightHeight))
                }
                return distance(a) < distance(b)
            }
            guard let floor else { return nil }
            let x = min(floor.right, max(floor.left, position.x))
            guard abs(position.x-x) + min(abs(position.y-floor.y), abs(position.y-floor.y-graph.knightHeight)) <= radius else { return nil }
            let ground = PathPoint(x, floor.y)
            guard let first = selection, first.room == room else {
                selection = Selection(room: room, floor: floor, point: ground)
                return nil
            }
            guard first.floor != floor else {
                selection = Selection(room: room, floor: floor, point: ground)
                return nil
            }
            let connection = PathConnection(first: first.point, second: ground, doubleJump: tool == .doubleJump)
            var next = calibration.map { connections(room: room, scene: $0.scene, origin: $0.origin, scale: $0.scale) }
                ?? connections(room: room)
            // A pair can have one authored connector. Editing its endpoints or kind replaces it.
            next.removeAll { existing in
                Self.isOn(existing.first, first.floor, graph.knightHeight)
                    && Self.isOn(existing.second, floor, graph.knightHeight)
                || Self.isOn(existing.second, first.floor, graph.knightHeight)
                    && Self.isOn(existing.first, floor, graph.knightHeight)
            }
            next.append(connection)
            guard save(next, room: room, calibration: calibration) else { return nil }
            selection = nil
            return next
        case .delete:
            selection = nil
            let list = calibration.map { connections(room: room, scene: $0.scene, origin: $0.origin, scale: $0.scale) }
                ?? connections(room: room)
            guard let index = list.indices.min(by: {
                Self.distance(position, to: list[$0], height: graph.knightHeight)
                    < Self.distance(position, to: list[$1], height: graph.knightHeight)
            }), Self.distance(position, to: list[index], height: graph.knightHeight) <= radius else { return nil }
            var next = list
            next.remove(at: index)
            return save(next, room: room, calibration: calibration) ? next : nil
        }
    }

    private static func isOn(_ point: PathPoint, _ floor: PathPlatform, _ height: Int) -> Bool {
        floor.contains(point.x) && abs(point.y - floor.y) <= max(4, height / 3)
    }

    private static func distance(_ p: PathPoint, to connection: PathConnection, height: Int) -> Int {
        let first = PathPoint(connection.first.x, connection.first.y + height)
        let second = PathPoint(connection.second.x, connection.second.y + height)
        let top = max(first.y, second.y) + max(2, height / 4)
        let segments = [
            PathStroke(a: first, b: PathPoint(first.x, top), kind: .jump),
            PathStroke(a: PathPoint(first.x, top), b: PathPoint(second.x, top), kind: .jump),
            PathStroke(a: second, b: PathPoint(second.x, top), kind: .jump)
        ]
        return segments.map { $0.closest(p).distance(p) }.min() ?? .max
    }

    private func save(_ connections: [PathConnection], room: String, calibration: AtlasPathCalibration?) -> Bool {
        guard writable else { return false }
        var next = saved
        next[room] = connections
        var nextScenes = scenes
        if let calibration, calibration.origin.x.isFinite, calibration.origin.y.isFinite,
           calibration.scale.x.isFinite, calibration.scale.y.isFinite,
           calibration.scale.x > 0, calibration.scale.y > 0 {
            let prior = self.connections(room: room, scene: calibration.scene, origin: calibration.origin, scale: calibration.scale)
            let replaced = Set(prior.map(\.id))
            var sceneLines = (scenes[calibration.scene] ?? []).filter { !replaced.contains($0.id) }
            func world(_ p: PathPoint) -> WorldPoint {
                WorldPoint(x: (Double(p.x)-Double(calibration.origin.x))/Double(calibration.scale.x),
                           y: (Double(p.y)-Double(calibration.origin.y))/Double(calibration.scale.y))
            }
            sceneLines += connections.map { SceneConnection(id: $0.id, first: world($0.first),
                                                            second: world($0.second), doubleJump: $0.doubleJump) }
            nextScenes[calibration.scene] = sceneLines
        }
        do {
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(Document(rooms: next, scenes: nextScenes)).write(to: file, options: .atomic)
            saved = next
            scenes = nextScenes
            issue = nil
            revision += 1
            return true
        } catch {
            issue = "Path connections could not be saved: \(error.localizedDescription)"
            return false
        }
    }
}
