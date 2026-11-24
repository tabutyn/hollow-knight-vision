using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        }
        finally
        {
            if (hasPointer) _ = SetCursorPos(pointer.X, pointer.Y);
            Environment.SetEnvironmentVariable(LabelRoot, oldRoot);
            directory.Delete(recursive: true);
        }
        Console.WriteLine("PASS native Hacker door controls draw both axes and keep an active drag attached to its room");
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
            var framing = viewport.Transform.Scale;
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
            var undo = Control<Button>(window, "GroundUndoButton");
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReadDoors().Count == 2, "Undo must restore the edited door on disk.");
            Choose("Objective");
            Check(overlay.Children.OfType<Line>().Count() == 2 && !overlay.Children.OfType<Rectangle>().Any()
                && viewport.Transform.Scale == framing, "Objective must display both doors without changing the camera framing.");

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
                var screen = atlas.PointToScreen(local);
                Check(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)), "Could not move the native test pointer.");
                WaitFor(() => (Mouse.GetPosition(atlas) - local).Length < 1.5, "The pointer did not reach the projected room coordinate.");
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
