namespace HollowKnightVision.Windows.Core;

public sealed partial class GroundCameraSolver
{
    // A sparse floor can still establish abrupt motion if the same independent
    // patches have no competing peak in a broad two-axis search. This verifies
    // the selected displacement; it never substitutes a different pose.
    internal bool VerifySparseTranslation(IReadOnlyList<Patch> patches, byte[] pixels,
        int width, int height, StencilRect[] exclusions, int dx, int dy)
    {
        if (patches.Count < 4) return false;
        var preparation = System.Diagnostics.Stopwatch.GetTimestamp();
        var sources = patches.Select(p => new GroundPatchSearchSource(p.X, p.Y, p.Pixels)).ToArray();
        var candidates = BuildTextureCandidates(patches, exclusions, width, height, dx, dy, 96, 96);
        profiledPreparation += System.Diagnostics.Stopwatch.GetElapsedTime(preparation).TotalMilliseconds;
        profiledReferenceSearches++;
        var search = System.Diagnostics.Stopwatch.GetTimestamp();
        var execution = GroundPatchMatcher.Shared.Search(sources, pixels, width, height,
            candidates, matcherBackend, allowVerticalSpread: true);
        profiledSearch += System.Diagnostics.Stopwatch.GetElapsedTime(search).TotalMilliseconds;
        if (execution.Results.Count == 0) return false;
        var best = execution.Results[0];
        foreach (var candidate in execution.Results)
            if (candidate.Error < best.Error) best = candidate;
        if (best.Support != patches.Count || best.Error > 10
            || Math.Abs(best.Dx - dx) > 2 || Math.Abs(best.Dy - dy) > 2) return false;
        foreach (var candidate in execution.Results)
            if ((Math.Abs(candidate.Dx - best.Dx) > 2 || Math.Abs(candidate.Dy - best.Dy) > 2)
                && candidate.Error <= best.Error * 1.12 + .25) return false;
        return true;
    }
}
