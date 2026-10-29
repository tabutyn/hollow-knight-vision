namespace HollowKnightVision.Windows.Core;

/// <summary>Verify the broad match with independently registered, spatially separated patches.</summary>
internal static class GroundPatchConsensus
{
    internal sealed record Result(int Dx, int Dy, double Error, GroundCameraSolver.Patch[] Patches);
    private sealed record Match(GroundCameraSolver.Patch Patch, int Dx, int Dy, double Error);
    internal static Result? Refine(IReadOnlyList<GroundCameraSolver.Patch> patches, byte[] pixels,
        int width, int height, int coarseX, int coarseY, IReadOnlyList<StencilRect> exclusions, bool allowVerticalSpread = false)
    {
        var matches = new List<Match>();
        foreach (var patch in patches)
        {
            var scores = new List<Match>(81);
            for (var dy = coarseY - 4; dy <= coarseY + 4; dy++)
            for (var dx = coarseX - 4; dx <= coarseX + 4; dx++)
            {
                var x = patch.X + dx; var y = patch.Y + dy;
                if (x < 0 || y < 0 || x + 16 > width || y + 12 > height) continue;
                var excluded = false;
                for (var i = 0; i < exclusions.Count; i++)
                {
                    var r = exclusions[i];
                    if (r.IsFinite && !r.IsEmpty && r.X < x + 16 && r.Right > x && r.Y < y + 12 && r.Bottom > y)
                    { excluded = true; break; }
                }
                if (excluded) continue;
                scores.Add(new(patch, dx, dy, GroundCameraSolver.PatchError(patch, pixels, width, x, y)));
            }
            var best = scores.MinBy(value => value.Error);
            if (best is null || best.Error > 12) continue;
            var rival = scores.Where(value => Math.Abs(value.Dx - best.Dx) > 2 || Math.Abs(value.Dy - best.Dy) > 2).MinBy(value => value.Error);
            if (rival is not null && rival.Error <= best.Error * 1.12 + .25) continue;
            matches.Add(best);
        }
        if (matches.Count < 4) return null;
        // A mean over all patches compromises between foreground and floor.
        // Choose the largest independent motion consensus before subpixel fitting.
        var group = matches.Select(seed => matches.Where(value =>
            Math.Abs(value.Dx - seed.Dx) <= 1 && Math.Abs(value.Dy - seed.Dy) <= 1).ToArray())
            .OrderByDescending(values => values.Length).ThenBy(values => values.Average(value => value.Error)).First();
        if (group.Length < 4 || group.Length * 2 <= matches.Count
            || (group.Max(value => value.Patch.X) - group.Min(value => value.Patch.X) < 32
                && (!allowVerticalSpread || group.Max(value => value.Patch.Y) - group.Min(value => value.Patch.Y) < 32))) return null;
        var xMedian = group.Select(value => value.Dx).Order().ElementAt(group.Length / 2);
        var yMedian = group.Select(value => value.Dy).Order().ElementAt(group.Length / 2);
        var inliers = group.Select(value => value.Patch).Where(patch => patch.X + xMedian >= 0 && patch.Y + yMedian >= 0 && patch.X + xMedian + 16 <= width && patch.Y + yMedian + 12 <= height && !exclusions.Any(r => r.IsFinite && !r.IsEmpty && r.X < patch.X + xMedian + 16 && r.Right > patch.X + xMedian && r.Y < patch.Y + yMedian + 12 && r.Bottom > patch.Y + yMedian)).ToArray();
        if (inliers.Length < 4) return null;
        var error = inliers.Average(patch => GroundCameraSolver.PatchError(patch, pixels, width, patch.X + xMedian, patch.Y + yMedian));
        return new(xMedian, yMedian, error, inliers);
    }
}
