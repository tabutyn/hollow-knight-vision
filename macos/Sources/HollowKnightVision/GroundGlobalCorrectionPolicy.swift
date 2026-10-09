import CoreGraphics

/// Descriptor votes propose a pose; independent visible-strip evidence accepts it.
enum GroundGlobalCorrectionPolicy {
    struct Quality {
        let tested: Int
        let inliers: Int
        let horizontalSpread: CGFloat
        let error: CGFloat
    }

    static func accepts(candidate: Quality?, current: Quality?,
                        displacement: CGFloat, hasTrustedPose: Bool) -> Bool {
        guard let candidate else { return false }
        // Returning ground is frequently covered by vegetation, enemies, and
        // the Knight. For a correction smaller than one tile, accept a sparse
        // but wide ordered strip only when it decisively beats the current
        // pose in both independent inliers and bounded whole-strip error.
        if hasTrustedPose, displacement > 0.01, displacement <= 12,
           let current,
           candidate.tested >= 8, candidate.inliers >= 8,
           candidate.inliers * 100 >= candidate.tested * 30,
           candidate.horizontalSpread >= 192, candidate.error <= 20,
           candidate.inliers >= current.inliers + 5,
           candidate.error + 1 < current.error * 0.85 {
            return true
        }
        guard candidate.tested >= 4, candidate.inliers >= 4,
              candidate.inliers * 100 >= candidate.tested * 65,
              candidate.horizontalSpread >= 48, candidate.error <= 12 else { return false }
        if displacement <= 0.01 { return true }
        guard hasTrustedPose else { return true }
        guard let current else {
            // A return can put the drifted local projection on empty pixels,
            // so there is no meaningful current-pose strip to compare. Accept
            // only an independently strong, ordered global strip; weaker
            // phase proposals still fail closed.
            return candidate.tested >= 8
                && candidate.inliers >= 8
                && candidate.inliers * 100 >= candidate.tested * 75
                && candidate.horizontalSpread >= 128
                && candidate.error <= 10
        }
        // A known ordered ground sequence may remove sub-cell X/Y drift with
        // a smaller improvement than a global jump. The refinement search has
        // already rejected ambiguous alternatives and requires six widely
        // spread tiles, so do not leave a visible seam merely because the
        // locally drifted texture was still passable.
        if displacement <= 8, candidate.tested >= 6, candidate.inliers >= 6,
           candidate.horizontalSpread >= 96 {
            return candidate.error + 0.5 < current.error * 0.9
        }
        // Ordered tiles at the wrong phase must not overrule a better local pose.
        return candidate.error + 1 < current.error * 0.75
    }
}
