using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record HackerPathCache(GroundTruthLineDocument Document, AtlasPoint Scale, string Room, int LinkRevision,
        AtlasPlatformGraph Graph, IReadOnlyList<AtlasPathStroke> Display);
    private readonly AtlasPathLinkStore hackerPathLinks;
    private AtlasPathEditTool hackerPathTool;
    private string previousHackerTerrainTarget = "Objective";
    private bool EditsHackerPaths => hackerEditingTarget == "Path";
    private readonly Dictionary<string, HackerPathCache> hackerPathGraphs = new(StringComparer.Ordinal);
    private readonly HackerAtlasRoute hackerPathRoute = new();
    private HackerAtlasObjectives hackerObjectives = HackerAtlasObjectives.Default;
    private ViewportRect? hackerPathFitBounds;
    private HackerPipelinePublication? hackerPathPreview;
    private const double KnightWorldHeight = 1.28;

    private bool HasHackerPathPreview => workspace == "Hacker" && visionCancellation is not null && !hackerPreviewActive
        && hackerPathPreview is { Prepared.RoomPlacementReady: true } preview
        && preview.CaptureGeneration == Interlocked.Read(ref recentCaptureGeneration)
        && preview.WorkGeneration == Interlocked.Read(ref hackerWorkGeneration)
        && atlasPathEvidence.AdmitsHacker(preview.CaptureGeneration, preview.Prepared.LiveImage.CaptureId,
            preview.Prepared.CapturedAt, preview.Prepared.Sample.SessionId, preview.PathSceneSignal,
            preview.Prepared.Sample.AtlasGameplayActive);

    private bool ShowsHackerPaths => AtlasPathsCheck?.IsChecked == true && HasHackerPathPreview;

    private void RefreshAtlasPathVisibility()
    {
        UpdateGameplayPathVisibility();
        var showsPaths = ShowsHackerPaths;
        if (showsPaths && HackerPathCanvas.Visibility != Visibility.Visible)
            RedrawHackerEdits();
        else if (!showsPaths)
        {
            if (HackerPathCanvas.Visibility == Visibility.Visible) hackerPathRoute.Interrupt();
            HackerPathCanvas.Visibility = Visibility.Collapsed;
        }
        HackerCameraControlsPanel.Visibility = HasHackerPathPreview ? Visibility.Visible : Visibility.Collapsed;
        RenderHackerWallContacts();
    }

    private HackerPathCache HackerPaths(TerrainRoom room)
    {
        var scale = room.Projection.PixelsPerWorldUnit;
        var document = groundTruthLabels.Document;
        if (hackerPathGraphs.TryGetValue(room.SceneName, out var cached)
            && ReferenceEquals(cached.Document, document) && cached.Scale == scale && cached.Room == room.Key
            && cached.LinkRevision == hackerPathLinks.Revision) return cached;
        var graph = AuthoredAtlasGraph.Build(document, PathCalibration(room), hackerPathLinks, cached?.Graph);
        var result = new HackerPathCache(document, scale, room.Key, hackerPathLinks.Revision,
            graph, AtlasPathRouting.DisplayStrokes(graph.Strokes));
        hackerPathGraphs[room.SceneName] = result;
        return result;
    }

    private void AtlasPaths_Changed(object sender, RoutedEventArgs e)
    {
        Volatile.Write(ref atlasPathsEnabled, AtlasPathsCheck?.IsChecked == true);
        if (!IsLoaded) return;
        if (!atlasPathsEnabled)
        {
            hackerPathLinks.CancelSelection();
            if (EditsHackerPaths)
                HackerTerrainTarget.SelectedItem = HackerTerrainTarget.Items.OfType<ComboBoxItem>()
                    .First(item => Equals(item.Content, previousHackerTerrainTarget));
        }
        RenderGameplayPaths();
        if (workspace == "Hacker")
        {
            RedrawHackerEdits();
            ApplyViewport(AtlasCanvas, HackerViewportPanel, hackerFraming, HackerLiveImage);
        }
    }

    private void HackerPathTool_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<AtlasPathEditTool>(tag, out var tool))
            return;
        hackerPathLinks.CancelSelection();
        hackerPathTool = tool;
        RedrawHackerEdits();
    }

    private bool EditHackerPath(MouseButtonEventArgs e)
    {
        if (!EditsHackerPaths || !ShowsHackerPaths) return false;
        var pointer = GroundPointerAtlas(e);
        var scaleTolerance = GroundPointerTolerance();
        if (scaleTolerance is not (> 0 and <= 10_000)) return false;
        var tolerance = Math.Max(1, (int)Math.Ceiling(scaleTolerance));
        var best = tolerance + 1;
        TerrainRoom? hit = null;
        AtlasPathPoint point = default;
        foreach (var room in HackerTerrainRooms().OrderByDescending(room => room.SceneName == hackerLivePrepared?.SceneName))
        {
            var offset = PathOffset(room);
            if (!AtlasPathPoint.TryQuantize(pointer.X - offset.X, pointer.Y - offset.Y, out var roomPoint)) continue;
            var distance = hackerPathLinks.HitDistance(HackerPaths(room).Graph, roomPoint, PathCalibration(room), hackerPathTool);
            if (distance is not { } candidate || candidate >= best) continue;
            best = candidate;
            hit = room;
            point = roomPoint;
        }
        if (hit is null) return false;
        hackerPathLinks.Click(HackerPaths(hit).Graph, point, tolerance, PathCalibration(hit), hackerPathTool);
        if (hackerPathLinks.Issue is { } issue) SetStatus(issue);
        RedrawHackerEdits();
        e.Handled = true;
        return true;
    }

    private bool SelectHackerObjective(MouseButtonEventArgs e)
    {
        if (hackerEditingTarget != "Objective" || !ShowsHackerPaths) return false;
        var pointer = GroundPointerAtlas(e);
        // The paths include all authored terrain and may extend past the visited image.
        var placements = HackerTerrainRooms().OrderByDescending(room => room.SceneName == hackerLivePrepared?.SceneName).Select(room =>
        {
            var paths = HackerPaths(room);
            return new HackerObjectivePlacement(room.SceneName, PathOffset(room), paths.Scale, paths.Graph);
        }).ToArray();
        var selected = hackerObjectives.Select(pointer, GroundPointerTolerance(), placements);
        if (selected is null) return false;
        Volatile.Write(ref hackerObjectives, selected);
        RedrawHackerEdits();
        e.Handled = true;
        return true;
    }

    private void RenderHackerPaths(double originX, double originY, IReadOnlyList<TerrainRoom> rooms)
    {
        HackerPathCanvas.Children.Clear();
        hackerPathFitBounds = null;
        HackerPathCanvas.Width = AtlasCanvas.Width;
        HackerPathCanvas.Height = AtlasCanvas.Height;
        HackerPathCanvas.Visibility = ShowsHackerPaths ? Visibility.Visible : Visibility.Collapsed;
        if (HackerPathCanvas.Visibility != Visibility.Visible)
        {
            hackerPathRoute.Interrupt();
            return;
        }

        foreach (var room in rooms)
        {
            var paths = HackerPaths(room);
            var graph = paths.Graph;
            var offset = PathOffset(room);
            var currentRoom = room.SceneName == hackerLivePrepared?.SceneName;
            var target = currentRoom || hackerObjectives.SelectedFor(room.SceneName) is not null
                ? hackerObjectives.Target(new(room.SceneName, offset, paths.Scale, graph)) : null;

            IReadOnlyList<AtlasPathPoint> route = [];
            if (currentRoom)
            {
                if (target is { } endpoint && hackerLivePrepared is { Sample.HeroAvailable: true } live
                    && AtlasPathPoint.TryQuantize(live.Sample.HeroX * paths.Scale.X,
                        (live.Sample.HeroY + KnightWorldHeight / 2) * paths.Scale.Y, out var point))
                    route = hackerPathRoute.Observe(graph, point with { Y = point.Y - graph.KnightHeight },
                        live.Sample.Grounded, endpoint, live.CapturedAt);
                else
                    hackerPathRoute.Interrupt();
            }
            if (AtlasPathPresentation.Draw(HackerPathCanvas, paths.Display, graph.KnightWidth,
                    new(offset.X - originX, -offset.Y - originY), target, route) is { } span)
                hackerPathFitBounds = hackerPathFitBounds is { } fit ? fit.Union(span) : span;
            if (EditsHackerPaths && hackerPathLinks.Selection is { } selectedEndpoint && selectedEndpoint.Room == room.Key)
            {
                var mark = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, IsHitTestVisible = false };
                Canvas.SetLeft(mark, selectedEndpoint.Point.X + offset.X - originX - 4);
                Canvas.SetTop(mark, -selectedEndpoint.Point.Y - graph.KnightHeight - offset.Y - originY - 4);
                HackerPathCanvas.Children.Add(mark);
            }
        }
        HackerPathActions.ToolTip = hackerPathLinks.Issue;
    }

    private void SetHackerPathPreview(HackerPipelinePublication publication)
    {
        if (hackerPathPreview is not { } previous
            || previous.CaptureGeneration != publication.CaptureGeneration
            || previous.WorkGeneration != publication.WorkGeneration
            || previous.Prepared.Sample.SessionId != publication.Prepared.Sample.SessionId
            || previous.Prepared.SceneName != publication.Prepared.SceneName)
            hackerPathRoute.Interrupt();
        hackerPathPreview = publication;
    }

    private static AtlasPathCalibration PathCalibration(TerrainRoom room) =>
        new(room.Key, room.SceneName, new(0, 0), room.Projection.PixelsPerWorldUnit);

    private static AtlasPoint PathOffset(TerrainRoom room) =>
        new(room.Position.X + room.Projection.LocalOrigin.X, room.Position.Y + room.Projection.LocalOrigin.Y);

}
