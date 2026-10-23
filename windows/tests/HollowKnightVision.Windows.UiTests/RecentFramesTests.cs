using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using Path = System.IO.Path;

internal static class RecentFramesTests
{
    private static readonly Type PickerType = typeof(MainWindow).Assembly.GetType("HollowKnightVision.Windows.App.RecentFramesWindow")!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .000001, $"Expected {expected}, got {actual}.");
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private static BgraFrame Frame(long id = 1, int width = 100, int height = 60) => new(width, height, width * 4,
        Enumerable.Range(0, width * height * 4).Select(index => (byte)(index % 4 == 3 ? 255 : 32)).ToArray(),
        id, DateTimeOffset.UnixEpoch.AddSeconds(id));
    private static LabelingExampleAnnotation Box(bool negative = false) => new(Guid.NewGuid(), "game.health", .2, .3, .4, .2, negative);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static Window Picker(IReadOnlyList<SavedLabelingExample> saved, IReadOnlyList<RecentCapturedFrame> captures,
        Action<SavedLabelingExample>? openSaved = null, Action<RecentCapturedFrame>? openCapture = null, Action? resume = null)
    {
        var window = (Window)Activator.CreateInstance(PickerType, BindingFlags.NonPublic | BindingFlags.Instance, null,
            [saved, captures, (Func<string, string>)(key => key), openSaved ?? (_ => { }), openCapture ?? (_ => { }), resume ?? (() => { })], null)!;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000; window.Top = -20000; window.ShowActivated = false;
        window.Show(); Pump(); return window;
    }
    private static Button Button(Window window, string id) => Children<Button>(window)
        .Single(button => AutomationProperties.GetAutomationId(button) == id);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); }
    private static bool Key(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false) =>
        (bool)PickerType.GetMethod("HandleShortcut", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [key, modifiers, repeat])!;

    internal static void Run()
    {
        TestColumnsAndSelection(); TestShortcutsAndEmptyStates(); TestThumbnailGeometry(); TestLivePublicationIntegration();
        Console.WriteLine("PASS Recent Frames shows labeled/captured columns, box previews, exact capture publication and native Windows controls");
    }

    private static void TestColumnsAndSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-recent-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LabelingExampleStore(root); var saved = store.Save(Frame(), "game", [Box(), Box(true)]);
            var missing = new SavedLabelingExample(Path.Combine(root, "missing"), saved.Manifest with { Id = Guid.NewGuid() });
            var empty = saved with { Manifest = saved.Manifest with { Id = Guid.NewGuid(), Annotations = [], KnownClassIdentifiers = [] } };
            var captures = Enumerable.Range(1, 64).Reverse().Select(id => new RecentCapturedFrame(4, id, Frame(id),
                [new("game.health", new(.2, .3, .4, .2), .9f)])).ToArray();
            SavedLabelingExample? selectedSaved = null; RecentCapturedFrame? selectedCapture = null; var resumes = 0;
            var window = Picker([saved, missing, empty], captures, value => selectedSaved = value, value => selectedCapture = value, () => resumes++);
            try
            {
                Check(window.WindowStyle == WindowStyle.SingleBorderWindow && window.ResizeMode == ResizeMode.CanResize,
                    "Picker replaced native Windows title-bar controls.");
                Check(Children<ScrollViewer>(window).Count() == 2, "Capture sources do not scroll independently.");
                var headings = Children<TextBlock>(window).Select(item => item.Text).ToArray();
                Check(headings.Contains("Recent Labeled Frames") && headings.Contains("Recent Captured Frames"), "Missing source headings.");
                var buttons = Children<Button>(window).ToArray();
                Check(buttons.Count(item => AutomationProperties.GetAutomationId(item).StartsWith("RecentSaved_")) == 2
                    && buttons.Count(item => AutomationProperties.GetAutomationId(item).StartsWith("RecentCapture_")) == 64,
                    "Picker merged sources, lost captures, or included unlabeled saved examples.");
                var missingPreview = (Canvas)Button(window, "RecentSaved_" + missing.Id.ToString("N")).Content;
                Check(missingPreview.Children.OfType<TextBlock>().Any() && !missingPreview.Children.OfType<Image>().Any(), "Missing image has no placeholder.");
                var capture = Button(window, "RecentCapture_4_64");
                Check(AutomationProperties.GetName(capture) == "Open captured frame with 1 predicted boxes", "Capture has no accessible description.");
                Check(capture.BorderBrush == Brushes.MediumOrchid && ((Canvas)capture.Content).Children.OfType<Rectangle>().Count() == 1,
                    "Capture predictions have no preview or distinct border.");
                Click(capture);
                Check(!window.IsVisible && selectedCapture?.SourceFrameIdentifier == 64 && selectedSaved is null && resumes == 0,
                    "Single-click did not open the chosen capture and dismiss only the picker.");
            }
            finally { if (window.IsVisible) window.Close(); }
            window = Picker([saved], [], value => selectedSaved = value);
            try
            {
                Click(Button(window, "RecentSaved_" + saved.Id.ToString("N")));
                Check(selectedSaved?.Id == saved.Id && !window.IsVisible, "Single-click failed to open the saved example.");
            }
            finally { if (window.IsVisible) window.Close(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void TestShortcutsAndEmptyStates()
    {
        var resumes = 0; var window = Picker([], [], resume: () => resumes++);
        try
        {
            var text = Children<TextBlock>(window).Select(item => item.Text).ToArray();
            Check(text.Contains("No Recent Labeled Frames") && text.Contains("No Recent Captured Frames")
                && text.Contains("Resume gameplay briefly to fill the capture buffer."), "Empty states give no recovery guidance.");
            Check(!Key(window, System.Windows.Input.Key.L, ModifierKeys.Control) && !Key(window, System.Windows.Input.Key.X, repeat: true)
                && !Key(window, System.Windows.Input.Key.A) && window.IsVisible && resumes == 0, "Picker stole modified/repeated shortcuts.");
            Click(Button(window, "RecentClose")); Check(resumes == 0 && !window.IsVisible, "Close unexpectedly resumed gameplay.");
        }
        finally { if (window.IsVisible) window.Close(); }
        foreach (var key in new[] { System.Windows.Input.Key.L, System.Windows.Input.Key.X, System.Windows.Input.Key.Escape })
        {
            var before = resumes; window = Picker([], [], resume: () => resumes++);
            try
            {
                Check(Key(window, key) && !window.IsVisible, "Picker shortcut was not handled.");
                Check(resumes == before + (key == System.Windows.Input.Key.Escape ? 0 : 1), "Resume and close shortcuts share the wrong action.");
            }
            finally { if (window.IsVisible) window.Close(); }
        }
        window = Picker([], [], resume: () => resumes++); var count = resumes;
        try { Click(Button(window, "RecentResume")); Check(resumes == count + 1 && !window.IsVisible, "Resume button only dismissed the picker."); }
        finally { if (window.IsVisible) window.Close(); }
    }

    private static void TestThumbnailGeometry()
    {
        var frame = Frame(width: 100, height: 200);
        var bitmap = BitmapSource.Create(100, 200, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride); bitmap.Freeze();
        Canvas Preview(bool predicted) => (Canvas)PickerType.GetMethod("Thumbnail", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [bitmap, new[] { Box(), Box(true) with { X = .65, Width = .1 } }, predicted])!;
        var saved = Preview(false); var predicted = Preview(true);
        var image = saved.Children.OfType<Image>().Single();
        Near(53.5, image.Width); Near(107, image.Height); Near(68.25, Canvas.GetLeft(image)); Near(0, Canvas.GetTop(image));
        var positive = saved.Children.OfType<Rectangle>().First();
        Near(78.95, Canvas.GetLeft(positive)); Near(32.1, Canvas.GetTop(positive)); Near(21.4, positive.Width); Near(21.4, positive.Height);
        Check(positive.Stroke == Brushes.Cyan && saved.Children.OfType<Rectangle>().Last().Stroke == Brushes.HotPink,
            "Saved positive/negative previews lost their separate colors.");
        Check(predicted.Children.OfType<Rectangle>().All(box => box.Stroke == Brushes.MediumOrchid && box.StrokeThickness == 1.5),
            "Predictions do not use Mac's distinct box style.");
        Check(!saved.IsHitTestVisible && saved.ClipToBounds, "Overlay intercepts thumbnail clicks or overflows its frame.");
        saved.Measure(new Size(190, 107)); saved.Arrange(new Rect(0, 0, 190, 107)); saved.UpdateLayout();
        var rendered = new RenderTargetBitmap(190, 107, 96, 96, PixelFormats.Pbgra32); rendered.Render(saved);
        var pixels = new byte[190 * 107 * 4]; rendered.CopyPixels(pixels, 190 * 4, 0);
        Check(Enumerable.Range(0, 190 * 107).Any(i => pixels[i * 4] > 180 && pixels[i * 4 + 1] > 180 && pixels[i * 4 + 2] < 80),
            "Cyan box exists in the tree but did not render.");
    }

    private static void TestLivePublicationIntegration()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-recent-recognizer-" + Guid.NewGuid().ToString("N"));
        var type = typeof(MainWindow).Assembly.GetType("HollowKnightVision.Windows.App.WindowsMenuStencilRecognizer")!;
        object? recognizer = null;
        object? Call(string name, params object?[] args) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(recognizer, args);
        void Drain() => Check(((Task)Call("DrainObjectInferenceAsync")!).Wait(TimeSpan.FromSeconds(3)), "Recognizer did not drain predictions.");
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            recognizer = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null,
                [Path.Combine(root, "labeling-v1", "examples"), "en"], null)!;
            var buffer = new RecentCapturedFrameBuffer(); var frame = Frame(); var second = Frame(2);
            buffer.Append(new(41, 1, frame, []));
            Action<LiveObjectInferencePublication> published = value => buffer.ReplaceDetections(value.Detections, 41, value.Frame.CaptureId);
            Func<IReadOnlyList<ObjectDetection>> infer = () =>
            {
                started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture not released.");
                return [new("game.playable-knight", new(.2, .4, .1, .2), .95f) { SourceFrameIdentifier = 1 }];
            };
            var analysis = Task.Run(() => Call("AnalyzeLiveWithPublication", frame, false, infer, published));
            Check(analysis.Wait(TimeSpan.FromSeconds(2)) && started.Wait(TimeSpan.FromSeconds(2)), "Live recognizer blocked on inference.");
            var snapshot = (LiveObjectDetectionSnapshot)Call("SnapshotObjectsAt", second)!;
            buffer.Append(new(41, 2, second, snapshot.Tracked));
            release.Set(); Drain();
            Check(buffer.Frames[0].Detections.Count == 0 && buffer.Frames[1].Detections.Single().SourceFrameIdentifier == 1,
                "Production recognizer did not update the exact buffered capture.");
            Call("Refresh", new object?[] { null });
            var third = Frame(3); buffer.Append(new(41, 3, third, []));
            Call("AnalyzeLiveWithPublication", third, false,
                (Func<IReadOnlyList<ObjectDetection>>)(() => [new("game.playable-knight", new(.2, .4, .1, .2), .95f) { SourceFrameIdentifier = 3 }]), published);
            Drain();
            Check(buffer.Frames.Count == 3 && buffer.Frames[0].Detections.Single().SourceFrameIdentifier == 3,
                "Reference/model refresh destroyed capture history or misidentified new results.");
        }
        finally
        {
            release.Set();
            if (recognizer is not null) { Call("StopTracking"); Drain(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
