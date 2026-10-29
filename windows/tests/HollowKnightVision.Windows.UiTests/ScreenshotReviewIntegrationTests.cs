using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using Path = System.IO.Path;

internal static class ScreenshotReviewIntegrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .001, $"Expected {expected}; got {actual}.");
    private static LabelingExampleAnnotation Box(string id, double x = .1, bool negative = false) => new(Guid.NewGuid(), id, x, .2, .2, .3, negative);
    private static LabelingExampleAnnotation[] ValidBoxes => [Box("game.playable-knight"), Box("game.mana", .4), Box("game.health", .7)];
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hkv-review-ui-" + Guid.NewGuid().ToString("N"));
        public LabelingExampleStore Store { get; }
        private int tick;
        public Fixture() { Store = new(Path.Combine(Root, "examples"), now: () => DateTimeOffset.UnixEpoch.AddSeconds(++tick)); }
        public SavedLabelingExample Save(params LabelingExampleAnnotation[] annotations)
        {
            var bytes = new byte[160 * 90 * 4];
            for (var y = 0; y < 90; y++) for (var x = 0; x < 160; x++)
            { var i = (y * 160 + x) * 4; bytes[i] = (byte)(40 + x / 3); bytes[i + 1] = (byte)(25 + y / 2); bytes[i + 2] = 20; bytes[i + 3] = 255; }
            return Store.Save(new(160, 90, 640, bytes, 0, DateTimeOffset.UnixEpoch), "game", annotations);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static Window Show(UIElement content, double width = 1100, double height = 700)
    {
        var window = new Window { Content = content, Width = width, Height = height, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = xml.Descendants(ns + "Style").Single(item => (string?)item.Attribute("TargetType") == "Button" && item.Attribute(x + "Key") is null);
        window.Resources[typeof(Button)] = (Style)System.Windows.Markup.XamlReader.Parse(style.ToString());
        window.Show(); Pump(); return window;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Children<T>(child)) yield return nested; }
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Children<T>(root).Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); }
    private static void Render(FrameworkElement element, string filename)
    {
        element.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, filename)); encoder.Save(stream);
    }
    public static void Run()
    {
        TestNavigationAndVirtualization(); TestContractsAndDeletion(); TestWriteAndDeleteFailures(); TestReviewImage();
        Console.WriteLine("PASS Screenshot review navigation, virtualization, exemptions, confirmed deletion, failed writes and colored previews");
    }

    private static void TestNavigationAndVirtualization()
    {
        using var fixture = new Fixture(); var examples = Enumerable.Range(0, 60).Select(_ => fixture.Save(ValidBoxes)).ToArray();
        var session = new LabelingScreenshotReviewSession(fixture.Store); session.Reload(); SavedLabelingExample? opened = null;
        var panel = new ScreenshotReviewPanel(session, key => key, example => opened = example, _ => false, () => { }, _ => { });
        panel.Refresh("game.playable-knight"); var window = Show(panel);
        try
        {
            var list = Find<ListBox>(panel, "ReviewScreenshots");
            Check(list.Items.Count == 60 && Children<ScreenshotReviewThumbnail>(list).Count() is > 0 and < 20, "Screenshot list decoded all images instead of virtualizing.");
            panel.Move(int.MaxValue); Pump();
            Check(session.Selected?.Id == examples[^1].Id && ((ScreenshotReviewRow)list.SelectedItem).Example.Id == examples[^1].Id, "Navigation did not clamp/scroll to the last screenshot.");
            Check(Children<ScreenshotReviewThumbnail>(list).Any(item => ((ScreenshotReviewRow)item.DataContext).Example.Id == examples[^1].Id), "Selected screenshot was not realized after scrolling.");
            Click(Find<Button>(panel, "ReviewOpenScreenshot")); Check(opened?.Id == examples[^1].Id, "Repair opened a different screenshot.");
            panel.Move(int.MinValue); Pump(); Check(session.Selected?.Id == examples[0].Id, "Navigation did not clamp to the first screenshot.");
            list.SelectedItem = list.Items[2]; Pump(); Check(session.Selected?.Id == examples[2].Id, "Mouse/list selection did not update the session.");
            session.Reload(); panel.Refresh("game.mana"); Pump(); Check(session.Selected?.Id == examples[2].Id, "Reload lost the selected screenshot.");
            var preview = Children<ReviewedLabelImage>(panel).Single();
            Check(Children<Rectangle>(preview.OpenButton).Count(rect => rect.StrokeThickness == 3) == 1, "Changed object selection did not update box emphasis.");
            Render(panel, "screenshot-review-preview.png");
        }
        finally { window.Close(); }
    }

    private static void TestContractsAndDeletion()
    {
        using var fixture = new Fixture(); var good = fixture.Save(ValidBoxes); var bad = fixture.Save(Box("game.playable-knight"), Box("game.playable-knight"), Box("world.sign"), Box("world.sign"));
        var session = new LabelingScreenshotReviewSession(fixture.Store); session.Reload(); session.Select(bad.Id);
        var allowDelete = false; var confirmations = 0; var changes = 0; LabelingScreenshotDeletionResult? deleted = null;
        var panel = new ScreenshotReviewPanel(session, key => key, _ => { }, _ => { confirmations++; return allowDelete; }, () => changes++, result => deleted = result);
        panel.Refresh("game.playable-knight"); var window = Show(panel);
        try
        {
            var failed = session.Evaluations(bad.Id).Where(item => item.IsBreach).Select(item => item.Id).ToArray();
            foreach (var rule in failed) Click(Find<Button>(panel, "Contract_" + rule));
            Check(changes == failed.Length && session.CanTrainSelectedClass("game.playable-knight"), "Contract controls did not update training admission.");
            Check(new LabelingContractExemptionStore().Load(bad).SetEquals(failed), "Exemption buttons did not persist their checks.");
            var selectedThumbnail = Children<ScreenshotReviewThumbnail>(panel).Single(item => ((ScreenshotReviewRow)item.DataContext).Example.Id == bad.Id);
            Check(((SolidColorBrush)selectedThumbnail.BorderBrush).Color == Colors.DodgerBlue, "Exempt screenshot lost its blue outline.");
            Click(Find<Button>(panel, "Contract_" + failed[0]));
            Check(((SolidColorBrush)selectedThumbnail.BorderBrush).Color == Colors.IndianRed && !session.CanTrainSelectedClass("game.playable-knight"), "Removing exemption did not restore breach/training block.");
            Render(panel, "screenshot-review-breach.png");
            Click(Find<Button>(panel, "ReviewDeleteScreenshot"));
            Check(confirmations == 1 && deleted is null && File.Exists(bad.ImagePath), "Cancelling confirmation deleted a screenshot.");
            allowDelete = true; Click(Find<Button>(panel, "ReviewDeleteScreenshot"));
            Check(deleted?.Deleted.SequenceEqual(new[] { bad.Id }) == true && !Directory.Exists(bad.DirectoryPath)
                && File.Exists(good.ImagePath) && session.Selected?.Id == good.Id, "Confirmed deletion did not isolate and reconcile its target.");
            Check(Children<Button>(panel).Where(item => AutomationProperties.GetAutomationId(item).StartsWith("Contract_")).All(item => !item.IsEnabled), "Satisfied checks can be newly exempted.");
            var satisfied = Children<Button>(panel).First(item => AutomationProperties.GetAutomationId(item).StartsWith("Contract_"));
            var chrome = (Border)satisfied.Template.FindName("ContractTermBorder", satisfied);
            Check(chrome.Opacity == 1 && ((SolidColorBrush)chrome.Background).Color == Colors.Transparent, "Satisfied status was faded or used unreadable white disabled chrome.");
            Click(Find<Button>(panel, "ReviewDeleteScreenshot"));
            Check(session.Selected is null && !Find<Button>(panel, "ReviewDeleteScreenshot").IsEnabled && !Find<Button>(panel, "ReviewOpenScreenshot").IsEnabled, "Empty review retained enabled actions.");
        }
        finally { window.Close(); }
    }

    private static void TestWriteAndDeleteFailures()
    {
        using var fixture = new Fixture(); var bad = fixture.Save(Box("game.playable-knight"), Box("game.playable-knight"), Box("world.sign"), Box("world.sign"));
        var session = new LabelingScreenshotReviewSession(fixture.Store); session.Reload(); var changes = 0;
        var panel = new ScreenshotReviewPanel(session, key => key, _ => { }, _ => true, () => changes++, _ => { });
        panel.Refresh("game.playable-knight"); var window = Show(panel);
        try
        {
            var rule = session.Evaluations(bad.Id).First(item => item.IsBreach).Id;
            var blocked = Path.Combine(bad.DirectoryPath, LabelingContractExemptionStore.Filename); Directory.CreateDirectory(blocked);
            Click(Find<Button>(panel, "Contract_" + rule));
            Check(Find<TextBlock>(panel, "ReviewError").Text.StartsWith("Exemption failed:") && changes == 0 && session.BreachCount == 1,
                "Failed writes were hidden or allowed training.");
            Directory.Delete(blocked); Click(Find<Button>(panel, "Contract_" + rule));
            Check(Find<TextBlock>(panel, "ReviewError").Text == "" && changes == 1, "Retry did not clear the obsolete error.");
            File.Delete(bad.ImagePath); panel.Refresh("game.playable-knight"); Pump();
            Check(!Find<Button>(panel, "ReviewOpenScreenshot").IsEnabled && Find<Button>(panel, "ReviewDeleteScreenshot").IsEnabled, "Missing image cannot be managed without opening it.");
            var path = Path.Combine(bad.DirectoryPath, LabelingExampleStore.ManifestFilename);
            var manifest = JsonNode.Parse(File.ReadAllText(path))!; manifest["id"] = Guid.NewGuid(); File.WriteAllText(path, manifest.ToJsonString());
            Click(Find<Button>(panel, "ReviewDeleteScreenshot"));
            Check(Find<TextBlock>(panel, "ReviewError").Text.StartsWith("Delete screenshot failed:")
                && session.Selected?.Id == bad.Id && Directory.Exists(bad.DirectoryPath), "Changed screenshot was deleted or its error disappeared.");
        }
        finally { window.Close(); }
    }

    private static void TestReviewImage()
    {
        using var fixture = new Fixture(); var saved = fixture.Save(Box("main-title.options"), Box("custom.sign", .4), Box("custom.sign", .7, true));
        var extra = saved with { Manifest = saved.Manifest with { Annotations = [.. saved.Manifest.Annotations,
            Box("shared.options") with { X = -.2, Width = .4 }, Box("shared.options") with { X = double.NaN }, Box("shared.options") with { X = 1.2 }] } };
        var review = new ReviewedLabelImage(key => key, _ => { }, "PreviewTest");
        review.SetExample(extra, "main-title.options", ["custom.sign"], reviewPredictions: [new("custom.sign", .6, .7, -.2, -.2, .9), new("bad", 2, 0, .2, .2, .8)]);
        var window = Show(review, 800, 400);
        try
        {
            var rectangles = Children<Rectangle>(review.OpenButton).ToArray();
            Check(rectangles.Length == 5, $"Preview failed to clip/standardize or reject invalid annotations/predictions: {rectangles.Length} rectangles.");
            Check(rectangles[0].StrokeThickness == 3 && rectangles[1].StrokeThickness == 2 && rectangles[2].Stroke == Brushes.HotPink,
                "Canonical selection or negative-box styling differs from Mac review.");
            var expected = new LabelingVisualIdentity(["custom.sign"]).Rgb("shared.options");
            var actual = ((SolidColorBrush)rectangles[0].Stroke).Color;
            Check(actual.R == Math.Round(expected.Red * 255) && actual.G == Math.Round(expected.Green * 255) && actual.B == Math.Round(expected.Blue * 255), "Review color does not match canonical palette.");
            Near(0, Canvas.GetLeft(rectangles[3])); Near(130, rectangles[3].Width);
            Check(rectangles[^1].Stroke == Brushes.Yellow && rectangles[^1].StrokeDashArray.SequenceEqual(new double[] { 3, 2 }), "Predictions lost their 6/4 pixel dashed stroke.");
            var names = Children<TextBlock>(review).Select(item => item.Text).ToArray();
            Check(names.Count(name => name == "Options") == 1 && names.Count(name => name == "custom.sign") == 1, "Legend duplicated canonical aliases or used raw known identifiers.");
            var atlas = new LabelingObjectAtlasStore(fixture.Root).LoadOrBuild(new([saved], ["shared.options", "custom.sign"], new Dictionary<string, Guid>()));
            review.SetAtlas(atlas); Pump();
            Check(Children<Image>(review).Count() == 3, "Legend did not display cached reference icons.");
            Render(review, "screenshot-review-colors.png");
        }
        finally { window.Close(); }
    }
}
