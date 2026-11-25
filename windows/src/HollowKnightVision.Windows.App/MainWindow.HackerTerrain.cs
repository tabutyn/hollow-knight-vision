using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private sealed record TerrainRoom(string Key, string SceneName, AtlasPoint Position,
        HackerAtlasRect Bounds, HackerTerrainProjection Projection);

    private string hackerEditingTarget = "Objective";
    private GroundTruthTerrainTool? hackerTerrainTool;
    private bool EditsHackerTerrain => hackerTerrainTool.HasValue;
    private TerrainRoom? groundDragRoom;

    private static readonly Brush GroundLineBrush = FrozenBrush(Color.FromRgb(69, 255, 120));
    private static readonly Brush NegativeGroundLineBrush = FrozenBrush(Color.FromRgb(255, 46, 46));
    private static readonly Brush DoorLineBrush = FrozenBrush(Color.FromRgb(102, 222, 247));

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void HackerTerrainTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (groundTruthLabels is null || (sender as ComboBox)?.SelectedItem is not ComboBoxItem item
            || item.Content is not string target || !DashboardPresentationPolicy.HackerEditingTargets.Contains(target)) return;
        CancelGroundTruthGesture();
        if (target == "Path" && !EditsHackerPaths) previousHackerTerrainTarget = hackerEditingTarget;
        hackerEditingTarget = target;
        hackerPathLinks.CancelSelection();
        hackerPathTool = AtlasPathEditTool.Jump;
        HackerPathJumpButton.IsChecked = true;
        HackerPathActions.Visibility = EditsHackerPaths ? Visibility.Visible : Visibility.Collapsed;
        if (EditsHackerPaths) AtlasPathsCheck.IsChecked = true;
        hackerTerrainTool = Enum.TryParse<GroundTruthTerrainTool>(target, out var tool) ? tool : null;
        HackerTerrainActions.Visibility = EditsHackerTerrain ? Visibility.Visible : Visibility.Collapsed;
        ReleaseForwardedInput();
        if (hackerTerrainTool is { } activeTool)
        {
            groundTruthLabels.Tool = activeTool;
            SelectGroundTruthMode();
            _ = PresentLatestGroundTruthContext();
        }
        else groundTruthLabels.ClearSelection();
        RedrawHackerEdits();
    }

    private IReadOnlyList<TerrainRoom> HackerTerrainRooms()
    {
        var topology = BuildHackerRoomLayout();
        var current = hackerLivePrepared is { RoomPlacementReady: true } prepared ? prepared : null;
        var mapped = new List<TerrainRoom>();
        foreach (var room in topology.Rooms.Values)
        {
            var projection = room.TerrainProjection;
            var bounds = room.Bounds;
            if (current is not null && room.SceneName == current.SceneName)
            {
                if (projection is not { IsValid: true })
                    projection = HackerTerrainProjection.FromFrame(current, room.Position);
                var live = current.LiveBounds.Offset(-room.Position.X, -room.Position.Y);
                bounds = bounds.IsEmpty ? live : bounds.Union(live);
            }
            if (projection is not { IsValid: true } || bounds.IsEmpty) continue;
            mapped.Add(new(room.Id.ToString("D").ToUpperInvariant(), room.SceneName,
                HackerRoomPosition(room, topology.Edits, topology.Layout),
                bounds, projection));
        }

        if (current is not null && !mapped.Any(room => room.SceneName == current.SceneName))
        {
            var projection = HackerTerrainProjection.FromFrame(current, new(0, 0));
            if (projection.IsValid)
                mapped.Add(new(HackerLayoutId("room", current.SceneName).ToString("D").ToUpperInvariant(),
                    current.SceneName, new(0, 0), current.LiveBounds, projection));
        }
        return mapped;
    }

    private TerrainRoom? TerrainRoomAt(AtlasPoint atlasPoint, double tolerance)
    {
        var roomsInHitOrder = HackerTerrainRooms().Reverse()
            .OrderByDescending(room => room.SceneName == hackerLivePrepared?.SceneName);
        foreach (var room in roomsInHitOrder)
        {
            var bounds = room.Bounds.Offset(room.Position.X, room.Position.Y);
            var minX = bounds.MinX;
            var minY = bounds.MinY;
            var maxX = bounds.MaxX;
            var maxY = bounds.MaxY;
            // Authored terrain can extend past the captured image.
            foreach (var line in groundTruthLabels.Document.AuthoredLines.Where(line => line.SceneName == room.SceneName))
            {
                Include(room.Projection.WorldToAtlas(line.Start, room.Position), 0);
                Include(room.Projection.WorldToAtlas(line.End, room.Position), 0);
            }
            if (hackerTerrainTool == GroundTruthTerrainTool.Door)
            {
                // Let a doorway start beside a route beyond the visited tiles.
                const double routeHitPadding = 5;
                var offset = PathOffset(room);
                foreach (var stroke in HackerPaths(room).Display)
                {
                    Include(new(offset.X + stroke.A.X, offset.Y + stroke.A.Y), routeHitPadding);
                    Include(new(offset.X + stroke.B.X, offset.Y + stroke.B.Y), routeHitPadding);
                }
            }
            if (atlasPoint.X >= minX - tolerance && atlasPoint.X <= maxX + tolerance
                && atlasPoint.Y >= minY - tolerance && atlasPoint.Y <= maxY + tolerance) return room;

            void Include(AtlasPoint point, double padding)
            {
                minX = Math.Min(minX, point.X - padding);
                minY = Math.Min(minY, point.Y - padding);
                maxX = Math.Max(maxX, point.X + padding);
                maxY = Math.Max(maxY, point.Y + padding);
            }
        }
        return null;
    }

    private AtlasPoint GroundPointerAtlas(MouseEventArgs e)
    {
        var global = ToAtlasGlobal(e.GetPosition(AtlasCanvas));
        return new(global.X, -global.Y);
    }

    private double GroundPointerTolerance()
    {
        var zoom = Math.Abs((AtlasCanvas.RenderTransform as MatrixTransform)?.Matrix.M11 ?? 1);
        return 8 / (double.IsFinite(zoom) && zoom > 0 ? zoom : 1);
    }

    private bool GroundEditorMouseDown(MouseButtonEventArgs e)
    {
        var atlas = GroundPointerAtlas(e);
        var tolerance = GroundPointerTolerance();
        if (TerrainRoomAt(atlas, tolerance) is not { } room) return false;
        SelectGroundTruthMode();
        groundTruthLabels.HandleWorld(GroundTruthPointerPhase.Began, room.Projection.AtlasToWorld(atlas, room.Position),
            tolerance, room.SceneName, room.Projection.PixelsPerWorldUnit);
        groundDragRoom = room;
        Mouse.Capture(AtlasCanvas, CaptureMode.Element);
        PresentGroundTruthPersistenceError();
        RedrawHackerEdits();
        e.Handled = true;
        return true;
    }

    private void GroundEditorMouseMove(MouseEventArgs e)
    {
        if (groundDragRoom is not { } room || e.LeftButton != MouseButtonState.Pressed) return;
        groundTruthLabels.HandleWorld(GroundTruthPointerPhase.Changed,
            room.Projection.AtlasToWorld(GroundPointerAtlas(e), room.Position),
            GroundPointerTolerance(), room.SceneName, room.Projection.PixelsPerWorldUnit);
        RedrawHackerEdits();
        e.Handled = true;
    }

    private void GroundEditorMouseUp(MouseButtonEventArgs e)
    {
        if (groundDragRoom is not { } room) return;
        var before = groundTruthLabels.Document;
        groundTruthLabels.HandleWorld(GroundTruthPointerPhase.Ended,
            room.Projection.AtlasToWorld(GroundPointerAtlas(e), room.Position),
            GroundPointerTolerance(), room.SceneName, room.Projection.PixelsPerWorldUnit);
        groundDragRoom = null;
        Mouse.Capture(null);
        if (!ReferenceEquals(before, groundTruthLabels.Document))
        {
            try { PersistHackerEdits(); }
            catch (Exception error) { SetStatus($"Terrain saved, but room placement could not be saved: {error.Message}"); }
        }
        PresentGroundTruthPersistenceError();
        RedrawHackerEdits();
        e.Handled = true;
    }

    private void CancelGroundTruthGesture()
    {
        DiscardGroundTruthGesture();
        if (GroundUndoButton is not null) GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
        if (HackerOverlayCanvas is not null) RedrawHackerEdits();
    }

    private void DiscardGroundTruthGesture()
    {
        groundTruthLabels.CancelGesture();
        groundDragRoom = null;
        if (ReferenceEquals(Mouse.Captured, AtlasCanvas)) Mouse.Capture(null);
    }

    private void UpdateGroundDragPlacement(IReadOnlyList<TerrainRoom> rooms)
    {
        if (groundDragRoom is not { } active) return;
        // The controller stores the anchor in the room's world coordinates.
        // Subsequent pointer positions must use the room's current placement.
        var current = rooms.FirstOrDefault(room => room.Key == active.Key && room.SceneName == active.SceneName);
        if (current is null) DiscardGroundTruthGesture();
        else groundDragRoom = current;
    }

    private void RenderHackerTerrain(double originX, double originY, IReadOnlyList<TerrainRoom> rooms)
    {
        if (hackerTerrainTool == GroundTruthTerrainTool.Ground)
        {
            foreach (var legacy in legacyGroundEdits.Where(RenderableLegacyGroundLine))
                DrawTerrainLine(new(legacy.X0 - originX, legacy.Y0 - originY), new(legacy.X1 - originX, legacy.Y1 - originY),
                    legacy.IsNegative ? NegativeGroundLineBrush : GroundLineBrush, drawHandles: false);
        }

        var roomsByScene = rooms.ToDictionary(room => room.SceneName, StringComparer.Ordinal);
        foreach (var line in groundTruthLabels.VisibleLines)
        {
            if (!roomsByScene.TryGetValue(line.SceneName, out var room)) continue;
            var first = room.Projection.WorldToAtlas(line.Start, room.Position);
            var last = room.Projection.WorldToAtlas(line.End, room.Position);
            var kind = GroundTruthLineProjection.OverlayKind(line, groundTruthLabels.SelectedId);
            var brush = kind switch
            {
                GroundTruthOverlayLineKind.NegativeGroundTruth => NegativeGroundLineBrush,
                GroundTruthOverlayLineKind.SpikeGroundTruth => Brushes.OrangeRed,
                GroundTruthOverlayLineKind.DoorGroundTruth => DoorLineBrush,
                GroundTruthOverlayLineKind.SelectedGroundTruth => Brushes.White,
                _ => GroundLineBrush
            };
            var tool = line.Kind switch
            {
                GroundTruthLineKind.Wall => GroundTruthTerrainTool.Wall,
                GroundTruthLineKind.Spike => GroundTruthTerrainTool.Spike,
                GroundTruthLineKind.Door or GroundTruthLineKind.DoorVertical => GroundTruthTerrainTool.Door,
                _ => GroundTruthTerrainTool.Ground
            };
            var handles = EditsHackerTerrain && (line.Id == groundTruthLabels.SelectedId
                || groundTruthLabels.Mode == GroundTruthEditingMode.Modify
                    && (tool == hackerTerrainTool || hackerTerrainTool == GroundTruthTerrainTool.Wall
                        && line.Kind == GroundTruthLineKind.Positive));
            DrawTerrainLine(new(first.X - originX, -first.Y - originY), new(last.X - originX, -last.Y - originY),
                brush, handles);
        }

        if (!EditsHackerTerrain) return;
        var sceneName = groundDragRoom?.SceneName ?? hackerLivePrepared?.SceneName;
        GroundCountText.Text = (sceneName is null ? 0 : groundTruthLabels.LineCount(sceneName))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        GroundUndoButton.IsEnabled = groundTruthLabels.CanUndo;
        HackerGroundToolbar.ToolTip = groundTruthLabels.Error;
    }

    private void DrawTerrainLine(AtlasPoint first, AtlasPoint last, Brush brush, bool drawHandles)
    {
        HackerOverlayCanvas.Children.Add(new Line
        {
            X1 = first.X, Y1 = first.Y, X2 = last.X, Y2 = last.Y,
            Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false
        });
        if (!drawHandles) return;
        foreach (var endpoint in new[] { first, last })
        {
            var handle = new Rectangle { Width = 6, Height = 6, Fill = brush, IsHitTestVisible = false };
            Canvas.SetLeft(handle, endpoint.X - 3);
            Canvas.SetTop(handle, endpoint.Y - 3);
            HackerOverlayCanvas.Children.Add(handle);
        }
    }
}
