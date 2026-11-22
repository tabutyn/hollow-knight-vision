import Foundation
import CoreGraphics

/// Bottom-left capture pixels, using the exact camera that rendered this frame.
struct BotScreenTerrain: Codable, Equatable {
    let width: Double
    let height: Double
    var floors = [BotTerrainSegment]()
    var walls = [BotWallSegment]()
    // Optional so recordings made before door labeling remain decodable.
    var doors: [BotDoorSegment]? = nil
    // nil means unavailable in historical/visual evidence; [] means observed and empty.
    var spikes: [BotDoorSegment]? = nil

    static func stitchedFloors(_ segments: [BotTerrainSegment], walls: [DetectedWall], frameHeight: Int,
                               knightHeight: Int, knightWidth: Int, surfaces: [CleanFloorLine] = []) -> [BotTerrainSegment] {
        guard segments.count > 1 else { return segments }
        let spans = segments.map {
            PathPlatform(left: Int($0.minimumX), right: Int($0.maximumX), y: Int($0.y))
        }
        let barriers = walls.map {
            PathWall(x: $0.x, low: frameHeight - $0.bottom, high: frameHeight - $0.top, solidSide: $0.solidOnRight ? 1 : -1)
        }
        // Only intervals between consecutive evidence runs of one accepted
        // surface can bridge a wider opening. Separate ledges supply no seam.
        let seams = surfaces.flatMap { floor in
            zip(floor.evidenceRanges, floor.evidenceRanges.dropFirst()).map { left, right in
                PathPlatform(left: left.lowerBound, right: right.upperBound + 1, y: frameHeight - floor.row)
            }
        }
        let stitched = AtlasPlatformGraph.stitchDetected(spans, walls: barriers, height: knightHeight,
                                                        width: knightWidth, supportedSeams: seams)
        return stitched.map { f in
            let confidence = zip(spans, segments).compactMap { s, sample in
                s.y == f.y && s.right > f.left && s.left < f.right ? sample.confidence : nil
            }.max() ?? 0
            return .init(minimumX: Double(f.left), maximumX: Double(f.right), y: Double(f.y), confidence: confidence)
        }
    }
}

struct BotDoorSegment: Codable, Equatable {
    let x1, y1, x2, y2: Double
}

/// Row zero is the bottom of the screen. Floor and wall channels are independent,
/// so both survive when a platform and wall meet in the same cell.
enum BotTerrainGrid {
    static let columns = 24, rows = 14
    static let cells = columns * rows
    static let names = ["floor", "wall"].flatMap { kind in
        (0..<cells).map { "screen_\(kind)_\($0)" }
    }

    static func features(_ geometry: BotScreenTerrain?) -> [Float] {
        var values = [Float](repeating: 0, count: 2 * cells)
        guard let geometry, geometry.width.isFinite, geometry.height.isFinite,
              geometry.width > 0, geometry.height > 0 else { return values }
        func column(_ x: Double) -> Int { min(columns - 1, Int(max(0, min(1, x / geometry.width)) * Double(columns))) }
        func row(_ y: Double) -> Int { min(rows - 1, Int(max(0, min(1, y / geometry.height)) * Double(rows))) }
        for floor in geometry.floors where floor.minimumX.isFinite && floor.maximumX.isFinite && floor.y.isFinite
            && floor.minimumX < floor.maximumX && floor.maximumX > 0 && floor.minimumX < geometry.width
            && (0...geometry.height).contains(floor.y) {
            let base = row(floor.y) * columns
            for x in column(floor.minimumX)...column(floor.maximumX) { values[base + x] = 1 }
        }
        for wall in geometry.walls where wall.x.isFinite && wall.bottomY.isFinite && wall.topY.isFinite
            && wall.bottomY < wall.topY && wall.topY > 0 && wall.bottomY < geometry.height
            && (0...geometry.width).contains(wall.x) {
            let base = cells + column(wall.x)
            for y in row(wall.bottomY)...row(wall.topY) { values[base + y * columns] = 1 }
        }
        return values
    }
}

// A separate appended channel preserves every earlier policy schema.
extension BotTerrainGrid {
    static let doorNames = (0..<cells).map { "screen_door_\($0)" }
    static func doorFeatures(_ geometry: BotScreenTerrain?) -> [Float] {
        var result = [Float](repeating: 0, count: cells)
        guard let geometry, geometry.width.isFinite, geometry.height.isFinite, geometry.width > 0, geometry.height > 0 else { return result }
        func col(_ x: Double) -> Int { min(columns - 1, Int(max(0, min(1, x / geometry.width)) * Double(columns))) }
        func row(_ y: Double) -> Int { min(rows - 1, Int(max(0, min(1, y / geometry.height)) * Double(rows))) }
        for d in geometry.doors ?? [] where [d.x1, d.y1, d.x2, d.y2].allSatisfy(\.isFinite) {
            if d.y1 == d.y2, (0...geometry.height).contains(d.y1), max(d.x1, d.x2) > 0, min(d.x1, d.x2) < geometry.width {
                for x in col(min(d.x1, d.x2))...col(max(d.x1, d.x2)) { result[row(d.y1) * columns + x] = 1 }
            } else if d.x1 == d.x2, (0...geometry.width).contains(d.x1), max(d.y1, d.y2) > 0, min(d.y1, d.y2) < geometry.height {
                for y in row(min(d.y1, d.y2))...row(max(d.y1, d.y2)) { result[y * columns + col(d.x1)] = 1 }
            }
        }
        return result
    }
}

/// Project only already-observed visual portals from the same room and camera frame.
enum BotVisualPortalProjection {
    static func doors(_ portals: [VisualRoomPortal], roomID: Int, camera: CGPoint,
                      solveWidth: CGFloat, frameSize: CGSize) -> [BotDoorSegment] {
        guard camera.x.isFinite, camera.y.isFinite, solveWidth.isFinite, solveWidth > 0,
              frameSize.width.isFinite, frameSize.height.isFinite,
              frameSize.width > 0, frameSize.height > 0 else { return [] }
        let scale = frameSize.width / solveWidth
        return portals.compactMap { portal in
            let worldX: CGFloat
            let range: ClosedRange<CGFloat>
            if portal.leftRoomID == roomID { worldX = portal.leftDoorWorldX; range = portal.leftDoorYRange }
            else if portal.rightRoomID == roomID { worldX = portal.rightDoorWorldX; range = portal.rightDoorYRange }
            else { return nil }
            guard worldX.isFinite, range.lowerBound.isFinite, range.upperBound.isFinite else { return nil }
            let x = (worldX - camera.x) * scale
            let low = max(0, (range.lowerBound - camera.y) * scale)
            let high = min(frameSize.height, (range.upperBound - camera.y) * scale)
            guard (0...frameSize.width).contains(x), low < high else { return nil }
            return .init(x1: x, y1: low, x2: x, y2: high)
        }
    }
}
