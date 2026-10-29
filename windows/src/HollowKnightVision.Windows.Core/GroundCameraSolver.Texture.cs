namespace HollowKnightVision.Windows.Core;

public sealed partial class GroundCameraSolver
{
    // Floor rows can leave the image while a textured wall remains visible.
    // This fallback measures that texture; it never uses game coordinates.
    private static List<Patch> MakeTexturePatches(Reference prior, int width, int height)
    {
        var groups = new List<Patch>[16];
        for (var i = 0; i < groups.Length; i++) groups[i] = [];
        var textured = new Dictionary<(int X, int Y), Patch>();
        for (var y = 8; y + 12 <= height - 8; y += 12)
        for (var x = 8; x + 16 <= width - 8; x += 16)
        {
            if (Excluded(prior.Exclusions, x, y, 16, 12)) continue;
            var patch = new Patch(x, y, ReadPatch(prior.Pixels, width, height, x, y)!);
            if (patch.Energy < 16 || patch.Pixels.Max() - patch.Pixels.Min() < 16) continue;
            textured.Add((x, y), patch);
        }
        // Floating particles produce very sharp but isolated patches. Require
        // a connected textured region before it can provide camera anchors.
        // Thin walls remain eligible through their vertical connectivity.
        var visited = new HashSet<(int X, int Y)>();
        foreach (var start in textured.Values)
        {
            if (!visited.Add((start.X, start.Y))) continue;
            var component = new List<Patch>(); var pending = new Queue<Patch>(); pending.Enqueue(start);
            while (pending.TryDequeue(out var patch))
            {
                component.Add(patch);
                for (var dy = -12; dy <= 12; dy += 12)
                for (var dx = -16; dx <= 16; dx += 16)
                {
                    var key = (patch.X + dx, patch.Y + dy);
                    if (textured.TryGetValue(key, out var neighbor) && visited.Add(key)) pending.Enqueue(neighbor);
                }
            }
            if (component.Count < 6) continue;
            foreach (var patch in component)
                groups[Math.Min(3, patch.Y * 4 / height) * 4 + Math.Min(3, patch.X * 4 / width)].Add(patch);
        }
        foreach (var group in groups) group.Sort((a, b) => b.Energy.CompareTo(a.Energy));
        var patches = new List<Patch>(16);
        for (var offset = 0; patches.Count < 16; offset++)
        {
            var added = false;
            foreach (var group in groups)
                if (offset < group.Count)
                { patches.Add(group[offset]); added = true; if (patches.Count == 16) break; }
            if (!added) break;
        }
        return patches;
    }

    private List<GroundPatchSearchCandidate> BuildTextureCandidates(IReadOnlyList<Patch> patches,
        StencilRect[] exclusions, int width, int height, int predictedX, int predictedY, int radiusX, int radiusY)
    {
        // Texture without floor-row proposals needs a complete two-axis search.
        // Score every pixel displacement within 96 pixels: a sparse lattice can
        // alias detailed patterns and miss the correct correlation peak entirely.
        radiusX = Math.Min(96, radiusX); radiusY = Math.Min(96, radiusY);
        return BuildSearchCandidates(patches, [], exclusions, width, height,
            Enumerable.Range(predictedY - radiusY, 2 * radiusY + 1).ToArray(), predictedX, radiusX, allTextureRows: true);
    }

    private static GroundPatchSearchResult? SelectTexturePeak(IReadOnlyList<Patch> patches,
        byte[] pixels, int width, int height, StencilRect[] exclusions,
        IReadOnlyList<GroundPatchSearchResult> candidates, GroundPatchSearchResult best)
    {
        // Nearby integer costs are not enough to disambiguate repeating texture:
        // the true peak can lie between pixels. Compare held-out subpixel fits
        // for the competitive, separated peaks before independent consensus.
        var tested = new List<GroundPatchSearchResult>(8);
        GroundPatchSearchResult? selected = null; var selectedError = double.PositiveInfinity;
        foreach (var candidate in candidates.Where(value => value.Error <= best.Error * 1.5 + .5).OrderBy(value => value.Error))
        {
            if (tested.Any(value => Math.Abs(value.Dx - candidate.Dx) <= 4 && Math.Abs(value.Dy - candidate.Dy) <= 4)) continue;
            tested.Add(candidate);
            var consensus = GroundPatchConsensus.Refine(patches, pixels, width, height,
                candidate.Dx, candidate.Dy, exclusions, allowVerticalSpread: true);
            if (consensus is null || consensus.Error > 8) { if (tested.Count == 8) break; continue; }
            var visible = patches.Count(p => p.X + consensus.Dx >= 0 && p.Y + consensus.Dy >= 0
                && p.X + consensus.Dx + 16 <= width && p.Y + consensus.Dy + 12 <= height
                && !Excluded(exclusions, p.X + consensus.Dx, p.Y + consensus.Dy, 16, 12));
            // A small animated foreground group can match exceptionally well.
            // Require a majority of ALL visible patches, including patches that
            // did not match this peak, before accepting its photometric score.
            if (consensus.Patches.Length * 2 <= visible) { if (tested.Count == 8) break; continue; }
            var refinement = new List<GroundSubpixelRegistration.Patch>();
            foreach (var patch in consensus.Patches)
            {
                var x = patch.X + consensus.Dx; var y = patch.Y + consensus.Dy;
                if (x < 5 || y < 5 || x + 21 >= width || y + 17 >= height
                    || Excluded(exclusions, x - 5, y - 5, 26, 22)
                    || PatchError(patch, pixels, width, x, y) > 12) continue;
                refinement.Add(new(patch.X, patch.Y, patch.Pixels));
            }
            var fit = refinement.Count >= 4
                ? GroundSubpixelRegistration.Refine(refinement, pixels, width, height, consensus.Dx, consensus.Dy) : null;
            var error = fit?.ValidationError ?? consensus.Error;
            if (error < selectedError)
            {
                selected = new(consensus.Dx, consensus.Dy, consensus.Error, consensus.Patches.Length);
                selectedError = error;
            }
            if (tested.Count == 8) break;
        }
        return selected;
    }

    internal void WarmTextureFallback(byte[] pixels, int width, int height)
    {
        frameWidth = width; frameHeight = height;
        var initial = new Reference(pixels, [], [], 0, CameraVector.Zero);
        reference = recentReference = initial; lastAcceptedTimestamp = 0;
        for (var i = 1; i <= 8; i++)
        {
            reference = initial;
            _ = SolveReference(pixels, width, height, [], i / 30.0, [], "texture-recent", textureFallback: true);
        }
        Reset();
    }
}
