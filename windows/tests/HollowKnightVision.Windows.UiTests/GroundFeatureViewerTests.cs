using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            new(0, 0, 100, 60), [floor], [feature]);
        Check(drawing?.Project(feature.AtlasPosition) == new Point(120, 20)
            && MainWindow.GroundViewerKeyAt(atlas, new(120, 20)) == new GroundFeatureOverlayPresentation.FeatureKey(12, 1),
            "A retained patch must remain selectable in the same space as the merged terrain.");
        Console.WriteLine("PASS Ground Viewer paints paths and browses correctly aligned live patch evidence");
    }

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
