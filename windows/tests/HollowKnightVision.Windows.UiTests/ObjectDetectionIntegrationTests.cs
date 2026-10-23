using System.IO;
using Path = System.IO.Path;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

internal static class ObjectDetectionIntegrationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .000001, $"Expected {expected}, got {actual}.");
    internal static void Run()
    {
        TestRecognitionStreams(); TestAsynchronousRecognition(); TestOverlay(); TestFailureNotice();
        TestObjectDetectionIconSitsOutsideBoxLikeLabelMode();
        TestObjectDetectionIconMovesBelowTopEdgeBox();
        TestTallWindowRemovesTitleBarFromTopEdge();
        TestLegacyRegionBoxesAreNotPaintedOnLiveFrame();
        TestCaptureSourceRectExcludesTitleBarBeforeRendering();
        TestCaptureSourceRectDoesNotCropGenuinelyTallSource();
        TestCaptureConfigurationExcludesCursor();
        Console.WriteLine("PASS Live objects keep raw debug output separate, render pixel edges, and survive delayed/failed inference without blocking capture");
    }
    private static BgraFrame Frame(double time = 10)
    {
        var bytes = new byte[100 * 100 * 4];
        for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
        for (var y = 60; y <= 80; y++) for (var x = 20; x <= 40; x++)
        { var index = (y * 100 + x) * 4; bytes[index] = bytes[index + 1] = bytes[index + 2] = 255; }
        return new(100, 100, 400, bytes, (long)(time * 10), DateTimeOffset.UnixEpoch.AddSeconds(time));
    }
    private static ObjectDetection Knight(float confidence = .9f) => new("game.playable-knight", new(.22, .62, .16, .16), confidence)
        { SourceFrameIdentifier = 100, ModelVersion = new(3, 0) };

    private static void TestRecognitionStreams()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-object-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var type = typeof(MainWindow).Assembly.GetType("HollowKnightVision.Windows.App.WindowsMenuStencilRecognizer")!;
            var instance = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null,
                [Path.Combine(root, "labeling-v1", "examples"), "en"], null)!;
            var analyze = type.GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void Call(string name, params object?[] args) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
            LiveObjectDetectionSnapshot Snapshot(object observation) => (LiveObjectDetectionSnapshot)observation.GetType().GetProperty("Objects")!.GetValue(observation)!;
            object Observe(double time, IReadOnlyList<ObjectDetection> detections) =>
                analyze.Invoke(instance, [Frame(time), false, (Func<IReadOnlyList<ObjectDetection>>)(() => detections)])!;
            Call("ConfigureObjectDiagnostics", .5, true);
            var strong = Observe(10, [Knight(), new("enemies.crawlid", new(.5, .4, .1, .1), .9f)]);
            var snapshot = Snapshot(strong);
            Check((string?)strong.GetType().GetProperty("Context")!.GetValue(strong) == "game", "Tracked objects never reach live gameplay recognition.");
            Check(snapshot.Raw[0].Box == Knight().Box && snapshot.Tracked[0].Box != Knight().Box, "Raw and tracked streams still share the same geometry.");
            Check(snapshot.PixelDiagnostic is not null, "Pixel diagnostic was not produced by the live recognizer.");
            Check(snapshot.Masks[1].ClassIdentifier == "game.playable-knight" && snapshot.Masks[1].Box.Width > snapshot.Tracked[0].Box.Width,
                "Motion masks reused the lagging display box.");
            Call("ResetTracking"); Call("ConfigureObjectDiagnostics", .5, false);
            var quiet = Snapshot(Observe(10, [Knight(), new("enemies.crawlid", new(.5, .4, .1, .1), .9f)]));
            Check(quiet.PixelDiagnostic is null && quiet.Tracked.SequenceEqual(snapshot.Tracked) && quiet.Masks.SequenceEqual(snapshot.Masks),
                "Pixel debug toggle changed recognition or mapping evidence.");
            Call("ResetGameSession");
            var ordinary = Observe(11, [Knight(.7f)]);
            Check(Snapshot(ordinary).Tracked.Count == 0 && Snapshot(ordinary).Raw.Count == 1, "New game reused a previous confirmed track.");
            var confirmed = Snapshot(Observe(11.1, [Knight(.7f)]));
            Check(confirmed.Tracked.Count == 1, "Second agreeing inference did not confirm the candidate.");
            Call("Refresh", new object?[] { null });
            Check(Snapshot(Observe(12, [Knight(.7f)])).Tracked.Count == 0, "Reference/model refresh retained old tracks.");
            Call("StopTracking");
            Check(Snapshot(Observe(13, [Knight()])).Raw.Count == 0, "Stopped capture still produced object overlays.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void TestAsynchronousRecognition()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-live-object-integration-" + Guid.NewGuid().ToString("N"));
        var type = typeof(MainWindow).Assembly.GetType("HollowKnightVision.Windows.App.WindowsMenuStencilRecognizer")!;
        object? instance = null;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        void Call(string name, params object?[] args) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
        void Drain() => Check(((Task)type.GetMethod("DrainObjectInferenceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(instance, null)!).Wait(TimeSpan.FromSeconds(3)), "App did not drain object inference.");
        LiveObjectDetectionSnapshot Snapshot(object observation) => (LiveObjectDetectionSnapshot)observation.GetType().GetProperty("Objects")!.GetValue(observation)!;
        object Observe(double time, Func<IReadOnlyList<ObjectDetection>> inference) => type.GetMethod("AnalyzeLive", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(instance, [Frame(time), false, inference])!;
        try
        {
            instance = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null,
                [Path.Combine(root, "labeling-v1", "examples"), "en"], null)!;
            var first = Task.Run(() => Observe(10, () =>
            {
                started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Blocked fixture was not released.");
                return [Knight()];
            }));
            Check(first.Wait(TimeSpan.FromSeconds(2)) && started.Wait(TimeSpan.FromSeconds(2)), "Live analysis waits for the object model.");
            Check(Snapshot(first.Result).Raw.Count == 0, "Incomplete inference was published.");
            Check(Snapshot(Observe(10.02, () => throw new Exception("throttle failed"))).Raw.Count == 0,
                "Another preview frame could not be processed while the model was occupied.");
            Call("ResetTracking"); Call("SetObjectInferenceSuspended", true);
            release.Set(); Drain();
            Check(Snapshot(Observe(11, () => throw new Exception("label pause ignored"))).Raw.Count == 0,
                "Labeling retained old model boxes.");
            Call("SetObjectInferenceSuspended", false);
            Observe(12, () => throw new InvalidOperationException("isolated model failure")); Drain();
            var failed = Observe(12.01, () => throw new Exception("cadence was ignored"));
            var diagnostics = (LiveObjectInferenceDiagnostics)failed.GetType().GetProperty("ObjectDiagnostics")!.GetValue(failed)!;
            Check(diagnostics.Issue == "Inference failed: isolated model failure", "Live model failure was swallowed or stopped capture.");
            Observe(12.1, () => [Knight() with { SourceFrameIdentifier = 121 },
                new("enemies.crawlid", new(.5, .4, .1, .1), .9f) { SourceFrameIdentifier = 121 }]);
            Drain();
            var recovered = Observe(12.11, () => throw new Exception("too-soon inference ran"));
            Check((string?)recovered.GetType().GetProperty("Context")!.GetValue(recovered) == "game",
                "Accepted asynchronous evidence did not restore gameplay recognition.");
            Check(Snapshot(recovered).Raw.All(item => item.SourceFrameIdentifier == 121), "Old epoch evidence reappeared after failure recovery.");
            diagnostics = (LiveObjectInferenceDiagnostics)recovered.GetType().GetProperty("ObjectDiagnostics")!.GetValue(recovered)!;
            Check(diagnostics.Issue is null && diagnostics.SourceFrameIdentifier == 121, "Recovered inference retained the old failure indicator.");
            Call("StopTracking"); Drain();
            Check(Snapshot(Observe(13, () => throw new Exception("stopped inference ran"))).Raw.Count == 0,
                "Stopped recognizer still schedules work.");
        }
        finally
        {
            release.Set();
            if (instance is not null) { Call("StopTracking"); Drain(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void TestFailureNotice()
    {
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = xml.Descendants().Single(item => (string?)item.Attribute(x + "Name") == "ObjectInferenceIssueOverlay");
        var notice = (Border)System.Windows.Markup.XamlReader.Parse(source.ToString());
        var text = (TextBlock)notice.Child; var settings = new TextBlock(); var options = new StackPanel();
        var present = typeof(MainWindow).GetMethod("PresentObjectInferenceDiagnostics", BindingFlags.NonPublic | BindingFlags.Static)!;
        present.Invoke(null, [notice, text, settings, options, new LiveObjectInferenceDiagnostics(Issue: "Inference failed: test model")]);
        Check(notice.Visibility == Visibility.Visible && settings.Visibility == Visibility.Visible && text.Text == settings.Text,
            "Model failure has no visible non-modal notice.");
        Check(!notice.IsHitTestVisible && notice.HorizontalAlignment == HorizontalAlignment.Right
            && notice.VerticalAlignment == VerticalAlignment.Top, "Failure notice obstructs gameplay or differs from Mac placement.");
        present.Invoke(null, [notice, text, settings, options, new LiveObjectInferenceDiagnostics(42, .02, .001)]);
        Check(notice.Visibility == Visibility.Collapsed && settings.Visibility == Visibility.Collapsed && text.Text.Length == 0,
            "Recovered inference did not clear the warning.");
        Check(options.ToolTip is string tooltip && tooltip.Contains("source frame: 42", StringComparison.Ordinal), "Inference timing/source metadata is hidden.");
        present.Invoke(null, [notice, text, settings, options, new LiveObjectInferenceDiagnostics()]);
        Check(options.ToolTip is null && notice.Visibility == Visibility.Collapsed, "Stopped capture retained old diagnostics.");
    }

    private static void TestObjectDetectionIconSitsOutsideBoxLikeLabelMode()
    {
        var extent = new Rect(0, 0, 640, 360);
        var box = new Rect(120, 100, 80, 40);
        var destination = ObjectIconDestination(box, new Size(64, 32), extent)
            ?? throw new Exception("Production icon placement rejected a valid detection.");

        Check(!destination.IntersectsWith(box), "Object icon overlaps its detection box.");
        Near(120, destination.X);
        Near(76, destination.Y); // Top-origin mirror of Mac y=144.
        Near(40, destination.Width);
        Near(20, destination.Height);

        var detection = new ObjectDetection(
            "game.playable-knight",
            new(.1875, 100d / 360, .125, 40d / 360),
            .9f);
        var objects = new LiveObjectDetectionSnapshot([detection], [detection], [], null);
        var canvas = new Canvas { Width = 640, Height = 360 };
        DrawOverlay(canvas, objects, 640, 360, raw: false, pixels: false, IconAtlas(64, 32));
        var icon = canvas.Children.OfType<Image>().Single();
        Near(destination.X, Canvas.GetLeft(icon));
        Near(destination.Y, Canvas.GetTop(icon));
        Near(destination.Width, icon.Width);
        Near(destination.Height, icon.Height);
        Check(icon.Source is not null
              && !canvas.Children.OfType<TextBlock>().Any(),
            "Live overlay did not render the object-atlas icon or retained its text fallback.");
        AssertRenderedIconBounds(
            canvas,
            expected: new Int32Rect(120, 76, 40, 20),
            red: 223,
            green: 127,
            blue: 31);
    }

    private static void TestObjectDetectionIconMovesBelowTopEdgeBox()
    {
        var extent = new Rect(0, 0, 640, 360);
        // Core Image's Mac fixture y=330 maps to top-origin y=0.
        var box = new Rect(120, 0, 80, 30);
        var destination = ObjectIconDestination(box, new Size(32, 32), extent)
            ?? throw new Exception("Production icon placement rejected a top-edge detection.");

        Check(!destination.IntersectsWith(box), "Top-edge object icon overlaps its detection box.");
        Near(120, destination.X);
        Near(34, destination.Y);
        Near(20, destination.Width);
        Near(20, destination.Height);
        Near(box.Bottom + 4, destination.Top);
    }

    private static void TestTallWindowRemovesTitleBarFromTopEdge()
    {
        var capture = CaptureDecoratedClient(1600, 900);
        Check(capture.WindowHeight > capture.ClientHeight,
            "Decorated fixture did not contain non-client title-bar chrome.");
        Check(capture.Frame.Width == 1600 && capture.Frame.Height == 900,
            $"Client-only capture retained chrome: {capture.Frame.Width}x{capture.Frame.Height}.");
        CheckPixel(capture.Frame, 800, 0, red: 17, green: 31, blue: 47);
    }

    private static void TestLegacyRegionBoxesAreNotPaintedOnLiveFrame()
    {
        const int width = 100;
        const int height = 100;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        for (var offset = 3; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
            pixels[offset] = byte.MaxValue;
        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * BgraFrame.BytesPerPixel);
        source.Freeze();
        var regions = new SceneRegions(
            new StencilRect(45, 30, 10, 20),
            new StencilRect(50, 80, 20, 10),
            new StencilRect(10, 60, 15, 8),
            new StencilRect(5, 75, 10, 15));
        Check(regions.OmittedRects.Count == 4, "Legacy-region fixture lost one of its boxes.");

        var gameplay = new Image { Source = source, Width = width, Height = height };
        var overlay = new Canvas { Width = width, Height = height };
        PresentGameplayFrame(gameplay, overlay, source, regions);

        Check(ReferenceEquals(gameplay.Source, source),
            "Legacy scene regions replaced the clean live presentation source.");
        var regionOverlays = overlay.Children.OfType<Rectangle>().ToArray();
        Check(regionOverlays.Length == 4,
            "Legacy scene regions were not routed to the separate overlay.");
        var expected = regions.OmittedRects.ToArray();
        for (var index = 0; index < expected.Length; index++)
        {
            var rectangle = regionOverlays[index];
            Check(rectangle.Tag is StencilRect tagged && tagged == expected[index],
                "Legacy overlay lost its production region identity.");
            Near(expected[index].X, Canvas.GetLeft(rectangle));
            Near(expected[index].Y, Canvas.GetTop(rectangle));
            Near(expected[index].Width, rectangle.Width);
            Near(expected[index].Height, rectangle.Height);
        }
        var actual = new byte[pixels.Length];
        source.CopyPixels(actual, width * BgraFrame.BytesPerPixel, 0);
        Check(actual.SequenceEqual(pixels),
            "Legacy region rendering changed clean gameplay source bytes.");
    }

    private static void PresentGameplayFrame(
        Image gameplay,
        Canvas overlay,
        BitmapSource source,
        SceneRegions regions)
    {
        var method = typeof(MainWindow).GetMethod(
            "PresentGameplayFrameCore",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "PresentGameplayFrameCore");
        method.Invoke(null, [gameplay, overlay, source, regions]);
    }

    private static void TestCaptureSourceRectExcludesTitleBarBeforeRendering()
    {
        var capture = CaptureDecoratedClient(1600, 902);
        Check(capture.WindowHeight > capture.ClientHeight,
            "Capture fixture did not expose real Windows non-client chrome.");
        Check(capture.Frame.Width == 1600 && capture.Frame.Height == 902,
            "GDI capture did not return the complete client source rectangle.");
        CheckPixel(capture.Frame, 800, 0, red: 17, green: 31, blue: 47);
        var presentation = CpuBgraNormalizer.Normalize(capture.Frame);
        Check(presentation.Width == 640 && presentation.Height == 360,
            "Client-only source did not produce the canonical 640x360 presentation.");
    }

    private static void TestCaptureSourceRectDoesNotCropGenuinelyTallSource()
    {
        var capture = CaptureDecoratedClient(960, 1080);
        Check(capture.Frame.Width == 960 && capture.Frame.Height == 1080,
            $"Tall client was pre-cropped to {capture.Frame.Width}x{capture.Frame.Height}.");
        CheckPixel(capture.Frame, 480, 0, red: 17, green: 31, blue: 47);
        CheckPixel(capture.Frame, 480, 1079, red: 17, green: 31, blue: 47);
    }

    private static void TestCaptureConfigurationExcludesCursor()
    {
        const int width = 800;
        const int height = 450;
        const byte patchRed = 211;
        const byte patchGreen = 53;
        const byte patchBlue = 29;
        Check(GetCursorPos(out var originalCursor), "Could not save the pointer position for cursor-exclusion capture.");
        try
        {
            using var window = new NativeCaptureWindow(patterned: true);
            ResizeClient(window.Handle, width, height);
            PositionOnScreen(window.Handle);
            window.CompleteLayoutAndRender(width, height);
            Check(GetClientRect(window.Handle, out var client), "Could not inspect the cursor fixture client bounds.");
            Check(GetWindowRect(window.Handle, out var outer), "Could not inspect the cursor fixture window bounds.");
            var target = new HollowKnightWindow(
                window.Handle,
                Environment.ProcessId,
                "capture-test",
                "capture-test",
                client.Width,
                client.Height,
                false);
            Check(SetCursorPos(GetSystemMetrics(76) + 1, GetSystemMetrics(77) + 1),
                "Could not move the pointer away from the capture fixture for its client-owned baseline.");
            Pump();
            var capture = new GdiWindowCapture();
            BgraFrame? baseline = null;
            var patch = Int32Rect.Empty;
            for (var attempt = 0; attempt < 8 && patch.IsEmpty; attempt++)
            {
                window.CompleteLayoutAndRender(width, height);
                baseline = capture.Capture(target, 76);
                patch = FindPixelBounds(baseline, patchRed, patchGreen, patchBlue, byte.MaxValue);
                if (!patch.IsEmpty) break;
                Thread.Sleep(16);
                Pump();
            }
            var capturedBaseline = baseline
                ?? throw new Exception("Decorated client did not produce a baseline capture.");
            Check(patch.Width >= 96 && patch.Height >= 96,
                $"Decorated client did not render its deterministic contrasting patch: {patch}.");
            CheckPixelBgra(capturedBaseline, 8, 8, red: 17, green: 31, blue: 47, alpha: byte.MaxValue);

            var pointer = new NativePoint(
                patch.X + patch.Width / 2,
                patch.Y + patch.Height / 2);
            Check(ClientToScreen(window.Handle, ref pointer), "Could not project the client pointer target to the desktop.");
            Check(SetCursorPos(pointer.X, pointer.Y), "Could not place the real pointer over the capture fixture.");
            Pump();
            Check(GetCursorPos(out var placedPointer)
                  && placedPointer.X == pointer.X
                  && placedPointer.Y == pointer.Y,
                "The real pointer did not remain over the known contrasting client pixel.");
            var frame = capture.Capture(target, 77);

            Check(frame.Width == client.Width && frame.Height == client.Height
                  && outer.Width > client.Width && outer.Height > client.Height,
                "PrintWindow did not capture exactly the decorated window's client surface.");
            Check(frame.Stride == checked(frame.Width * BgraFrame.BytesPerPixel)
                  && BgraFrame.BytesPerPixel == 4,
                "Windows capture did not preserve packed 32-bit BGRA row-stride semantics.");
            var patchCenterX = patch.X + patch.Width / 2;
            var patchCenterY = patch.Y + patch.Height / 2;
            for (var y = patchCenterY - 24; y < patchCenterY + 40; y++)
            {
                for (var x = patchCenterX - 24; x < patchCenterX + 40; x++)
                {
                    CheckPixelBgra(frame, x, y, patchRed, patchGreen, patchBlue, byte.MaxValue);
                }
            }
            Check(frame.Pixels.SequenceEqual(capturedBaseline.Pixels),
                "Real pointer pixels were composited into the PrintWindow client capture.");

            // GDI capture is pixel-only and exposes no ScreenCaptureKit-style
            // audio stream/property to fabricate for this Windows adaptation.
            var normalized = CpuBgraNormalizer.Normalize(frame);
            Check(normalized.Width == FrameGeometry.ReferenceWidth
                  && normalized.Height == FrameGeometry.ReferenceHeight
                  && normalized.Width == 640,
                "Production capture normalization did not preserve the canonical 640-wide surface.");
            Check(normalized.Stride == checked(normalized.Width * BgraFrame.BytesPerPixel),
                "Normalized capture lost packed BGRA row-stride semantics.");
            Console.WriteLine("PASS testCaptureConfigurationExcludesCursor");
        }
        finally
        {
            _ = SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static void TestOverlay()
    {
        var frame = Frame(); var refined = LiveObjectPixelRefiner.Refine([Knight()], frame, .5);
        var objects = new LiveObjectDetectionSnapshot(refined.Detections, [Knight()], [], refined.DiagnosticImage);
        var canvas = new Canvas { Width = 100, Height = 100 };
        DrawOverlay(canvas, objects, 100, 100, raw: false, pixels: true, iconAtlas: null);
        var tracked = canvas.Children.OfType<Rectangle>().Single();
        Near(19, Canvas.GetLeft(tracked)); Near(59, Canvas.GetTop(tracked)); Near(23, tracked.Width);
        Check(canvas.Children.OfType<Image>().Count() == 1, "Detection Pixels is still only a rectangle fill.");
        var image = canvas.Children.OfType<Image>().Single();
        image.Measure(new Size(100, 100)); image.Arrange(new Rect(0, 0, 100, 100)); image.UpdateLayout();
        var target = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32); target.Render(image);
        var bytes = new byte[40000]; target.CopyPixels(bytes, 400, 0);
        var index = (60 * 100 + 20) * 4;
        Check(bytes[index + 3] == 128 && bytes[index + 2] == 128 && bytes[index] is >= 97 and <= 99,
            "Pixel-edge BGRA channels or alpha were rendered incorrectly.");
        Check(bytes[(70 * 100 + 30) * 4 + 3] == 0, "Interior was filled rather than displaying edge support.");
        canvas.Children.Clear();
        DrawOverlay(canvas, objects, 100, 100, raw: true, pixels: false, iconAtlas: null);
        var raw = canvas.Children.OfType<Rectangle>().Single();
        Near(22, Canvas.GetLeft(raw)); Near(62, Canvas.GetTop(raw)); Near(16, raw.Width);
        Check(((SolidColorBrush)raw.Stroke).Color == Colors.Gold && !canvas.Children.OfType<Image>().Any(), "Raw display/pixel visibility did not follow the selected options.");
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var slider = xml.Descendants().Single(item => (string?)item.Attribute(x + "Name") == "DetectionExtendSlider");
        Check((string?)slider.Attribute("Value") == "12", "Windows default detection extension differs from Mac's 12% per side.");
    }

    private static Rect? ObjectIconDestination(Rect box, Size iconSize, Rect extent)
    {
        var result = typeof(MainWindow).GetMethod(
                "ObjectIconDestination",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [box, iconSize, extent]);
        return result is Rect destination ? destination : null;
    }

    private static void DrawOverlay(
        Canvas canvas,
        LiveObjectDetectionSnapshot objects,
        double width,
        double height,
        bool raw,
        bool pixels,
        LabelingObjectAtlasSnapshot? iconAtlas) =>
        typeof(MainWindow).GetMethod(
                "DrawObjectDetectionOverlayCore",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [canvas, objects, width, height, raw, pixels, iconAtlas]);

    private static LabelingObjectAtlasSnapshot IconAtlas(int width, int height)
    {
        var stride = width * BgraFrame.BytesPerPixel;
        var pixels = new byte[stride * height];
        for (var offset = 0; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
        {
            pixels[offset] = 31;
            pixels[offset + 1] = 127;
            pixels[offset + 2] = 223;
            pixels[offset + 3] = byte.MaxValue;
        }
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        var constructor = typeof(LabelingObjectAtlasSnapshot).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (LabelingObjectAtlasSnapshot)constructor.Invoke([
            bitmap,
            new[] { "game.playable-knight" },
            new[] { new Int32Rect(0, 0, width, height) }
        ]);
    }

    private static void AssertRenderedIconBounds(
        Canvas canvas,
        Int32Rect expected,
        byte red,
        byte green,
        byte blue)
    {
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        canvas.UpdateLayout();
        var rendered = new RenderTargetBitmap(
            (int)canvas.Width,
            (int)canvas.Height,
            96,
            96,
            PixelFormats.Pbgra32);
        rendered.Render(canvas);
        var stride = rendered.PixelWidth * BgraFrame.BytesPerPixel;
        var pixels = new byte[stride * rendered.PixelHeight];
        rendered.CopyPixels(pixels, stride, 0);

        var minX = rendered.PixelWidth;
        var minY = rendered.PixelHeight;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < rendered.PixelHeight; y++)
        {
            for (var x = 0; x < rendered.PixelWidth; x++)
            {
                var offset = y * stride + x * BgraFrame.BytesPerPixel;
                if (pixels[offset] != blue
                    || pixels[offset + 1] != green
                    || pixels[offset + 2] != red
                    || pixels[offset + 3] != byte.MaxValue)
                {
                    continue;
                }
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        var actual = maxX < minX || maxY < minY
            ? Int32Rect.Empty
            : new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        Check(actual == expected,
            $"Rendered icon bounds {actual} did not fill exact aspect-fit destination {expected}.");
        Near(2, actual.Width / (double)actual.Height);
    }

    private static CapturedClient CaptureDecoratedClient(int width, int height)
    {
        using var window = new NativeCaptureWindow();
        ResizeClient(window.Handle, width, height);
        window.CompleteLayoutAndRender(width, height);
        Check(GetClientRect(window.Handle, out var client), "Could not inspect decorated client bounds.");
        Check(GetWindowRect(window.Handle, out var outer), "Could not inspect decorated window bounds.");
        Check(client.Width == width && client.Height == height,
            $"Decorated client did not settle at {width}x{height}: {client.Width}x{client.Height}.");
        var target = new HollowKnightWindow(
            window.Handle,
            Environment.ProcessId,
            "capture-test",
            "capture-test",
            client.Width,
            client.Height,
            false);
        return new(
            new GdiWindowCapture().Capture(target, 1),
            client.Width,
            client.Height,
            outer.Width,
            outer.Height);
    }

    private static void ResizeClient(nint handle, int width, int height)
    {
        const uint noMoveNoZOrderNoActivate = 0x0002 | 0x0004 | 0x0010;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Check(GetClientRect(handle, out var client), "Could not read client bounds before resize.");
            Check(GetWindowRect(handle, out var outer), "Could not read window bounds before resize.");
            var chromeWidth = outer.Width - client.Width;
            var chromeHeight = outer.Height - client.Height;
            Check(SetWindowPos(
                    handle,
                    nint.Zero,
                    0,
                    0,
                    checked(width + chromeWidth),
                    checked(height + chromeHeight),
                    noMoveNoZOrderNoActivate),
                "Could not resize decorated capture fixture.");
            Pump();
            Check(GetClientRect(handle, out client), "Could not read resized client bounds.");
            if (client.Width == width && client.Height == height) return;
        }
        throw new Exception($"Decorated capture fixture could not reach client size {width}x{height}.");
    }

    private static void PositionOnScreen(nint handle)
    {
        const uint noSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
        Check(GetWindowRect(handle, out var outer), "Could not read decorated fixture bounds before positioning.");
        var virtualLeft = GetSystemMetrics(76);
        var virtualTop = GetSystemMetrics(77);
        var virtualWidth = GetSystemMetrics(78);
        var virtualHeight = GetSystemMetrics(79);
        var x = virtualLeft + Math.Max(0, (virtualWidth - outer.Width) / 2);
        var y = virtualTop + Math.Max(0, (virtualHeight - outer.Height) / 2);
        Check(SetWindowPos(handle, nint.Zero, x, y, 0, 0, noSizeNoZOrderNoActivate),
            "Could not position the decorated cursor fixture on screen.");
        Pump();
    }

    private static void CheckPixel(
        BgraFrame frame,
        int x,
        int y,
        byte red,
        byte green,
        byte blue)
    {
        var offset = checked(y * frame.Stride + x * BgraFrame.BytesPerPixel);
        Check(frame.Pixels[offset] == blue
              && frame.Pixels[offset + 1] == green
              && frame.Pixels[offset + 2] == red,
            $"Captured client pixel ({x},{y}) included non-client chrome or stale desktop content: "
            + $"expected RGB=({red},{green},{blue}), actual RGB="
            + $"({frame.Pixels[offset + 2]},{frame.Pixels[offset + 1]},{frame.Pixels[offset]}).");
    }

    private static Int32Rect FindPixelBounds(
        BgraFrame frame,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var minX = frame.Width;
        var minY = frame.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var offset = checked(y * frame.Stride + x * BgraFrame.BytesPerPixel);
                if (frame.Pixels[offset] != blue
                    || frame.Pixels[offset + 1] != green
                    || frame.Pixels[offset + 2] != red
                    || frame.Pixels[offset + 3] != alpha)
                {
                    continue;
                }
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return maxX < minX || maxY < minY
            ? Int32Rect.Empty
            : new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static void CheckPixelBgra(
        BgraFrame frame,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var offset = checked(y * frame.Stride + x * BgraFrame.BytesPerPixel);
        Check(frame.Pixels[offset] == blue
              && frame.Pixels[offset + 1] == green
              && frame.Pixels[offset + 2] == red
              && frame.Pixels[offset + 3] == alpha,
            $"Captured BGRA pixel ({x},{y}) differs from its client-owned pattern: "
            + $"expected ({blue},{green},{red},{alpha}), actual "
            + $"({frame.Pixels[offset]},{frame.Pixels[offset + 1]},"
            + $"{frame.Pixels[offset + 2]},{frame.Pixels[offset + 3]}).");
    }

    private sealed record CapturedClient(
        BgraFrame Frame,
        int ClientWidth,
        int ClientHeight,
        int WindowWidth,
        int WindowHeight);

    private sealed class NativeCaptureWindow : IDisposable
    {
        private const int VisibleOverlappedWindow = unchecked((int)0x10CF0000);
        private const int ExtendedToolWindow = 0x00000080;
        private const int GetMinMaxInfo = 0x0024;
        private readonly HwndSource source;
        internal nint Handle => source.Handle;

        internal NativeCaptureWindow(bool patterned = false)
        {
            var parameters = new HwndSourceParameters("HKV capture test")
            {
                PositionX = -20000,
                PositionY = -20000,
                Width = 640,
                Height = 480,
                WindowStyle = VisibleOverlappedWindow,
                ExtendedWindowStyle = ExtendedToolWindow,
                UsesPerPixelOpacity = false
            };
            FrameworkElement root;
            if (patterned)
            {
                root = new Grid
                {
                    Background = new SolidColorBrush(Color.FromRgb(17, 31, 47)),
                    Children =
                    {
                        new Border
                        {
                            Width = 192,
                            Height = 192,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Background = new SolidColorBrush(Color.FromRgb(211, 53, 29))
                        }
                    }
                };
            }
            else
            {
                root = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(17, 31, 47))
                };
            }
            source = new HwndSource(parameters) { RootVisual = root };
            source.AddHook(AllowTallClient);
            source.CompositionTarget.BackgroundColor = Color.FromRgb(17, 31, 47);
            Pump();
        }

        internal void CompleteLayoutAndRender(int width, int height)
        {
            if (source.RootVisual is not FrameworkElement root)
                throw new InvalidOperationException("Capture fixture has no WPF root visual.");
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            root.InvalidateVisual();
            source.Dispatcher.Invoke(
                DispatcherPriority.Render,
                new Action(root.UpdateLayout));
            source.Dispatcher.Invoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => { }));
        }

        private static nint AllowTallClient(
            nint window,
            int message,
            nint wParam,
            nint lParam,
            ref bool handled)
        {
            if (message != GetMinMaxInfo) return nint.Zero;
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxSize = new NativePoint(4096, 4096);
            limits.MaxTrackSize = new NativePoint(4096, 4096);
            Marshal.StructureToPtr(limits, lParam, false);
            handled = true;
            return nint.Zero;
        }

        public void Dispose()
        {
            source.Dispose();
            Pump();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved;
        internal NativePoint MaxSize;
        internal NativePoint MaxPosition;
        internal NativePoint MinTrackSize;
        internal NativePoint MaxTrackSize;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

}
