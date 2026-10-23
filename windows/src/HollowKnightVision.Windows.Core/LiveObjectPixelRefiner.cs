namespace HollowKnightVision.Windows.Core;

public sealed record LiveObjectPixelRefinement(IReadOnlyList<ObjectDetection> Detections, BgraFrame? DiagnosticImage);

public static class LiveObjectPixelRefiner
{
    public static LiveObjectPixelRefinement Refine(IReadOnlyList<ObjectDetection> detections, BgraFrame image,
        double extensionFraction, bool createsDiagnosticImage = true)
    {
        if (detections.Count == 0 || image.Width <= 2 || image.Height <= 2) return new(detections, null);
        var width = image.Width; var height = image.Height; var pixels = image.Pixels;
        var mask = createsDiagnosticImage ? new byte[checked(width * height * 4)] : null;
        var extension = double.IsFinite(extensionFraction) ? Math.Max(0, extensionFraction) : 0;
        var refined = new List<ObjectDetection>(detections.Count);
        foreach (var detection in detections)
        {
            var original = ObjectDetectionGeometry.UnitRect(detection.Box);
            if (original.IsEmpty) continue;
            var dx = original.Width * extension; var dy = original.Height * extension;
            var search = new StencilRect(original.X - dx, original.Y - dy, original.Width + dx * 2,
                original.Height + dy * 2).Intersect(ObjectDetectionGeometry.Unit);
            var extended = detection with { Box = ObjectDetectionGeometry.Box(search) };
            var minX = Math.Max(1, (int)Math.Floor(search.X * width));
            var maxX = Math.Min(width - 2, (int)Math.Ceiling(search.Right * width));
            var minY = Math.Max(1, (int)Math.Floor(search.Y * height));
            var maxY = Math.Min(height - 2, (int)Math.Ceiling(search.Bottom * height));
            var supportMinX = width; var supportMaxX = 0; var supportMinY = height; var supportMaxY = 0; var count = 0;
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
            {
                var center = y * image.Stride + x * 4;
                var gradient = Distance(center, center - 4) + Distance(center, center + 4)
                    + Distance(center, center - image.Stride) + Distance(center, center + image.Stride);
                if (gradient < 84) continue;
                count++; supportMinX = Math.Min(supportMinX, x); supportMaxX = Math.Max(supportMaxX, x);
                supportMinY = Math.Min(supportMinY, y); supportMaxY = Math.Max(supportMaxY, y);
                if (mask is not null)
                {
                    var index = (y * width + x) * 4;
                    // Straight BGRA; WPF's Bgra32 renderer premultiplies at its boundary.
                    mask[index] = 196; mask[index + 1] = 38; mask[index + 2] = 255; mask[index + 3] = 128;
                }
            }
            if (count < Math.Max(10, (int)(search.Width * search.Height * width * height * .006)))
            { refined.Add(extended); continue; }
            var support = new StencilRect(supportMinX / (double)width, supportMinY / (double)height,
                (supportMaxX - supportMinX + 1d) / width, (supportMaxY - supportMinY + 1d) / height).Intersect(search);
            refined.Add(support.Intersect(original).IsEmpty ? extended
                : detection with { Box = ObjectDetectionGeometry.Box(support) });
        }
        return new(refined, mask is null ? null : new(width, height, width * 4, mask, image.CaptureId, image.ObservedAt));

        int Distance(int first, int second) => (Math.Abs(pixels[first] - pixels[second])
            + Math.Abs(pixels[first + 1] - pixels[second + 1]) + Math.Abs(pixels[first + 2] - pixels[second + 2])) / 3;
    }
}
