import CoreGraphics
import Foundation

/// A Hacker restore point retains room pixels and the exact scene camera
/// anchors, independently of the vision atlas and its saved states.
struct HackerAtlasArchive: @unchecked Sendable {
    let rooms: [HackerAtlasRoom]
    let anchors: [String: CGPoint]
    let connections: [HackerRoomConnection]
    let floorAlignedConnectionIDs: Set<UUID>
    let registrationCount: Int
    let createdAt: Date
    var profile: SaveProfileAtlas? = nil
    var roomPositionOverrides = [UUID: CGPoint]()

    static var empty: Self {
        .init(rooms: [], anchors: [:], connections: [], floorAlignedConnectionIDs: [],
              registrationCount: 0, createdAt: Date())
    }

    var autoSaveSummary: AtlasAutoSaveSummary {
        .init(registrationCount: registrationCount, createdAt: createdAt, profile: profile)
    }
}

enum HackerAtlasArchiveStore {
    private struct Tile: Codable {
        let id: Int
        let filename: String
        let bounds: CGRect
    }
    private struct Room: Codable {
        let id: UUID
        let sceneName: String
        let position: CGPoint
        let bounds: CGRect
        let tiles: [Tile]
        var pathCalibration: AtlasPathCalibration? = nil
    }
    private struct Manifest: Codable {
        let version: Int
        let rooms: [Room]
        let anchors: [String: CGPoint]
        let connections: [HackerRoomConnection]
        let floorAlignedConnectionIDs: Set<UUID>
        let registrationCount: Int
        let createdAt: Date
        var profile: SaveProfileAtlas? = nil
        var roomPositionOverrides: [String: CGPoint]? = nil
    }

    static func rootURL(beside world: URL) -> URL {
        world.deletingLastPathComponent().appendingPathComponent("hacker-atlas-v1", isDirectory: true)
    }

    static func write(_ archive: HackerAtlasArchive, to destination: URL) throws {
        let fm = FileManager.default
        let parent = destination.deletingLastPathComponent()
        try fm.createDirectory(at: parent, withIntermediateDirectories: true)
        let staging = parent.appendingPathComponent(".hacker-staging-\(UUID())", isDirectory: true)
        let backup = parent.appendingPathComponent(".hacker-previous-\(UUID())", isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: false)
        defer { try? fm.removeItem(at: staging) }
        var rooms = [Room]()
        for room in archive.rooms {
            var tiles = [Tile]()
            for (index, tile) in room.atlasTiles.enumerated() {
                let filename = "\(room.id.uuidString)-\(index).png"
                guard ImageFileIO.writePNG(tile.image, to: staging.appendingPathComponent(filename)) else {
                    throw LabelingExampleStoreError.imageEncodingFailed
                }
                tiles.append(.init(id: tile.id, filename: filename, bounds: tile.bounds))
            }
            rooms.append(.init(id: room.id, sceneName: room.sceneName, position: room.position,
                bounds: room.bounds, tiles: tiles, pathCalibration:room.pathCalibration))
        }
        let manifest = Manifest(version: 1, rooms: rooms, anchors: archive.anchors,
            connections: archive.connections, floorAlignedConnectionIDs: archive.floorAlignedConnectionIDs,
            registrationCount: archive.registrationCount, createdAt: archive.createdAt, profile: archive.profile,
            roomPositionOverrides: Dictionary(uniqueKeysWithValues: archive.roomPositionOverrides.map {
                ($0.key.uuidString, $0.value)
            }))
        try JSONEncoder().encode(manifest).write(to: staging.appendingPathComponent("hacker.json"), options: .atomic)
        if fm.fileExists(atPath: destination.path) { try fm.moveItem(at: destination, to: backup) }
        do { try fm.moveItem(at: staging, to: destination) }
        catch {
            if fm.fileExists(atPath: backup.path) { try fm.moveItem(at: backup, to: destination) }
            throw error
        }
        if fm.fileExists(atPath: backup.path) { try fm.removeItem(at: backup) }
    }

    static func read(from source: URL) throws -> HackerAtlasArchive {
        let manifest = try JSONDecoder().decode(Manifest.self,
            from: Data(contentsOf: source.appendingPathComponent("hacker.json")))
        guard manifest.version == 1, manifest.registrationCount >= 0,
              Set(manifest.rooms.map(\.id)).count == manifest.rooms.count,
              Set(manifest.rooms.map(\.sceneName)).count == manifest.rooms.count,
              Set(manifest.connections.map(\.id)).count == manifest.connections.count else {
            throw AtlasStateStoreError.missingState
        }
        let ids = Set(manifest.rooms.map(\.id))
        guard manifest.connections.allSatisfy({ ids.contains($0.fromRoomID) && ids.contains($0.toRoomID)
            && $0.fromCoordinate.isFinite && $0.toCoordinate.isFinite }),
              manifest.floorAlignedConnectionIDs.isSubset(of: Set(manifest.connections.map(\.id))) else {
            throw AtlasStateStoreError.missingState
        }
        func finite(_ point: CGPoint) -> Bool { point.x.isFinite && point.y.isFinite }
        var overrides = [UUID: CGPoint]()
        for (key, position) in manifest.roomPositionOverrides ?? [:] {
            guard let id = UUID(uuidString: key), ids.contains(id), finite(position),
                  overrides[id] == nil else { throw AtlasStateStoreError.missingState }
            overrides[id] = position
        }
        let rooms = try manifest.rooms.map { room -> HackerAtlasRoom in
            guard let anchor = manifest.anchors[room.sceneName], finite(anchor), finite(room.position),
                  finite(room.bounds.origin), room.bounds.width.isFinite, room.bounds.height.isFinite,
                  room.bounds.width > 0, room.bounds.height > 0 else { throw AtlasStateStoreError.missingState }
            let tiles = try room.tiles.map { tile -> LiveAtlasOutput.AtlasLayer in
                guard tile.filename == URL(fileURLWithPath: tile.filename).lastPathComponent,
                      finite(tile.bounds.origin), tile.bounds.width > 0, tile.bounds.height > 0,
                      let image = ImageFileIO.load(source.appendingPathComponent(tile.filename)),
                      tile.bounds.width == CGFloat(image.width), tile.bounds.height == CGFloat(image.height) else {
                    throw AtlasStateStoreError.missingState
                }
                return .init(id: tile.id, image: image, bounds: tile.bounds, revision: 0, contentBounds: room.bounds)
            }
            return .init(id: room.id, sceneName: room.sceneName, position: room.position,
                bounds: room.bounds, atlasTiles: tiles,
                botCameraOrigin: CGPoint(x:anchor.x+room.position.x,y:anchor.y+room.position.y),pathCalibration:room.pathCalibration)
        }
        return .init(rooms: rooms, anchors: manifest.anchors, connections: manifest.connections,
            floorAlignedConnectionIDs: manifest.floorAlignedConnectionIDs,
            registrationCount: manifest.registrationCount, createdAt: manifest.createdAt,
            profile: manifest.profile, roomPositionOverrides: overrides)
    }
}
