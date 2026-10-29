namespace HollowKnightVision.Windows.Core;

/// <summary>Optional refinement of an accepted integer match; disjoint fit/validation pixels.</summary>
public static class GroundSubpixelRegistration
{
    public sealed record Patch(int X, int Y, byte[] Pixels);
    public sealed record Result(CameraVector Offset, double ValidationError, int Patches, int Iterations);
    private sealed class Evaluation
    {
        public double Loss, H00, H01, H11, G0, G1;
        public int Count;
        public List<double> Errors { get; } = [];
        public double MeanError => Errors.Sum() / Math.Max(1, Errors.Count);
    }

    public static Result? Refine(IReadOnlyList<Patch> patches, byte[] pixels, int width, int height, int dx, int dy) =>
        RefineCore(patches, pixels, width, height, dx, dy, 4);

    public static Result? RefineSymmetric(IReadOnlyList<Patch> patches, byte[] referencePixels,
        byte[] pixels, int width, int height, int dx, int dy) =>
        RefineSymmetricCore(patches, referencePixels, pixels, width, height, dx, dy, 4);

    private static Result? RefineSymmetricCore(IReadOnlyList<Patch> patches, byte[] referencePixels,
        byte[] pixels, int width, int height, int dx, int dy, int minimumPatches)
    {
        var forward = RefineCore(patches, pixels, width, height, dx, dy, minimumPatches);
        return forward is null ? null : BalanceReverse(forward, patches, referencePixels, pixels, width, height, dx, dy, minimumPatches);
    }

    private static Result BalanceReverse(Result forward, IReadOnlyList<Patch> patches, byte[] referencePixels,
        byte[] pixels, int width, int height, int dx, int dy, int minimumPatches)
    {
        if (referencePixels.LongLength != (long)width * height) return forward;
        var reverse = new List<Patch>(patches.Count);
        foreach (var patch in patches)
        {
            var x = patch.X + dx; var y = patch.Y + dy;
            if (x < 0 || y < 0 || x + 16 > width || y + 12 > height) return forward;
            var values = new byte[192];
            for (var row = 0; row < 12; row++) pixels.AsSpan((y + row) * width + x, 16).CopyTo(values.AsSpan(row * 16));
            reverse.Add(new(x, y, values));
        }
        var backward = RefineCore(reverse, referencePixels, width, height, -dx, -dy, minimumPatches);
        if (backward is null || Math.Abs(forward.Offset.X + backward.Offset.X) > .5
            || Math.Abs(forward.Offset.Y + backward.Offset.Y) > .5) return forward;
        // Reversing image roles reverses interpolation bias. Both directions
        // must independently pass held-out pixel validation before averaging.
        return new(new((forward.Offset.X - backward.Offset.X) * .5,
            (forward.Offset.Y - backward.Offset.Y) * .5),
            Math.Max(forward.ValidationError, backward.ValidationError), patches.Count,
            Math.Max(forward.Iterations, backward.Iterations));
    }

    private static Result? RefineCore(IReadOnlyList<Patch> patches, byte[] pixels, int width, int height, int dx, int dy, int minimumPatches)
    {
        if (width <= 4 || height <= 4 || pixels.LongLength != (long)width * height || patches.Count < minimumPatches
            || patches.Any(patch => patch.Pixels.Length != 192)) return null;
        var baseline = Evaluate(0, 0, patches, pixels, width, height, dx, dy, validation: true, derivatives: false);
        if (baseline.Errors.Count != patches.Count || baseline.MeanError <= .15) return null;
        double offsetX = 0, offsetY = 0; var iterations = 0;
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var current = Evaluate(offsetX, offsetY, patches, pixels, width, height, dx, dy, false, true);
            if (current.Errors.Count != patches.Count || current.Count <= 0) break;
            var damping = .01 * current.Count; var h00 = current.H00 + damping; var h11 = current.H11 + damping;
            var determinant = h00 * h11 - current.H01 * current.H01;
            if (Math.Abs(determinant) <= .000001) break;
            var stepX = (current.G0 * h11 - current.G1 * current.H01) / determinant;
            var stepY = (current.G1 * h00 - current.G0 * current.H01) / determinant;
            iterations++; var accepted = false;
            foreach (var scale in new[] { 1.0, .5, .25 })
            {
                var proposedX = offsetX - stepX * scale; var proposedY = offsetY - stepY * scale;
                if (!double.IsFinite(proposedX) || !double.IsFinite(proposedY) || Math.Abs(proposedX) > 1.25 || Math.Abs(proposedY) > 1.25) continue;
                var candidate = Evaluate(proposedX, proposedY, patches, pixels, width, height, dx, dy, false, false);
                if (candidate.Errors.Count == current.Errors.Count && candidate.Loss + .0001 < current.Loss)
                { offsetX = proposedX; offsetY = proposedY; accepted = true; break; }
            }
            if (!accepted || Math.Max(Math.Abs(stepX), Math.Abs(stepY)) < .005) break;
        }
        var refined = Evaluate(offsetX, offsetY, patches, pixels, width, height, dx, dy, true, false);
        if (refined.Errors.Count != baseline.Errors.Count || refined.MeanError + .08 >= baseline.MeanError * .98
            || refined.Errors.Zip(baseline.Errors).Count(pair => pair.First <= pair.Second + .1) * 3 < baseline.Errors.Count * 2) return null;
        return new(new(offsetX, offsetY), refined.MeanError, patches.Count, iterations);
    }

    // Integer consensus cannot separate subpixel foreground parallax from
    // the ground plane. Require independent, spatially spread fine matches.
    // Among equally supported nearby modes prefer the slower ground layer.
    public static Result? RefineConsensus(IReadOnlyList<Patch> patches, byte[] pixels, int width, int height, int dx, int dy, byte[]? referencePixels = null)
    {
        if (Math.Sqrt((double)dx * dx + (double)dy * dy) < 24) return null;
        var matches = new List<(Patch Patch, Result Fit)>();
        foreach (var patch in patches)
        {
            var fit = RefineCore([patch], pixels, width, height, dx, dy, 1);
            if (fit is not null && fit.ValidationError <= 12) matches.Add((patch, fit));
        }
        if (matches.Count < Math.Max(6, (patches.Count + 1) / 2)) return null;
        (Patch Patch, Result Fit)[] best = [];
        var bestMagnitude = double.PositiveInfinity;
        foreach (var x in matches)
        foreach (var y in matches)
        {
            var group = matches.Where(value => value.Fit.Offset.X >= x.Fit.Offset.X
                && value.Fit.Offset.X <= x.Fit.Offset.X + .4
                && value.Fit.Offset.Y >= y.Fit.Offset.Y && value.Fit.Offset.Y <= y.Fit.Offset.Y + .4).ToArray();
            if (group.Length < Math.Max(4, (matches.Count + 2) / 3) || group.Length < best.Length
                || group.Max(value => value.Patch.X) - group.Min(value => value.Patch.X) < 32) continue;
            var ox = Median(group.Select(value => value.Fit.Offset.X));
            var oy = Median(group.Select(value => value.Fit.Offset.Y));
            var magnitude = Math.Sqrt((dx + ox) * (dx + ox) + (dy + oy) * (dy + oy));
            if (group.Length == best.Length && magnitude >= bestMagnitude) continue;
            best = group; bestMagnitude = magnitude;
        }
        if (best.Length == 0) return null;
        var offsetX = Median(best.Select(value => value.Fit.Offset.X));
        var offsetY = Median(best.Select(value => value.Fit.Offset.Y));
        var selected = best.Select(value => value.Patch).ToArray();
        var baseline = Evaluate(0, 0, selected, pixels, width, height, dx, dy, true, false);
        var refined = Evaluate(offsetX, offsetY, selected, pixels, width, height, dx, dy, true, false);
        if (refined.Errors.Count != selected.Length || baseline.Errors.Count != selected.Length
            || refined.MeanError + .08 >= baseline.MeanError * .98
            || refined.Errors.Zip(baseline.Errors).Count(pair => pair.First <= pair.Second + .1) * 3 < selected.Length * 2) return null;
        var result = new Result(new(offsetX, offsetY), refined.MeanError, selected.Length, best.Max(value => value.Fit.Iterations));
        // Select the parallax layer before balancing interpolation. Re-clustering
        // slightly shifted reverse offsets can exchange two nearby layers.
        return referencePixels is null ? result
            : BalanceReverse(result, selected, referencePixels, pixels, width, height, dx, dy, 4);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray(); var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) * .5 : ordered[middle];
    }

    private static Evaluation Evaluate(double offsetX, double offsetY, IReadOnlyList<Patch> patches,
        byte[] pixels, int width, int height, int dx, int dy, bool validation, bool derivatives)
    {
        var result = new Evaluation();
        // One bounded stack buffer replaces a new 3 KB list for every patch,
        // line-search trial, fit pass and validation pass.
        Span<(double Source, double Target, double Gx, double Gy)> samples = stackalloc (double, double, double, double)[96];
        foreach (var patch in patches)
        {
            var count = 0;
            for (var row = 0; row < 12; row++)
                for (var column = (row + (validation ? 1 : 0)) % 2; column < 16; column += 2)
                {
                    var x = (double)patch.X + column + dx + offsetX; var y = (double)patch.Y + row + dy + offsetY;
                    if (x < 1 || y < 1 || x >= width - 2 || y >= height - 2) continue;
                    var target = Sample(pixels, width, x, y);
                    var gx = derivatives ? (Sample(pixels, width, x + 1, y) - Sample(pixels, width, x - 1, y)) * .5 : 0;
                    var gy = derivatives ? (Sample(pixels, width, x, y + 1) - Sample(pixels, width, x, y - 1)) * .5 : 0;
                    samples[count++] = (patch.Pixels[row * 16 + column], target, gx, gy);
                }
            if (count != 96) continue;
            double sourceMean = 0, targetMean = 0;
            foreach (var value in samples) { sourceMean += value.Source; targetMean += value.Target; }
            sourceMean /= 96; targetMean /= 96;
            double sourceEnergy = 0, targetEnergy = 0;
            foreach (var value in samples)
            {
                sourceEnergy += Math.Pow(value.Source - sourceMean, 2);
                targetEnergy += Math.Pow(value.Target - targetMean, 2);
            }
            if (targetEnergy < Math.Max(96, sourceEnergy * .1)) continue;
            var gain = Math.Clamp(Math.Sqrt(sourceEnergy / targetEnergy), .5, 2); double patchError = 0;
            foreach (var value in samples)
            {
                var residual = (value.Target - targetMean) * gain - (value.Source - sourceMean);
                var magnitude = Math.Abs(residual); var weight = Math.Min(1, 12 / Math.Max(magnitude, .0001));
                result.Loss += magnitude <= 12 ? residual * residual : 24 * magnitude - 144;
                patchError += magnitude;
                if (derivatives)
                {
                    var gx = value.Gx * gain; var gy = value.Gy * gain;
                    result.G0 += weight * gx * residual; result.G1 += weight * gy * residual;
                    result.H00 += weight * gx * gx; result.H01 += weight * gx * gy; result.H11 += weight * gy * gy;
                }
            }
            result.Count += count; result.Errors.Add(patchError / 96);
        }
        result.Loss /= Math.Max(1, result.Count); return result;
    }

    private static double Sample(byte[] pixels, int width, double x, double y)
    {
        var ix = (int)x; var iy = (int)y; var fx = x - ix; var fy = y - iy; var index = iy * width + ix;
        var top = pixels[index] * (1 - fx) + pixels[index + 1] * fx;
        var bottom = pixels[index + width] * (1 - fx) + pixels[index + width + 1] * fx;
        return top * (1 - fy) + bottom * fy;
    }
}
