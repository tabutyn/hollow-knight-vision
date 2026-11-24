namespace HollowKnightVision.Windows.Core;

/// <summary>Verify the broad match with independently registered, spatially separated patches.</summary>
internal static class GroundPatchConsensus
{
    internal sealed record Result(int Dx, int Dy, double Error, GroundCameraSolver.Patch[] Patches);
    private sealed record Match(GroundCameraSolver.Patch Patch, int Dx, int Dy, double Error);
    internal static Result? Refine(IReadOnlyList<GroundCameraSolver.Patch> patches, byte[] pixels,
        int width, int height, int coarseX, int coarseY, IReadOnlyList<StencilRect> exclusions, bool allowVerticalSpread = false)
    {
        const int patchWidth = GroundPixels.PatchWidth, patchHeight = GroundPixels.PatchHeight;
        if (width < patchWidth || height < patchHeight || pixels.Length != (long)width * height
            || patches.Count is < 4 or > 32 || coarseX < -width || coarseX > width
            || coarseY < -height || coarseY > height) return null;

        var matches = new List<Match>();
        var locations = new HashSet<(int X, int Y)>();
        foreach (var patch in patches)
        {
            if (patch.X < 0 || patch.Y < 0 || patch.X > width - patchWidth || patch.Y > height - patchHeight
                || patch.Pixels.Length != patchWidth * patchHeight || !locations.Add((patch.X, patch.Y))) continue;

            var scores = new List<Match>(81);
            for (var dy = coarseY - 4; dy <= coarseY + 4; dy++)
            for (var dx = coarseX - 4; dx <= coarseX + 4; dx++)
            {
                var x = patch.X + dx;
                var y = patch.Y + dy;
                if (!CanScore(x, y)) continue;
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
        if (group.Length < 4 || group.Length * 2 <= matches.Count) return null;
        var xMedian = group.Select(value => value.Dx).Order().ElementAt(group.Length / 2);
        var yMedian = group.Select(value => value.Dy).Order().ElementAt(group.Length / 2);
        var verified = new List<GroundCameraSolver.Patch>(group.Length);
        var totalError = 0d;
        // A patch can match its own preferred shift and fail at the shared one.
        // Recheck the pixels before assigning the majority support and quality.
        foreach (var match in group)
        {
            var patch = match.Patch;
            var x = patch.X + xMedian;
            var y = patch.Y + yMedian;
            if (!CanScore(x, y)) continue;
            var error = GroundCameraSolver.PatchError(patch, pixels, width, x, y);
            if (!double.IsFinite(error) || error > 12) continue;
            verified.Add(patch);
            totalError += error;
        }
        if (verified.Count < 4 || verified.Count * 2 <= matches.Count
            || (verified.Max(patch => patch.X) - verified.Min(patch => patch.X) < 32
                && (!allowVerticalSpread || verified.Max(patch => patch.Y) - verified.Min(patch => patch.Y) < 32))) return null;
        return new(xMedian, yMedian, totalError / verified.Count, verified.ToArray());

        bool CanScore(int x, int y)
        {
            if (x < 0 || y < 0 || x > width - patchWidth || y > height - patchHeight) return false;
            for (var index = 0; index < exclusions.Count; index++)
            {
                var rect = exclusions[index];
                if (rect.IsFinite && !rect.IsEmpty && rect.X < x + patchWidth && rect.Right > x
                    && rect.Y < y + patchHeight && rect.Bottom > y) return false;
            }
            return true;
        }
    }
}
