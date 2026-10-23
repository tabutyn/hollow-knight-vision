using System.IO;
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

internal static class ModelSummaryIntegrationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static LabelingExampleAnnotation Box(string id, double x = .1, bool negative = false) => new(Guid.NewGuid(), id, x, .2, .12, .25, negative);
    private static LabelingReviewBox Prediction(string id, double x = .1, double confidence = .9) => new(id, x, .2, .12, .25, confidence);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hkv-summary-ui-" + Guid.NewGuid().ToString("N"));
        public LabelingExampleStore Store => new(Path.Combine(Root, "examples"));
        public SavedLabelingExample Save(params LabelingExampleAnnotation[] extra)
        {
            var bytes = new byte[320 * 180 * 4];
            for (var y = 0; y < 180; y++) for (var x = 0; x < 320; x++)
            { var offset = (y * 320 + x) * 4; bytes[offset] = (byte)(30 + x / 3); bytes[offset + 1] = (byte)(15 + y / 2); bytes[offset + 2] = 15; bytes[offset + 3] = 255; }
            return Store.Save(new(320, 180, 1280, bytes, 0, DateTimeOffset.UnixEpoch), "game", [Box("game.playable-knight", .4), Box("game.mana", .03), Box("game.health", .17), .. extra]);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static LabelingModelCandidate Candidate(SavedLabelingExample first, SavedLabelingExample last)
    {
        var review = new LabelingPredictionReview([
            new(last.Id, "last.png", "validation", [], [Prediction("enemies.crawlid", .6), Prediction("enemies.crawlid", .2, .2), Prediction("game.mana")]),
            new(first.Id, "first.png", "training", [Prediction("enemies.crawlid", .1), Prediction("enemies.crawlid", .7)], [Prediction("enemies.crawlid", .1)])], new(.5, .5, 0, 0, 0, 0, 0, 0));
        return new("test-run", "test-dataset", new() { Id = Guid.NewGuid(), DatasetIdentifier = Guid.NewGuid(), CompletedAt = DateTimeOffset.UnixEpoch,
            ClassIdentifier = LabelingModelVersionStore.SharedObjectModel, ModelFilename = "model.onnx", ClassIdentifiers = ["enemies.crawlid", "game.playable-knight"] }, review);
    }
    private static Window Show(UIElement content)
    {
        var window = new Window { Content = content, Width = 1300, Height = 680, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"; System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = xml.Descendants(ns + "Style").Single(item => (string?)item.Attribute("TargetType") == "Button" && item.Attribute(x + "Key") is null);
        window.Resources[typeof(Button)] = (Style)System.Windows.Markup.XamlReader.Parse(style.ToString()); window.Show(); Pump(); return window;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Children<T>(child)) yield return nested; }
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Children<T>(root).Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); }
    private static void Choose(ListBox list, string id)
    { list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => ((LabelingObjectPrioritySummary)item.Tag).ClassIdentifier == id); Pump(); }
    public static void Run()
    {
        TestSummaryControls(); TestMissingImagesAndRecovery();
        Console.WriteLine("PASS Model Summary rankings, selected styling, prediction overlays, wraparound repair navigation, retained state and failures");
    }

    private static void TestSummaryControls()
    {
        using var fixture = new Fixture(); var first = fixture.Save(Box("enemies.crawlid"), Box("enemies.crawlid", .7), Box("enemies.crawlid", .55, true)); var last = fixture.Save();
        var candidate = Candidate(first, last); var examples = fixture.Store.Load(); var classes = LabelingObjectReferencePolicy.ModelClasses([new("custom.bench", "Bench")]);
        var session = new LabelingModelSummarySession(); session.SetContent(examples, candidate, classes.Select(item => item.Identifier), "game.playable-knight");
        var reviewSession = new LabelingScreenshotReviewSession(fixture.Store); reviewSession.Reload();
        var selected = ""; var changes = 0; var trainable = false; SavedLabelingExample? opened = null;
        var panel = new ModelSummaryReviewPanel(session, key => key, id => { selected = id; changes++; trainable = reviewSession.CanTrainSelectedClass(id); }, example => opened = example);
        panel.Refresh(["custom.bench"]); var window = Show(panel);
        try
        {
            var least = Find<ListBox>(panel, "SummaryLeastRegistered"); var errors = Find<ListBox>(panel, "SummaryMostErrors");
            Check(least.Items.Count == 8 && errors.Items.Count == 1 && ((LabelingObjectPrioritySummary)((ListBoxItem)least.Items[0]).Tag).RegistrationCount == 0,
                "Summary omitted unlabeled classes or included non-model HUD errors.");
            Check(Find<TextBlock>(panel, "SummaryFailurePosition").Text == "0 / 0" && !Find<Button>(panel, "SummaryNextFailure").IsEnabled, "Summary invented an initial failure selection.");
            Choose(errors, "enemies.crawlid");
            Check(changes == 1 && selected == "enemies.crawlid" && trainable && session.SelectedFailure?.Id == last.Id, "Priority selection did not update shared object/training/failure state exactly once.");
            Check(Find<TextBlock>(panel, "SummaryObjectTitle").Text == "Crawlid" && Find<TextBlock>(panel, "SummaryFailurePosition").Text == "1 / 2", "Summary header does not describe its selected failure.");
            foreach (var list in new[] { least, errors })
            {
                var item = (ListBoxItem)list.SelectedItem; var border = (Border)item.Template.FindName("PriorityBorder", item);
                Check(((SolidColorBrush)border.Background).Color == Color.FromRgb(36, 91, 134), "Priority row lacks the shared selected highlight.");
            }
            var preview = Find<Button>(panel, "SummaryOpenScreenshot");
            Check(Children<Rectangle>(preview).Count(box => box.Stroke == Brushes.Yellow) == 1, "Preview included another class or below-threshold predictions.");
            Click(Find<Button>(panel, "SummaryNextFailure"));
            Check(session.SelectedFailure?.Id == first.Id && session.PositionText == "2 / 2", "Next did not use review order.");
            Check(Children<Rectangle>(preview).Count(box => box.StrokeThickness == 3) == 3 && Children<Rectangle>(preview).Any(box => box.Stroke == Brushes.HotPink), "Selected/negative human boxes disappeared from failure review.");
            Click(preview); Check(opened?.Id == first.Id, "Repair callback opened a different screenshot.");
            Click(Find<Button>(panel, "SummaryNextFailure")); Check(session.SelectedFailure?.Id == last.Id, "Next did not wrap.");
            Click(Find<Button>(panel, "SummaryPreviousFailure")); Check(session.SelectedFailure?.Id == first.Id, "Previous did not wrap.");
            var atlas = new LabelingObjectAtlasStore(fixture.Root).LoadOrBuild(new(examples, classes.Select(item => item.Identifier).ToArray(), new Dictionary<string, Guid>()));
            panel.SetAtlas(atlas); Pump();
            Check(Children<Image>(panel).Count() >= 3, "Summary legend did not receive reference icons.");
            var rendered = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32); rendered.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
            using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "model-summary-preview.png"))) encoder.Save(stream);
            session.SetContent(examples, candidate, classes.Select(item => item.Identifier), selected); panel.Refresh(["custom.bench"], atlas); Pump();
            Check(session.SelectedFailure?.Id == first.Id && session.PositionText == "2 / 2", "Refreshing Summary reset the failure position.");
            Choose(least, "custom.bench");
            Check(!trainable && session.FailureExamples.Count == 0 && Find<TextBlock>(panel, "SummaryEmptyReview").IsVisible
                && !Find<Button>(panel, "SummaryOpenScreenshot").IsVisible, "Unlabeled class retained another object's errors or enabled training.");
            Check(changes == 2, "Programmatic refresh triggered object-selection callbacks.");
        }
        finally { window.Close(); }
    }

    private static void TestMissingImagesAndRecovery()
    {
        using var fixture = new Fixture(); var first = fixture.Save(Box("enemies.crawlid")); var last = fixture.Save(); var candidate = Candidate(first, last);
        var classes = LabelingObjectReferencePolicy.ModelClasses().Select(item => item.Identifier).ToArray();
        var session = new LabelingModelSummarySession(); session.SetContent([first, last], candidate, classes, "enemies.crawlid"); session.SelectPriority("enemies.crawlid"); session.Move(1);
        var opened = 0; var panel = new ModelSummaryReviewPanel(session, key => key, _ => { }, _ => opened++); panel.Refresh(); var window = Show(panel);
        try
        {
            Check(session.SelectedFailure?.Id == first.Id, "Recreated panel lost session selection.");
            File.Delete(first.ImagePath); panel.Refresh(); Pump();
            Check(!Find<Button>(panel, "SummaryOpenScreenshot").IsEnabled && Children<TextBlock>(panel).Any(item => item.Text == "Image unavailable."), "Missing screenshot crashed or left repair enabled.");
            panel.Move(1); Check(session.SelectedFailure?.Id == last.Id && Find<Button>(panel, "SummaryOpenScreenshot").IsEnabled, "Missing image prevented navigation to a readable failure.");
            panel.ShowError("Model review failed: invalid test metadata"); Pump();
            Check(Find<TextBlock>(panel, "SummaryReviewError").IsVisible && !Find<ListBox>(panel, "SummaryMostErrors").IsVisible, "Review failure left stale clickable content visible.");
            Click(Find<Button>(panel, "SummaryOpenScreenshot")); Check(opened == 0, "Hidden failed review retained its old repair callback target.");
            session.SetContent([last], candidate, classes, "enemies.crawlid"); panel.Refresh(); Pump();
            Check(!Find<TextBlock>(panel, "SummaryReviewError").IsVisible && session.PositionText == "1 / 1" && !Find<Button>(panel, "SummaryNextFailure").IsEnabled, "Recovery did not clear errors or remove the deleted failure.");
            Click(Find<Button>(panel, "SummaryOpenScreenshot")); Check(opened == 1, "Recovery did not restore repair.");
            session.SetContent([last], null, classes, "enemies.crawlid"); panel.Refresh(); Pump();
            Check(session.MostErrors.Count == 0 && Find<TextBlock>(panel, "SummaryEmptyReview").IsVisible && Find<ListBox>(panel, "SummaryLeastRegistered").Items.Count == 7, "No-model state hid the class catalog or reused prior predictions.");
        }
        finally { window.Close(); }
    }
}
