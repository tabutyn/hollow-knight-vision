using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.Core;

internal static class DatasetExporterTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static void Check(bool value, string message = "Dataset parity assertion failed.") { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < 1e-9, $"Expected {expected}; got {actual}.");
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static LabelingExampleAnnotation Box(string label = "game.mana", double x = .1, double y = .1, double width = .2, double height = .2, bool negative = false) => new(Guid.NewGuid(), label, x, y, width, height, negative ? true : null);
    private static string[] Catalog => LabelingContractCatalog.Contexts.SelectMany(context => context.Labels).Select(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier)).Distinct().Order(StringComparer.Ordinal).ToArray();
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public Fixture(string? root = null) => Root = root ?? Directory.CreateTempSubdirectory("hkv-dataset-parity-").FullName;
        public LabelingExampleStore Store => new(Path.Combine(Root, "examples"));
        public LabelingDatasetExporter Exporter => new(Path.Combine(Root, "datasets"));
        public string Ledger => Path.Combine(Root, "split-assignments.json");
        public SavedLabelingExample Save(byte red, string context, LabelingExampleAnnotation[] boxes, Guid? group = null)
        {
            var bytes = new byte[100 * 50 * 4];
            for (var i = 0; i < bytes.Length; i += 4) { bytes[i] = 178; bytes[i + 1] = 76; bytes[i + 2] = red; bytes[i + 3] = 255; }
            return Store.Save(new(100, 50, 400, bytes, 0, DateTimeOffset.UnixEpoch), context, boxes, captureGroupIdentifier: group);
        }
        public void Assign(string model, IEnumerable<SavedLabelingExample> examples, string split)
        {
            File.WriteAllText(Ledger, JsonSerializer.Serialize(new { schemaVersion = 1, assignments = new Dictionary<string, Dictionary<string, string>>
                { [model] = examples.ToDictionary(example => example.Id.ToString("D"), _ => split) } }, Json));
        }
        public void Dispose() { Directory.Delete(Root, true); }
    }
    private static ImageAnnotations[] Annotations(LabelingDatasetSnapshot snapshot, string split) => JsonSerializer.Deserialize<ImageAnnotations[]>(
        File.ReadAllBytes(Path.Combine(snapshot.DirectoryPath, split, "annotations.json")), Json)!;
    private static ImageAnnotations AnnotationFor(LabelingDatasetSnapshot snapshot, Guid id)
    {
        var item = snapshot.Manifest.Items.Single(item => item.ExampleIdentifier == id);
        return Annotations(snapshot, item.Split == LabelingDatasetSplit.Training ? "training" : "validation").Single(value => value.Image == item.ImageFilename);
    }

    public static void TestCatalogRepeatedInstances()
    {
        using var fixture = new Fixture();
        var example = fixture.Save(51, "main-title", [Box("main-title.select-decoration", .1, .1, .1, .1), Box("main-title.select-decoration", .3, .1, .1, .1), Box("main-title.start-game", .4, .5, .2, .1)]);
        var snapshot = fixture.Exporter.ExportSharedModel(Catalog, [example]);
        Check(snapshot.Manifest.ClassIdentifier == "shared-object-model" && snapshot.Manifest.ClassIdentifiers!.SequenceEqual(Catalog));
        Check(snapshot.Manifest.TrainingAnnotationCount == 3);
        Check(snapshot.Manifest.TrainingItems.Single().KnownClassIdentifiers!.SequenceEqual(new[] { "main-title.start-game", "shared.select-decoration" }));
        Check(Annotations(snapshot, "training").Single().Annotations.Select(box => box.Label).SequenceEqual(new[] { "shared.select-decoration", "shared.select-decoration", "main-title.start-game" }));
    }
    public static void TestCrossContextExport()
    {
        using var fixture = new Fixture();
        var title = fixture.Save(51, "main-title", [Box("main-title.start-game")]); var game = fixture.Save(102, "game", [Box("game.mana", .3, .3)]);
        var snapshot = fixture.Exporter.ExportSharedModel(Catalog, [title, game]);
        var labels = new[] { "training", "validation" }.SelectMany(split => Annotations(snapshot, split)).SelectMany(image => image.Annotations).Select(box => box.Label).ToHashSet();
        Check(labels.SetEquals(["main-title.start-game", "game.mana"]) && snapshot.Manifest.ClassIdentifier == "shared-object-model");
    }
    public static void TestKnownEmptyFrame()
    {
        using var fixture = new Fixture(); var positive = fixture.Save(51, "game", [Box("game.mana", .1, .2, .3, .4)]); var negative = fixture.Save(204, "game", []);
        var snapshot = fixture.Exporter.ExportSharedModel(["game.mana"], fixture.Store.Load());
        Check(snapshot.Manifest.Items.Select(item => item.Id).ToHashSet().SetEquals([positive.Id, negative.Id]));
        var item = snapshot.Manifest.Items.Single(item => item.Id == negative.Id);
        Check(item.AnnotationCount == 0 && item.KnownClassIdentifiers!.SequenceEqual(new[] { "game.mana" }) && AnnotationFor(snapshot, negative.Id).Annotations.Count == 0);
    }
    public static void TestMenuKnownCatalogExclusion()
    {
        using var fixture = new Fixture(); var game = fixture.Save(51, "game", [Box()]); var menu = fixture.Save(204, "main-title", []);
        Check(menu.Manifest.EffectiveKnownClassIdentifiers.Contains("game.mana"));
        var snapshot = fixture.Exporter.ExportSharedModel(["game.mana"], [game, menu]);
        Check(snapshot.Manifest.Items.Single().Id == game.Id, "A menu's remembered gameplay catalog became a negative training sample.");
    }
    public static void TestHardNegativeRegistration()
    {
        using var fixture = new Fixture();
        var corrected = fixture.Save(128, "main-title", [Box("shared.select-decoration"), Box("shared.select-decoration", .6, .2, .2, .3, true)]);
        var ordinary = fixture.Save(178, "main-title", [Box("shared.select-decoration", .2, .2)]);
        var snapshot = fixture.Exporter.ExportSharedModel(["shared.select-decoration"], [corrected, ordinary]);
        var annotations = Annotations(snapshot, "training").Single().Annotations;
        Check(snapshot.Manifest.TrainingAnnotationCount == 1 && snapshot.Manifest.TrainingItems.Single().Id == corrected.Id);
        Check(annotations.Count == 2 && annotations[0].IsNegative is null && annotations[1].IsNegative == true);
    }
    public static void TestSinglePreliminarySnapshot()
    {
        using var fixture = new Fixture(); var saved = fixture.Save(51, "game", [Box("game.mana", .1, .2, .3, .4)]); var id = Guid.NewGuid(); var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var exporter = new LabelingDatasetExporter(Path.Combine(fixture.Root, "datasets"), () => id, () => now);
        var snapshot = exporter.Export("game.mana", [saved]);
        Check(snapshot.Id == id && snapshot.Manifest.CreatedAt == now && snapshot.Manifest.IsPreliminary && snapshot.Manifest.TrainingItems.Single().Id == saved.Id);
        Check(snapshot.Manifest.ValidationItems.Count == 0 && snapshot.Manifest.TrainingAnnotationCount == 1 && snapshot.Manifest.ValidationAnnotationCount == 0);
        var annotations = Annotations(snapshot, "training"); Check(annotations.Length == 1 && annotations[0].Annotations.Count == 1 && annotations[0].Annotations[0].Label == "game.mana");
        var box = annotations[0].Annotations[0].Coordinates; Near(25, box.X); Near(20, box.Y); Near(30, box.Width); Near(20, box.Height);
        Check(Annotations(snapshot, "validation").Length == 0 && File.Exists(Path.Combine(snapshot.DirectoryPath, "training", snapshot.Manifest.TrainingItems[0].ImageFilename)));
        Check(Directory.EnumerateFileSystemEntries(exporter.RootDirectory).Select(Path.GetFileName).SequenceEqual(new[] { id.ToString("D") }));
    }
    public static void TestCaptureAndDuplicateGroups()
    {
        using var fixture = new Fixture(); var group = Guid.NewGuid();
        var first = fixture.Save(51, "game", [Box()], group); var second = fixture.Save(102, "game", [Box()], group);
        var duplicate = fixture.Save(102, "game", [Box()]); var independent = fixture.Save(204, "game", [Box()]);
        var snapshot = fixture.Exporter.Export("game.mana", [first, second, duplicate, independent]); var items = snapshot.Manifest.Items.ToDictionary(item => item.Id);
        Check(items[first.Id].Split == items[second.Id].Split && items[second.Id].Split == items[duplicate.Id].Split && items[first.Id].Split != items[independent.Id].Split);
        Check(snapshot.Manifest.TrainingAnnotationCount == 3 && snapshot.Manifest.ValidationAnnotationCount == 1 && snapshot.Manifest.TrainingItems.Count == 3 && snapshot.Manifest.ValidationItems.Count == 1);
    }
    public static void TestStableValidationAssignment()
    {
        using var fixture = new Fixture(); var first = fixture.Save(51, "game", [Box()]); var second = fixture.Save(102, "game", [Box()]);
        var initial = fixture.Exporter.Export("game.mana", [first, second]).Manifest.Items.ToDictionary(item => item.Id, item => item.Split);
        var third = fixture.Save(153, "game", [Box()]); var updated = fixture.Exporter.Export("game.mana", [third, second, first]).Manifest.Items.ToDictionary(item => item.Id, item => item.Split);
        Check(updated[first.Id] == initial[first.Id] && updated[second.Id] == initial[second.Id] && updated[third.Id] == LabelingDatasetSplit.Training);
    }
    public static void TestRepairNewClassCoverage()
    {
        using var fixture = new Fixture(); var examples = Enumerable.Range(1, 3).Select(i => fixture.Save((byte)(i * 64), "quit-to-menu", [Box("shared.yes"), Box("shared.no", .5)])).ToArray();
        fixture.Assign("shared-object-model", examples, "validation");
        var snapshot = fixture.Exporter.ExportSharedModel(["shared.yes", "shared.no"], examples);
        var labels = Annotations(snapshot, "training").SelectMany(item => item.Annotations).Select(box => box.Label).ToHashSet();
        Check(labels.Contains("shared.yes") && labels.Contains("shared.no") && snapshot.Manifest.TrainingItems.Count == 2 && snapshot.Manifest.ValidationItems.Count == 1);
    }

    public static void TestContextAndCustomAdmission()
    {
        using var fixture = new Fixture();
        var positive = fixture.Save(10, "game", [Box("custom.bench")]);
        var emptyGame = fixture.Save(20, "game", []); var emptyMenu = fixture.Save(30, "main-title", []);
        var emptyUnknown = fixture.Save(40, "unknown", []); var unknownLabel = fixture.Save(50, "unknown", [Box("custom.bench")]);
        var all = new[] { positive, emptyGame, emptyMenu, emptyUnknown, unknownLabel };
        var onlyCustom = fixture.Exporter.ExportSharedModel(["custom.bench"], all);
        Check(onlyCustom.Manifest.Items.Select(item => item.Id).ToHashSet().SetEquals([positive.Id, unknownLabel.Id]),
            "Mac's context.labels guard must not invent built-in context membership for a custom-only export.");
        var shared = fixture.Exporter.ExportSharedModel(["custom.bench", "game.playable-knight"], all);
        Check(shared.Manifest.Items.Select(item => item.Id).ToHashSet().SetEquals([positive.Id, emptyGame.Id, unknownLabel.Id]),
            "Shared gameplay vocabulary must admit known custom negatives but not menus or unknown empty contexts.");
        var menuPositive = fixture.Save(60, "main-title", [Box("main-title.options")]);
        var menuNegative = fixture.Save(70, "main-title", []);
        var canonical = fixture.Exporter.ExportSharedModel(["main-title.options", "shared.options"], [menuPositive, menuNegative]);
        Check(canonical.Manifest.ClassIdentifiers!.SequenceEqual(new[] { "shared.options" }) && canonical.Manifest.Items.Count == 2);
    }
    public static void TestPortableMetadataAndReversedBoxes()
    {
        using var fixture = new Fixture(); var source = fixture.Save(51, "main-title", [Box("main-title.options")]);
        var reversed = source with { Manifest = source.Manifest with { Annotations = [Box("main-title.options", .4, .6, -.3, -.4), Box("main-title.options", .7, .5, -.2, -.1, true)] } };
        var time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, TimeSpan.FromHours(3));
        var snapshot = new LabelingDatasetExporter(Path.Combine(fixture.Root, "datasets"), now: () => time).ExportSharedModel(["main-title.options"], [reversed]);
        Check(snapshot.Manifest.CreatedAt == time);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(snapshot.DirectoryPath, "dataset.json")));
        var root = manifest.RootElement; var item = root.GetProperty("items")[0];
        Check(root.GetProperty("createdAt").GetString() == "2026-01-02T00:04:05Z" && item.GetProperty("split").GetString() == "training");
        Check(root.GetProperty("schemaVersion").GetInt32() == 2 && !root.TryGetProperty("trainingItems", out _) && !item.TryGetProperty("id", out _));
        var copied = Path.Combine(snapshot.DirectoryPath, "training", item.GetProperty("imageFilename").GetString()!);
        var original = File.ReadAllBytes(source.ImagePath); Check(original.SequenceEqual(File.ReadAllBytes(copied)));
        Check(item.GetProperty("imageDigest").GetString() == Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant());
        var annotations = Annotations(snapshot, "training").Single().Annotations;
        Near(25, annotations[0].Coordinates.X); Near(20, annotations[0].Coordinates.Y); Near(30, annotations[0].Coordinates.Width); Near(20, annotations[0].Coordinates.Height);
        Near(60, annotations[1].Coordinates.X); Near(22.5, annotations[1].Coordinates.Y); Near(20, annotations[1].Coordinates.Width); Near(5, annotations[1].Coordinates.Height);
        Check(annotations.All(box => box.Label == "shared.options") && annotations[1].IsNegative == true);
        using var annotationJson = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(snapshot.DirectoryPath, "training", "annotations.json")));
        Check(!annotationJson.RootElement[0].GetProperty("annotations")[0].TryGetProperty("isNegative", out _), "Optional nil fields are not omitted like Swift.");
    }
    public static void TestHardNegativeLedgerOverride()
    {
        using var fixture = new Fixture(); var group = Guid.NewGuid();
        var first = fixture.Save(10, "game", [Box()], group); var second = fixture.Save(20, "game", [Box()], group);
        var duplicate = fixture.Save(20, "game", [Box()]); var independent = fixture.Save(30, "game", [Box()]);
        SavedLabelingExample[] all = [first, second, duplicate, independent]; fixture.Assign("game.mana", all, "validation");
        second = second with { Manifest = second.Manifest with { Annotations = [.. second.Manifest.Annotations, Box(negative: true)] } };
        var snapshot = fixture.Exporter.Export("game.mana", [first, second, duplicate, independent]);
        Check(snapshot.Manifest.TrainingItems.Select(item => item.Id).ToHashSet().SetEquals([first.Id, second.Id, duplicate.Id]));
        Check(snapshot.Manifest.ValidationItems.Single().Id == independent.Id && snapshot.Manifest.TrainingAnnotationCount == 3);
        var repeated = fixture.Exporter.Export("game.mana", [independent, duplicate, second, first]);
        Check(repeated.Manifest.TrainingItems.Select(item => item.Id).ToHashSet().SetEquals([first.Id, second.Id, duplicate.Id]));
        using var ledger = JsonDocument.Parse(File.ReadAllBytes(fixture.Ledger));
        Check(ledger.RootElement.GetProperty("assignments").GetProperty("game.mana").GetProperty(second.Id.ToString("D")).GetString() == "training");
    }
    public static void TestFailureAtomicity()
    {
        using var fixture = new Fixture(); var source = fixture.Save(51, "game", [Box()]); var id = Guid.NewGuid();
        var exporter = new LabelingDatasetExporter(Path.Combine(fixture.Root, "datasets"), () => id);
        var committed = exporter.Export("game.mana", [source]); var manifestPath = Path.Combine(committed.DirectoryPath, "dataset.json"); var before = File.ReadAllBytes(manifestPath);
        Throws<IOException>(() => exporter.Export("game.mana", [source])); Check(File.ReadAllBytes(manifestPath).SequenceEqual(before));
        var invalid = source with { Manifest = source.Manifest with { Annotations = [Box(width: double.NaN)] } };
        Throws<ArgumentException>(() => fixture.Exporter.Export("game.mana", [invalid]));
        Throws<ArgumentException>(() => fixture.Exporter.Export("game.mana", [source, source]));
        Check(Directory.GetDirectories(exporter.RootDirectory).SequenceEqual(new[] { committed.DirectoryPath }), "An export failure left staging or replaced the committed snapshot.");
        File.WriteAllBytes(source.ImagePath, []);
        Throws<InvalidDataException>(() => fixture.Exporter.Export("game.mana", [source]));
        Check(File.ReadAllBytes(manifestPath).SequenceEqual(before) && File.ReadAllBytes(Path.Combine(committed.DirectoryPath, "training", committed.Manifest.Items[0].ImageFilename)).Length > 0);
        using var empty = new Fixture(); var negative = empty.Save(10, "game", [Box(negative: true)]); var knownOnly = empty.Save(20, "game", []);
        Throws<InvalidOperationException>(() => empty.Exporter.Export("game.mana", [negative, knownOnly]));
        Check(!Directory.Exists(empty.Exporter.RootDirectory) && !File.Exists(empty.Ledger), "Rejected negative-only dataset wrote training state.");
    }
    public static void TestInvalidLedgerPreservation()
    {
        using var fixture = new Fixture(); var first = fixture.Save(10, "game", [Box()]); var second = fixture.Save(20, "game", [Box()]);
        var numeric = JsonSerializer.Serialize(new { schemaVersion = 1, assignments = new Dictionary<string, object> { ["game.mana"] = new Dictionary<string, object> { [first.Id.ToString("D")] = 0 } } });
        foreach (var invalid in new[] { "null", "{}", "{", "{\"schemaVersion\":1}", "{\"schemaVersion\":1,\"assignments\":null}", "{\"schemaVersion\":1,\"assignments\":{\"game.mana\":null}}", numeric })
        {
            File.WriteAllText(fixture.Ledger, invalid); var bytes = File.ReadAllBytes(fixture.Ledger);
            Throws<JsonException>(() => fixture.Exporter.Export("game.mana", [first, second]));
            Check(File.ReadAllBytes(fixture.Ledger).SequenceEqual(bytes) && !Directory.Exists(fixture.Exporter.RootDirectory), "Invalid ledger was silently replaced or published a snapshot.");
        }
        if (!OperatingSystem.IsWindows()) return; // Read-only replacement semantics are platform-specific.
        fixture.Assign("game.mana", [first], "validation"); var old = File.ReadAllBytes(fixture.Ledger);
        File.SetAttributes(fixture.Ledger, FileAttributes.ReadOnly);
        try
        {
            var failed = false;
            try { fixture.Exporter.Export("game.mana", [first, second]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed && File.ReadAllBytes(fixture.Ledger).SequenceEqual(old) && !Directory.Exists(fixture.Exporter.RootDirectory));
        }
        finally { File.SetAttributes(fixture.Ledger, FileAttributes.Normal); }
        Check(!Directory.EnumerateFiles(fixture.Root, "*.tmp-*").Any(), "Failed ledger write leaked a temporary file.");
    }
    public static void TestDeterministicSplitsAndThresholds()
    {
        using var fixture = new Fixture(); var examples = Enumerable.Range(1, 10).Select(i => fixture.Save((byte)(i * 20), "game", [Box(), Box("enemies.crawlid")])).ToArray();
        var first = new LabelingDatasetExporter(Path.Combine(fixture.Root, "first", "datasets")).ExportSharedModel(["game.mana", "enemies.crawlid"], examples);
        var second = new LabelingDatasetExporter(Path.Combine(fixture.Root, "second", "datasets")).ExportSharedModel(["enemies.crawlid", "game.mana"], examples.Reverse());
        var expected = first.Manifest.Items.ToDictionary(item => item.Id, item => item.Split);
        Check(second.Manifest.Items.All(item => expected[item.Id] == item.Split) && !first.Manifest.IsPreliminary && first.Manifest.ValidationItems.Count == 2);
        var grouped = examples.Select(item => item with { Manifest = item.Manifest with { CaptureGroupIdentifier = Guid.Empty } }).ToArray();
        var singleGroup = new LabelingDatasetExporter(Path.Combine(fixture.Root, "group", "datasets")).Export("game.mana", grouped);
        Check(singleGroup.Manifest.IsPreliminary && singleGroup.Manifest.ValidationItems.Count == 0 && singleGroup.Manifest.TrainingItems.Count == 10);
        Check(!File.Exists(Path.Combine(fixture.Root, "group", "split-assignments.json")), "An inseparable capture group unexpectedly changed the split ledger.");
    }
    public static string CreateTrainerFixture(string requestedRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("hkv-dataset-probe-", StringComparison.Ordinal)
            || !Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any() || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Probe requires a new empty hkv-dataset-probe-* directory in the temporary folder.");
        var fixture = new Fixture(root); // The caller owns cleanup after Python inspects this exact fixture.
        var corrected = fixture.Save(51, "game", [Box("game.playable-knight"), Box("enemies.crawlid", .4), Box("enemies.crawlid", .7, .5, .2, .2, true)]);
        var knownEmpty = fixture.Save(102, "game", []); var ordinary = fixture.Save(153, "game", [Box("game.playable-knight")]); var menu = fixture.Save(204, "main-title", []);
        var snapshot = fixture.Exporter.ExportSharedModel(LabelingObjectReferencePolicy.ModelClasses().Select(item => item.Identifier), [corrected, knownEmpty, ordinary, menu]);
        Check(snapshot.Manifest.Items.Count == 3 && snapshot.Manifest.Items.All(item => item.Id != menu.Id));
        return snapshot.DirectoryPath;
    }

    public static void TestTrailingRootLedgerLocation()
    {
        using var fixture = new Fixture(); var first = fixture.Save(10, "game", [Box()]); var second = fixture.Save(20, "game", [Box()]);
        var root = Path.Combine(fixture.Root, "datasets");
        var snapshot = new LabelingDatasetExporter(root + Path.DirectorySeparatorChar).Export("game.mana", [first, second]);
        Check(File.Exists(fixture.Ledger) && !File.Exists(Path.Combine(root, "split-assignments.json")), "Trailing slash moved the split ledger inside the snapshot root.");
        var secondExport = fixture.Exporter.Export("game.mana", [second, first]);
        var expected = snapshot.Manifest.Items.ToDictionary(item => item.Id, item => item.Split);
        Check(secondExport.Manifest.Items.All(item => expected[item.Id] == item.Split));
    }
}
