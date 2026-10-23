namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Refines a recognized ground sequence inside one 16-pixel lattice cell.
/// Spatially distinct near-ties are rejected instead of guessed.
/// </summary>
public static class GroundPoseRefinementSearch
{
    public readonly record struct Offset(int Dx, int Dy);

    public static Offset? Find(
        Func<int, int, GroundGlobalCorrectionPolicy.Quality?> quality,
        int radius = 8)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var candidates = new List<(Offset Offset, GroundGlobalCorrectionPolicy.Quality Quality)>();
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (quality(dx, dy) is not { } value
                    || value.Tested < 6 || value.Inliers < 6
                    || value.Inliers * 100 < value.Tested * 65
                    || value.HorizontalSpread < 96 || value.Error > 12)
                {
                    continue;
                }
                candidates.Add((new Offset(dx, dy), value));
            }
        }
        candidates.Sort((left, right) =>
        {
            var comparison = left.Quality.Error.CompareTo(right.Quality.Error);
            if (comparison != 0) return comparison;
            comparison = right.Quality.Inliers.CompareTo(left.Quality.Inliers);
            if (comparison != 0) return comparison;
            return Length(left.Offset).CompareTo(Length(right.Offset));
        });
        if (candidates.Count == 0) return null;
        var best = candidates[0];
        var rival = candidates.FirstOrDefault(candidate =>
            Distance(candidate.Offset, best.Offset) > 2);
        if (rival != default
            && rival.Quality.Error <= best.Quality.Error * 1.12 + .25)
        {
            return null;
        }
        return best.Offset;
    }

    private static double Length(Offset value) => Math.Sqrt(
        (double)value.Dx * value.Dx + (double)value.Dy * value.Dy);

    private static double Distance(Offset left, Offset right) => Math.Sqrt(
        (double)(left.Dx - right.Dx) * (left.Dx - right.Dx)
        + (double)(left.Dy - right.Dy) * (left.Dy - right.Dy));
}
