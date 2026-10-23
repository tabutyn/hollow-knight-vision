using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class MenuIntegrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        TestSavedReferenceRecognition(); TestRenderer();
        Console.WriteLine("PASS Menu integration reads contract-valid PNG labels, invalidates old generations, and renders Mac diagnostic geometry");
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
            var game = analyze.Invoke(instance, [At(blank, 5), false, worldInference])!;
            Check((string?)game.GetType().GetProperty("Context")!.GetValue(game) == "game", "Production status ignores current gameplay object evidence.");
            // Stay beyond the 100ms untracked search cadence; a binary-double
            // timestamp exactly 0.1 apart can legitimately fall just below it.
            var firstMenu = analyze.Invoke(instance, [At(pixels, 5.15), false, inference])!;
            Check((string?)firstMenu.GetType().GetProperty("Context")!.GetValue(firstMenu) == "game"
                && !(bool)firstMenu.GetType().GetProperty("HasFreshGameplayEvidence")!.GetValue(firstMenu)!, "First menu candidate replaces Gameplay or enters the atlas.");
            Check(((string)firstMenu.GetType().GetProperty("Diagnostic")!.GetValue(firstMenu)!).Contains("raw=main-title/", StringComparison.Ordinal), "First menu fixture was not actually scanned.");
            var secondMenu = analyze.Invoke(instance, [At(pixels, 5.3), false, inference])!;
            Check((string?)secondMenu.GetType().GetProperty("Context")!.GetValue(secondMenu) == "main-title", "Stable real menu did not release Gameplay latch.");
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
