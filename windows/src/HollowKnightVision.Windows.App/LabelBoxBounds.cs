namespace HollowKnightVision.Windows.App;

// Selection geometry is independent of its decoration. WPF Rectangle strokes
// sit inside their layout bounds, so expand the layout by the full stroke width
// to put the border's interior edge exactly on the saved selection boundary.
internal readonly record struct LabelBoxBounds(double X, double Y, double Width, double Height)
{
    internal static LabelBoxBounds FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    internal LabelBoxBounds OutsideStroke(double strokeWidth) =>
        new(X - strokeWidth, Y - strokeWidth, Width + 2 * strokeWidth, Height + 2 * strokeWidth);

    internal static double CanvasStrokeWidth(double displayScale) =>
        2 / (double.IsFinite(displayScale) && displayScale > 0 ? displayScale : 1);
}
