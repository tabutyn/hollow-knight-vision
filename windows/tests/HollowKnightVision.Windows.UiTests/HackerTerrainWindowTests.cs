using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;

internal static class HackerTerrainWindowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Room = "Tutorial_01";
    private const string LabelRoot = "HKV_GROUND_LABEL_ROOT";
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;

    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("hkv-hacker-doors-");
        var oldRoot = Environment.GetEnvironmentVariable(LabelRoot);
        var hasPointer = GetCursorPos(out var pointer);
        try
        {
            Environment.SetEnvironmentVariable(LabelRoot, directory.FullName);
            VerifyGestures(directory.FullName);
            var objectiveRoot = Path.Combine(directory.FullName, "objective");
            Environment.SetEnvironmentVariable(LabelRoot, objectiveRoot);
            TestClickBeyondVisitedAtlasHighlightsFullRouteImmediately(objectiveRoot);
        }
        finally
        {
            if (hasPointer) _ = SetCursorPos(pointer.X, pointer.Y);
            Environment.SetEnvironmentVariable(LabelRoot, oldRoot);
            directory.Delete(recursive: true);
        }
        Console.WriteLine("PASS native Hacker door editing reaches paths beyond visited tiles and keeps drafts attached to their room");
    }

    private static void TestClickBeyondVisitedAtlasHighlightsFullRouteImmediately(string root)
    {
        const int size = 100;
        const long generation = 8;
        const double capturedAt = 1;
        new GroundTruthLineStore(Path.Combine(root, GroundTruthLineStore.Filename)).Save(new()
        {
            Lines = [new(Room, 0, 30, 0, GroundTruthLineKind.Positive, false),
                new(Room, 32, 42, 2, GroundTruthLineKind.Positive, false)],
            Walls = [new GroundTruthWorldLine(Room, 9, 9, -3, GroundTruthLineKind.Wall, false) { EndWorldY = -1 }],
            Doors = [new(Room, 12, 14, -2, GroundTruthLineKind.Door, false)],
            Spikes = [new(Room, 16, 17, -2, GroundTruthLineKind.Spike, false)]
        });
        using var capture = new CancellationTokenSource();
        var window = new MainWindow(GameRuntimeMode.Modded, gamePath: null, root: root, documents: root) { Topmost = true };
        try
        {
            Field(window, "isClosing").SetValue(window, true);
            window.Show();
            WaitFor(() => window.IsLoaded, "The objective fixture did not load its window.");
            Field(window, "workspace").SetValue(window, "Hacker");
            Field(window, "visionCancellation").SetValue(window, capture);
            Field(window, "recentCaptureGeneration").SetValue(window, generation);
            Control<FrameworkElement>(window, "GameplayPanel").Visibility = Visibility.Collapsed;
            Control<FrameworkElement>(window, "HackerPanel").Visibility = Visibility.Visible;
            Control<FrameworkElement>(window, "GameplayControlsPanel").Visibility = Visibility.Collapsed;
            Field(window, "hackerFramingValue").SetValue(window, "Free Fly");
            var viewport = (GameplayViewportController)Field(window, "hackerViewport").GetValue(window)!;
            var atlas = Control<Canvas>(window, "AtlasCanvas");
            var overlay = Control<Canvas>(window, "HackerOverlayCanvas");
            var routeCanvas = Control<Canvas>(window, "HackerPathCanvas");
            var live = Control<FrameworkElement>(window, "HackerLiveImage");
            var showPaths = Control<CheckBox>(window, "AtlasPathsCheck");
            var target = Control<ComboBox>(window, "HackerTerrainTarget");
            Check(target.SelectedItem is ComboBoxItem { Content: "Objective" }, "Hacker should open with Objective selected.");
            var pathActions = Control<Panel>(window, "HackerPathActions");
            var terrainActions = Control<FrameworkElement>(window, "HackerTerrainActions");
            Check(pathActions.Visibility == Visibility.Collapsed && terrainActions.Visibility == Visibility.Collapsed,
                "Objective must keep the path and terrain editing actions closed.");

            var sample = new ReceiverGroundTruthSample(
                Version: 2, Type: "groundTruth", SessionId: Guid.NewGuid(), Sequence: 1,
                UnityFrame: 1, UnityRealtime: capturedAt, SceneName: Room,
                HeroAvailable: true, HeroX: 5, HeroY: 0.64, HeroZ: 0,
                VelocityX: 0, VelocityY: 0, FacingRight: true, Grounded: true,
                CameraAvailable: true, CameraX: 5, CameraY: 5, CameraZ: 0,
                CameraTargetX: 5, CameraTargetY: 5, CameraTargetZ: 0, OrthographicSize: 5,
                PixelsPerWorldUnitX: 10, PixelsPerWorldUnitY: 10, HeroScreenX: null, HeroScreenY: null,
                ProjectionPixelWidth: size, ProjectionPixelHeight: size, ScreenWidth: size, ScreenHeight: size)
            { AtlasGameplayActive = true };
            var image = new BgraFrame(size, size, size * 4, new byte[size * size * 4], 1, DateTimeOffset.UnixEpoch);
            var bitmap = typeof(MainWindow).GetMethod("FrameBitmap", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [image]);
            var publication = typeof(MainWindow).GetNestedType("HackerPipelinePublication", BindingFlags.NonPublic)!;
            var work = (long)Field(window, "hackerWorkGeneration").GetValue(window)!;
            Present(gameplay: true);
            Invoke(window, "ApplySettledViewportTransform", atlas, viewport.SetFreeFlyTransform(new(1, 65, 20)));
            _ = window.Activate();
            window.UpdateLayout();
            Check(routeCanvas.Visibility == Visibility.Visible, "The frame-matched Hacker path must be visible.");
            AssertAuthoredTerrain();

            var originX = ((int?)Field(window, "hackerAtlasMinimumTileX").GetValue(window) ?? 0) * LiveTiledAtlas.TileSize;
            var originY = ((int?)Field(window, "hackerAtlasMinimumTileY").GetValue(window) ?? 0) * LiveTiledAtlas.TileSize;
            var point = new Point(395 - originX, -33 - originY);
            Check(point.X > Canvas.GetLeft(live) + live.Width, "The objective click should be beyond the live image.");
            ClickAt(point);
            WaitFor(() => Objectives().SelectedFor(Room) is not null, "The viewport did not apply the objective click.");
            Check(Objectives().SelectedFor(Room) is { } chosen && Math.Abs(chosen.WorldPosition.X - 39.5) < .15,
                "Clicking a route beyond the visited image should select its actual position.");
            AssertWhiteRoute();

            showPaths.IsChecked = false;
            Check(routeCanvas.Visibility == Visibility.Collapsed, "Turning Atlas paths off must hide the white route.");
            AssertAuthoredTerrain();
            showPaths.IsChecked = true;
            AssertWhiteRoute();
            Present(gameplay: false);
            Check(routeCanvas.Visibility == Visibility.Collapsed, "A frame outside Gameplay must hide Hacker paths.");
            Present(gameplay: true);
            AssertWhiteRoute();
            ClickAt(point);
            WaitFor(() => Objectives().IsAutomatic(Room), "The viewport did not clear the objective on the second click.");
            Check(!routeCanvas.Children.OfType<Line>().Any(line => line.Stroke == Brushes.White)
                && routeCanvas.Children.OfType<Line>().Any(line => line.Stroke == Brushes.Gold),
                "Clicking the target again should clear the white route and leave the authored path visible.");
            showPaths.IsChecked = false;
            target.SelectedItem = target.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Content, "Path"));
            Check(showPaths.IsChecked == true && routeCanvas.Visibility == Visibility.Visible
                && pathActions.IsVisible && !terrainActions.IsVisible,
                "Selecting Path must turn the overlay on and expose its editor.");
            AssertAuthoredTerrain();
            var firstEndpoint = new Point(250 - originX, -13 - originY);
            var secondEndpoint = new Point(360 - originX, -33 - originY);
            window.UpdateLayout();
            var viewportHost = Control<FrameworkElement>(window, "HackerViewportPanel");
            var panX = (viewportHost.ActualWidth - firstEndpoint.X - secondEndpoint.X) / 2;
            var panY = (viewportHost.ActualHeight - firstEndpoint.Y - secondEndpoint.Y) / 2;
            Invoke(window, "ApplySettledViewportTransform", atlas, viewport.SetFreeFlyTransform(new(1, panX, panY)));
            var links = (AtlasPathLinkStore)Field(window, "hackerPathLinks").GetValue(window)!;
            var connectionFile = Path.Combine(root, AtlasPathLinkStore.Filename);
            var reopenedRoom = new AtlasPathCalibration("reopened-room", Room, default, new(10, 10));

            SelectPathTool(AtlasPathEditTool.DoubleJump);
            ClickAt(new Point(310 - originX, -13 - originY));
            Check(!links.Selection.HasValue && !File.Exists(connectionFile),
                "A click away from a floor must not start an authored connection.");
            ClickAt(firstEndpoint);
            WaitFor(() => links.Selection.HasValue, "The first endpoint click did not select a floor.");
            var selectionMark = routeCanvas.Children.OfType<Rectangle>().Single();
            Check(links.Selection is { } selected && selected.Point.Y == 0
                && Math.Abs(Canvas.GetLeft(selectionMark) + selectionMark.Width / 2 - firstEndpoint.X) <= 2
                && Canvas.GetTop(selectionMark) + selectionMark.Height / 2 == firstEndpoint.Y,
                "The selection square must sit on the clicked floor's route.");
            ClickAt(secondEndpoint);
            WaitFor(() => links.Revision == 1, "The second endpoint click did not save a connection.");
            var connection = ReopenedConnection();
            Check(!links.Selection.HasValue && connection.DoubleJump
                && Math.Abs(connection.First.X - 250) <= 2 && connection.First.Y == 0
                && Math.Abs(connection.Second.X - 360) <= 2 && connection.Second.Y == 20,
                "The double jump must save on the two clicked platforms and survive a room-key change.");
            var shelf = routeCanvas.Children.OfType<Line>()
                .Single(line => line.Stroke == Brushes.MediumVioletRed && line.Y1 == line.Y2);
            var shelfCenter = new Point((shelf.X1 + shelf.X2) / 2, shelf.Y1);

            SelectPathTool(AtlasPathEditTool.Jump);
            ClickAt(firstEndpoint);
            ClickAt(secondEndpoint);
            WaitFor(() => links.Revision == 2, "The same endpoint pair did not replace its saved connection.");
            Check(!ReopenedConnection().DoubleJump
                && routeCanvas.Children.OfType<Line>().Any(line => line.Stroke == Brushes.Gold && IsSavedShelf(line))
                && routeCanvas.Children.OfType<Line>().All(line => line.Stroke != Brushes.MediumVioletRed),
                "Replacing the connection must update both the saved jump type and the rendered shelf.");

            SelectPathTool(AtlasPathEditTool.Delete);
            ClickAt(shelfCenter);
            WaitFor(() => links.Revision == 3, "Clicking the saved shelf did not delete its connection.");
            Check(new AtlasPathLinkStore(connectionFile).Connections(reopenedRoom).Count == 0
                && routeCanvas.Children.OfType<Line>().Any(line => line.Stroke == Brushes.Gold)
                && routeCanvas.Children.OfType<Line>().All(line => !IsSavedShelf(line)),
                "Deleting the authored connection must preserve the automatically calculated route.");
            AssertAuthoredTerrain();
            SelectPathTool(AtlasPathEditTool.Jump);
            ClickAt(firstEndpoint);
            WaitFor(() => links.Selection.HasValue, "The editor did not start a pending connection.");
            showPaths.IsChecked = false;
            Check(routeCanvas.Visibility == Visibility.Collapsed
                && target.SelectedItem is ComboBoxItem { Content: "Objective" } && !links.Selection.HasValue,
                "Turning the overlay off again must hide the paths and leave Path editing.");
            AssertAuthoredTerrain();
            Console.WriteLine("PASS Windows Hacker objective clicks beyond captured tiles immediately show the whole reachable route");
            Console.WriteLine("PASS Windows Hacker Objective and Path modes keep authored walls, doors, spikes, and ground visible");
            Console.WriteLine("PASS Windows Hacker path gestures save, replace, and delete a connection through the viewport");

            HackerAtlasObjectives Objectives() => (HackerAtlasObjectives)Field(window, "hackerObjectives").GetValue(window)!;

            void AssertAuthoredTerrain()
            {
                var authored = overlay.Children.OfType<Line>().ToArray();
                var green = Color.FromRgb(69, 255, 120);
                var door = Color.FromRgb(102, 222, 247);
                Check(overlay.IsVisible && authored.Length == 5 && !overlay.Children.OfType<Rectangle>().Any()
                    && authored.Count(line => line.Stroke is SolidColorBrush brush && brush.Color == green && line.Y1 == line.Y2) == 2
                    && authored.Count(line => line.Stroke is SolidColorBrush brush && brush.Color == green && line.X1 == line.X2
                        && Math.Abs(Math.Abs(line.Y2 - line.Y1) - 20) < .01) == 1
                    && authored.Count(line => line.Stroke is SolidColorBrush brush && brush.Color == door && line.Y1 == line.Y2) == 1
                    && authored.Count(line => line.Stroke == Brushes.OrangeRed && line.Y1 == line.Y2) == 1,
                    "The authored overlay must show the two floors, vertical green wall, horizontal blue door, and spike without edit handles.");
            }

            void Present(bool gameplay)
            {
                var now = sample with { AtlasGameplayActive = gameplay };
                var camera = GroundTruthCameraTransform.FromSample(now, capturedAt, new(size, size))!;
                var prepared = new HackerPreparedFrame(image, new(0, 0, size, size), default, camera,
                    now, default, Room, 1, capturedAt, true, true);
                Invoke(window, "ApplyHackerPipelinePublication", Activator.CreateInstance(publication,
                    [prepared, bitmap, null, generation, work, gameplay, 0])!);
                window.UpdateLayout();
            }

            AtlasPathConnection ReopenedConnection() => new AtlasPathLinkStore(connectionFile).Connections(reopenedRoom).Single();

            bool IsSavedShelf(Line line) => line.Y1 == shelf.Y1 && line.Y2 == shelf.Y2
                && Math.Abs(line.X1 - shelf.X1) <= 2 && Math.Abs(line.X2 - shelf.X2) <= 2;

            void SelectPathTool(AtlasPathEditTool tool)
            {
                var button = pathActions.Children.OfType<RadioButton>().Single(item => Equals(item.Tag, tool.ToString()));
                button.IsChecked = true;
                button.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            }

            void ClickAt(Point position)
            {
                Pump();
                window.UpdateLayout();
                var screen = atlas.PointToScreen(position);
                Check(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)),
                    "Could not position the pointer on the Atlas.");
                WaitFor(() => (Mouse.GetPosition(atlas) - position).Length < 1.5,
                    $"The pointer did not reach the Atlas target {position} at screen position {screen}.");
                MouseEvent(LeftDown, 0, 0, 0, 0);
                MouseEvent(LeftUp, 0, 0, 0, 0);
                Pump();
            }

            void AssertWhiteRoute()
            {
                var white = routeCanvas.Children.OfType<Line>().Where(line => line.Stroke == Brushes.White).ToArray();
                Check(routeCanvas.Visibility == Visibility.Visible
                    && white.Any(line => line.X1 == 50 - originX && line.X2 == 298 - originX
                        && line.Y1 == -13 - originY && line.Y2 == line.Y1)
                    && white.Any(line => line.X1 == 298 - originX && line.X1 == line.X2
                        && line.Y1 == -13 - originY && line.Y2 == -36 - originY)
                    && white.Any(line => line.X1 == 322 - originX && Math.Abs(line.X2 - point.X) <= 1.5
                        && line.Y1 == point.Y && line.Y2 == point.Y),
                    "The white route must immediately cover the floor, the rise, and the part of the ledge beyond the image.");
            }
        }
        finally
        {
            Field(window, "visionCancellation").SetValue(window, null);
            var shutdown = (Task)Invoke(window, "PrepareShutdownAsync")!;
            WaitFor(() => shutdown.IsCompleted, "The objective fixture did not finish shutdown.");
            shutdown.GetAwaiter().GetResult();
            Field(window, "shutdownReady").SetValue(window, true);
            window.Close();
        }
    }

    private static void VerifyGestures(string root)
    {
        const int size = LiveTiledAtlas.TileSize;
        var projection = new HackerTerrainProjection(new(4, 4), new(24, 20));
        var pixels = Enumerable.Repeat((byte)155, size * size * 4).ToArray();
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = new BgraFrame(size, size, size * 4, pixels, 1, DateTimeOffset.UnixEpoch);
        var archived = new HackerAtlasArchivedRoom(Guid.NewGuid(), Room, new(0, 0), default,
            projection, new(0, 0, size, size), [new(new(0, 0), image)]);
        new HackerAtlasArchiveStore(Path.Combine(root, "hacker-atlas-v1"))
            .Write(null, new([archived], [], new Dictionary<string, AtlasPoint>(), 1, DateTimeOffset.UtcNow));
        var store = new GroundTruthLineStore(Path.Combine(root, GroundTruthLineStore.Filename));
        var window = new MainWindow(GameRuntimeMode.Modded, gamePath: null, root: root, documents: root) { Topmost = true };
        var held = false;
        try
        {
            // Load the archived room before showing the window with live game
            // startup disabled. The shutdown guard also skips archive loading.
            var loadArchive = (Task)Invoke(window, "InitializeHackerArchiveAsync")!;
            WaitFor(() => loadArchive.IsCompleted, "The terrain fixture did not finish loading the archive.");
            loadArchive.GetAwaiter().GetResult();
            Check(Field(window, "hackerAtlasOutput").GetValue(window) is HackerAtlasOutput { RoomCount: 1 },
                "The terrain fixture did not load its archived room.");
            Field(window, "isClosing").SetValue(window, true);
            window.Show();
            WaitFor(() => window.IsLoaded && Field(window, "atlasOperationInProgress").GetValue(window) is false,
                "The terrain fixture window did not finish loading.");
            Field(window, "workspace").SetValue(window, "Hacker");
            Control<FrameworkElement>(window, "GameplayPanel").Visibility = Visibility.Collapsed;
            Control<FrameworkElement>(window, "HackerPanel").Visibility = Visibility.Visible;
            Control<FrameworkElement>(window, "GameplayControlsPanel").Visibility = Visibility.Collapsed;
            Field(window, "hackerFramingValue").SetValue(window, "Free Fly");
            var viewport = (GameplayViewportController)Field(window, "hackerViewport").GetValue(window)!;
            viewport.SetFreeFlyTransform(new(1.25, 70, 55));
            Invoke(window, "RenderHackerAtlasLayout");
            _ = window.Activate();
            window.UpdateLayout();

            var atlas = Control<Canvas>(window, "AtlasCanvas");
            var overlay = Control<Canvas>(window, "HackerOverlayCanvas");
            var controller = (GroundTruthLabelController)Field(window, "groundTruthLabels").GetValue(window)!;
            var target = Control<ComboBox>(window, "HackerTerrainTarget");
            var add = Control<RadioButton>(window, "GroundAddButton");
            var modify = Control<RadioButton>(window, "GroundModifyButton");
            var delete = Control<RadioButton>(window, "GroundDeleteButton");
            var undo = Control<Button>(window, "GroundUndoButton");
            TestDoorDragUsesPathBeyondCapturedTiles();
            TestEndpointHandlesFollowVisibleLinesAndRemainReadableWhenZoomed();
            TestOverlappingRoomEditsKeepTheirOwner();
            Choose("Door");
            SetMode(add);
            Check(controller.Tool == GroundTruthTerrainTool.Door && controller.Mode == GroundTruthEditingMode.Add
                && Control<FrameworkElement>(window, "HackerTerrainActions").IsVisible,
                "The window must expose Add when Door is selected.");

            MoveTo(new(10, 17));
            Press();
            WaitFor(() => controller.HasActiveGesture && ReferenceEquals(Mouse.Captured, atlas),
                "The door tool did not take the native drag.");
            Check(ReadDoors().Count == 0, "Mouse-down must leave the new door as a draft.");

            var edits = (IDictionary)Field(window, "rooms").GetValue(window)!;
            var room = edits[Room]!;
            room.GetType().GetProperty("OffsetX")!.SetValue(room, 48d);
            room.GetType().GetProperty("OffsetY")!.SetValue(room, -120d);
            Invoke(window, "RenderHackerAtlasLayout");
            MoveTo(new(25, 17));
            WaitFor(() => controller.Draft is { Kind: GroundTruthLineKind.Door } draft
                && Near(draft.End.X, 25), "The open draft did not follow the corrected room.");
            Check(ReadDoors().Count == 0, "Moving the pointer must not write the draft.");
            Release();
            WaitFor(() => !controller.HasActiveGesture && File.Exists(store.FilePath), "Mouse-up did not save the door.");
            var first = ReadDoors().Single();
            Check(first.SceneName == Room && first.Kind == GroundTruthLineKind.Door
                && Near(first.Start.X, 10) && Near(first.End.X, 25) && Near(first.Start.Y, 17) && Near(first.End.Y, 17),
                "The horizontal door was saved in the wrong room coordinates after the layout change.");

            Drag(new(33, 8), new(33, 27));
            var vertical = ReadDoors().Single(line => line.Kind == GroundTruthLineKind.DoorVertical);
            Check(Near(vertical.Start.X, 33) && Near(vertical.End.X, 33)
                && Near(vertical.Start.Y, 8) && Near(vertical.End.Y, 27), "A vertical drag did not produce a vertical world door.");
            SetMode(modify);
            Check(overlay.Children.OfType<Rectangle>().Count() == 4, "Both doors must show adjustable endpoints in Modify.");
            Drag(new(33, 27), new(33, 33));
            var extended = ReadDoors().Single(line => line.Id == vertical.Id);
            Check(Near(extended.End.X, 33) && Near(extended.End.Y, 33), "Dragging the endpoint must extend a vertical door vertically.");

            SetMode(delete);
            MoveTo(new(33, 17));
            Press();
            WaitFor(() => controller.HasActiveGesture, "Delete did not receive the native press.");
            Release();
            WaitFor(() => !controller.HasActiveGesture, "Delete did not receive the native release.");
            Check(ReadDoors().Count == 1, "Delete must remove only the selected door.");
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReadDoors().Count == 2, "Undo must restore the edited door on disk.");
            var framing = viewport.Transform;
            Choose("Objective");
            var doorLines = overlay.Children.OfType<Line>().Count();
            var editHandles = overlay.Children.OfType<Rectangle>().Count();
            Check(doorLines == 2 && editHandles == 0 && viewport.Transform == framing,
                $"Objective expected two doors, no handles and framing {framing}. Found {doorLines} lines, {editHandles} handles and framing {viewport.Transform}.");

            Choose("Door");
            SetMode(add);
            MoveTo(new(8, 10));
            Press();
            WaitFor(() => controller.HasActiveGesture, "The room-change fixture could not start a new draft.");
            var output = (HackerAtlasOutput)Field(window, "hackerAtlasOutput").GetValue(window)!;
            Field(window, "hackerAtlasOutput").SetValue(window, output with { Rooms = [] });
            Invoke(window, "RenderHackerAtlasLayout");
            Check(!controller.HasActiveGesture && !ReferenceEquals(Mouse.Captured, atlas),
                "Removing the active room must cancel the draft and release the pointer.");
            Release();
            Pump();
            Check(ReadDoors().Count == 2, "A cancelled drag must not write a line to the removed room.");
            TestTabFramingKeepsDoorEditor();

            void TestDoorDragUsesPathBeyondCapturedTiles()
            {
                // The ground starts inside the bitmap, then extends past its right edge.
                Choose("Ground");
                SetMode(add);
                Drag(new(13, 58), new(69, 58));
                Check(store.Load().Lines.Single() is { Kind: GroundTruthLineKind.Positive },
                    "Could not author the floor that extends beyond the bitmap.");
                var start = new AtlasPoint(69, 61.25);
                var roomPosition = (AtlasPoint)Invoke(window, "HackerScenePosition", Room)!;
                var pointer = projection.WorldToAtlas(start, roomPosition);
                var tolerance = (double)Invoke(window, "GroundPointerTolerance")!;
                Check(Invoke(window, "TerrainRoomAt", pointer, tolerance) is null,
                    "The exit must be outside ordinary ground hit bounds.");

                Choose("Door");
                Drag(start, new(69, 64.5));
                var door = ReadDoors().Single();
                Check(door.SceneName == Room && door.Kind == GroundTruthLineKind.DoorVertical
                    && Near(door.Start.X, start.X) && Near(door.Start.Y, start.Y),
                    "The visible route should let the Door tool start beyond the room bitmap and save in its scene.");
                undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ReadDoors().Count == 0 && store.Load().Lines.Count == 0, "Undo must remove the staged floor and door.");
            }

            void TestTabFramingKeepsDoorEditor()
            {
                // Expose the controls that a frame-matched game normally activates.
                Control<FrameworkElement>(window, "HackerCameraControlsPanel").Visibility = Visibility.Visible;
                foreach (var preset in new[] { "Current Near", "Current Wide", "Fit", "Current Near" })
                {
                    Check(Invoke(window, "HandleLocalShortcut", Key.Tab, false, ModifierKeys.None) is true
                        && Equals(Field(window, "hackerFramingValue").GetValue(window), preset),
                        $"The Hacker Tab shortcut should select {preset}.");
                    Check(controller.Tool == GroundTruthTerrainTool.Door && controller.Mode == GroundTruthEditingMode.Add
                        && Control<FrameworkElement>(window, "HackerGroundToolbar").IsVisible,
                        "Tab framing should keep the Door editor and its mode in the top bar.");
                }
            }

            void TestEndpointHandlesFollowVisibleLinesAndRemainReadableWhenZoomed()
            {
                Choose("Ground");
                SetMode(add);
                Drag(new(15, 20), new(59, 20));
                Choose("Wall");
                Drag(new(15, 20), new(15, 44));
                SetMode(modify);
                var handles = overlay.Children.OfType<Rectangle>().ToArray();
                Check(handles.Length == 3 && handles.Count(handle => handle.Fill is null && handle.Stroke is not null) == 2,
                    "The two free endpoints should be outlined and the attachment should have one solid marker.");
                Check(handles.Any(handle => ReferenceEquals(handle.Stroke, Brushes.White) && handle.StrokeThickness == 2),
                    "The selected wall should have a white outlined free endpoint.");
                var corner = handles.Single(handle => handle.Fill is not null);
                var center = Center(corner);
                Check(overlay.Children.OfType<Line>().Count(line =>
                    new Point(line.X1, line.Y1) == center || new Point(line.X2, line.Y2) == center) == 2,
                    "The solid marker must be centered over both attached endpoints.");

                var host = Control<FrameworkElement>(window, "HackerViewportPanel");
                var before = viewport.Transform;
                try
                {
                    foreach (var scale in new[] { .25, 1, 8 })
                    {
                        Invoke(window, "ApplySettledViewportTransform", atlas, before with { Scale = scale });
                        window.UpdateLayout();
                        foreach (var handle in handles)
                        {
                            var displayed = handle.TransformToAncestor(host).TransformBounds(new Rect(handle.RenderSize));
                            var anchor = overlay.TranslatePoint(Center(handle), host);
                            Check(Math.Abs(displayed.Width - 8) < .001 && Math.Abs(displayed.Height - 8) < .001
                                && (anchor - new Point(displayed.Left + 4, displayed.Top + 4)).Length < .001,
                                $"The endpoint should stay centered and 8 display units wide at zoom {scale}.");
                        }
                    }
                }
                finally { Invoke(window, "ApplySettledViewportTransform", atlas, before); }
                undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(store.Load().Walls is null or { Count: 0 } && store.Load().Lines.Count == 0,
                    "Both staged terrain edits must undo cleanly.");
                Console.WriteLine("PASS Hacker terrain endpoints retain their display size and show one solid marker for each attached corner");

                static Point Center(Rectangle handle) =>
                    new(Canvas.GetLeft(handle) + handle.Width / 2, Canvas.GetTop(handle) + handle.Height / 2);
            }

            void TestOverlappingRoomEditsKeepTheirOwner()
            {
                const string otherScene = "Tutorial_02";
                var savedOutput = (HackerAtlasOutput)Field(window, "hackerAtlasOutput").GetValue(window)!;
                var firstRoom = savedOutput.Rooms.Single();
                var otherProjection = new HackerTerrainProjection(new(2, 4), new(12, 10));
                var secondRoom = new HackerAtlasRoom(Guid.NewGuid(), otherScene, default, new(0, 0, 140, 120))
                {
                    TerrainProjection = otherProjection
                };
                // The larger room is topmost, with its floor body under the other room's endpoint.
                var overlapping = savedOutput with { Rooms = [secondRoom, firstRoom] };
                var before = viewport.Transform;
                var edits = (IDictionary)Field(window, "rooms").GetValue(window)!;
                try
                {
                    Field(window, "hackerAtlasOutput").SetValue(window, overlapping);
                    Invoke(window, "SynchronizeHackerRoomEdits", overlapping);
                    var firstPosition = (AtlasPoint)Invoke(window, "HackerScenePosition", Room)!;
                    var otherPosition = (AtlasPoint)Invoke(window, "HackerScenePosition", otherScene)!;
                    var edit = edits[otherScene]!;
                    edit.GetType().GetProperty("OffsetX")!.SetValue(edit, firstPosition.X + 20 - otherPosition.X);
                    edit.GetType().GetProperty("OffsetY")!.SetValue(edit, otherPosition.Y - firstPosition.Y - 10);
                    Invoke(window, "RenderHackerAtlasLayout");

                    Choose("Ground");
                    SetMode(add);
                    AddFloor(Room, 17, 55, projection);
                    AddFloor(otherScene, 50, 70, otherProjection);
                    var firstFloor = store.Load().Lines.Single(line => line.SceneName == Room);
                    SetMode(modify);
                    Drag(new(27, 16), new(28.5, 18));
                    var modified = store.Load().Lines.Single(line => line.SceneName == otherScene);
                    Check(modified.Start.X is > 52.5 and < 53.5 && modified.Start.Y == 16
                        && store.Load().Lines.Single(line => line.SceneName == Room) == firstFloor,
                        "The smaller room's endpoint should win over the overlapping room's line body.");

                    Choose("Wall");
                    SetMode(add);
                    Drag(new(28.5, 16), new(28.5, 26));
                    var wall = store.Load().Walls!.Single();
                    Check(wall.SceneName == otherScene && wall.Start == modified.Start && wall.StartCornerId is not null,
                        "Wall Add should choose the room that owns the corner under the pointer.");

                    Choose("Ground");
                    Drag(new(14, 8), new(56, 8));
                    var reopened = new GroundTruthLabelController(store).Document;
                    var crossed = reopened.Lines.Single(line => line.Id != firstFloor.Id && line.Id != modified.Id);
                    Check(crossed.SceneName == otherScene && crossed.End.X is > 107.5 and < 108.5
                        && reopened.Lines.Single(line => line.SceneName == Room) == firstFloor,
                        "Empty overlap should start in the smaller room and keep its owner across the drag and reload.");
                    for (var i = 0; i < 5; i++) undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(store.Load().Lines.Count == 0 && store.Load().Walls is null or { Count: 0 },
                        "Undo did not restore both rooms' terrain.");
                    Console.WriteLine("PASS Hacker terrain edits select the authored room through overlaps and retain ownership after reload");
                }
                finally
                {
                    Field(window, "hackerAtlasOutput").SetValue(window, savedOutput);
                    edits.Remove(otherScene);
                    Invoke(window, "SynchronizeHackerRoomEdits", savedOutput);
                    Invoke(window, "RenderHackerAtlasLayout");
                    Invoke(window, "ApplySettledViewportTransform", atlas, viewport.SetFreeFlyTransform(before));
                }

                void AddFloor(string scene, double start, double end, HackerTerrainProjection roomProjection)
                {
                    controller.HandleWorld(GroundTruthPointerPhase.Began, new(start, 16), 8, scene, roomProjection.PixelsPerWorldUnit);
                    controller.HandleWorld(GroundTruthPointerPhase.Ended, new(end, 16), 8, scene, roomProjection.PixelsPerWorldUnit);
                }
            }

            List<GroundTruthWorldLine> ReadDoors() => store.Load().Doors ?? [];

            void Choose(string name) => target.SelectedItem = target.Items.OfType<ComboBoxItem>()
                .Single(item => Equals(item.Content, name));

            void SetMode(RadioButton mode)
            {
                mode.IsChecked = true;
                mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            void MoveTo(AtlasPoint world)
            {
                window.UpdateLayout();
                var roomPosition = (AtlasPoint)Invoke(window, "HackerScenePosition", Room)!;
                var global = projection.WorldToAtlas(world, roomPosition);
                var originX = (int?)Field(window, "hackerAtlasMinimumTileX").GetValue(window) ?? 0;
                var originY = (int?)Field(window, "hackerAtlasMinimumTileY").GetValue(window) ?? 0;
                var local = new Point(global.X - originX * size, -global.Y - originY * size);
                // Reproject while WPF settles the layout and the animated viewport.
                WaitFor(() =>
                {
                    if ((Mouse.GetPosition(atlas) - local).Length < 1.5) return true;
                    var screen = atlas.PointToScreen(local);
                    Check(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)), "Could not move the native test pointer.");
                    return false;
                }, $"The pointer did not reach the projected room coordinate {local}.");
            }

            void Press()
            {
                held = true;
                MouseEvent(LeftDown, 0, 0, 0, 0);
            }

            void Release()
            {
                MouseEvent(LeftUp, 0, 0, 0, 0);
                held = false;
            }

            void Drag(AtlasPoint a, AtlasPoint b)
            {
                MoveTo(a);
                Press();
                WaitFor(() => controller.HasActiveGesture, "The terrain drag did not start.");
                MoveTo(b);
                Release();
                WaitFor(() => !controller.HasActiveGesture, "The terrain drag did not finish.");
            }
        }
        finally
        {
            if (held) MouseEvent(LeftUp, 0, 0, 0, 0);
            var shutdown = (Task)Invoke(window, "PrepareShutdownAsync")!;
            WaitFor(() => shutdown.IsCompleted, "The editor fixture did not finish shutdown.");
            shutdown.GetAwaiter().GetResult();
            Field(window, "shutdownReady").SetValue(window, true);
            window.Close();
        }
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .4;

    private static void WaitFor(Func<bool> ready, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(12))
        {
            Pump();
            Thread.Sleep(5);
        }
        Check(ready(), message);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException($"Missing control {name}."));
    private static FieldInfo Field(MainWindow window, string name) => window.GetType().GetField(name, Private)!;
    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        window.GetType().GetMethod(name, Private)!.Invoke(window, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint x, uint y, uint data, nuint info);
}
