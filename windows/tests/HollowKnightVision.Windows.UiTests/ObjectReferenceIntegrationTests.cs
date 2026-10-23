using System.Diagnostics;
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

internal static class ObjectReferenceIntegrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static LabelingExampleAnnotation Box(string id = "game.mana", double x = 0, double width = 1, bool negative = false) => new(Guid.NewGuid(), id, x, 0, width, 1, negative);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hkv-reference-ui-" + Guid.NewGuid().ToString("N"));
        public LabelingExampleStore Store => new(Path.Combine(Root, "examples"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static BgraFrame Frame(int width = 20, int height = 10, bool split = false)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var offset = (y * width + x) * 4; pixels[offset + (split && x >= width / 2 ? 0 : 2)] = 255; pixels[offset + 3] = 255; }
        return new(width, height, width * 4, pixels, 0, DateTimeOffset.UnixEpoch);
    }
    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var data = new byte[4];
        converted.CopyPixels(new(x, y, 1, 1), data, 4, 0); return Color.FromArgb(data[3], data[2], data[1], data[0]);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Wait(ObjectReferencePanel panel)
    {
        var timer = Stopwatch.StartNew();
        while (!panel.PendingBuild.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(5); }
        Check(panel.PendingBuild.IsCompleted, "Object atlas background build did not complete.");
        panel.PendingBuild.GetAwaiter().GetResult(); Pump();
    }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); if (child is T value) yield return value; foreach (var nested in Children<T>(child)) yield return nested; }
    }
    private static Button Button(DependencyObject panel, string id) => Children<Button>(panel).Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); }
    public static void Run()
    {
        TestAtlas(); TestEditedAndMissingReferences(); TestCropAndCapacity(); TestPanel(); TestBuildFailure(); TestShutdownDrains();
        Console.WriteLine("PASS Object reference crops, stars, preview boxes, persistent atlas cache and shutdown draining");
    }

    public static void TestAtlas()
    {
        using var fixture = new Fixture();
        var example = fixture.Store.Save(Frame(), "game", [Box()]);
        var input = new LabelingObjectAtlasBuildInput([example], ["game.health", "game.mana"], new Dictionary<string, Guid> { ["game.mana"] = example.Id });
        var store = new LabelingObjectAtlasStore(fixture.Root); var built = store.LoadOrBuild(input);
        Check(built.Image.PixelWidth == 2048 && built.Image.PixelHeight == 2048 && built.ClassIdentifiers.SequenceEqual(new[] { "game.health", "game.mana" }), "Atlas layout is not fixed and deterministic.");
        var icon = built.Icon("game.mana")!;
        Check(icon.PixelWidth == 128 && icon.PixelHeight == 64 && Pixel(icon, 64, 32) == Colors.Red, "Wide crop lost its aspect, color or alpha.");
        Check(ReferenceEquals(icon, built.Icon("game.mana")) && built.Icon("game.health") is null, "Icon lookup allocated repeatedly or invented an unlabeled icon.");
        Check(Pixel(built.Image, 0, 0).A == 0 && Pixel(built.Image, 128 + 64, 32 + 32) == Colors.Red, "Slots are flipped or the atlas background is opaque.");
        var path = Path.Combine(fixture.Root, LabelingObjectAtlasStore.ImageFilename); var bytes = File.ReadAllBytes(path); var time = File.GetLastWriteTimeUtc(path);
        var cached = store.LoadOrBuild(input); var cachedIcon = cached.Icon("game.mana")!;
        Check(ReferenceEquals(cachedIcon, cached.Icon("game.mana")) && cachedIcon.PixelWidth == 128 && cachedIcon.PixelHeight == 64, "Cached atlas changed crop geometry or reuse.");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path) == time, "Cache hit rebuilt the PNG.");
        Check(File.Exists(Path.Combine(fixture.Root, LabelingObjectAtlasStore.MetadataFilename)), "Atlas metadata was not persisted.");
    }

    private static void TestEditedAndMissingReferences()
    {
        using var fixture = new Fixture(); var store = new LabelingObjectAtlasStore(fixture.Root);
        var example = fixture.Store.Save(Frame(20, 10, true), "game", [Box(x: 0, width: .5)]);
        var input = new LabelingObjectAtlasBuildInput([example], ["game.mana"], new Dictionary<string, Guid>());
        Check(Pixel(store.LoadOrBuild(input).Icon("game.mana")!, 64, 64) == Colors.Red, "Left reference crop is not red.");
        var changed = fixture.Store.Update(example, "game", [example.Manifest.Annotations[0] with { X = .5 }]);
        input = input with { Examples = [changed] };
        Check(Pixel(store.LoadOrBuild(input).Icon("game.mana")!, 64, 64) == Colors.Blue, "Editing bounds under the same annotation ID reused stale cache pixels.");
        File.WriteAllText(Path.Combine(fixture.Root, LabelingObjectAtlasStore.MetadataFilename), "corrupt");
        Check(store.LoadOrBuild(input).Icon("game.mana") is not null, "Corrupt metadata did not rebuild.");
        File.WriteAllText(Path.Combine(fixture.Root, LabelingObjectAtlasStore.ImageFilename), "corrupt-png");
        Check(store.LoadOrBuild(input).Icon("game.mana") is not null, "Mismatched cache image was reused.");
        var missing = changed with { Manifest = changed.Manifest with { Id = Guid.NewGuid(), ImageFilename = "absent.png" } };
        Check(store.LoadOrBuild(input with { Examples = [changed, missing], PreferredExampleIdentifiers = new Dictionary<string, Guid> { ["game.mana"] = missing.Id } }).Icon("game.mana") is null,
            "Missing preferred image silently showed a different reference.");
        Check(!Directory.EnumerateFiles(fixture.Root, ".object-atlas-*").Any(), "Atlas staging files leaked.");
    }

    private static void TestCropAndCapacity()
    {
        using var fixture = new Fixture(); var example = fixture.Store.Save(Frame(20, 10, true), "game", [Box()]);
        var image = ObjectReferenceImages.Load(example.ImagePath)!;
        var crop = ObjectReferenceImages.Crop(image, Box(x: .5, width: -.5))!;
        Check(crop.PixelWidth == 10 && crop.PixelHeight == 10 && Pixel(crop, 5, 5) == Colors.Red, "Negative bounds did not standardize in top-origin coordinates.");
        Check(ObjectReferenceImages.Crop(image, Box(x: 1.1)) is null && ObjectReferenceImages.Crop(image, Box(x: double.NaN)) is null, "Invalid crop escaped bounds checks.");
        var classes = Enumerable.Range(0, 17).Select(index => $"object-{index:D2}").ToArray();
        var row = example with { Manifest = example.Manifest with { Annotations = [Box("object-16")] } };
        var store = new LabelingObjectAtlasStore(fixture.Root);
        var atlas = store.LoadOrBuild(new([row], classes, new Dictionary<string, Guid>()));
        Check(Pixel(atlas.Image, 64, 128 + 64).A == 255 && Pixel(atlas.Image, 64, 64).A == 0, "Second atlas row used bottom-origin coordinates.");
        try { store.LoadOrBuild(new([], Enumerable.Range(0, 257).Select(i => "class-" + i).ToArray(), new Dictionary<string, Guid>())); throw new Exception("Capacity overflow accepted."); }
        catch (InvalidDataException) { }
        File.WriteAllText(Path.Combine(fixture.Root, "bad.png"), "not-an-image");
        Check(ObjectReferenceImages.Load(Path.Combine(fixture.Root, "bad.png")) is null, "Malformed reference image escaped graceful handling.");
    }

    private static void TestPanel()
    {
        using var fixture = new Fixture(); var firstBox = Box("game.playable-knight", width: .5); var secondBox = Box("game.playable-knight", x: .5, width: .5);
        var first = fixture.Store.Save(Frame(20, 10, true), "game", [firstBox, secondBox, Box("game.playable-knight", negative: true)]);
        var next = fixture.Store.Save(Frame(), "game", [Box("game.playable-knight")]);
        var later = next.Manifest.Annotations[0]; string? selectedClass = null; SavedLabelingExample? opened = null;
        var panel = new ObjectReferencePanel(fixture.Root, key => key, id => selectedClass = id, example => opened = example);
        var window = new Window { Content = panel, Width = 1200, Height = 600, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var classes = LabelingObjectReferencePolicy.ModelClasses([new("custom.bench", "Bench")]);
            panel.SetExamples([next, first], classes, "game.playable-knight"); Wait(panel);
            panel.UpdateLayout(); Pump();
            var screenshot = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            screenshot.Render(panel); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
            using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "object-reference-preview.png"))) encoder.Save(stream);
            Check(panel.ColumnDefinitions.Count == 3 && panel.SelectedExample == first.Id, "Reference browser did not default to the oldest screenshot.");
            Check(Children<Button>(panel).Count(button => AutomationProperties.GetAutomationId(button).StartsWith("ObjectReference_")) == 3, "References were collapsed by screenshot or included a negative box.");
            Check(Children<ListBoxItem>(panel).Count() == 8, "Unlabeled/custom object choices are missing.");
            Click(Button(panel, "ObjectReference_" + later.Id.ToString("N")));
            Check(panel.SelectedExample == next.Id, "Clicking a crop did not select its screenshot.");
            Click(Button(panel, "ModelObjectScreenshot")); Check(opened?.Id == next.Id, "Full-screenshot edit callback opened the wrong example.");
            Click(Button(panel, "ObjectReferenceStar_" + later.Id.ToString("N"))); Wait(panel);
            var preferences = new LabelingObjectReferenceStore(Path.Combine(fixture.Root, "object-references.json"));
            Check(preferences.ExampleIdentifier("game.playable-knight") == next.Id, "Star button did not persist the preferred example.");
            Check(!panel.AcceptReferenceDrop("reference-star:custom.bench", first.Id) && !panel.AcceptReferenceDrop("untrusted", first.Id), "Foreign star payload was accepted.");
            Check(panel.AcceptReferenceDrop("reference-star:game.playable-knight", first.Id), "Same-class star drop failed."); Wait(panel);
            Click(Button(panel, "ObjectReference_" + firstBox.Id.ToString("N")));
            Check(Children<Rectangle>(Button(panel, "ModelObjectScreenshot")).Count() == 3, "Full preview dropped other positive or negative annotations.");
            var catalog = Children<ListBox>(panel).Single();
            catalog.SelectedItem = catalog.Items.OfType<ListBoxItem>().Single(item => (string)item.Tag == "custom.bench"); Pump();
            Check(selectedClass == "custom.bench" && panel.SelectedExample is null
                && Children<TextBlock>(panel).Any(item => item.Text == "No positive reference boxes." && item.IsVisible), "Empty custom object retained another object's preview.");
            panel.SetExamples([next], classes, "game.playable-knight"); Wait(panel);
            Check(panel.SelectedExample == next.Id, "Deleting the preferred example did not fall back to an available reference.");
        }
        finally { panel.StopAndDrain(); window.Close(); }
    }

    private static void TestShutdownDrains()
    {
        using var fixture = new Fixture(); var example = fixture.Store.Save(Frame(), "game", [Box("game.playable-knight")]);
        var panel = new ObjectReferencePanel(fixture.Root, key => key, _ => { }, _ => { });
        for (var index = 0; index < 3; index++) panel.SetExamples([example], LabelingObjectReferencePolicy.ModelClasses());
        panel.StopAndDrain(); Check(panel.PendingBuild.IsCompleted, "Shutdown left an atlas write running.");
        var moved = fixture.Root + "-archived";
        Directory.Move(fixture.Root, moved);
        try { Pump(); Check(!Directory.Exists(fixture.Root), "A late atlas callback recreated the archived support directory."); }
        finally { Directory.Move(moved, fixture.Root); }
    }

    private static void TestBuildFailure()
    {
        using var fixture = new Fixture(); var example = fixture.Store.Save(Frame(), "game", [Box("game.playable-knight")]);
        var blocked = Path.Combine(fixture.Root, LabelingObjectAtlasStore.MetadataFilename); Directory.CreateDirectory(blocked);
        var panel = new ObjectReferencePanel(fixture.Root, key => key, _ => { }, _ => { });
        var window = new Window { Content = panel, Width = 900, Height = 400, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var classes = LabelingObjectReferencePolicy.ModelClasses(); panel.SetExamples([example], classes);
            var timer = Stopwatch.StartNew();
            while (!panel.PendingBuild.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(5); }
            Check(panel.PendingBuild.IsFaulted, "Blocked atlas commit unexpectedly succeeded."); Pump();
            Check(Children<TextBlock>(panel).Any(item => AutomationProperties.GetAutomationId(item) == "ObjectAtlasError" && item.Text.StartsWith("Object atlas failed:")),
                "A cache write failure was not visible in the reference panel.");
            Check(Button(panel, "ModelObjectScreenshot").IsEnabled && File.Exists(example.ImagePath), "Derived-cache failure disabled or damaged screenshot review.");
            Directory.Delete(blocked); panel.SetExamples([example], classes); Wait(panel);
            Check(Children<TextBlock>(panel).Single(item => AutomationProperties.GetAutomationId(item) == "ObjectAtlasError").Text == "", "Successful retry retained an obsolete cache failure.");
        }
        finally { panel.StopAndDrain(); window.Close(); }
    }
}
