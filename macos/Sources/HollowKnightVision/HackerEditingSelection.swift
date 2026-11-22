enum HackerEditingTarget: String, CaseIterable, Identifiable {
    case ground = "Ground"
    case wall = "Wall"
    case spike = "Spike"
    case door = "Door"
    case rooms = "Rooms"
    case path = "Path"
    case objective = "Objective"
    var id: Self { self }
}

extension HackerEditingTarget {
    static let defaultTarget: Self = .objective
    var editsTerrain: Bool { self != .rooms && !editsPaths }
    var editsPaths: Bool { self == .path || self == .objective }
}

import CoreGraphics

enum HackerTerrainRoomGeometry {
    static func editingBounds(roomBounds: CGRect, position: CGPoint, isCurrent: Bool, liveBounds: CGRect,
                              pathBounds: CGRect? = nil) -> CGRect {
        var bounds = roomBounds
        if isCurrent, !liveBounds.isEmpty {
            bounds = bounds.union(liveBounds.offsetBy(dx: -position.x, dy: -position.y))
        }
        // A doorway can begin at the end of an authored platform beyond the
        // captured tiles. Include its boundary in the room's hit region.
        if let pathBounds, !pathBounds.isNull { bounds = bounds.union(pathBounds.insetBy(dx: -4, dy: -4)) }
        return bounds
    }
}

enum HackerTerrainOverlay {
    static func paths(terrain: LayerSceneFeatureReviewOverlay,
                      computed: [LayerSceneFeatureReviewOverlay.Line],
                      selection: [LayerSceneFeatureReviewOverlay.Line]) -> LayerSceneFeatureReviewOverlay {
        .init(markers: [], lines: terrain.lines + computed + selection)
    }
}
