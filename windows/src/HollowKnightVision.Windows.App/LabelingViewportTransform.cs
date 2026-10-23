namespace HollowKnightVision.Windows.App;

// Mac label-editor navigation in viewport DIPs, independent of annotation pixels.
internal readonly record struct LabelingViewportTransform(double Zoom, double PanX, double PanY)
{
    internal const double MinimumZoom = 1;
    internal const double MaximumZoom = 8;
    internal static LabelingViewportTransform Fit => new(MinimumZoom, 0, 0);

    internal LabelingViewportTransform ZoomAt(
        double logarithmicDelta, double focusX, double focusY, double width, double height)
    {
        if (!double.IsFinite(logarithmicDelta) || !double.IsFinite(focusX) || !double.IsFinite(focusY)
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return this;
        var nextZoom = Math.Clamp(Zoom * Math.Exp(Math.Clamp(logarithmicDelta, -0.35, 0.35)),
            MinimumZoom, MaximumZoom);
        if (Math.Abs(nextZoom - Zoom) < 0.0001) return this;
        var ratio = nextZoom / Zoom;
        return new(nextZoom,
            focusX - (focusX - (width / 2 + PanX)) * ratio - width / 2,
            focusY - (focusY - (height / 2 + PanY)) * ratio - height / 2);
    }

    internal LabelingViewportTransform PanBy(double deltaX, double deltaY) =>
        double.IsFinite(deltaX) && double.IsFinite(deltaY)
            ? this with { PanX = PanX + deltaX, PanY = PanY + deltaY } : this;
}
