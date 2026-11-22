import CoreGraphics

/// Display-only footprints and connectors derived from one immutable world
/// revision. Estimated spacing never becomes a motion/pose-graph constraint.
enum LiveIslandOverlayGeometry {
    static func visibleOverlay(_ overlay: LayerSceneFeatureReviewOverlay,
                              showsEstimatedPlacement: Bool,
                              hasGameplayStarted: Bool) -> LayerSceneFeatureReviewOverlay {
        guard hasGameplayStarted else { return .init(markers: [], lines: []) }
        return .init(markers: overlay.markers, lines: overlay.lines.filter {
            showsEstimatedPlacement || $0.kind != .provisionalIsland
        })
    }

    static func make(snapshot: LiveWorldSnapshot, scale: CGFloat,
                     anchor: CGPoint) -> LayerSceneFeatureReviewOverlay {
        guard scale.isFinite, scale > 0, anchor.x.isFinite, anchor.y.isFinite else {
            return LayerSceneFeatureReviewOverlay(markers: [], lines: [])
        }
        let observations = Dictionary(uniqueKeysWithValues: snapshot.observations.map { ($0.id, $0) })
        func point(_ value: CGPoint) -> CGPoint {
            CGPoint(x: (value.x - anchor.x) * scale, y: (value.y - anchor.y) * scale)
        }
        var lines = [LayerSceneFeatureReviewOverlay.Line]()
        // Show passage evidence first; huge imported legacy atlases cannot crowd
        // out these links. The renderer separately bounds dashed-quad work.
        for connector in snapshot.ownedDoorwayConnectors.suffix(256) {
            lines.append(.init(start: point(connector.start), end: point(connector.end),
                               kind: .doorwayFloor))
        }
        for island in snapshot.trackingIslands.filter(\.isProvisional).suffix(512) {
            var bounds = CGRect.null
            for member in island.members {
                guard let observation = observations[member.observationID] else { continue }
                bounds = bounds.union(CGRect(origin: observation.optimizedPose,
                    size: CGSize(width: observation.solveWidth,
                        height: CGFloat(observation.sourceHeight) / CGFloat(observation.sourceWidth)
                            * observation.solveWidth)))
            }
            guard !bounds.isNull, !bounds.isInfinite, bounds.width > 0, bounds.height > 0 else { continue }
            let corners = [CGPoint(x: bounds.minX, y: bounds.minY),
                           CGPoint(x: bounds.maxX, y: bounds.minY),
                           CGPoint(x: bounds.maxX, y: bounds.maxY),
                           CGPoint(x: bounds.minX, y: bounds.maxY)]
            for index in 0..<4 {
                lines.append(.init(start: point(corners[index]), end: point(corners[(index + 1) % 4]),
                                   kind: .provisionalIsland))
            }
        }
        return LayerSceneFeatureReviewOverlay(markers: [], lines: lines)
    }
}
