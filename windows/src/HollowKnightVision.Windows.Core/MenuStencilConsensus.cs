namespace HollowKnightVision.Windows.Core;

/// <summary>Integer registration and sparse text consensus ported from MenuStencilConsensus.swift.</summary>
public static class MenuStencilConsensus
{
    public sealed record Capture(ImageStencilPixels Pixels, StencilRect AnnotationBounds);
    public readonly record struct Offset(int X, int Y);
    public sealed record Result(StencilRect Bounds, ImageStencilReference Reference, IReadOnlyList<Offset> AlignedOffsets, int IncludedPixelCount);

    public static Result? Build(IReadOnlyList<Capture> captures, StencilRect nominalBounds,
        int alignmentRadius = 6, bool preservesAnnotationBounds = false)
    {
        if (captures.Count == 0 || !nominalBounds.IsFinite || nominalBounds.IsEmpty || alignmentRadius < 0) return null;
        var width = captures[0].Pixels.Width; var height = captures[0].Pixels.Height;
        if (captures.Any(capture => capture.Pixels.Width != width || capture.Pixels.Height != height || capture.Pixels.OriginY != 0)) return null;
        var referenceIndex = Enumerable.Range(0, captures.Count).MinBy(index => AnnotationDistance(captures[index].AnnotationBounds, nominalBounds));
        if (ClampedBounds(nominalBounds.Expand(2), width, height, alignmentRadius) is not { } alignmentBounds) return null;
        var reference = captures[referenceIndex].Pixels;
        var offsets = captures.Select(capture => BestOffset(reference, capture.Pixels, alignmentBounds, alignmentRadius)).ToArray();
        offsets[referenceIndex] = default;
        var medianX = Median(offsets.Select(offset => offset.X)); var medianY = Median(offsets.Select(offset => offset.Y));
        var centered = offsets.Select(offset => new Offset(offset.X - medianX, offset.Y - medianY)).ToArray();
        var margin = centered.Max(offset => Math.Max(Math.Abs(offset.X), Math.Abs(offset.Y)));
        var envelope = captures.Select(capture => capture.AnnotationBounds).Aggregate(nominalBounds, (rect, next) => rect.Union(next));
        var requested = preservesAnnotationBounds ? nominalBounds : envelope.Expand(2);
        if (ClampedBounds(requested.Offset(medianX, medianY), width, height, margin) is not { } bounds) return null;
        var x0 = (int)bounds.X; var y0 = (int)bounds.Y; var workWidth = (int)bounds.Width; var workHeight = (int)bounds.Height;
        var minimumSupport = captures.Count == 1 ? 1 : Math.Max(2, (int)Math.Ceiling(captures.Count * .7));
        var included = new bool[checked(workWidth * workHeight)];
        var colors = new (byte Red, byte Green, byte Blue)[included.Length];
        for (var y = 0; y < workHeight; y++) for (var x = 0; x < workWidth; x++)
        {
            var samples = captures.Select((capture, index) => capture.Pixels.Rgb(x0 + x + centered[index].X, y0 + y + centered[index].Y))
                .Where(color => color.HasValue && IsLikelyMenuText(color.Value)).Select(color => color!.Value).ToArray();
            if (samples.Length < minimumSupport) continue;
            var color = ((byte)Median(samples.Select(value => (int)value.Red)), (byte)Median(samples.Select(value => (int)value.Green)),
                (byte)Median(samples.Select(value => (int)value.Blue)));
            if (!IsLikelyMenuText(color)) continue;
            var lumas = samples.Select(Luma).ToArray(); var medianLuma = Median(lumas);
            if (Median(lumas.Select(luma => Math.Abs(luma - medianLuma))) > Math.Max(42, medianLuma * .35)) continue;
            included[y * workWidth + x] = true; colors[y * workWidth + x] = color;
        }
        included = RemoveIsolated(included, workWidth, workHeight);
        var indices = Enumerable.Range(0, included.Length).Where(index => included[index]).ToArray();
        if (indices.Length < 8) return null;
        var minX = preservesAnnotationBounds ? 0 : indices.Min(index => index % workWidth);
        var maxX = preservesAnnotationBounds ? workWidth - 1 : indices.Max(index => index % workWidth);
        var minY = preservesAnnotationBounds ? 0 : indices.Min(index => index / workWidth);
        var maxY = preservesAnnotationBounds ? workHeight - 1 : indices.Max(index => index / workWidth);
        var outputWidth = maxX - minX + 1; var outputHeight = maxY - minY + 1;
        var rgb = new byte[checked(outputWidth * outputHeight * 3)]; var mask = new byte[outputWidth * outputHeight];
        for (var y = 0; y < outputHeight; y++) for (var x = 0; x < outputWidth; x++)
        {
            var source = (minY + y) * workWidth + minX + x; var target = y * outputWidth + x;
            if (!included[source]) continue;
            var color = colors[source]; rgb[target * 3] = color.Red; rgb[target * 3 + 1] = color.Green; rgb[target * 3 + 2] = color.Blue;
            mask[target] = 255;
        }
        return new(new(x0 + minX, y0 + minY, outputWidth, outputHeight), new(outputWidth, outputHeight, rgb, mask), centered, indices.Length);
    }

    public static Result? BuildLocalizedText(IReadOnlyList<Capture> captures, StencilRect nominalBounds, double horizontalPadding = 80)
    {
        var expanded = new StencilRect(nominalBounds.X - horizontalPadding, nominalBounds.Y - 3,
            nominalBounds.Width + horizontalPadding * 2, nominalBounds.Height + 6);
        var expandedCaptures = captures.Select(capture => capture with { AnnotationBounds = expanded }).ToArray();
        var raw = Build(expandedCaptures, expanded, alignmentRadius: 0, preservesAnnotationBounds: true);
        if (raw?.Reference.Mask is not { } mask) return null;
        var width = raw.Reference.Width; var height = raw.Reference.Height;
        var active = Enumerable.Range(0, width).Where(x => Enumerable.Range(0, height).Any(y => mask[y * width + x] != 0)).ToArray();
        if (active.Length == 0) return null;
        var clusters = new List<(int Start, int End)>(); var start = active[0]; var previous = start;
        foreach (var x in active.Skip(1))
        {
            if (x - previous > 10) { clusters.Add((start, previous)); start = x; }
            previous = x;
        }
        clusters.Add((start, previous));
        var nominalLocal = nominalBounds.Offset(-raw.Bounds.X, -raw.Bounds.Y);
        var selected = clusters.MinBy(cluster => ClusterDistance(cluster, nominalLocal));
        var x0 = Math.Max(0, selected.Start - 1); var x1 = Math.Min(width - 1, selected.End + 1);
        var minY = height; var maxY = -1;
        for (var y = 0; y < height; y++) for (var x = x0; x <= x1; x++)
            if (mask[y * width + x] != 0) { minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxY < minY) return null;
        var y0 = Math.Max(0, minY - 1); var y1 = Math.Min(height - 1, maxY + 1);
        var outputWidth = x1 - x0 + 1; var outputHeight = y1 - y0 + 1;
        var rgb = new byte[checked(outputWidth * outputHeight * 3)]; var cropped = new byte[outputWidth * outputHeight];
        for (var y = 0; y < outputHeight; y++)
        {
            Array.Copy(raw.Reference.Rgb, ((y0 + y) * width + x0) * 3, rgb, y * outputWidth * 3, outputWidth * 3);
            Array.Copy(mask, (y0 + y) * width + x0, cropped, y * outputWidth, outputWidth);
        }
        var included = cropped.Count(value => value != 0);
        return included < 8 ? null : new(new(raw.Bounds.X + x0, raw.Bounds.Y + y0, outputWidth, outputHeight),
            new(outputWidth, outputHeight, rgb, cropped), raw.AlignedOffsets, included);
    }

    public static ImageStencilReference? RemovingStableBackground(ImageStencilReference reference, StencilRect bounds,
        IReadOnlyList<ImageStencilPixels> negativeFrames)
    {
        if (!reference.IsValid || negativeFrames.Count < 2 || !bounds.IsFinite
            || (int)bounds.Width != reference.Width || (int)bounds.Height != reference.Height) return null;
        var required = Math.Max(2, (int)Math.Ceiling(negativeFrames.Count * .5));
        var included = reference.Mask?.Select(value => value != 0).ToArray() ?? Enumerable.Repeat(true, reference.Width * reference.Height).ToArray();
        for (var y = 0; y < reference.Height; y++) for (var x = 0; x < reference.Width; x++)
        {
            var index = y * reference.Width + x;
            if (!included[index]) continue;
            var support = negativeFrames.Count(frame => frame.Rgb((int)bounds.X + x, (int)bounds.Y + y) is { } color && IsLikelyMenuText(color));
            if (support >= required) included[index] = false;
        }
        included = RemoveIsolated(included, reference.Width, reference.Height);
        return included.Count(value => value) < 8 ? null : reference with { Mask = included.Select(value => value ? (byte)255 : (byte)0).ToArray() };
    }

    private static bool[] RemoveIsolated(bool[] input, int width, int height)
    {
        var result = new bool[input.Length];
        for (var index = 0; index < input.Length; index++)
        {
            if (!input[index]) continue;
            var x = index % width; var y = index / width;
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx; var ny = y + dy;
                if (nx >= 0 && nx < width && ny >= 0 && ny < height && input[ny * width + nx]) result[index] = true;
            }
        }
        return result;
    }

    private static Offset BestOffset(ImageStencilPixels reference, ImageStencilPixels candidate, StencilRect bounds, int radius)
    {
        var points = new List<(int X, int Y, double Weight)>();
        for (var y = (int)bounds.Y; y < (int)bounds.Bottom; y++) for (var x = (int)bounds.X; x < (int)bounds.Right; x++)
            if (reference.Rgb(x, y) is { } color && TextWeight(color) is var weight && weight > 0) points.Add((x, y, weight));
        if (points.Count > 1200) points = points.OrderByDescending(point => point.Weight).Take(1200).ToList();
        if (points.Count == 0) return default;
        var referenceWeight = points.Sum(point => point.Weight);
        var bestScore = double.NegativeInfinity; var bestDistance = int.MaxValue; var best = new Offset();
        void Consider(int dx, int dy)
        {
            var overlap = points.Sum(point => candidate.Rgb(point.X + dx, point.Y + dy) is { } color ? Math.Min(point.Weight, TextWeight(color)) : 0);
            var score = overlap / Math.Max(1, referenceWeight); var distance = Math.Abs(dx) + Math.Abs(dy);
            if (score > bestScore || (score == bestScore && distance < bestDistance))
            { bestScore = score; bestDistance = distance; best = new(dx, dy); }
        }
        for (var dy = -radius; dy <= radius; dy += 2) for (var dx = -radius; dx <= radius; dx += 2) Consider(dx, dy);
        var coarse = best;
        for (var dy = Math.Max(-radius, coarse.Y - 1); dy <= Math.Min(radius, coarse.Y + 1); dy++)
            for (var dx = Math.Max(-radius, coarse.X - 1); dx <= Math.Min(radius, coarse.X + 1); dx++) Consider(dx, dy);
        return best;
    }
    private static double TextWeight((byte Red, byte Green, byte Blue) color)
    {
        var minimum = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        var chroma = Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - minimum;
        return Math.Max(0, minimum - Math.Max(0, chroma - 18) * 1.5 - 52);
    }
    private static bool IsLikelyMenuText((byte Red, byte Green, byte Blue) color) => Math.Min(color.Red, Math.Min(color.Green, color.Blue)) >= 66
        && Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - Math.Min(color.Red, Math.Min(color.Green, color.Blue)) <= 58 && TextWeight(color) >= 12;
    private static double Luma((byte Red, byte Green, byte Blue) color) => (color.Red + 2.0 * color.Green + color.Blue) / 4;
    private static double AnnotationDistance(StencilRect rect, StencilRect nominal) => Math.Abs(rect.MidX - nominal.MidX) + Math.Abs(rect.MidY - nominal.MidY)
        + Math.Abs(rect.Width - nominal.Width) + Math.Abs(rect.Height - nominal.Height);
    private static double ClusterDistance((int Start, int End) cluster, StencilRect nominal)
    {
        var overlap = Math.Max(0, Math.Min(cluster.End + 1, nominal.Right) - Math.Max(cluster.Start, nominal.X));
        return overlap > 0 ? -overlap : cluster.End + 1 < nominal.X ? nominal.X - cluster.End - 1 : cluster.Start - nominal.Right;
    }
    private static StencilRect? ClampedBounds(StencilRect rect, int width, int height, int inset)
    {
        if (!rect.IsFinite) return null;
        var margin = Math.Max(0, inset);
        var x0 = Math.Max(margin, (int)Math.Floor(rect.X)); var y0 = Math.Max(margin, (int)Math.Floor(rect.Y));
        var x1 = Math.Min(width - margin, (int)Math.Ceiling(rect.Right)); var y1 = Math.Min(height - margin, (int)Math.Ceiling(rect.Bottom));
        return x1 > x0 && y1 > y0 ? new(x0, y0, x1 - x0, y1 - y0) : null;
    }
    private static int Median(IEnumerable<int> values) => (int)Median(values.Select(value => (double)value));
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray(); var middle = sorted.Length / 2;
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
