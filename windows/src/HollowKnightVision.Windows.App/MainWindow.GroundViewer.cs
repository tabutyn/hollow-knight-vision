using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HollowKnightVision.Windows.Core;
using FeatureKey = HollowKnightVision.Windows.App.GroundFeatureOverlayPresentation.FeatureKey;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record GroundViewerFrame(long Generation, string? Room, long? Epoch,
        IReadOnlyList<GroundHypothesisAtlasFeature> Features, GroundFeatureOverlayPresentation.AtlasDrawing Drawing)
    {
        internal (long Generation, string? Room, long? Epoch) Scope => (Generation, Room, Epoch);
    }

    private static readonly object GroundViewerHighlight = new();
    private GroundFeatureViewer? groundFeatureViewer;
    private GroundViewerFrame? groundViewerFrame;
    private (long Generation, string? Room, long? Epoch)? groundViewerPatchScope;
    private static readonly Brush ViewerGroundBrush =
        GroundFeatureOverlayPresentation.Brush(DashboardFeatureReviewLineKind.PersistentGround);

    private void GroundViewer_Click(object sender, RoutedEventArgs e)
    {
        var viewer = OpenGroundViewer();
        viewer.ClearPatch();
        RefreshGroundViewer();
    }

    private GroundFeatureViewer OpenGroundViewer()
    {
        if (groundFeatureViewer is { } existing) return existing;
        var viewer = new GroundFeatureViewer { Owner = this };
        var refresh = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(125) };
        refresh.Tick += (_, _) => RefreshGroundViewer();
        viewer.SelectionChanged += DrawGroundViewerHighlight;
        viewer.Closed += (_, _) =>
        {
            refresh.Stop();
            groundFeatureViewer = null;
            groundViewerPatchScope = null;
            DrawGroundViewerHighlight();
        };
        groundFeatureViewer = viewer;
        viewer.Show();
        refresh.Start();
        return viewer;
    }

    private GroundViewerFrame? CurrentGroundViewerFrame() => workspace == "Gameplay"
        && groundViewerFrame is { } frame && frame.Generation == Interlocked.Read(ref recentCaptureGeneration)
        ? frame : null;

    private bool SelectGroundViewerPatch(MouseButtonEventArgs e)
    {
        if (workspace != "Gameplay" || GroundLinesCheck.IsChecked != true || GroundTrackingPatchesCheck.IsChecked != true
            || CurrentGroundViewerFrame() is not { } frame || recognizedMenuContext != "game") return false;
        AdvanceViewportAnimations();
        var hit = GroundViewerKeyAt(LiveGroundPatchesCanvas, e.GetPosition(LiveGroundPatchesCanvas))
            ?? GroundViewerKeyAt(AtlasGroundPatchesCanvas, e.GetPosition(AtlasGroundPatchesCanvas));
        if (hit is null || frame.Features.FirstOrDefault(feature => new FeatureKey(feature) == hit) is not { } feature)
            return false;

        var viewer = OpenGroundViewer();
        groundViewerPatchScope = frame.Scope;
        viewer.ShowPatch(feature, frame.Features);
        RefreshGroundViewer();
        e.Handled = true;
        return true;
    }

    internal static FeatureKey? GroundViewerKeyAt(Canvas canvas, Point point) => canvas.Children.OfType<Rectangle>()
        .Where(box => box.Tag is FeatureKey && new Rect(Canvas.GetLeft(box), Canvas.GetTop(box), box.Width, box.Height).Contains(point))
        .OrderBy(box => (point - new Point(Canvas.GetLeft(box) + box.Width / 2, Canvas.GetTop(box) + box.Height / 2)).LengthSquared)
        .FirstOrDefault()?.Tag as FeatureKey?;

    private void DrawGroundViewerHighlight()
    {
        var key = CurrentGroundViewerFrame() is { } frame && groundViewerPatchScope == frame.Scope
            && groundFeatureViewer?.Selected is { } patch ? new FeatureKey(patch) : (FeatureKey?)null;
        foreach (var canvas in new[] { LiveGroundPatchesCanvas, AtlasGroundPatchesCanvas })
        {
            var prior = canvas.Children.OfType<Rectangle>().FirstOrDefault(box => ReferenceEquals(box.Tag, GroundViewerHighlight));
            if (prior is not null) canvas.Children.Remove(prior);
            if (key is null || canvas.Children.OfType<Rectangle>().FirstOrDefault(box => box.Tag is FeatureKey k && k == key) is not { } box)
                continue;
            var selected = new Rectangle
            {
                Width = box.Width, Height = box.Height, StrokeThickness = 3, Tag = GroundViewerHighlight,
                Stroke = GroundFeatureOverlayPresentation.Brush(DashboardFeatureReviewMarkerKind.SelectedGroundFeature),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(selected, Canvas.GetLeft(box));
            Canvas.SetTop(selected, Canvas.GetTop(box));
            canvas.Children.Add(selected);
        }
    }

    private void ClearGroundViewerFrame()
    {
        groundViewerFrame = null;
        groundViewerPatchScope = null;
        groundFeatureViewer?.ClearPatch();
    }

    private void RefreshGroundViewer()
    {
        if (groundFeatureViewer is not { } viewer) return;
        var frame = CurrentGroundViewerFrame();
        if (viewer.Selected is not null)
        {
            if (workspace != "Gameplay" || groundViewerPatchScope?.Generation != Interlocked.Read(ref recentCaptureGeneration)
                || frame is not null && groundViewerPatchScope != frame.Scope)
                viewer.ClearPatch();
            else if (frame is not null) viewer.RefreshPatches(frame.Features);
        }

        var strokes = new List<GroundFeatureViewer.Stroke>();
        var live = GameplayPathImageBounds();
        if (workspace == "Hacker")
        {
            var originX = (hackerAtlasMinimumTileX ?? 0) * (double)HackerAtlasTileSize;
            var originY = (hackerAtlasMinimumTileY ?? 0) * (double)HackerAtlasTileSize;
            var rooms = HackerTerrainRooms();
            var labels = groundTruthLabels.Document.AuthoredLines;
            foreach (var room in rooms)
            {
                var offset = PathOffset(room);
                foreach (var floor in HackerPaths(room).Graph.Floors)
                    strokes.Add(new(new(floor.Left + offset.X - originX, -floor.Y - offset.Y - originY),
                        new(floor.Right + offset.X - originX, -floor.Y - offset.Y - originY), ViewerGroundBrush));
                foreach (var spike in labels.Where(line => line.SceneName == room.SceneName && line.Kind == GroundTruthLineKind.Spike))
                {
                    Point Project(AtlasPoint point) => new(point.X - originX, -point.Y - originY);
                    strokes.Add(new(Project(room.Projection.WorldToAtlas(spike.Start, room.Position)),
                        Project(room.Projection.WorldToAtlas(spike.End, room.Position)), Brushes.OrangeRed));
                }
            }
            if (ShowsHackerPaths) AppendPaths(HackerPathCanvas);
            live = new(Canvas.GetLeft(HackerLiveImage), Canvas.GetTop(HackerLiveImage), HackerLiveImage.Width, HackerLiveImage.Height);
        }
        else if (workspace == "Gameplay")
        {
            var drawing = frame?.Drawing;
            var project = drawing?.Project ?? SavedGroundProjection.ToCanvas;
            foreach (var floor in drawing?.Lines ?? gameplaySavedWorld.DisplayLines)
                strokes.Add(new(project(floor.AtlasStart), project(floor.AtlasEnd), ViewerGroundBrush));
            if (ShowsGameplayPaths) AppendPaths(GameplayPathCanvas);
        }
        else live = default;

        var center = new Point(0, 0);
        if (live.IsUsable) center = new(live.X + live.Width / 2, live.Y + live.Height / 2);
        else if (strokes.Count > 0)
            center = new((strokes.Min(line => Math.Min(line.Start.X, line.End.X)) + strokes.Max(line => Math.Max(line.Start.X, line.End.X))) / 2,
                (strokes.Min(line => Math.Min(line.Start.Y, line.End.Y)) + strokes.Max(line => Math.Max(line.Start.Y, line.End.Y))) / 2);
        viewer.PresentOverview(strokes, center, frame?.Drawing.Project);

        void AppendPaths(Canvas canvas)
        {
            foreach (var line in canvas.Children.OfType<Line>())
                strokes.Add(new(new(line.X1, line.Y1), new(line.X2, line.Y2), line.Stroke));
        }
    }
}
