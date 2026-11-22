import CoreGraphics

extension TerrainWallDetection {
    func overlay(project: (CGPoint) -> CGPoint) -> LayerSceneFeatureReviewOverlay {
        func point(_ x: Int, _ y: Int) -> CGPoint {
            project(CGPoint(x: CGFloat(x) + 0.5, y: CGFloat(y) + 0.5))
        }
        // Wall detections remain available to the bot, but their vertical strokes
        // are hidden in Gameplay until the visual detector is improved.
        var lines: [LayerSceneFeatureReviewOverlay.Line] = []
        if let floor = supportedFloor {
            lines.append(.init(start: point(floor.xRange.lowerBound, floor.row),
                               end: point(floor.xRange.upperBound, floor.row), kind: .currentGround, coordinateSpace: .live))
        }
        for corner in corners where corner.floorEndpoint != corner.x {
            lines.append(.init(start: point(corner.floorEndpoint, corner.y), end: point(corner.x, corner.y),
                               kind: .detectedTerrain, coordinateSpace: .live))
        }
        return .init(markers: corners.map {
            .init(worldPosition: point($0.x, $0.y), kind: .detectedTerrainCorner,
                  worldSize: CGSize(width: 3, height: 3), coordinateSpace: .live)
        }, lines: lines)
    }
}
