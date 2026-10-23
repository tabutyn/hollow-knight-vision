using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class ObjectReferenceTests
{
    private static void Check(bool condition, string message = "Object reference assertion failed.")
    { if (!condition) throw new Exception(message); }

    public static void TestPositiveOrderingAndFallback()
    {
        var first = LabelingContractTests.Example("main-title", "main-title.options", "shared.options");
        first = first with { Manifest = first.Manifest with { CreatedAt = DateTimeOffset.UnixEpoch } };
        var later = LabelingContractTests.Example("options", "shared.options");
        var negative = later with { Manifest = later.Manifest with { Id = Guid.NewGuid(), Annotations = later.Manifest.Annotations.Select(a => a with { IsNegative = true }).ToArray() } };
        SavedLabelingExample[] examples = [later, negative, first];
        var values = LabelingObjectReferencePolicy.References(examples, "main-title.options");
        Check(values.Count == 3 && values[0].Example.Id == first.Id && values[1].Example.Id == first.Id && values[2].Example.Id == later.Id);
        Check(values[0].Annotation.Id == first.Manifest.Annotations[0].Id);
        Check(LabelingObjectReferencePolicy.Preferred(examples, "shared.options", later.Id)?.Example.Id == later.Id);
        Check(LabelingObjectReferencePolicy.Preferred(examples, "shared.options", negative.Id)?.Example.Id == first.Id);
        Check(LabelingObjectReferencePolicy.Preferred(examples, "shared.options", Guid.NewGuid())?.Example.Id == first.Id);
        Check(LabelingObjectReferencePolicy.Preferred(examples, "game.mana", null) is null);
    }

    public static void TestReferencePreferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-reference-prefs-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "object-references.json");
        try
        {
            var store = new LabelingObjectReferenceStore(path); var selected = Guid.NewGuid(); var unrelated = Guid.NewGuid();
            Check(store.ExampleIdentifier("shared.options") is null && !Directory.Exists(root));
            store.SetExampleIdentifier("main-title.options", selected); store.SetExampleIdentifier("game.mana", unrelated);
            var loaded = new LabelingObjectReferenceStore(path);
            Check(loaded.ExampleIdentifier("shared.options") == selected && loaded.ExampleIdentifier("game.mana") == unrelated);
            var batch = loaded.ExampleIdentifiers(["shared.options", "main-title.options", "game.health"]);
            Check(batch.Count == 1 && batch["shared.options"] == selected);
            var bytes = File.ReadAllBytes(path); var time = File.GetLastWriteTimeUtc(path);
            loaded.SetExampleIdentifier("shared.options", selected);
            Check(File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path) == time);
            loaded.SetExampleIdentifier("game.health", Guid.Empty);
            Check(loaded.ExampleIdentifier("game.health") == Guid.Empty, "A valid all-zero UUID was treated as absent.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static void TestInvalidPreferencesAndWriteFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-reference-errors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "preferences.json");
        try
        {
            var store = new LabelingObjectReferenceStore(path);
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string> { ["game.mana"] = "invalid-uuid" }));
            Check(store.ExampleIdentifiers(["game.mana"]).Count == 0);
            File.WriteAllText(path, "invalid-json"); Check(store.ExampleIdentifier("game.mana") is null);
            var identifier = Guid.NewGuid(); store.SetExampleIdentifier("game.mana", identifier);
            Check(store.ExampleIdentifier("game.mana") == identifier);
            var blocked = Path.Combine(root, "blocked"); Directory.CreateDirectory(blocked);
            try { new LabelingObjectReferenceStore(blocked).SetExampleIdentifier("game.mana", identifier); throw new Exception("Expected blocked preference write."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Check(Directory.Exists(blocked) && !Directory.EnumerateFiles(root, "*.tmp-*").Any());
        }
        finally { Directory.Delete(root, true); }
    }

    public static void TestModelCatalogIncludesUnlabeledObjects()
    {
        var classes = LabelingObjectReferencePolicy.ModelClasses([new("custom.bench", "Bench"), new("enemies.crawlid", "duplicate")]);
        Check(classes.Count == 8 && classes[0].Identifier == "game.playable-knight" && classes[^1].Identifier == "custom.bench");
        Check(!classes.Any(item => item.Identifier is "game.health" or "game.mana" or "game.geo"));
        Check(classes.Count(item => item.Identifier == "enemies.crawlid") == 1);
    }
}
