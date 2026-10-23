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

    public static Result? Refine(IReadOnlyList<Patch> patches, byte[] pixels, int width, int height, int dx, int dy)
    {
        if (width <= 4 || height <= 4 || pixels.LongLength != (long)width * height || patches.Count < 4
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

    private static Evaluation Evaluate(double offsetX, double offsetY, IReadOnlyList<Patch> patches,
        byte[] pixels, int width, int height, int dx, int dy, bool validation, bool derivatives)
    {
        var result = new Evaluation();
        foreach (var patch in patches)
        {
            var samples = new List<(double Source, double Target, double Gx, double Gy)>(96);
            for (var row = 0; row < 12; row++)
                for (var column = (row + (validation ? 1 : 0)) % 2; column < 16; column += 2)
                {
                    var x = (double)patch.X + column + dx + offsetX; var y = (double)patch.Y + row + dy + offsetY;
                    if (x < 1 || y < 1 || x >= width - 2 || y >= height - 2) continue;
                    var target = Sample(pixels, width, x, y);
                    var gx = derivatives ? (Sample(pixels, width, x + 1, y) - Sample(pixels, width, x - 1, y)) * .5 : 0;
                    var gy = derivatives ? (Sample(pixels, width, x, y + 1) - Sample(pixels, width, x, y - 1)) * .5 : 0;
                    samples.Add((patch.Pixels[row * 16 + column], target, gx, gy));
                }
            if (samples.Count != 96) continue;
            var sourceMean = samples.Sum(value => value.Source) / 96; var targetMean = samples.Sum(value => value.Target) / 96;
            var sourceEnergy = samples.Sum(value => Math.Pow(value.Source - sourceMean, 2));
            var targetEnergy = samples.Sum(value => Math.Pow(value.Target - targetMean, 2));
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
            result.Count += samples.Count; result.Errors.Add(patchError / 96);
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
