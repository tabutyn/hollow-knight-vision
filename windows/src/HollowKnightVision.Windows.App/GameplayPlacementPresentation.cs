using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;
using Path = System.Windows.Shapes.Path;

namespace HollowKnightVision.Windows.App;

internal static class GameplayPlacementPresentation
{
    private static readonly Brush MeasuredDoorway = Frozen(new SolidColorBrush(Color.FromArgb(242, 31, 230, 255)));
    private static readonly Brush EstimatedPlacement = Frozen(new SolidColorBrush(Color.FromArgb(179, 255, 179, 64)));
    internal static void Draw(Canvas canvas, LiveWorldPlacementProjection placement,
        GroundFeatureOverlayPresentation.AtlasProjection projection,
        ViewportTransform transform, ViewportSize viewport, bool showsEstimatedOutlines)
    {
        canvas.Children.Clear();
        if (!double.IsFinite(projection.Scale) || projection.Scale <= 0 || !transform.IsUsable) return;

        var dashes = GameplayPlacementGeometry.Build(placement, Project, viewport, showsEstimatedOutlines);
        if (dashes.Count == 0) return;
        // Keep the overlay in its Atlas layer while drawing measured screen-space dashes.
        var inverseScale = 1 / transform.Scale;
        canvas.RenderTransform = new MatrixTransform(new Matrix(
            inverseScale, 0, 0, inverseScale,
            -transform.OffsetX * inverseScale, -transform.OffsetY * inverseScale));
        var measured = new StreamGeometry();
        var estimated = new StreamGeometry();
        using (var measuredPath = measured.Open())
        using (var estimatedPath = estimated.Open())
        {
            foreach (var dash in dashes)
            {
                var path = dash.Estimated ? estimatedPath : measuredPath;
                path.BeginFigure(new(dash.Start.X, dash.Start.Y), isFilled: false, isClosed: false);
                path.LineTo(new(dash.End.X, dash.End.Y), isStroked: true, isSmoothJoin: false);
            }
        }
        AddPath(measured, MeasuredDoorway);
        AddPath(estimated, EstimatedPlacement);

        ViewportPoint Project(AtlasPoint point)
        {
            var local = projection.ToCanvas(point);
            return new(local.X * transform.Scale + transform.OffsetX,
                local.Y * transform.Scale + transform.OffsetY);
        }

        void AddPath(StreamGeometry geometry, Brush stroke)
        {
            if (geometry.IsEmpty()) return;
            canvas.Children.Add(new Path
            {
                Data = Frozen(geometry), Stroke = stroke, StrokeThickness = GameplayPlacementGeometry.StrokeWidth
            });
        }
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
