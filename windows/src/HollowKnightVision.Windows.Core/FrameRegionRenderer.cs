namespace HollowKnightVision.Windows.Core;

public static class FrameRegionRenderer
{
    private const double MinimumFeatureMarkerSize = 8;
    private const double KnightMarkerPadding = 2;

    public static bool ContainsGameplaySignal(BgraFrame frame) => SignalProfile(frame).HasGameplaySignal;

    public static FrameSignalProfile SignalProfile(BgraFrame frame)
    {
        const int width = 64;
        var height = Math.Max(16, checked((int)Math.Round((double)width * frame.Height / frame.Width,
            MidpointRounding.AwayFromZero)));
        // Preserve the full source aspect ratio; do not use the 16:9 editor crop.
        var pixels = new ImageStencilPixels(frame, width, height);
        var peak = new byte[checked(width * height)];
        var luma = new byte[peak.Length];
        var signaled = 0; var visible = 0; long total = 0;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var (red, green, blue) = pixels.Rgb(x, y)!.Value;
            var index = y * width + x;
            peak[index] = Math.Max(red, Math.Max(green, blue));
            if (peak[index] >= 5) signaled++;
            if (peak[index] >= 16) visible++;
            total += peak[index];
            luma[index] = (byte)((77 * red + 150 * green + 29 * blue) >> 8);
        }
        return new(peak.Length, signaled, visible, (double)total / peak.Length,
            new(width, height, luma), EdgeBlackBands(peak, width, height));
    }

    private static FrameEdgeBlackBands EdgeBlackBands(byte[] peaks, int width, int height)
    {
        FrameEdgeBlackBand? Measure(bool fromLeft)
        {
            var lastAccepted = -1; var brightRun = 0;
            for (var depth = 0; depth < Math.Min(width * 3 / 4, width - 1); depth++)
            {
                var x = fromLeft ? depth : width - 1 - depth;
                var rows = 0;
                for (var y = 0; y < height; y++) if (peaks[y * width + x] >= 6) rows++;
                if (rows <= Math.Max(2, height / 9)) { lastAccepted = depth; brightRun = 0; }
                else if (++brightRun >= 2) break;
            }
            var columns = lastAccepted + 1;
            if (columns < 2) return null;
            var boundary = fromLeft ? columns : width - columns - 1;
            var supported = new bool[height];
            for (var y = 0; y < height; y++) for (var offset = 0; offset < 6; offset++)
            {
                var x = boundary + (fromLeft ? offset : -offset);
                if (x >= 0 && x < width && peaks[y * width + x] >= 6) { supported[y] = true; break; }
            }
            var range = LongestSupportedRun(supported);
            var lower = .38; var upper = .68;
            if (range is { } run && run.End - run.Start >= 3)
            {
                lower = (double)Math.Max(0, run.Start - 1) / height;
                upper = (double)Math.Min(height, run.End + 2) / height;
                if (upper - lower > .46)
                {
                    var center = (lower + upper) * .5;
                    lower = Math.Max(0, center - .23); upper = Math.Min(1, lower + .46);
                }
            }
            return new((double)columns / width, new(lower, upper));
        }
        return new(Measure(true), Measure(false));
    }

    private static (int Start, int End)? LongestSupportedRun(bool[] rows)
    {
        (int Start, int End)? best = null;
        int? start = null;
        var gap = 0;
        void Consider(int first, int end)
        { if (best is null || end - first > best.Value.End - best.Value.Start) best = (first, end); }
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i]) { start ??= i; gap = 0; }
            else if (start is not null && ++gap > 1)
            { Consider(start.Value, i - gap + 1); start = null; gap = 0; }
        }
        if (start is not null) Consider(start.Value, Math.Max(start.Value, rows.Length - gap));
        return best;
    }

    /// <summary>Clear exclusions only. Legitimate black scenery remains opaque.</summary>
    public static BgraFrame MapFrame(BgraFrame frame, SceneRegions regions,
        IReadOnlyList<StencilRect>? additionalOmittedRects = null)
    {
        var pixels = frame.Pixels.ToArray();
        foreach (var rect in SceneRegions.PixelRectangles(frame.Width, frame.Height,
            regions.OmittedRects.Concat(additionalOmittedRects ?? [])))
            for (var y = rect.Y; y < rect.Bottom; y++)
                Array.Clear(pixels, y * frame.Stride + rect.X * 4, rect.Width * 4);
        return new(frame.Width, frame.Height, frame.Stride, pixels, frame.CaptureId, frame.ObservedAt);
    }

    /// <summary>
    /// Draws the persistent-feature diagnostic without changing the captured
    /// frame. Markers overlapping the Knight are suppressed so the diagnostic
    /// never obscures the foreground box it is meant to explain.
    /// </summary>
    public static BgraFrame AnnotatedFrame(
        BgraFrame frame,
        SceneRegions regions,
        FeatureTrackingResult featureTracking,
        bool showFeatures = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(featureTracking);

        var pixels = frame.Pixels.ToArray();
        if (!showFeatures || featureTracking.Features.Count == 0)
            return new(frame.Width, frame.Height, frame.Stride, pixels,
                frame.CaptureId, frame.ObservedAt);

        var markerSize = Math.Max(MinimumFeatureMarkerSize, frame.Width / 64d);
        var lineWidth = Math.Max(1.5, frame.Width / 480d);
        var extent = new StencilRect(0, 0, frame.Width, frame.Height);
        var knight = regions.Knight is { } bounds && bounds.IsFinite && !bounds.IsEmpty
            ? new StencilRect(
                bounds.X - KnightMarkerPadding,
                bounds.Y - KnightMarkerPadding,
                bounds.Width + KnightMarkerPadding * 2,
                bounds.Height + KnightMarkerPadding * 2)
            : (StencilRect?)null;

        foreach (var feature in featureTracking.Features)
        {
            if (!double.IsFinite(feature.Point.X) || !double.IsFinite(feature.Point.Y))
                continue;
            var fullMarker = new StencilRect(
                feature.Point.X - markerSize / 2,
                feature.Point.Y - markerSize / 2,
                markerSize,
                markerSize);
            if (knight is { } exclusion && Intersects(fullMarker, exclusion))
                continue;

            var marker = fullMarker.Intersect(extent);
            if (marker.IsEmpty) continue;
            var color = feature.Kind switch
            {
                MapFeatureKind.Candidate => (Blue: (byte)41, Green: (byte)194, Red: (byte)255),
                MapFeatureKind.Landmark => (Blue: (byte)255, Green: (byte)235, Red: (byte)31),
                MapFeatureKind.Relocalized => (Blue: (byte)255, Green: (byte)82, Red: (byte)209),
                _ => throw new ArgumentOutOfRangeException(nameof(featureTracking), feature.Kind,
                    "Unknown feature marker kind.")
            };
            DrawBorder(pixels, frame, marker, lineWidth, color);
            DrawFilledRect(pixels, frame, new StencilRect(
                feature.Point.X - 1.5,
                feature.Point.Y - 1.5,
                3,
                3).Intersect(extent), color);
        }
        return new(frame.Width, frame.Height, frame.Stride, pixels,
            frame.CaptureId, frame.ObservedAt);
    }

    private static bool Intersects(StencilRect left, StencilRect right) =>
        left.X < right.Right && left.Right > right.X
        && left.Y < right.Bottom && left.Bottom > right.Y;

    private static void DrawBorder(
        byte[] pixels,
        BgraFrame frame,
        StencilRect rect,
        double lineWidth,
        (byte Blue, byte Green, byte Red) color)
    {
        DrawFilledRect(pixels, frame,
            new(rect.X, rect.Y, rect.Width, lineWidth), color);
        DrawFilledRect(pixels, frame,
            new(rect.X, rect.Bottom - lineWidth, rect.Width, lineWidth), color);
        DrawFilledRect(pixels, frame,
            new(rect.X, rect.Y, lineWidth, rect.Height), color);
        DrawFilledRect(pixels, frame,
            new(rect.Right - lineWidth, rect.Y, lineWidth, rect.Height), color);
    }

    private static void DrawFilledRect(
        byte[] pixels,
        BgraFrame frame,
        StencilRect rect,
        (byte Blue, byte Green, byte Red) color)
    {
        if (!rect.IsFinite || rect.IsEmpty) return;
        var left = Math.Clamp((int)Math.Floor(rect.X), 0, frame.Width);
        var top = Math.Clamp((int)Math.Floor(rect.Y), 0, frame.Height);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right), left, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), top, frame.Height);
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var offset = checked(y * frame.Stride + x * BgraFrame.BytesPerPixel);
            pixels[offset] = color.Blue;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Red;
            pixels[offset + 3] = 255;
        }
    }
}
