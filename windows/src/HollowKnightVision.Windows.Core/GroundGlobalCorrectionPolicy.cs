namespace HollowKnightVision.Windows.Core;

/// <summary>Descriptor votes propose a pose; independent visible-strip evidence accepts it.</summary>
public static class GroundGlobalCorrectionPolicy
{
    public readonly record struct Quality(int Tested, int Inliers, double HorizontalSpread, double Error);

    public static bool Accepts(Quality? candidate, Quality? current,
        double displacement, bool hasTrustedPose)
    {
        if (candidate is not { } proposed) return false;
        // Returning ground is frequently covered by vegetation, enemies, and
        // the Knight. For a correction smaller than one tile, accept a sparse
        // but wide ordered strip only when it decisively beats the current
        // pose in both independent inliers and bounded whole-strip error.
        if (hasTrustedPose && displacement > 0.01 && displacement <= 12
            && current is { } local
            && proposed.Tested >= 8 && proposed.Inliers >= 8
            && proposed.Inliers * 100 >= proposed.Tested * 30
            && proposed.HorizontalSpread >= 192 && proposed.Error <= 20
            && proposed.Inliers >= local.Inliers + 5
            && proposed.Error + 1 < local.Error * 0.85)
        {
            return true;
        }

        if (proposed.Tested < 4 || proposed.Inliers < 4
            || proposed.Inliers * 100 < proposed.Tested * 65
            || proposed.HorizontalSpread < 48 || proposed.Error > 12)
        {
            return false;
        }
        if (displacement <= 0.01) return true;
        if (!hasTrustedPose) return true;
        if (current is not { } currentQuality)
        {
            // A return can put the drifted local projection on empty pixels,
            // so there is no meaningful current-pose strip to compare. Accept
            // only an independently strong, ordered global strip; weaker
            // phase proposals still fail closed.
            return proposed.Tested >= 8
                && proposed.Inliers >= 8
                && proposed.Inliers * 100 >= proposed.Tested * 75
                && proposed.HorizontalSpread >= 128
                && proposed.Error <= 10;
        }
        // A known ordered ground sequence may remove sub-cell X/Y drift with
        // a smaller improvement than a global jump. The refinement search has
        // already rejected ambiguous alternatives and requires six widely
        // spread tiles, so do not leave a visible seam merely because the
        // locally drifted texture was still passable.
        if (displacement <= 8 && proposed.Tested >= 6 && proposed.Inliers >= 6
            && proposed.HorizontalSpread >= 96)
        {
            return proposed.Error + 0.5 < currentQuality.Error * 0.9;
        }
        // Ordered tiles at the wrong phase must not overrule a better local pose.
        return proposed.Error + 1 < currentQuality.Error * 0.75;
    }
}
