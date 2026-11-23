using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>
/// Native WPF rendering for the Mac feature-review palette. Keeping element
/// creation here makes the real live overlay testable without constructing the
/// game-launching MainWindow.
/// </summary>
internal static class GroundFeatureOverlayPresentation
{
    internal static int DrawLive(
        Canvas canvas,
        GroundHypothesisTrackingResult hypotheses)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(hypotheses);
        var count = 0;
        foreach (var feature in hypotheses.Features)
        {
            if (feature.Classification == GroundHypothesisClassification.Candidate)
                continue;
            var kind = Kind(feature.Classification);
            var marker = Rectangle(
                feature.ImageRect.Width,
                feature.ImageRect.Height,
                kind,
                opacity: 1,
                $"ground feature {feature.Id} • {feature.Classification}");
            Canvas.SetLeft(marker, feature.ImageRect.X);
            Canvas.SetTop(marker, feature.ImageRect.Y);
            canvas.Children.Add(marker);
            count++;
        }
        return count;
    }

    internal static void DrawAtlas(Canvas canvas, AtlasPoint camera, ViewportSize capture, ViewportRect live,
        IReadOnlyList<GroundHypothesisAtlasLine> lines, IReadOnlyList<GroundHypothesisAtlasFeature> features)
    {
        if (!capture.IsUsable || !live.IsUsable || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y))
            return;
        var scaleX = live.Width / capture.Width;
        var scaleY = live.Height / capture.Height;
        Point Project(AtlasPoint p) => new(live.X + (p.X - camera.X) * scaleX,
            live.Y + (capture.Height + camera.Y - p.Y) * scaleY);
        foreach (var feature in features)
        {
            if (!double.IsFinite(feature.AtlasPosition.X) || !double.IsFinite(feature.AtlasPosition.Y)) continue;
            var center = Project(feature.AtlasPosition);
            var marker = PersistentRectangle(feature, scaleX, scaleY);
            Canvas.SetLeft(marker, center.X - marker.Width * .5);
            Canvas.SetTop(marker, center.Y - marker.Height * .5);
            canvas.Children.Add(marker);
        }

        const DashboardFeatureReviewLineKind kind = DashboardFeatureReviewLineKind.PersistentGround;
        var brush = Brush(kind);
        var opacity = DashboardFeatureReviewPalette.Rgba(kind).Alpha;
        foreach (var ground in lines)
        {
            var a = Project(ground.AtlasStart);
            var b = Project(ground.AtlasEnd);
            canvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = brush,
                StrokeThickness = DashboardFeatureReviewGeometry.LineWidth,
                Opacity = opacity,
                IsHitTestVisible = false,
                ToolTip = ground.SegmentId == WalkedGroundMemory.WalkedSegmentId
                    ? "Walked ground" : $"Detected ground {ground.SegmentId}"
            });
        }
    }

    internal static Rectangle PersistentRectangle(
        GroundHypothesisAtlasFeature feature,
        double scaleX,
        double scaleY)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var opacity = feature.ReferenceOpacity.Count
            == GroundHypothesisTracker.FeatureWidth
                * GroundHypothesisTracker.FeatureHeight
            ? Math.Max(
                .15,
                feature.ReferenceOpacity.Sum(value => (int)value)
                    / (double)(feature.ReferenceOpacity.Count * byte.MaxValue))
            : .15;
        return Rectangle(
            GroundHypothesisTracker.FeatureWidth * scaleX,
            GroundHypothesisTracker.FeatureHeight * scaleY,
            DashboardFeatureReviewMarkerKind.GroundFeature,
            opacity,
            $"persistent ground feature {feature.SegmentId}:{feature.SequenceIndex}");
    }

    internal static Brush Brush(DashboardFeatureReviewMarkerKind kind)
    {
        var rgba = DashboardFeatureReviewPalette.Rgba(kind);
        var brush = new SolidColorBrush(Color.FromArgb(
            byte.MaxValue,
            Channel(rgba.Red),
            Channel(rgba.Green),
            Channel(rgba.Blue)));
        brush.Freeze();
        return brush;
    }

    internal static Brush Brush(DashboardFeatureReviewLineKind kind)
    {
        var rgba = DashboardFeatureReviewPalette.Rgba(kind);
        var brush = new SolidColorBrush(Color.FromArgb(
            byte.MaxValue,
            Channel(rgba.Red),
            Channel(rgba.Green),
            Channel(rgba.Blue)));
        brush.Freeze();
        return brush;
    }

    private static Rectangle Rectangle(
        double width,
        double height,
        DashboardFeatureReviewMarkerKind kind,
        double opacity,
        string tooltip)
    {
        var rgba = DashboardFeatureReviewPalette.Rgba(kind);
        return new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = Brush(kind),
            StrokeThickness = kind == DashboardFeatureReviewMarkerKind.SelectedGroundFeature
                ? DashboardFeatureReviewGeometry.LineWidth + 1
                : DashboardFeatureReviewGeometry.LineWidth,
            Fill = Brushes.Transparent,
            Opacity = Math.Clamp(opacity * rgba.Alpha, 0, 1),
            IsHitTestVisible = false,
            ToolTip = tooltip,
        };
    }

    private static DashboardFeatureReviewMarkerKind Kind(
        GroundHypothesisClassification classification) => classification switch
        {
            GroundHypothesisClassification.GroundPlane =>
                DashboardFeatureReviewMarkerKind.CameraConsistentGround,
            GroundHypothesisClassification.GlobalMatch =>
                DashboardFeatureReviewMarkerKind.GroundFeature,
            GroundHypothesisClassification.Occluded =>
                DashboardFeatureReviewMarkerKind.OccludedGroundFeature,
            GroundHypothesisClassification.DepthError =>
                DashboardFeatureReviewMarkerKind.GroundFeatureDepthError,
            _ => DashboardFeatureReviewMarkerKind.OccludedGroundFeature,
        };

    private static byte Channel(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);
}
