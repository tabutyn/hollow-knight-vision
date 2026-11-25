using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

internal static class MenuIntegrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        TestSavedReferenceRecognition(); TestEditorDeletionRestoresCaptureGuidance(); TestRenderer();
        Console.WriteLine("PASS Menu integration reads contract-valid PNG labels, invalidates old generations, and renders Mac diagnostic geometry");
    }

    private static void TestEditorDeletionRestoresCaptureGuidance()
    {
        var root = Directory.CreateTempSubdirectory("hkv-stencil-editor-deletion-");
        try
        {
            var store = new LabelingExampleStore(System.IO.Path.Combine(root.FullName, "labeling-v1", "examples"));
            var frame = new BgraFrame(640, 360, 640 * 4, new byte[640 * 360 * 4], 1, DateTimeOffset.UnixEpoch);
            var requirements = StencilInitializationPolicy.Contexts(GameRuntimeMode.Normal)
                .Single(item => item.Identifier == "select-profile").Requirements;
            var annotations = requirements.SelectMany(item => Enumerable.Range(0, item.Count).Select(index =>
                new LabelingExampleAnnotation(Guid.NewGuid(), item.Identifier, .1 + index * .2, .1, .1, .1))).ToArray();
            var example = store.Save(frame, "select-profile", annotations);
            var window = new MainWindow(GameRuntimeMode.Normal, gamePath: null, root: root.FullName, documents: root.FullName);
            try
            {
                const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(MainWindow);
                var capture = (LabelCaptureSession)type.GetField("labelCapture", Instance)!.GetValue(window)!;
                capture.Apply(GamePauseState.Ready);
                Check(capture.Accept(frame, capture.Revision), "Could not seed a frame for the normal editor.");
                var selected = type.GetMethod("SelectWorkspace", Instance)!.Invoke(window, ["Label", false]);
                Check(selected is true, "Could not enter the label editor.");
                type.GetMethod("ApplySavedLabelExample", Instance)!.Invoke(window, [example, null]);
                type.GetField("initializationTargetContext", Instance)!.SetValue(window, "main-title");
                type.GetMethod("DeleteOpenedExample", Instance)!.Invoke(window, [example]);
                var guidance = (TextBlock)window.FindName("StencilGuidanceText");
                Check(store.Load().Count == 0 && guidance.Visibility == Visibility.Visible
                    && guidance.Text.Contains("Capture with L only when Clear Save is visible.", StringComparison.Ordinal),
                    $"Deleting the Select Profile screenshot did not restore capture guidance. Blue text was {guidance.Text}");
            }
            finally
            {
                const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(MainWindow);
                type.GetField("isClosing", Instance)!.SetValue(window, true);
                var closing = (Task)type.GetMethod("PrepareShutdownAsync", Instance)!.Invoke(window, null)!;
                var deadline = Stopwatch.StartNew();
                while (!closing.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15))
                {
                    Program.Pump();
                    Thread.Sleep(5);
                }
                Check(closing.IsCompleted, "The stencil editor fixture did not finish shutdown.");
                closing.GetAwaiter().GetResult();
            }
        }
        finally { root.Delete(recursive: true); }
    }
    private static void TestSavedReferenceRecognition()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hkv-menu-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var examples = System.IO.Path.Combine(root, "labeling-v1", "examples"); var store = new LabelingExampleStore(examples);
            var pixels = new byte[640 * 360 * 4];
            for (var p = 0; p < 640 * 360; p++) { pixels[p * 4] = 62; pixels[p * 4 + 1] = 24; pixels[p * 4 + 2] = 12; pixels[p * 4 + 3] = 255; }
            var annotations = new List<LabelingExampleAnnotation>();
            void Paint(string id, int x, int y, int width, int height, int seed)
            {
                annotations.Add(new(Guid.NewGuid(), id, x / 640d, y / 360d, width / 640d, height / 360d));
                for (var dy = 0; dy < height; dy++) for (var dx = 0; dx < width; dx++)
                {
                    var p = ((y + dy) * 640 + x + dx) * 4;
                    var level = (byte)(140 + (dx * 17 + dy * 31 + seed * 23) % 114);
                    pixels[p] = pixels[p + 1] = pixels[p + 2] = level;
                }
            }
            var labels = LabelingContractCatalog.ScreenContexts.Single(item => item.Identifier == "main-title").Labels;
            var row = 0;
            foreach (var label in labels)
                if (label.Identifier.EndsWith("hollow-knight-logo", StringComparison.Ordinal)) Paint(label.Identifier, 100, 10, 440, 60, 8);
                else if (label.Identifier != "shared.select-decoration") { Paint(label.Identifier, 260, 130 + row * 30, 120, 8, row); row++; }
            Paint("shared.select-decoration", 240, 130, 8, 8, 17); Paint("shared.select-decoration", 392, 130, 8, 8, 21);
            var frame = new BgraFrame(640, 360, 640 * 4, pixels, 1, DateTimeOffset.UnixEpoch.AddSeconds(1));
            var saved = store.Save(frame, "main-title", annotations, languageIdentifier: "en");
            // The synthetic layout deliberately differs from the game's real
            // deployed row positions. Use an empty isolated calibration.
            new MenuStencilCalibration(1, 640, 360, DateTimeOffset.UnixEpoch, []).Write(MenuStencilCalibration.WritablePath(examples));
            Check(LabelingContractEvaluator.AcceptsMenuExample(saved), "Synthetic production reference violates menu contract.");
            var type = typeof(MainWindow).Assembly.GetType("HollowKnightVision.Windows.App.WindowsMenuStencilRecognizer")!;
            var instance = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, [examples, "en"], null)!;
            var observe = type.GetMethod("Observe", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = observe.Invoke(instance, [frame, false])!;
            var menu = (MenuStencilResult?)result.GetType().GetProperty("Menu")!.GetValue(result)
                ?? throw new Exception("Production recognizer returned no menu observation.");
            var generation = (long)result.GetType().GetProperty("Generation")!.GetValue(result)!;
            Check(menu.IsMatch && menu.ContextIdentifier == "main-title", "Production recognizer did not classify saved PNG through the full tracker.");
            Check(menu.SelectorSearchRegions.Count > 0 && menu.Anchors.Count >= 5, "Production recognizer still exposes old fixed bright patches.");
            Check(menu.SelectedPairAccepted && menu.SelectedOption == "Start Game", $"Production menu integration lost the selected arrow pair: {menu.SelectedOption}, candidates={menu.SelectorCandidates.Count}.");
            var analyze = type.GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var inferenceCalls = 0;
            Func<IReadOnlyList<ObjectDetection>> inference = () => { inferenceCalls++; return []; };
            BgraFrame At(byte[] buffer, double time) => new(640, 360, 640 * 4, buffer, (long)(time * 10), DateTimeOffset.UnixEpoch.AddSeconds(time));
            var analyzeLive = type.GetMethod("AnalyzeLive", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var repeated = analyzeLive.Invoke(instance, [At(pixels, 2), false, inference])!;
            Check(inferenceCalls == 0, "Matched menu still invokes the slower object detector.");
            Check(((string)repeated.GetType().GetProperty("Diagnostic")!.GetValue(repeated)!).Contains("admitted=true", StringComparison.Ordinal), "Live status omits menu admission diagnostic.");
            var blank = new byte[pixels.Length];
            analyze.Invoke(instance, [At(blank, 3), false, inference]); // One-frame probe grace.
            var missing = analyze.Invoke(instance, [At(blank, 3.1), false, inference])!;
            var held = (LiveGameStatus?)missing.GetType().GetProperty("Status")!.GetValue(missing);
            Check(held?.SelectedMenuOption == "Start Game" && missing.GetType().GetProperty("Menu")!.GetValue(missing) is null,
                "Menu animation grace lost selection or reused stale overlay pixels.");
            var expired = analyze.Invoke(instance, [At(blank, 4), false, inference])!;
            Check(expired.GetType().GetProperty("Context")!.GetValue(expired) is null && inferenceCalls > 0, "Unknown frame did not expire menu state/restart inference.");
            Func<IReadOnlyList<ObjectDetection>> worldInference = () => [new("game.playable-knight", new(.2, .2, .1, .1), .9f), new("enemies.crawlid", new(.4, .4, .1, .1), .9f)];
            var objectsOnly = analyze.Invoke(instance, [At(blank, 5), false, worldInference])!;
            Check(objectsOnly.GetType().GetProperty("Context")!.GetValue(objectsOnly) is null,
                "The production recognizer must wait for fresh HUD evidence.");
            var hud = HudStencilTests.SaveGameplayReference(store);
            type.GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, ["en"]);
            var gameplayFrame = new BgraFrame(hud.Width, hud.Height, hud.Stride, hud.Pixels, 51, DateTimeOffset.UnixEpoch.AddSeconds(5.1));
            var game = analyze.Invoke(instance, [gameplayFrame, false, worldInference])!;
            generation = (long)game.GetType().GetProperty("Generation")!.GetValue(game)!;
            Check((bool)game.GetType().GetProperty("HasFreshGameplayEvidence")!.GetValue(game)!,
                "The production recognizer should enter Gameplay when fresh Knight and HUD evidence agree.");
            // These menu frames are beyond the HUD hold and spaced beyond the search cadence.
            var firstMenu = analyze.Invoke(instance, [At(pixels, 5.6), false, inference])!;
            Check((string?)firstMenu.GetType().GetProperty("Context")!.GetValue(firstMenu) == "game"
                && !(bool)firstMenu.GetType().GetProperty("HasFreshGameplayEvidence")!.GetValue(firstMenu)!, "First menu candidate replaces Gameplay or enters the atlas.");
            Check(((string)firstMenu.GetType().GetProperty("Diagnostic")!.GetValue(firstMenu)!).Contains("raw=main-title/", StringComparison.Ordinal), "First menu fixture was not actually scanned.");
            var secondMenu = analyze.Invoke(instance, [At(pixels, 5.75), false, inference])!;
            Check((string?)secondMenu.GetType().GetProperty("Context")!.GetValue(secondMenu) == "main-title", "Stable real menu did not release Gameplay latch.");
            VerifyLiveCalibration(root, examples, frame);
            var current = type.GetMethod("IsCurrent", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Check((bool)current.Invoke(instance, [generation])!, "Fresh menu observation has wrong generation.");
            type.GetMethod("ResetTracking", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null);
            Check(!(bool)current.Invoke(instance, [generation])!, "Capture reset retained stale render evidence.");
            var afterReset = observe.Invoke(instance, [frame, false])!;
            Check((string?)afterReset.GetType().GetProperty("Context")!.GetValue(afterReset) == "main-title", "Capture clock reset prevented immediate menu reacquisition.");
            type.GetMethod("StopTracking", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null);
            var stopped = observe.Invoke(instance, [frame, false])!;
            Check(stopped.GetType().GetProperty("Menu")!.GetValue(stopped) is null, "Late captured frame restarted the stopped menu worker.");
            type.GetMethod("ResetTracking", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null);
            // Replace a complete draft with a ready-but-incomplete screenshot.
            Directory.Delete(saved.DirectoryPath, true);
            store.Save(frame, "main-title", annotations.Take(1).ToArray(), completion: LabelingExampleCompletion.Ready, languageIdentifier: "en");
            type.GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, [null]);
            var invalid = observe.Invoke(instance, [frame, false])!;
            Check(invalid.GetType().GetProperty("Context")!.GetValue(invalid) is null, "Catalog refresh retained deleted reference or admitted incomplete Ready screenshot.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void VerifyLiveCalibration(string root, string examples, BgraFrame reference)
    {
        var pixels = reference.Pixels.ToArray();
        // Mark the live frame outside the menu's stencil region so the saved
        // capture can be distinguished from the reference used to find it.
        const int markerX = 48, markerY = 310;
        for (var y = markerY; y < markerY + 20; y++)
            for (var x = markerX; x < markerX + 20; x++)
            {
                var index = y * reference.Stride + x * 4;
                pixels[index] = 29;
                pixels[index + 1] = 78;
                pixels[index + 2] = 209;
            }
        var image = new Image
        {
            Source = BitmapSource.Create(reference.Width, reference.Height, 96, 96,
                PixelFormats.Bgra32, null, pixels, reference.Stride),
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        var window = Program.Offscreen(image, reference.Width, reference.Height);
        window.Show();
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            window.Width = reference.Width / dpi.DpiScaleX;
            window.Height = reference.Height / dpi.DpiScaleY;
            window.UpdateLayout();
            Program.Pump();
            var target = new HollowKnightWindow(new WindowInteropHelper(window).Handle,
                Environment.ProcessId, "capture-test", "capture-test", reference.Width, reference.Height, false);
            var statusCommand = MenuCalibrationCapture.Parse("menu-calibration-status")!;
            var store = new MenuStencilLiveCaptureStore(MenuStencilLiveCaptureStore.RootForExamples(examples));
            var wait = Stopwatch.StartNew();
            MenuCalibrationCapture.Reply status;
            do
            {
                status = statusCommand.CaptureLive(target, root);
                if (status.Ok) break;
                Thread.Sleep(16);
                Program.Pump();
            } while (wait.Elapsed < TimeSpan.FromSeconds(4));
            Check(status is { Ok: true, ContextIdentifier: "main-title", SelectedIdentifier: "main-title.start-game",
                CapturePath: null }, $"Native menu calibration failed to recognize Start Game: {status.Serialize()}");
            Check(store.Load().Count == 0, "Calibration status wrote a capture.");

            var captureCommand = MenuCalibrationCapture.Parse("menu-calibration-capture")!;
            var capture = captureCommand.CaptureLive(target, root);
            Check(capture is { Ok: true, SelectedIdentifier: "main-title.start-game", CapturePath: not null },
                $"Native menu frame could not be saved: {capture.Serialize()}");
            var saved = store.Load().Single();
            Check(saved.ImagePath == capture.CapturePath && saved.Capture is { ImageWidth: 640, ImageHeight: 360 },
                "The saved native frame does not use the normalized client dimensions.");
            BitmapSource bitmap = BitmapDecoder.Create(new Uri(saved.ImagePath), BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            if (bitmap.Format != PixelFormats.Bgra32)
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var marker = new byte[4];
            bitmap.CopyPixels(new Int32Rect(markerX + 10, markerY + 10, 1, 1), marker, 4, 0);
            Check(marker[0] == 29 && marker[1] == 78 && marker[2] == 209,
                "The saved pixels did not come from the live client window.");
            Check(captureCommand.CaptureLive(target with { IsMinimized = true }, root) is { Ok: false, CapturePath: null }
                && store.Load().Count == 1, "A minimized window republished a prior menu screenshot.");
            Console.WriteLine("PASS native menu calibration recognized a captured window and saved the live pixels");
        }
        finally { window.Close(); }
    }

    private static void TestRenderer()
    {
        var reference = new ImageStencilReference(10, 6, Enumerable.Repeat((byte)255, 10 * 6 * 3).ToArray(),
            Enumerable.Range(0, 60).Select(i => (byte)(i % 10 is >= 3 and <= 6 ? 255 : 0)).ToArray());
        var anchor = new MenuStencilMatch("label", "Label", new(20, 20, 40, 24), .8, reference);
        var result = new MenuStencilResult("main-title", true, .8, "Start Game", [anchor],
            [new("left", "Left", new(8, 24, 8, 8), .23, null), new("right", "Right", new(64, 24, 8, 8), .9, null)],
            [new("start", "Start Game", MenuSelectorSide.Left, new(4, 20, 16, 16), true)], 0, 40, 1, MenuStencilPhase.Tracking, 1, 1, "en");
        var canvas = new Canvas { Width = 100, Height = 64 };
        typeof(MainWindow).GetMethod("DrawMenuStencil", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [canvas, result]);
        var shapes = canvas.Children.OfType<Rectangle>().ToArray();
        var dashed = shapes.Single(item => item.StrokeDashArray?.Count == 2);
        Check(dashed.StrokeDashArray.SequenceEqual(new[] { 4d, 3d }) && Canvas.GetLeft(dashed) == 4, "Menu search region dash/coordinate mismatch.");
        var accepted = shapes.Where(item => item.StrokeThickness == 2).ToArray();
        Check(accepted.Length == 2 && accepted.All(item => ((SolidColorBrush)item.Stroke).Color == Color.FromRgb(255, 204, 13)), "Faded accepted pair was rendered as a rejected side.");
        var green = shapes.Single(item => item.Stroke is SolidColorBrush brush && brush.Color.G == 255);
        Check(green.Width == reference.VerifiedBounds(anchor.Rect).Width && Canvas.GetLeft(green) == reference.VerifiedBounds(anchor.Rect).X,
            "Green outline used annotation bounds instead of verified stencil pixels.");
        Check(canvas.Children.OfType<Image>().Single().Opacity == .5, "Menu anchor reference pixels are missing.");
        canvas.Measure(new Size(100, 64)); canvas.Arrange(new Rect(0, 0, 100, 64)); canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(100, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
        var output = new byte[100 * 64 * 4]; bitmap.CopyPixels(output, 100 * 4, 0);
        Check(output[(30 * 100 + 40) * 4 + 3] > 0 && output[(60 * 100 + 95) * 4 + 3] == 0, "Menu overlay paints wrong pixels.");
    }
}
