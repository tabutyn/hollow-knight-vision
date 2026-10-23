using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private LabelingViewportTransform labelViewport = LabelingViewportTransform.Fit;
    private LabelingViewportTransform labelPanOrigin;
    private Point? labelPanStart;
    private MouseButton? labelPanButton;
    private bool labelPanMoved;

    private bool IsEditingLabelBox => dragStart is not null || modifyingBox is not null;

    private void LabelViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (LabelImage.Source is null) return;
        e.Handled = true;
        if (IsEditingLabelBox || labelPanStart is not null) return;
        var focus = e.GetPosition(LabelViewport);
        // One standard Windows wheel notch is 120; retain fractional deltas
        // from smooth wheels/trackpads instead of reducing them to a direction.
        labelViewport = LabelingImageLayout.ZoomedTransform(
            labelViewport,
            e.Delta / 120.0 * 0.12,
            focus,
            new Size(LabelViewport.ActualWidth, LabelViewport.ActualHeight));
        ApplyLabelViewport();
    }

    private void LabelViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (LabelImage.Source is null) return;
        var point = e.GetPosition(LabelViewport);
        UpdateLabelCrosshair(point);
        var panRequested = e.ChangedButton == MouseButton.Right
            || e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        // The parent receives preview input across the whole viewport. Make
        // the aspect-fit boundary explicit so a left drag in a letterbox can
        // never leak through as an annotation start.
        if (!panRequested && e.ChangedButton == MouseButton.Left
            && CurrentLabelingImageLayout.NormalizedPoint(point) is null)
        {
            e.Handled = true;
            return;
        }
        if (!panRequested) return;
        // Handle before the annotation canvas: navigation must never add,
        // move, delete, or autosave a label.
        e.Handled = true;
        if (IsEditingLabelBox || labelPanStart is not null) return;
        labelPanOrigin = labelViewport;
        labelPanStart = e.GetPosition(LabelViewport);
        labelPanButton = e.ChangedButton;
        labelPanMoved = false;
        if (!LabelViewport.CaptureMouse()) { EndLabelPan(); return; }
        LabelViewport.Cursor = Cursors.SizeAll;
        HideLabelCrosshair();
    }

    private void LabelViewport_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(LabelViewport);
        if (labelPanStart is { } start)
        {
            var buttonState = labelPanButton == MouseButton.Right ? e.RightButton : e.LeftButton;
            if (buttonState != MouseButtonState.Pressed) EndLabelPan();
            else
            {
                var dx = point.X - start.X;
                var dy = point.Y - start.Y;
                labelPanMoved |= Math.Abs(dx) >= SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(dy) >= SystemParameters.MinimumVerticalDragDistance;
                if (labelPanMoved)
                {
                    labelViewport = labelPanOrigin.PanBy(dx, dy);
                    ApplyLabelViewport();
                }
                e.Handled = true;
                return;
            }
        }
        UpdateLabelCrosshair(point);
    }

    private void LabelViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != labelPanButton || labelPanStart is null) return;
        var fit = labelPanButton == MouseButton.Right && !labelPanMoved;
        EndLabelPan();
        if (fit) ResetLabelViewport();
        UpdateLabelCrosshair(e.GetPosition(LabelViewport));
        e.Handled = true;
    }

    private void LabelViewport_MouseLeave(object sender, MouseEventArgs e) => HideLabelCrosshair();
    private void LabelViewport_LostMouseCapture(object sender, MouseEventArgs e) => EndLabelPan();
    private void LabelViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLabelBoxBorders();
        UpdateLabelCrosshair();
    }

    private void EndLabelPan()
    {
        if (labelPanStart is null) return;
        labelPanStart = null;
        labelPanButton = null;
        labelPanMoved = false;
        LabelViewport.ClearValue(CursorProperty);
        if (LabelViewport.IsMouseCaptured) LabelViewport.ReleaseMouseCapture();
    }

    private void ResetLabelViewport()
    {
        EndLabelPan();
        labelViewport = LabelingViewportTransform.Fit;
        ApplyLabelViewport();
    }

    private void ApplyLabelViewport()
    {
        // The image and annotations share a transform. WPF GetPosition on
        // LabelCanvas automatically returns unzoomed 640x360 coordinates.
        LabelingViewportRendering.Apply(
            LabelZoomTransform,
            LabelPanTransform,
            labelViewport);
        UpdateLabelBoxBorders();
        UpdateLabelCrosshair();
    }

    private double LabelBorderWidth => LabelBoxBounds.CanvasStrokeWidth(
        Math.Min(LabelViewport.ActualWidth / CanvasWidth, LabelViewport.ActualHeight / CanvasHeight)
        * labelViewport.Zoom);

    private LabelingImageLayout CurrentLabelingImageLayout => new(
        new Size(CanvasWidth, CanvasHeight),
        new Size(LabelViewport.ActualWidth, LabelViewport.ActualHeight),
        labelViewport.Zoom,
        new Vector(labelViewport.PanX, labelViewport.PanY));

    private void PositionLabelRectangle(Rectangle rectangle, Point first, Point second)
    {
        var normalized = new LabelingImageLayout(
                new Size(CanvasWidth, CanvasHeight),
                new Size(CanvasWidth, CanvasHeight))
            .NormalizedRect(first, second);
        if (normalized is null) return;
        var selection = new LabelBoxBounds(
            normalized.Value.X * CanvasWidth,
            normalized.Value.Y * CanvasHeight,
            normalized.Value.Width * CanvasWidth,
            normalized.Value.Height * CanvasHeight);
        rectangle.Tag = selection;
        ApplyLabelBoxBorder(rectangle, selection);
    }

    private void ApplyLabelBoxBorder(Rectangle rectangle, LabelBoxBounds selection)
    {
        var stroke = LabelBorderWidth;
        var outer = selection.OutsideStroke(stroke);
        rectangle.StrokeThickness = stroke;
        Canvas.SetLeft(rectangle, outer.X);
        Canvas.SetTop(rectangle, outer.Y);
        rectangle.Width = outer.Width;
        rectangle.Height = outer.Height;
    }

    private void UpdateLabelBoxBorders()
    {
        if (LabelCanvas is null) return;
        foreach (var rectangle in LabelCanvas.Children.OfType<Rectangle>())
            if (rectangle.Tag is LabelBoxBounds selection) ApplyLabelBoxBorder(rectangle, selection);
    }

    private void HideLabelCrosshair()
    {
        if (LabelCrosshair is not null) LabelCrosshair.Visibility = Visibility.Collapsed;
        // Scoped to this viewport, not a global cursor override. Preserve the
        // pan cursor and restore the ordinary cursor as soon as guides hide.
        if (LabelViewport?.Cursor == Cursors.None) LabelViewport.ClearValue(CursorProperty);
    }

    private void UpdateLabelCrosshair(Point? position = null)
    {
        if (LabelCrosshair is null || LabelImage.Source is null || workspace != "Label"
            || !IsActive || !LabelViewport.IsMouseOver || labelPanStart is not null
            || !(LabelAddButton.IsChecked == true || LabelNegativeButton.IsChecked == true))
        {
            HideLabelCrosshair();
            return;
        }
        var point = position ?? Mouse.GetPosition(LabelViewport);
        var fitted = CurrentLabelingImageLayout.FittedRect;
        var left = Math.Max(0, fitted.Left);
        var top = Math.Max(0, fitted.Top);
        var right = Math.Min(LabelViewport.ActualWidth, fitted.Right);
        var bottom = Math.Min(LabelViewport.ActualHeight, fitted.Bottom);
        if (right <= left || bottom <= top || point.X < left || point.X > right
            || point.Y < top || point.Y > bottom)
        {
            HideLabelCrosshair();
            return;
        }
        LabelCrosshairHorizontal.X1 = left;
        LabelCrosshairHorizontal.X2 = right;
        LabelCrosshairHorizontal.Y1 = LabelCrosshairHorizontal.Y2 = point.Y;
        LabelCrosshairVertical.X1 = LabelCrosshairVertical.X2 = point.X;
        LabelCrosshairVertical.Y1 = top;
        LabelCrosshairVertical.Y2 = bottom;
        LabelCrosshair.Visibility = Visibility.Visible;
        LabelViewport.Cursor = Cursors.None;
    }
}

internal readonly record struct LabelingRenderedBounds(
    Rect ImageBounds,
    Rect AnnotationBounds);

/// <summary>
/// Applies the transform used by the live labeling Viewbox and provides an
/// offscreen projection seam that measures those same WPF elements. This
/// keeps geometry regressions on the renderer path instead of duplicating its
/// scale and translation arithmetic in tests.
/// </summary>
internal static class LabelingViewportRendering
{
    internal static void Apply(
        ScaleTransform zoom,
        TranslateTransform pan,
        LabelingViewportTransform viewport)
    {
        zoom.ScaleX = zoom.ScaleY = viewport.Zoom;
        pan.X = viewport.PanX;
        pan.Y = viewport.PanY;
    }

    internal static LabelingRenderedBounds Measure(
        Size imageSize,
        Size containerSize,
        double zoom,
        Vector pan,
        Rect normalizedAnnotation)
    {
        if (!IsValid(imageSize) || !IsValid(containerSize))
            throw new ArgumentOutOfRangeException(nameof(imageSize));
        var layout = new LabelingImageLayout(imageSize, containerSize, zoom, pan);
        var annotation = layout.DisplayRect(normalizedAnnotation)
            ?? throw new ArgumentOutOfRangeException(nameof(normalizedAnnotation));

        var root = new Grid
        {
            Width = containerSize.Width,
            Height = containerSize.Height,
            ClipToBounds = true
        };
        var zoomTransform = new ScaleTransform();
        var panTransform = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(zoomTransform);
        transforms.Children.Add(panTransform);
        var viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = transforms
        };
        var content = new Grid
        {
            Width = imageSize.Width,
            Height = imageSize.Height
        };
        var image = new Image
        {
            Width = imageSize.Width,
            Height = imageSize.Height,
            Stretch = Stretch.Fill,
            Source = new DrawingImage(new GeometryDrawing(
                Brushes.Black,
                null,
                new RectangleGeometry(new Rect(new Point(), imageSize))))
        };
        var canvas = new Canvas
        {
            Width = imageSize.Width,
            Height = imageSize.Height,
            Background = Brushes.Transparent
        };
        var rectangle = new Rectangle
        {
            Width = normalizedAnnotation.Width * imageSize.Width,
            Height = normalizedAnnotation.Height * imageSize.Height,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(rectangle, normalizedAnnotation.X * imageSize.Width);
        Canvas.SetTop(rectangle, normalizedAnnotation.Y * imageSize.Height);
        canvas.Children.Add(rectangle);
        content.Children.Add(image);
        content.Children.Add(canvas);
        viewbox.Child = content;
        root.Children.Add(viewbox);

        Apply(zoomTransform, panTransform, new LabelingViewportTransform(
            zoom,
            pan.X,
            pan.Y));
        root.Measure(containerSize);
        root.Arrange(new Rect(new Point(), containerSize));
        root.UpdateLayout();

        var imageBounds = BoundsIn(image, root);
        var annotationBounds = BoundsIn(rectangle, root);
        if (!NearlyEqual(annotationBounds, annotation))
        {
            throw new InvalidOperationException(
                $"WPF annotation bounds {annotationBounds} diverged from canonical bounds {annotation}.");
        }
        return new LabelingRenderedBounds(
            imageBounds,
            annotationBounds);
    }

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(new Point(), element.RenderSize));

    private static bool NearlyEqual(Rect left, Rect right) =>
        Math.Abs(left.X - right.X) <= .0001
        && Math.Abs(left.Y - right.Y) <= .0001
        && Math.Abs(left.Width - right.Width) <= .0001
        && Math.Abs(left.Height - right.Height) <= .0001;

    private static bool IsValid(Size size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height)
        && size.Width > 0 && size.Height > 0;
}

/// <summary>
/// Canonical top-left-origin conversion between a clean source image and its
/// aspect-fit labeling viewport. The image and every annotation use this same
/// zoom/pan geometry; saved rectangles remain normalized image coordinates.
/// </summary>
internal readonly record struct LabelingImageLayout(
    Size ImageSize,
    Size ContainerSize,
    double ZoomScale,
    Vector PanOffset)
{
    internal LabelingImageLayout(Size imageSize, Size containerSize)
        : this(imageSize, containerSize, 1, new Vector())
    {
    }

    internal Rect BaseFittedRect
    {
        get
        {
            if (!IsValid(ImageSize) || !IsValid(ContainerSize)) return Rect.Empty;
            var scale = Math.Min(
                ContainerSize.Width / ImageSize.Width,
                ContainerSize.Height / ImageSize.Height);
            var width = ImageSize.Width * scale;
            var height = ImageSize.Height * scale;
            return new Rect(
                (ContainerSize.Width - width) / 2,
                (ContainerSize.Height - height) / 2,
                width,
                height);
        }
    }

    internal Rect FittedRect
    {
        get
        {
            var basis = BaseFittedRect;
            if (basis.IsEmpty
                || !double.IsFinite(ZoomScale) || ZoomScale <= 0
                || !double.IsFinite(PanOffset.X) || !double.IsFinite(PanOffset.Y))
            {
                return Rect.Empty;
            }
            var width = basis.Width * ZoomScale;
            var height = basis.Height * ZoomScale;
            return new Rect(
                basis.X + basis.Width / 2 + PanOffset.X - width / 2,
                basis.Y + basis.Height / 2 + PanOffset.Y - height / 2,
                width,
                height);
        }
    }

    internal Point? NormalizedPoint(Point point, bool clamped = false)
    {
        var fitted = FittedRect;
        if (fitted.IsEmpty
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !clamped && !fitted.Contains(point))
        {
            return null;
        }
        return new Point(
            Math.Clamp((point.X - fitted.Left) / fitted.Width, 0, 1),
            Math.Clamp((point.Y - fitted.Top) / fitted.Height, 0, 1));
    }

    internal Rect? NormalizedRect(Point start, Point end)
    {
        var first = NormalizedPoint(start);
        var last = NormalizedPoint(end, clamped: true);
        if (first is null || last is null) return null;
        return new Rect(
            Math.Min(first.Value.X, last.Value.X),
            Math.Min(first.Value.Y, last.Value.Y),
            Math.Abs(last.Value.X - first.Value.X),
            Math.Abs(last.Value.Y - first.Value.Y));
    }

    internal Rect? DisplayRect(Rect normalizedRect)
    {
        var fitted = FittedRect;
        if (fitted.IsEmpty || normalizedRect.IsEmpty
            || !double.IsFinite(normalizedRect.Left)
            || !double.IsFinite(normalizedRect.Top)
            || !double.IsFinite(normalizedRect.Right)
            || !double.IsFinite(normalizedRect.Bottom))
        {
            return null;
        }
        var clipped = Rect.Intersect(normalizedRect, new Rect(0, 0, 1, 1));
        if (clipped.IsEmpty || clipped.Width <= 0 || clipped.Height <= 0) return null;
        return new Rect(
            fitted.Left + clipped.Left * fitted.Width,
            fitted.Top + clipped.Top * fitted.Height,
            clipped.Width * fitted.Width,
            clipped.Height * fitted.Height);
    }

    internal static Vector ZoomedPan(
        Vector currentPan,
        double currentZoom,
        double newZoom,
        Point focus,
        Size containerSize)
    {
        if (!double.IsFinite(currentZoom) || currentZoom <= 0
            || !double.IsFinite(newZoom) || newZoom <= 0)
        {
            return currentPan;
        }
        var ratio = newZoom / currentZoom;
        var oldCenter = new Point(
            containerSize.Width / 2 + currentPan.X,
            containerSize.Height / 2 + currentPan.Y);
        var newCenter = new Point(
            focus.X - (focus.X - oldCenter.X) * ratio,
            focus.Y - (focus.Y - oldCenter.Y) * ratio);
        return new Vector(
            newCenter.X - containerSize.Width / 2,
            newCenter.Y - containerSize.Height / 2);
    }

    internal static LabelingViewportTransform ZoomedTransform(
        LabelingViewportTransform current,
        double logarithmicDelta,
        Point focus,
        Size containerSize)
    {
        if (!double.IsFinite(logarithmicDelta)
            || !double.IsFinite(focus.X) || !double.IsFinite(focus.Y)
            || !IsValid(containerSize))
        {
            return current;
        }
        var nextZoom = Math.Clamp(
            current.Zoom * Math.Exp(Math.Clamp(logarithmicDelta, -0.35, 0.35)),
            LabelingViewportTransform.MinimumZoom,
            LabelingViewportTransform.MaximumZoom);
        if (Math.Abs(nextZoom - current.Zoom) < 0.0001) return current;
        var pan = ZoomedPan(
            new Vector(current.PanX, current.PanY),
            current.Zoom,
            nextZoom,
            focus,
            containerSize);
        return new LabelingViewportTransform(nextZoom, pan.X, pan.Y);
    }

    private static bool IsValid(Size size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height)
        && size.Width > 0 && size.Height > 0;
}
