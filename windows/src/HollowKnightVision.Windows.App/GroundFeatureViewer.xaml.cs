using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class GroundFeatureViewer : Window
{
    internal sealed record Stroke(Point Start, Point End, Brush Color);
    private const double TileWidth = 96;
    private const double TileHeight = 96;
    private readonly Overview overview = new();
    private GroundViewerRowState? row;
    private Point? pointerDown;
    private double dragCenter;
    private double stripCenter;
    private bool settingPosition;
    private Point overviewCenter;
    private Func<AtlasPoint, Point>? patchProjection;

    internal GroundHypothesisAtlasFeature? Selected => row?.Selected;
    internal event Action? SelectionChanged;

    public GroundFeatureViewer()
    {
        InitializeComponent();
        OverviewHost.Children.Add(overview);
    }

    internal void PresentOverview(IReadOnlyList<Stroke> strokes, Point center, Func<AtlasPoint, Point>? project)
    {
        overviewCenter = center;
        patchProjection = project;
        overview.Lines = strokes;
        UpdateOverviewCenter();
    }

    internal void ShowPatch(GroundHypothesisAtlasFeature feature, IReadOnlyList<GroundHypothesisAtlasFeature> features)
    {
        row = GroundViewerRowState.Refreshed(feature, features);
        stripCenter = row.Selected.SequenceIndex;
        RefreshStrip();
        SelectionChanged?.Invoke();
    }

    internal void RefreshPatches(IReadOnlyList<GroundHypothesisAtlasFeature> features)
    {
        if (row is null) return;
        row = GroundViewerRowState.Refreshed(row.Selected, features);
        SetCenter(stripCenter);
    }

    internal void ClearPatch()
    {
        if (row is null) return;
        row = null;
        pointerDown = null;
        PatchStrip.ReleaseMouseCapture();
        PatchPixels.Source = null;
        PatchPanel.Visibility = Visibility.Collapsed;
        UpdateOverviewCenter();
        SelectionChanged?.Invoke();
    }

    private void SetCenter(double position)
    {
        if (row is null) return;
        var first = row.Slots[0].SequenceIndex;
        stripCenter = Math.Clamp(position, first, row.Slots[^1].SequenceIndex);
        var next = GroundViewerRowAssembler.NearestFeature((int)Math.Floor(stripCenter - first + .5), row.Slots)!;
        var changed = row.Selected.SequenceIndex != next.SequenceIndex;
        row = row with { Selected = next };
        RefreshStrip();
        if (changed) SelectionChanged?.Invoke();
    }

    private void RefreshStrip()
    {
        if (row is not { } current) return;
        PatchPanel.Visibility = Visibility.Visible;
        var first = current.Slots[0].SequenceIndex;
        settingPosition = true;
        PatchPosition.Minimum = first;
        PatchPosition.Maximum = current.Slots[^1].SequenceIndex;
        PatchPosition.Value = stripCenter;
        settingPosition = false;
        PatchDetails.Text = FormattableString.Invariant(
            $"Line {current.Selected.SegmentId} · Patch {current.Selected.SequenceIndex} · X {current.Selected.AtlasPosition.X:F1} · Y {current.Selected.AtlasPosition.Y:F1}");
        PreviousPatch.IsEnabled = current.Selected.SequenceIndex > first;
        NextPatch.IsEnabled = current.Selected.SequenceIndex < current.Slots[^1].SequenceIndex;
        UpdateOverviewCenter();
        if (PatchStrip.ActualWidth <= 0) return;

        // Only assemble cells intersecting the visible strip, including gaps.
        var halfVisible = PatchStrip.ActualWidth / (2 * TileWidth);
        var localCenter = stripCenter - first;
        var start = Math.Max(0, (int)Math.Floor(localCenter - halfVisible));
        var end = Math.Min(current.Slots.Count, (int)Math.Ceiling(localCenter + halfVisible) + 1);
        var slots = current.Slots.Skip(start).Take(end - start).ToArray();
        var width = slots.Length * GroundHypothesisTracker.FeatureWidth;
        var pixels = GroundViewerRowAssembler.StitchedPixels(slots).ToArray();
        var bitmap = BitmapSource.Create(width, GroundHypothesisTracker.FeatureHeight,
            96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        PatchPixels.Source = bitmap;
        PatchPixels.Width = slots.Length * TileWidth;
        PatchPixels.Height = TileHeight;
        var origin = PatchStrip.ActualWidth / 2 - (localCenter + .5) * TileWidth;
        Canvas.SetLeft(PatchPixels, origin + start * TileWidth);
        Canvas.SetLeft(PatchSelection, origin + (current.Selected.SequenceIndex - first) * TileWidth + 2);
    }

    private void UpdateOverviewCenter()
    {
        overview.Center = row is not null && patchProjection is not null ? patchProjection(row.Selected.AtlasPosition) : overviewCenter;
        overview.InvalidateVisual();
    }

    internal void MovePatch(int direction)
    {
        if (row is not { } current) return;
        var neighbor = direction < 0
            ? current.Features.LastOrDefault(feature => feature.SequenceIndex < current.Selected.SequenceIndex)
            : current.Features.FirstOrDefault(feature => feature.SequenceIndex > current.Selected.SequenceIndex);
        if (neighbor is not null) SetCenter(neighbor.SequenceIndex);
    }

    private void PreviousPatch_Click(object sender, RoutedEventArgs e) => MovePatch(-1);
    private void NextPatch_Click(object sender, RoutedEventArgs e) => MovePatch(1);
    private void PatchStrip_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshStrip();

    private void PatchPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!settingPosition) SetCenter(e.NewValue);
    }

    private void PatchStrip_Down(object sender, MouseButtonEventArgs e)
    {
        if (row is null || !PatchStrip.CaptureMouse()) return;
        pointerDown = e.GetPosition(PatchStrip);
        dragCenter = stripCenter;
        e.Handled = true;
    }

    private void PatchStrip_Move(object sender, MouseEventArgs e)
    {
        if (pointerDown is { } start) SetCenter(dragCenter - (e.GetPosition(PatchStrip).X - start.X) / TileWidth);
    }

    private void PatchStrip_Up(object sender, MouseButtonEventArgs e)
    {
        if (pointerDown is not { } start) return;
        var x = e.GetPosition(PatchStrip).X;
        if (Math.Abs(x - start.X) < SystemParameters.MinimumHorizontalDragDistance)
            SetCenter(dragCenter + (x - PatchStrip.ActualWidth / 2) / TileWidth);
        pointerDown = null;
        PatchStrip.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PatchStrip_LostCapture(object sender, MouseEventArgs e) => pointerDown = null;

    private void PatchStrip_Wheel(object sender, MouseWheelEventArgs e)
    {
        SetCenter(stripCenter - e.Delta / 120d);
        e.Handled = true;
    }

    private sealed class Overview : FrameworkElement
    {
        internal IReadOnlyList<Stroke> Lines { get; set; } = [];
        internal Point Center { get; set; }

        protected override void OnRender(DrawingContext drawing)
        {
            var scale = ActualWidth / 480;
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            if (scale <= 0 || ActualHeight <= 0) return;
            var pens = new Dictionary<Brush, Pen>();
            foreach (var line in Lines)
            {
                var a = Project(line.Start);
                var b = Project(line.End);
                if (!bounds.IntersectsWith(new Rect(a, b))) continue;
                if (!pens.TryGetValue(line.Color, out var pen)) pens[line.Color] = pen = new Pen(line.Color, 2);
                drawing.DrawLine(pen, a, b);
            }
            Point Project(Point position) => new((position.X - Center.X) * scale + ActualWidth / 2,
                (position.Y - Center.Y) * scale + ActualHeight / 2);
        }
    }
}
