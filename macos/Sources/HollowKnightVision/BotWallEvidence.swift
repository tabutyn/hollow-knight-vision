import CoreGraphics
import Foundation

struct BotWallSegment: Codable, Equatable {
    let x: Double
    let bottomY: Double
    let topY: Double
    let solidSide: Int
}

enum BotWallEvidence {
    static func detect(image: CGImage, excluding: [CGRect], knight: CGRect?,
                       tuning: GroundTheoryTuning) -> [DetectedWall]? {
        guard let analysis = GroundLineDetector.analyze(image, excluding: excluding) else { return nil }
        let comparison = GroundLineDetector.compare(analysis)
        let lines = GroundLineDetector.semanticTheory(from: comparison, tuning: tuning)
            .map { GroundLineDetector.cleanFloorLines(from: $0) } ?? []
        let floors = GroundLineDetector.gameplayFloors(lines, imageHeight: image.height,
            foregroundRects: excluding, knightRect: knight)
        return TerrainWallDetector.detect(analysis, floors: floors).walls
    }

    static func features(walls: [DetectedWall]?, knightX: Double, knightY: Double,
                         width: Int, height: Int) -> [Double] {
        let segments = walls.map { Self.segments(from: $0, height: height) }
        return features(segments: segments, knightX: knightX, knightY: knightY, width: Double(width))
    }

    static func segments(from walls: [DetectedWall], height: Int) -> [BotWallSegment] {
        walls.map {
            BotWallSegment(x: Double($0.x), bottomY: Double(height - $0.bottom),
                           topY: Double(height - $0.top), solidSide: $0.solidOnRight ? 1 : -1)
        }
    }

    /// The closest face on each side of the Knight, in bottom-left capture coordinates.
    static func features(segments: [BotWallSegment]?, knightX: Double, knightY: Double,
                         width: Double) -> [Double] {
        var values = Array(repeating: 0.0, count: BotObservationContract.wallFeatureNames.count)
        guard let segments, width > 0, knightX.isFinite, knightY.isFinite else { return values }
        values[0] = 1
        let scale = Double(width), centerX = knightX * scale, centerY = knightY * scale
        var nearest = [Double.infinity, Double.infinity]
        for wall in segments where wall.bottomY < wall.topY {
            let dx = Double(wall.x) - centerX
            let low = wall.bottomY - centerY, high = wall.topY - centerY
            let side = dx < 0 ? 0 : 1
            let distance = hypot(dx, max(0, low, -high))
            guard distance < nearest[side] else { continue }
            nearest[side] = distance
            let start = 1 + 5 * side
            values.replaceSubrange(start..<(start + 5), with: [
                dx / scale, low / scale, high / scale, Double(wall.solidSide), 1,
            ])
        }
        return values.map { min(4, max(-4, $0)) }
    }
}
