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
    internal readonly record struct AtlasProjection(double Scale, double OriginX, double OriginY)
    {
        internal Point ToCanvas(AtlasPoint p) => new(p.X * Scale - OriginX, -p.Y * Scale - OriginY);
    }

    internal static int DrawLive(
        Canvas canvas,
        GroundHypothesisTrackingResult hypotheses,
        IReadOnlyList<DetectedFloorLine> floors,
        IReadOnlyList<PixelRect> measuredPatches)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(hypotheses);
        var occupied = new List<PixelRect>();
        foreach (var feature in hypotheses.Features)
        {
            if (feature.Classification == GroundHypothesisClassification.Candidate) continue;
            var observed = feature.ImageRect;
            var floor = floors.Where(line => GroundPlatformPatches.Contains(line, observed.X, observed.Y))
                .MinBy(line => Math.Abs(line.Row - observed.Y));
            if (floor is null) continue;
            Append(observed with { Y = floor.Row }, Kind(feature.Classification),
                $"ground feature {feature.Id} • {feature.Classification}");
        }
        foreach (var box in measuredPatches)
        {
            if (occupied.Any(prior => GroundPlatformPatches.Overlaps(prior, box))) continue;
            Append(box, DashboardFeatureReviewMarkerKind.CameraConsistentGround, "Measured platform patch");
        }
        return occupied.Count;

        void Append(PixelRect box, DashboardFeatureReviewMarkerKind kind, string tooltip)
        {
            var marker = Rectangle(box.Width, box.Height, kind, opacity: 1, tooltip);
            Canvas.SetLeft(marker, box.X);
            Canvas.SetTop(marker, box.Y);
            canvas.Children.Add(marker);
            occupied.Add(box);
        }
    }

    internal static void DrawTerrainCorners(Canvas canvas, TerrainWallDetection terrain)
    {
        if (terrain.Corners.Count == 0) return;
        var brush = new SolidColorBrush(Color.FromArgb(224, 56, 217, 255));
        brush.Freeze();
        foreach (var corner in terrain.Corners)
        {
            var x = corner.X + .5;
            var y = corner.Row + .5;
            if (corner.FloorEndpoint != corner.X)
            {
                canvas.Children.Add(new Line
                {
                    X1 = corner.FloorEndpoint + .5, X2 = x, Y1 = y, Y2 = y,
                    Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false
                });
            }
            var marker = new Rectangle { Width = 3, Height = 3, Fill = brush, IsHitTestVisible = false };
            Canvas.SetLeft(marker, x - 1.5);
            Canvas.SetTop(marker, y - 1.5);
            canvas.Children.Add(marker);
        }
    }

    internal static void DrawSavedAtlas(Canvas canvas, AtlasProjection projection,
        IReadOnlyList<GroundHypothesisAtlasLine> lines) => DrawLines(canvas, lines, projection.ToCanvas);

    internal static void DrawAtlas(Canvas linesCanvas, Canvas? patchesCanvas,
        AtlasPoint camera, ViewportSize capture, ViewportRect live,
        IReadOnlyList<GroundHypothesisAtlasLine> lines, IReadOnlyList<GroundHypothesisAtlasFeature> features,
        AtlasProjection savedProjection = default, IReadOnlyList<GroundHypothesisAtlasLine>? saved = null,
        PixelRect? knight = null)
    {
        if (!capture.IsUsable || !live.IsUsable || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y))
            return;
        var scaleX = live.Width / capture.Width;
        var scaleY = live.Height / capture.Height;
        Point Project(AtlasPoint p) => new(live.X + (p.X - camera.X) * scaleX,
            live.Y + (capture.Height + camera.Y - p.Y) * scaleY);
        if (saved is { Count: > 0 })
        {
            AtlasPoint InLiveCoordinates(AtlasPoint p)
            {
                var point = savedProjection.ToCanvas(p);
                return new(camera.X + (point.X - live.X) / scaleX,
                    camera.Y + capture.Height - (point.Y - live.Y) / scaleY);
            }
            var projected = saved.Select(line => line with
            {
                AtlasStart = InLiveCoordinates(line.AtlasStart),
                AtlasEnd = InLiveCoordinates(line.AtlasEnd)
            }).ToArray();
            lines = LiveWorldGroundOverlay.CombineWithLive(projected, lines, (int)capture.Height, knight);
        }
        DrawLines(linesCanvas, lines, Project);
        if (patchesCanvas is null) return;
        foreach (var feature in features)
        {
            if (!double.IsFinite(feature.AtlasPosition.X) || !double.IsFinite(feature.AtlasPosition.Y)) continue;
            var center = Project(feature.AtlasPosition);
            if (center.X >= live.X && center.X < live.Right && center.Y >= live.Y && center.Y < live.Bottom)
                continue; // Inside the image, current matches own the boxes.
            var patchTop = feature.AtlasPosition.Y + GroundPixels.PatchHeight * .5;
            var left = feature.AtlasPosition.X - GroundPixels.PatchWidth * .5;
            if (!lines.Any(line => Math.Abs(line.AtlasStart.Y - patchTop) <= 3
                && line.AtlasStart.X <= left && line.AtlasEnd.X >= left + GroundPixels.PatchWidth)) continue;
            var marker = PersistentRectangle(feature, scaleX, scaleY);
            Canvas.SetLeft(marker, center.X - marker.Width * .5);
            Canvas.SetTop(marker, center.Y - marker.Height * .5);
            patchesCanvas.Children.Add(marker);
        }
    }

    private static void DrawLines(Canvas canvas, IReadOnlyList<GroundHypothesisAtlasLine> lines,
        Func<AtlasPoint, Point> project)
    {
        const DashboardFeatureReviewLineKind kind = DashboardFeatureReviewLineKind.PersistentGround;
        var brush = Brush(kind);
        var opacity = DashboardFeatureReviewPalette.Rgba(kind).Alpha;
        foreach (var ground in lines)
        {
            var a = project(ground.AtlasStart);
            var b = project(ground.AtlasEnd);
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
