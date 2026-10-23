using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private static void PresentObjectInferenceDiagnostics(Border notice, TextBlock noticeText,
        TextBlock settingsText, FrameworkElement options, LiveObjectInferenceDiagnostics diagnostics)
    {
        noticeText.Text = settingsText.Text = diagnostics.Issue ?? "";
        notice.Visibility = settingsText.Visibility = diagnostics.Issue is null ? Visibility.Collapsed : Visibility.Visible;
        notice.ToolTip = diagnostics.Issue;
        options.ToolTip = diagnostics.ModelSeconds is { } seconds
            ? $"Model: {seconds * 1000:F1} ms; pixel refinement: {diagnostics.RefinementSeconds * 1000:F1} ms; source frame: {diagnostics.SourceFrameIdentifier}"
            : null;
    }

    private void DrawObjectDetectionOverlay(Canvas canvas, LiveObjectDetectionSnapshot objects,
        double width, double height, bool raw, bool pixels)
    {
        var extent = new StencilRect(0, 0, width, height);
        var legacyRegions = SceneRegions.Hud(
            extent,
            SceneRegions.FromObjectDetections(objects.Tracked, extent).Knight);
        if (GameplayImage.Source is BitmapSource source)
            PresentGameplayFrameCore(GameplayImage, canvas, source, legacyRegions);
        else
            DrawLegacySceneRegionOverlayCore(canvas, legacyRegions, width, height);
        DrawObjectDetectionOverlayCore(
            canvas,
            objects,
            width,
            height,
            raw,
            pixels,
            objectReferencePanel?.Atlas);
    }

    /// <summary>
    /// Publishes clean capture pixels and keeps legacy scene-region diagnostics
    /// in the independent WPF overlay. Region geometry must never be baked into
    /// the source bitmap consumed by tracking, labeling, or atlas persistence.
    /// </summary>
    private static void PresentGameplayFrameCore(
        Image gameplay,
        Canvas overlay,
        BitmapSource source,
        SceneRegions legacyRegions)
    {
        gameplay.Source = source;
        DrawLegacySceneRegionOverlayCore(
            overlay,
            legacyRegions,
            source.PixelWidth,
            source.PixelHeight);
    }

    private static void DrawLegacySceneRegionOverlayCore(
        Canvas overlay,
        SceneRegions regions,
        double width,
        double height)
    {
        var extent = new StencilRect(0, 0, width, height);
        foreach (var region in regions.OmittedRects)
        {
            var clipped = region.Intersect(extent);
            if (!clipped.IsFinite || clipped.IsEmpty) continue;
            var rectangle = new Rectangle
            {
                Width = clipped.Width,
                Height = clipped.Height,
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 1,
                StrokeDashArray = [3, 2],
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                Tag = clipped
            };
            Canvas.SetLeft(rectangle, clipped.X);
            Canvas.SetTop(rectangle, clipped.Y);
            overlay.Children.Add(rectangle);
        }
    }

    private static void DrawObjectDetectionOverlayCore(
        Canvas canvas,
        LiveObjectDetectionSnapshot objects,
        double width,
        double height,
        bool raw,
        bool pixels,
        LabelingObjectAtlasSnapshot? iconAtlas)
    {
        if (pixels && objects.PixelDiagnostic is { } diagnostic)
            canvas.Children.Add(new Image { Source = FrameBitmap(diagnostic), Width = width, Height = height,
                Stretch = Stretch.Fill, IsHitTestVisible = false });
        foreach (var detection in raw ? objects.Raw : objects.Tracked)
        {
            var box = detection.Box;
            var rectangle = new Rectangle { Width = box.Width * width, Height = box.Height * height,
                Stroke = raw ? Brushes.Gold : Brushes.Lime, StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(20, 0, 255, 0)), IsHitTestVisible = false };
            Canvas.SetLeft(rectangle, box.X * width); Canvas.SetTop(rectangle, box.Y * height);
            canvas.Children.Add(rectangle);
            if (iconAtlas?.Icon(detection.ClassIdentifier) is { } icon
                && ObjectIconDestination(
                    new Rect(box.X * width, box.Y * height, box.Width * width, box.Height * height),
                    new Size(icon.PixelWidth, icon.PixelHeight),
                    new Rect(0, 0, width, height)) is { } destination)
            {
                var iconImage = new Image
                {
                    Source = icon,
                    Width = destination.Width,
                    Height = destination.Height,
                    Stretch = Stretch.Uniform,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(iconImage, destination.X);
                Canvas.SetTop(iconImage, destination.Y);
                canvas.Children.Add(iconImage);
                continue;
            }
            var label = new TextBlock { Text = $"{detection.ClassIdentifier} {detection.Confidence:P0}",
                Background = Brushes.Black, Foreground = raw ? Brushes.Gold : Brushes.Lime,
                FontSize = 11, Padding = new Thickness(2), IsHitTestVisible = false };
            Canvas.SetLeft(label, box.X * width); Canvas.SetTop(label, Math.Max(0, box.Y * height - 19));
            canvas.Children.Add(label);
        }
    }

    /// <summary>
    /// Places an object reference outside its detection rectangle. WPF uses a
    /// top-left origin, so this is the vertical mirror of the Core Image rule.
    /// </summary>
    private static Rect? ObjectIconDestination(Rect box, Size iconSize, Rect extent)
    {
        if (!IsFinitePositive(iconSize.Width)
            || !IsFinitePositive(iconSize.Height)
            || box.IsEmpty
            || extent.IsEmpty
            || !IsFinitePositive(box.Width)
            || !IsFinitePositive(box.Height)
            || !IsFinitePositive(extent.Width)
            || !IsFinitePositive(extent.Height))
        {
            return null;
        }

        var iconHeight = Math.Min(20, extent.Height);
        var iconWidth = Math.Min(
            80,
            Math.Max(iconHeight, iconHeight * iconSize.Width / iconSize.Height));
        const double gap = 4;
        var clampedX = Math.Min(
            Math.Max(box.Left, extent.Left),
            extent.Right - iconWidth);

        if (box.Top - gap - iconHeight >= extent.Top)
            return new Rect(clampedX, box.Top - gap - iconHeight, iconWidth, iconHeight);
        if (box.Bottom + gap + iconHeight <= extent.Bottom)
            return new Rect(clampedX, box.Bottom + gap, iconWidth, iconHeight);

        var clampedY = Math.Min(
            Math.Max(box.Top + (box.Height - iconHeight) / 2, extent.Top),
            extent.Bottom - iconHeight);
        if (box.Right + gap + iconWidth <= extent.Right)
            return new Rect(box.Right + gap, clampedY, iconWidth, iconHeight);
        if (box.Left - gap - iconWidth >= extent.Left)
            return new Rect(box.Left - gap - iconWidth, clampedY, iconWidth, iconHeight);
        return null;
    }

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;
}
