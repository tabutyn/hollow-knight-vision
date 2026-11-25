using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class GroundFeatureViewerTests
{
    internal static void Run()
    {
        var scene = new GroundFeatureViewer();
        try
        {
            scene.PresentOverview(
                [
                    new(new(20, 50), new(160, 50), Brushes.Lime),
                    new(new(100, 50), new(100, 70), Brushes.Gold)
                ], new(90, 50), point => new(point.X, point.Y));
            Layout(scene);
            var overview = (Grid)scene.FindName("OverviewHost");
            var bitmap = new RenderTargetBitmap((int)overview.ActualWidth, (int)overview.ActualHeight,
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overview);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Check(Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 1] > 200 && pixels[index * 4 + 2] < 30)
                && Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 1] > 160
                    && pixels[index * 4 + 2] > 210 && pixels[index * 4] < 40),
                "The overview must draw ground and route geometry without a selected patch.");

            var first = Feature(1, 100, 128);
            var last = Feature(3, 140, 255);
            scene.ShowPatch(first, [first, last]);
            Layout(scene);
            var slider = (Slider)scene.FindName("PatchPosition");
            var strip = (Image)scene.FindName("PatchPixels");
            var patchWidth = GroundHypothesisTracker.FeatureWidth;
            var row = new byte[patchWidth * 3 * GroundHypothesisTracker.FeatureHeight];
            ((BitmapSource)strip.Source).CopyPixels(row, patchWidth * 3, 0);
            Check(row[0] == 50 && row[patchWidth] == 0 && row[patchWidth * 2] == 140,
                "Patch tiles must use the saved opacity, keeping a black cell for a missing index.");

            var updated = Feature(1, 170, 255);
            scene.RefreshPatches([Feature(0, 80, 255), updated, last]);
            Check(scene.Selected == updated && slider.Value == 1 && slider.Minimum == 0,
                "Prepending new live patches must retain the current position and update selected evidence.");
            scene.MovePatch(1);
            Check(scene.Selected == last && slider.Value == 3, "Next must skip the missing patch.");
            slider.Value = 2;
            Check(scene.Selected == updated, "Scrolling over a gap must select its nearest patch.");
            scene.ClearPatch();
            Check(scene.Selected is null && strip.Source is null && ((StackPanel)scene.FindName("PatchPanel")).Visibility == Visibility.Collapsed,
                "Switching to the overview must release the patch strip.");
        }
        finally { scene.Close(); }

        var live = new Canvas();
        var real = new GroundHypothesisFeature(4, 12, 1, new(12, 20, 16, 12),
            GroundHypothesisClassification.GroundPlane, 0, 0);
        GroundFeatureOverlayPresentation.DrawLive(live, GroundHypothesisTrackingResult.Empty with { Features = [real] },
            [new(20, new(0, 83))], [new(46, 20, 16, 12)]);
        Check(MainWindow.GroundViewerKeyAt(live, new(18, 26)) == new GroundFeatureOverlayPresentation.FeatureKey(12, 1)
            && MainWindow.GroundViewerKeyAt(live, new(51, 26)) is null,
            "Only an actual feature reference should be selectable from the live overlay.");

        var atlas = new Canvas();
        var feature = Feature(1, 100, 255) with { AtlasPosition = new(120, 40) };
        var floor = new GroundHypothesisAtlasLine(12, new(100, 46), new(140, 46));
        var drawing = GroundFeatureOverlayPresentation.DrawAtlas(null, atlas, new(0, 0), new(100, 60),
            new(0, 0, 100, 60), [floor], [feature], []);
        Check(drawing?.Project(feature.AtlasPosition) == new Point(120, 20)
            && MainWindow.GroundViewerKeyAt(atlas, new(120, 20)) == new GroundFeatureOverlayPresentation.FeatureKey(12, 1),
            "A retained patch must remain selectable in the same space as the merged terrain.");

        var strokes = new Canvas();
        GroundFeatureOverlayPresentation.DrawAtlas(strokes, null, default, new(100, 60),
            new(0, 0, 100, 60), [new(7, new(0, 40), new(100, 40))], [],
            [new(20, new(10, 90), [new(10, 40), new(60, 90)])]);
        Check(strokes.Children.OfType<Line>().Select(line => (line.X1, line.Y1, line.X2, line.Y2))
                .ToHashSet().SetEquals([(10d, 20.5, 41d, 20.5), (60d, 20.5, 91d, 20.5)]),
            "A saved edge and a detector extent must both leave an unobserved gap open in the live overlay.");

        Console.WriteLine("PASS Ground Viewer paints paths and browses correctly aligned live patch evidence");
        VerifyMainWindowPatchSelection();
    }

    private static void VerifyMainWindowPatchSelection()
    {
        var root = Directory.CreateTempSubdirectory("hkv-ground-viewer-");
        var window = new MainWindow(GameRuntimeMode.Normal, gamePath: null, root: root.FullName, documents: root.FullName)
        { Topmost = true };
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        var highlightTag = type.GetField("GroundViewerHighlight", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        var hasCursor = GetCursorPos(out var originalCursor);
        try
        {
            Set("isClosing", true);
            window.Show();
            WaitFor(() => window.IsLoaded && type.GetField("persistentWorldInitialization", instance)!.GetValue(window) is null,
                "The Ground Viewer fixture did not load.");
            Set("workspace", "Gameplay");
            Set("recognizedMenuContext", "game");
            Set("recentCaptureGeneration", 12L);
            ((CheckBox)window.FindName("GroundLinesCheck")).IsChecked = true;
            ((CheckBox)window.FindName("GroundTrackingPatchesCheck")).IsChecked = true;
            ((CheckBox)window.FindName("AtlasPathsCheck")).IsChecked = false;
            var live = (Canvas)window.FindName("LiveGroundPatchesCanvas");
            var atlas = (Canvas)window.FindName("AtlasGroundPatchesCanvas");
            var viewport = (Grid)window.FindName("GameplayPanel");
            var open = (Button)window.FindName("GroundViewerButton");
            var first = Feature(1, 130, 255);
            var second = Feature(3, 150, 255) with { AtlasPosition = new(680, 50) };
            var floorY = first.AtlasPosition.Y + GroundHypothesisTracker.FeatureHeight / 2d;
            Publish("Room_A", first, second);
            window.UpdateLayout();
            var point = live.PointToScreen(new(18, 26));
            Check(SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)), "Could not place the pointer over the ground patch.");
            WaitFor(() => (Mouse.GetPosition(live) - new Point(18, 26)).Length < 1.5, "The pointer did not reach the live patch.");
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            viewport.RaiseEvent(click);
            var viewer = type.GetField("groundFeatureViewer", instance)!.GetValue(window) as GroundFeatureViewer
                ?? throw new InvalidOperationException("The viewport patch click did not open Ground Viewer.");
            Check(click.Handled && viewer.Selected == first && Highlights(live) == 1 && Highlights(atlas) == 0,
                "Selecting a patch inside the live image must outline its current tracking box.");
            viewer.MovePatch(1);
            Check(viewer.Selected == second && Highlights(live) == 0 && Highlights(atlas) == 1,
                "Moving to a retained patch outside the image must transfer the outline to the Atlas.");
            var refreshed = Feature(3, 175, 128) with { AtlasPosition = second.AtlasPosition };
            Publish("Room_A", first, refreshed);
            WaitFor(() => viewer.Selected == refreshed, "The viewer did not refresh the selected patch from the active room.");
            Publish("Room_B", first, refreshed);
            WaitFor(() => viewer.Selected is null && Highlights(live) == 0 && Highlights(atlas) == 0,
                "A new room must clear the old selection and both highlights.");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(type.GetField("groundFeatureViewer", instance)!.GetValue(window), viewer),
                "The settings button must reuse the open Ground Viewer.");
            viewer.Close();
            Check(type.GetField("groundFeatureViewer", instance)!.GetValue(window) is null,
                "Closing Ground Viewer must release the main window reference.");

            void Publish(string room, params GroundHypothesisAtlasFeature[] features)
            {
                live.Children.Clear();
                GroundFeatureOverlayPresentation.DrawLive(live, GroundHypothesisTrackingResult.Empty with
                {
                    Features = [new(4, 12, 1, new(12, 20, 16, 12), GroundHypothesisClassification.GroundPlane, 0, 0)]
                }, [new(20, new(0, 83))], []);
                atlas.Children.Clear();
                var drawing = GroundFeatureOverlayPresentation.DrawAtlas(null, atlas, new(0, 0), new(640, 360),
                    new(0, 0, 640, 360), [new(12, new(30, floorY), new(720, floorY))], features, [])!;
                var frame = type.GetNestedType("GroundViewerFrame", BindingFlags.NonPublic)!;
                Set("groundViewerFrame", Activator.CreateInstance(frame, 12L, room, null, features, drawing));
                type.GetMethod("DrawGroundViewerHighlight", instance)!.Invoke(window, null);
            }
        }
        finally
        {
            Mouse.Capture(null);
            if (hasCursor) _ = SetCursorPos(originalCursor.X, originalCursor.Y);
            var shutdown = (Task)type.GetMethod("PrepareShutdownAsync", instance)!.Invoke(window, null)!;
            WaitFor(() => shutdown.IsCompleted, "The Ground Viewer fixture did not finish shutdown.");
            shutdown.GetAwaiter().GetResult();
            Set("shutdownReady", true);
            window.Close();
            root.Delete(recursive: true);
        }
        Console.WriteLine("PASS MainWindow selects, refreshes and clears live Ground Viewer patches");

        void Set(string name, object? value) => type.GetField(name, instance)!.SetValue(window, value);
        int Highlights(Canvas canvas) => canvas.Children.OfType<Rectangle>().Count(marker => ReferenceEquals(marker.Tag, highlightTag));
    }

    private static void WaitFor(Func<bool> check, string reason)
    {
        var timeout = Stopwatch.StartNew();
        while (!check() && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            Program.Pump();
            Thread.Sleep(5);
        }
        Check(check(), reason);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    private static GroundHypothesisAtlasFeature Feature(int index, byte value, byte opacity) => new(12, index,
        new(index * 16 + 50, 50), Enumerable.Repeat(value, GroundHypothesisTracker.FeaturePixelCount).ToArray(),
        Enumerable.Repeat(opacity, GroundHypothesisTracker.FeaturePixelCount).ToArray());

    private static void Layout(GroundFeatureViewer viewer)
    {
        var content = (FrameworkElement)viewer.Content;
        content.Measure(new Size(700, 395));
        content.Arrange(new Rect(0, 0, 700, 395));
        content.UpdateLayout();
    }

    private static void Check(bool valid, string reason)
    { if (!valid) throw new InvalidOperationException(reason); }
}
