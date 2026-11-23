using System.Windows;
using System.Windows.Input;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record HackerPathCache(GroundTruthLineDocument Document, AtlasPoint Scale,
        AtlasPlatformGraph Graph, IReadOnlyList<AtlasPathStroke> Display);
    private readonly Dictionary<string, HackerPathCache> hackerPathGraphs = new(StringComparer.Ordinal);
    private (string Scene, AtlasPathPoint Point)? hackerObjective;
    private ViewportRect? hackerPathFitBounds;
    private const double KnightWorldHeight = 1.28;
    private const double KnightWorldWidth = .5;

    private bool ShowsHackerPaths => AtlasPathsCheck?.IsChecked == true && workspace == "Hacker"
        && DashboardPresentationPolicy.ShowsHackerFraming(workspace, recognizedMenuContext)
        && visionCancellation is not null && !hackerPreviewActive
        && hackerLivePrepared is { RoomPlacementReady: true };

    private HackerPathCache HackerPaths(TerrainRoom room)
    {
        var scale = room.Projection.PixelsPerWorldUnit;
        var document = groundTruthLabels.Document;
        if (hackerPathGraphs.TryGetValue(room.SceneName, out var cached)
            && ReferenceEquals(cached.Document, document) && cached.Scale == scale) return cached;
        var floors = new List<AtlasPathPlatform>();
        var walls = new List<AtlasPathWall>();
        var spikes = new List<AtlasPathPlatform>();
        var doors = new List<AtlasPathDoor>();
        foreach (var line in document.AuthoredLines.Where(line => line.SceneName == room.SceneName))
        {
            if (!AtlasPathPoint.TryQuantize(line.Start.X * scale.X, line.Start.Y * scale.Y, out var a)
                || !AtlasPathPoint.TryQuantize(line.End.X * scale.X, line.End.Y * scale.Y, out var b)) continue;
            switch (line.Kind)
            {
                case GroundTruthLineKind.Positive:
                    floors.Add(new(Math.Min(a.X, b.X), Math.Max(a.X, b.X), a.Y));
                    break;
                case GroundTruthLineKind.Wall:
                    walls.Add(new(a.X, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
                    break;
                case GroundTruthLineKind.Spike:
                    spikes.Add(new(Math.Min(a.X, b.X), Math.Max(a.X, b.X), a.Y));
                    break;
                case GroundTruthLineKind.Door:
                case GroundTruthLineKind.DoorVertical:
                    doors.Add(new(a, b));
                    break;
            }
        }

        var graph = new AtlasPlatformGraph(floors, walls, QuantizePath(KnightWorldHeight * scale.Y),
            QuantizePath(KnightWorldWidth * scale.X), pixelsPerWorldUnit: scale, previous: cached?.Graph, spikes: spikes, doors: doors);
        var result = new HackerPathCache(document, scale, graph, AtlasPathRouting.DisplayStrokes(graph.Strokes));
        hackerPathGraphs[room.SceneName] = result;
        return result;
    }

    private void AtlasPaths_Changed(object sender, RoutedEventArgs e)
    {
        Volatile.Write(ref atlasPathsEnabled, AtlasPathsCheck?.IsChecked == true);
        if (!IsLoaded) return;
        RenderGameplayPaths();
        if (workspace == "Hacker")
        {
            RedrawHackerEdits();
            ApplyViewport(AtlasCanvas, HackerViewportPanel, hackerFraming, HackerLiveImage);
        }
    }

    private bool SelectHackerObjective(MouseButtonEventArgs e)
    {
        if (hackerEditingTarget != "Objective" || !ShowsHackerPaths) return false;
        var pointer = GroundPointerAtlas(e);
        var tolerance = GroundPointerTolerance();
        if (!double.IsFinite(tolerance) || tolerance > 10_000) return false;
        var nearest = Math.Max(2, QuantizePath(Math.Ceiling(tolerance))) + 1;
        (string Scene, AtlasPathPoint Point)? selected = null;
        // The paths include all authored terrain and may extend past the visited image.
        foreach (var room in HackerTerrainRooms().OrderByDescending(room => room.SceneName == hackerLivePrepared?.SceneName))
        {
            var offset = PathOffset(room);
            if (!AtlasPathPoint.TryQuantize(pointer.X - offset.X, pointer.Y - offset.Y, out var point)) continue;
            var graph = HackerPaths(room).Graph;
            if (graph.NearestStroke(point, nearest - 1) is not { } stroke) continue;
            var snapped = stroke.Closest(point);
            nearest = snapped.DistanceTo(point);
            selected = (room.SceneName, snapped);
        }
        if (selected is null) return false;
        hackerObjective = (hackerObjective == selected) ? null : selected;
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
        if (HackerPathCanvas.Visibility != Visibility.Visible) return;

        foreach (var room in rooms)
        {
            var paths = HackerPaths(room);
            var graph = paths.Graph;
            var offset = PathOffset(room);
            var currentRoom = room.SceneName == hackerLivePrepared?.SceneName;
            var target = hackerObjective is { } selection
                ? selection.Scene == room.SceneName ? selection.Point : (AtlasPathPoint?)null
                : currentRoom ? graph.UpperRightObjective : null;

            IReadOnlyList<AtlasPathPoint> route = [];
            if (target is { } endpoint && graph.NearestStroke(endpoint, 0) is { } goal)
            {
                if (currentRoom && hackerLivePrepared is { Sample.HeroAvailable: true } live
                    && AtlasPathPoint.TryQuantize(live.Sample.HeroX * paths.Scale.X,
                        (live.Sample.HeroY + KnightWorldHeight / 2) * paths.Scale.Y, out var point)
                    && graph.NearestStroke(point, 2 * graph.KnightHeight, walkOnly: live.Sample.Grounded) is { } start)
                    route = AtlasPathRouting.FindRoute(graph, start, start.Closest(point), goal, endpoint);
            }
            else
            {
                target = null;
            }
            if (AtlasPathPresentation.Draw(HackerPathCanvas, paths.Display, graph.KnightWidth,
                    new(offset.X - originX, -offset.Y - originY), target, route) is { } span)
                hackerPathFitBounds = hackerPathFitBounds is { } fit ? fit.Union(span) : span;
        }
    }

    private static AtlasPoint PathOffset(TerrainRoom room) =>
        new(room.Position.X + room.Projection.LocalOrigin.X, room.Position.Y + room.Projection.LocalOrigin.Y);

    private static int QuantizePath(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

}
