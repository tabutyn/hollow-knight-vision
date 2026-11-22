import CoreGraphics
import Foundation

/// Read the same room-local graph used in Hacker. This command does not modify
/// the saved Atlas, ground labels, or the user's authored jump connections.
enum AtlasRoadInspection {
    static func data(world: URL) throws -> Data {
        let archive = try HackerAtlasArchiveStore.read(from: world)
        let document = try GroundTruthLineStore().load()
        let store = AtlasPathConnectionStore()
        if let issue = store.issue { throw BotRuntimeError.incompatible(issue) }
        let paths = AtlasPathMap(store: store)
        let rooms: [[String: Any]] = archive.rooms.map { room in
            let key = room.id.uuidString
            guard let calibration = AtlasPathCalibration.hacker(room: room, document: document) else {
                return ["scene": room.sceneName, "roomID": key, "tileCount": room.atlasTiles.count,
                        "issue": "No room calibration is available.", "floors": [], "strokes": [], "nodes": []]
            }
            paths.setAuthored(room: key, calibration: calibration, document: document)
            guard let graph = paths.graph(room: key) else {
                return ["scene": room.sceneName, "roomID": key, "tileCount": room.atlasTiles.count,
                        "issue": "No ground geometry is available.", "floors": [], "strokes": [], "nodes": []]
            }
            func xy(_ point: PathPoint) -> [Int] { [point.x, point.y] }
            return [
                "scene": room.sceneName, "roomID": key, "tileCount": room.atlasTiles.count,
                "calibration": ["origin": [calibration.origin.x, calibration.origin.y],
                                "scale": [calibration.scale.x, calibration.scale.y]],
                "knightHeight": graph.knightHeight, "knightWidth": graph.knightWidth,
                "floors": graph.floors.map { [[$0.left, $0.y], [$0.right, $0.y]] },
                "strokes": graph.strokes.map {
                    ["from": xy($0.a), "to": xy($0.b), "kind": $0.kind.rawValue, "forward": $0.forward] as [String: Any]
                },
                "nodes": graph.nodes.keys.sorted().map { point in
                    ["point": xy(point), "neighbors": (graph.nodes[point] ?? []).map(xy)] as [String: Any]
                }
            ]
        }
        return try JSONSerialization.data(withJSONObject: rooms, options: [.sortedKeys])
    }
}
