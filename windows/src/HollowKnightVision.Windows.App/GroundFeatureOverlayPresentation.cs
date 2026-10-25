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
