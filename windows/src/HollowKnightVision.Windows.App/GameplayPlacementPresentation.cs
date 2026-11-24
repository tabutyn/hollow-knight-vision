using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class GameplayPlacementPresentation
{
    private static readonly Brush MeasuredDoorway = Frozen(new SolidColorBrush(Color.FromArgb(242, 31, 230, 255)));
    private static readonly Brush EstimatedPlacement = Frozen(new SolidColorBrush(Color.FromArgb(179, 255, 179, 64)));
    private static readonly DoubleCollection Dashes = Frozen(new DoubleCollection([6, 4]));

    internal static void Draw(Canvas canvas, LiveWorldPlacementProjection placement,
        GroundFeatureOverlayPresentation.AtlasProjection projection, bool showsEstimatedOutlines)
    {
        canvas.Children.Clear();
        if (!double.IsFinite(projection.Scale) || projection.Scale <= 0) return;

        foreach (var doorway in placement.Doorways)
        {
            var start = projection.ToCanvas(doorway.Start);
            var end = projection.ToCanvas(doorway.End);
            if (!double.IsFinite(start.X) || !double.IsFinite(start.Y)
                || !double.IsFinite(end.X) || !double.IsFinite(end.Y)
                || Math.Abs(end.X - start.X) <= Math.Abs(end.Y - start.Y)) continue;
            canvas.Children.Add(new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Stroke = MeasuredDoorway, StrokeDashArray = Dashes, StrokeThickness = 1.5
            });
        }

        if (!showsEstimatedOutlines) return;
        foreach (var footprint in placement.EstimatedFootprints)
        {
            var topLeft = projection.ToCanvas(new(footprint.X, footprint.Y + footprint.Height));
            var bounds = new ViewportRect(topLeft.X, topLeft.Y,
                footprint.Width * projection.Scale, footprint.Height * projection.Scale);
            if (!bounds.IsUsable) continue;
            var outline = new Rectangle
            {
                Width = bounds.Width, Height = bounds.Height,
                Stroke = EstimatedPlacement, StrokeDashArray = Dashes, StrokeThickness = 1
            };
            Canvas.SetLeft(outline, bounds.X);
            Canvas.SetTop(outline, bounds.Y);
            canvas.Children.Add(outline);
        }
    }

    private static T Frozen<T>(T value) where T : System.Windows.Freezable
    {
        value.Freeze();
        return value;
    }
}
