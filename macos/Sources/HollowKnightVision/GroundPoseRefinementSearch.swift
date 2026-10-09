import CoreGraphics

/// Refines a recognized ground sequence inside one 16-pixel lattice cell.
/// The caller scores immutable ground texture at every proposed X/Y pose;
/// spatially distinct near-ties are rejected instead of guessing.
enum GroundPoseRefinementSearch {
    struct Offset: Equatable {
        let dx: Int
        let dy: Int
    }

    static func offset(
        radius: Int = 8,
        quality: (Int, Int) -> GroundGlobalCorrectionPolicy.Quality?
    ) -> Offset? {
        precondition(radius >= 0)
        var candidates = [(offset: Offset, quality: GroundGlobalCorrectionPolicy.Quality)]()
        for dy in -radius...radius {
            for dx in -radius...radius {
                guard let value = quality(dx, dy),
                      value.tested >= 6, value.inliers >= 6,
                      value.inliers * 100 >= value.tested * 65,
                      value.horizontalSpread >= 96, value.error <= 12 else { continue }
                candidates.append((Offset(dx: dx, dy: dy), value))
            }
        }
        candidates.sort {
            if $0.quality.error != $1.quality.error {
                return $0.quality.error < $1.quality.error
            }
            if $0.quality.inliers != $1.quality.inliers {
                return $0.quality.inliers > $1.quality.inliers
            }
            return hypot(CGFloat($0.offset.dx), CGFloat($0.offset.dy))
                < hypot(CGFloat($1.offset.dx), CGFloat($1.offset.dy))
        }
        guard let best = candidates.first else { return nil }
        if let rival = candidates.first(where: {
            hypot(CGFloat($0.offset.dx - best.offset.dx),
                  CGFloat($0.offset.dy - best.offset.dy)) > 2
        }), rival.quality.error <= best.quality.error * 1.12 + 0.25 {
            return nil
        }
        return best.offset
    }
}
