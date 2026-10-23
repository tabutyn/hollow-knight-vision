using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class StencilPreviewPresentation
{
    internal static LabelBoxBounds FitImage(double imageWidth, double imageHeight, double width, double height)
    {
        if (!double.IsFinite(imageWidth) || !double.IsFinite(imageHeight)
            || !double.IsFinite(width) || !double.IsFinite(height)
            || imageWidth <= 0 || imageHeight <= 0 || width <= 0 || height <= 0) return default;
        var scale = Math.Min(width / imageWidth, height / imageHeight);
        var fittedWidth = imageWidth * scale;
        var fittedHeight = imageHeight * scale;
        return new((width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight);
    }

    internal static LabelBoxBounds Project(LabelingExampleAnnotation annotation, LabelBoxBounds image) =>
        new(image.X + annotation.X * image.Width, image.Y + annotation.Y * image.Height,
            annotation.Width * image.Width, annotation.Height * image.Height);

    internal static LabelBoxBounds? ProjectClipped(double x, double y, double width, double height, LabelBoxBounds image)
    {
        if (image.Width <= 0 || image.Height <= 0 || !double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(x + width) || !double.IsFinite(y + height)) return null;
        var left = Math.Max(0, Math.Min(x, x + width)); var right = Math.Min(1, Math.Max(x, x + width));
        var top = Math.Max(0, Math.Min(y, y + height)); var bottom = Math.Min(1, Math.Max(y, y + height));
        if (right <= left || bottom <= top) return null;
        return new(image.X + left * image.Width, image.Y + top * image.Height,
            (right - left) * image.Width, (bottom - top) * image.Height);
    }

    internal static string LabelName(LabelingExampleAnnotation annotation)
    {
        var identifier = LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier);
        if (identifier == "shared.select-decoration")
            return annotation.X + annotation.Width / 2 < 0.5 ? "Left Select Decoration" : "Right Select Decoration";
        return StencilInitializationPolicy.AllContexts.SelectMany(context => context.Requirements)
            .FirstOrDefault(requirement => requirement.Identifier == identifier)?.Name ?? identifier;
    }
}
