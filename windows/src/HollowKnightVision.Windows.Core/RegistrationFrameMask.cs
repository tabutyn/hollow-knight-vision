namespace HollowKnightVision.Windows.Core;

/// <summary>Mac's padded presentation-to-solve masks, using Windows top-origin coordinates.</summary>
public static class RegistrationFrameMask
{
    public const double DefaultPresentationPadding = 12;

    public static IReadOnlyList<StencilRect> Rectangles(StencilRect extent,
        IReadOnlyList<StencilRect> foregroundRects, SourcePoint presentationSize,
        double padding = DefaultPresentationPadding)
    {
        if (!extent.IsFinite || extent.IsEmpty || !double.IsFinite(presentationSize.X)
            || !double.IsFinite(presentationSize.Y) || presentationSize.X <= 0 || presentationSize.Y <= 0
            || !double.IsFinite(padding) || padding < 0) return [];
        var scaleX = extent.Width / presentationSize.X; var scaleY = extent.Height / presentationSize.Y;
        var result = new List<StencilRect>();
        foreach (var foreground in foregroundRects)
        {
            if (!foreground.IsFinite || foreground.IsEmpty) continue;
            var padded = foreground.Expand(padding);
            if (!padded.IsFinite || padded.IsEmpty) continue;
            var solve = new StencilRect(extent.X + padded.X * scaleX, extent.Y + padded.Y * scaleY,
                padded.Width * scaleX, padded.Height * scaleY).Intersect(extent);
            if (solve.IsFinite && !solve.IsEmpty) result.Add(solve);
        }
        return result;
    }

    public static BgraFrame Applying(BgraFrame source, IReadOnlyList<StencilRect> foregroundRects,
        SourcePoint presentationSize, double padding = DefaultPresentationPadding, SourcePoint imageOrigin = default)
    {
        var extent = new StencilRect(imageOrigin.X, imageOrigin.Y, source.Width, source.Height);
        var areas = Rectangles(extent, foregroundRects, presentationSize, padding);
        if (areas.Count == 0) return source;
        var output = source.Pixels.ToArray();
        foreach (var rect in SceneRegions.PixelRectangles(source.Width, source.Height,
            areas.Select(area => area.Offset(-extent.X, -extent.Y))))
            for (var y = rect.Y; y < rect.Bottom; y++) for (var x = rect.X; x < rect.Right; x++)
            {
                var index = y * source.Stride + x * 4;
                output[index] = output[index + 1] = output[index + 2] = 0;
                output[index + 3] = 255;
            }
        return new(source.Width, source.Height, source.Stride, output, source.CaptureId, source.ObservedAt);
    }
}

/// <summary>
/// Protects Windows' current low-resolution motion estimator with Mac's
/// previous/current union-mask rule. Not a replacement for the full ground-first solver.
/// </summary>
public sealed class MaskedFrameMotionTracker
{
    private BgraFrame? previousFrame;
    private IReadOnlyList<StencilRect> previousExclusions = [];

    public void Reset() { previousFrame = null; previousExclusions = []; }
    public LowResolutionTranslationDiagnostic? Observe(BgraFrame frame, IReadOnlyList<StencilRect> exclusions)
    {
        var reference = previousFrame;
        var union = previousExclusions.Concat(exclusions).ToArray();
        previousFrame = frame; previousExclusions = exclusions.ToArray();
        if (reference is null || reference.Width != frame.Width || reference.Height != frame.Height) return null;
        var size = new SourcePoint(frame.Width, frame.Height);
        // Apply the same padded union to both images. A moving hole must not
        // become a feature which falsely drags the camera along with the Knight.
        return LowResolutionRoomMotionTracker.DiagnoseTranslation(
            LowResolutionMotionGrid.FromFrame(RegistrationFrameMask.Applying(reference, union, size)),
            LowResolutionMotionGrid.FromFrame(RegistrationFrameMask.Applying(frame, union, size)));
    }
}
