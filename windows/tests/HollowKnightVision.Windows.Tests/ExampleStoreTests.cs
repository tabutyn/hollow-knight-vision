using System.Text.Json;
using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class ExampleStoreTests
{
    private static void Check(bool value, string message = "Example store parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static LabelingExampleAnnotation Box(string label, bool negative = false) =>
        new(Guid.NewGuid(), label, .1, .2, .3, .4, negative ? true : null);
    private static void Known(SavedLabelingExample example, params string[] expected) =>
        Check(example.Manifest.KnownClassIdentifiers!.SequenceEqual(expected), "Known class snapshot differs from Mac.");
    private static void Ids(IEnumerable<SavedLabelingExample> actual, params SavedLabelingExample[] expected) =>
        Check(actual.Select(item => item.Id).ToHashSet().SetEquals(expected.Select(item => item.Id)));

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("hkv-example-parity-").FullName;
        public DateTimeOffset Time { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        public LabelingExampleStore Store => new(Path.Combine(Root, "examples"), now: () => Time);
        public static BgraFrame Image => new(7, 5, 28, Enumerable.Repeat((byte)255, 140).ToArray(), 0, DateTimeOffset.UnixEpoch);
        public SavedLabelingExample Save(string context, LabelingExampleAnnotation[] boxes,
            LabelingExampleCompletion completion = LabelingExampleCompletion.Draft) => Store.Save(Image, context, boxes, completion: completion);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static string ManifestPath(SavedLabelingExample example) => Path.Combine(example.DirectoryPath, LabelingExampleStore.ManifestFilename);
    private static void Rewrite(SavedLabelingExample example, Action<JsonObject> edit)
    {
        var path = ManifestPath(example); var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json); File.WriteAllText(path, json.ToJsonString());
    }

    public static void TestDeleteOnlySelected()
    {
        using var fixture = new Fixture(); using var foreign = new Fixture();
        var first = fixture.Save("game", [Box("game.mana")]); var second = fixture.Save("game", [Box("game.health")]);
        var outside = foreign.Save("game", [Box("game.mana")]);
        Throws<InvalidOperationException>(() => fixture.Store.Delete(outside));
        Throws<InvalidOperationException>(() => fixture.Store.Delete(first with { DirectoryPath = fixture.Store.RootDirectory }));
        fixture.Store.Delete(first);
        Check(!Directory.Exists(first.DirectoryPath) && Directory.Exists(second.DirectoryPath) && Directory.Exists(outside.DirectoryPath));
        Ids(fixture.Store.Load(), second);
    }

    public static void TestLegacyKnownCatalogFallback()
    {
        using var fixture = new Fixture(); var saved = fixture.Save("game", [Box("game.mana")]);
        var legacy = saved.Manifest with { SchemaVersion = 1, KnownClassIdentifiers = ["game.mana", "game.health", "game.playable-knight"] };
        Check(legacy.EffectiveKnownClassIdentifiers.SequenceEqual(new[] { "game.mana" }));
        Check((legacy with { SchemaVersion = 5, KnownClassIdentifiers = null }).EffectiveKnownClassIdentifiers.SequenceEqual(new[] { "game.mana" }));
        Check((legacy with { SchemaVersion = 5, KnownClassIdentifiers = [] }).EffectiveKnownClassIdentifiers.Count == 0);
    }

    public static void TestHardNegativeRoundTrip()
    {
        using var fixture = new Fixture(); var box = Box("shared.select-decoration", true);
        var saved = fixture.Save("main-title", [box]); var loaded = fixture.Store.LoadManifest(saved.DirectoryPath);
        Check(loaded.Annotations.Single() == box && loaded.Annotations.Single().IsHardNegative);
        Known(saved, "shared.select-decoration"); Ids(fixture.Store.LoadContainingClass("main-title.select-decoration"), saved);
        Ids(fixture.Store.LoadTrainingExamples("select-profile.select-decoration"), saved);
    }

    public static void TestLegacySelectMigration()
    {
        using var fixture = new Fixture(); var saved = fixture.Save("main-title", [Box("shared.select-decoration")]);
        var path = ManifestPath(saved);
        File.WriteAllText(path, File.ReadAllText(path).Replace("shared.select-decoration", "main-title.select-decoration", StringComparison.Ordinal));
        var loaded = fixture.Store.LoadManifest(saved.DirectoryPath);
        Check(loaded.Annotations.Single().ClassIdentifier == "shared.select-decoration"
            && loaded.KnownClassIdentifiers!.SequenceEqual(new[] { "shared.select-decoration" }));
        Check(!File.ReadAllText(path).Contains("main-title.select-decoration", StringComparison.Ordinal));
    }

    public static void TestLegacyGameSplit()
    {
        using var fixture = new Fixture();
        foreach (var context in new[] { "options", "game-options" })
        {
            var saved = fixture.Save(context, [Box("game-options.game-options")]);
            Rewrite(saved, json => { json["schemaVersion"] = 3; json["knownClassIdentifiers"] = new JsonArray("game-options.game-options"); });
            var loaded = fixture.Store.LoadManifest(saved.DirectoryPath);
            Check(loaded.SchemaVersion == 5 && loaded.ContextIdentifier == context);
            Check(loaded.Annotations.Single().ClassIdentifier == (context == "options" ? "options.game" : "game-options.game-options"));
            Check(loaded.KnownClassIdentifiers!.SequenceEqual(new[] { "game-options.game-options", "options.game" }));
            using var persisted = JsonDocument.Parse(File.ReadAllBytes(ManifestPath(saved)));
            Check(persisted.RootElement.GetProperty("schemaVersion").GetInt32() == 5);
        }
    }

    public static void TestNewFrameKnownClasses()
    {
        using var fixture = new Fixture(); var mana = fixture.Save("game", [Box("game.mana")]);
        fixture.Time = fixture.Time.AddSeconds(1); var knight = fixture.Save("game", [Box("game.playable-knight")]);
        Known(mana, "game.mana"); Known(knight, "game.mana", "game.playable-knight");
        Known(new(mana.DirectoryPath, fixture.Store.LoadManifest(mana.DirectoryPath)), "game.mana");
        Check(fixture.Store.Load().Select(item => item.Id).SequenceEqual(new[] { knight.Id, mana.Id }), "Examples must load newest first.");
    }

    public static void TestTrainingIgnoresCompletion()
    {
        using var fixture = new Fixture(); var boxes = new[] { Box("game.playable-knight") };
        var ready = fixture.Save("game", boxes, LabelingExampleCompletion.Ready); var draft = fixture.Save("game", boxes);
        Ids(fixture.Store.LoadTrainingExamples("game.playable-knight"), ready, draft);
        Check(fixture.Store.LoadManifest(ready.DirectoryPath).Completion == LabelingExampleCompletion.Ready);
        var updated = fixture.Store.Update(draft, "game", boxes, completion: LabelingExampleCompletion.Ready);
        Check(updated.Id == draft.Id && fixture.Store.LoadManifest(draft.DirectoryPath).Completion == LabelingExampleCompletion.Ready);
        Ids(fixture.Store.LoadTrainingExamples("game.playable-knight"), ready, draft);
        fixture.Store.Update(updated, "game", boxes);
        Check(fixture.Store.LoadManifest(draft.DirectoryPath).Completion == LabelingExampleCompletion.Draft);
        Ids(fixture.Store.LoadTrainingExamples("game.playable-knight"), ready, draft);
    }

    public static void TestUnknownEmptyReady()
    {
        using var fixture = new Fixture(); var ready = fixture.Save("game", [], LabelingExampleCompletion.Ready);
        var loaded = fixture.Store.LoadManifest(ready.DirectoryPath);
        Check(loaded.Completion == LabelingExampleCompletion.Ready && loaded.Annotations.Count == 0);
        Known(ready); Ids(fixture.Store.LoadTrainingExamples("game.playable-knight")); Ids(fixture.Store.LoadTrainingExamples());
        Ids(fixture.Store.LoadTrainingExamplesInContext("game")); Ids(fixture.Store.Load(), ready);
    }

    public static void TestKnownEmptyTrainingEvidence()
    {
        using var fixture = new Fixture(); var positive = fixture.Save("game", [Box("game.mana")]); var empty = fixture.Save("game", []);
        Known(empty, "game.mana"); Ids(fixture.Store.LoadTrainingExamples(), positive, empty);
        Ids(fixture.Store.LoadTrainingExamples("game.mana"), positive, empty);
        Ids(fixture.Store.LoadTrainingExamplesInContext("game"), positive, empty);
        Ids(fixture.Store.LoadContainingClass("game.mana"), positive);
        Ids(fixture.Store.LoadTrainingExamplesInContext("Gameplay"));
        var menu = fixture.Save("main-title", []);
        Ids(fixture.Store.LoadTrainingExamplesInContext("game"), positive, empty);
        Ids(fixture.Store.LoadTrainingExamplesInContext("main-title"), menu);
        Ids(fixture.Store.LoadTrainingExamples("game.mana"), positive, empty, menu);
        // Store queries are not the exporter's context gate; both policies matter.
    }

    public static void TestUpdateIntroducedClasses()
    {
        using var fixture = new Fixture(); var empty = fixture.Save("game", []);
        var mana = fixture.Save("game", [Box("game.mana")]);
        var updated = fixture.Store.Update(empty, "game", []); Known(updated, "game.mana");
        Check(updated.Id == empty.Id && updated.Manifest.ImageIdentifier == empty.Manifest.ImageIdentifier
            && updated.Manifest.CaptureGroupIdentifier == empty.Manifest.CaptureGroupIdentifier && updated.Manifest.CreatedAt == empty.Manifest.CreatedAt);
        fixture.Store.Delete(mana);
        // Neither an old in-memory snapshot nor other frames' remembered classes
        // may act as permanent introduction evidence after the last box is gone.
        var fresh = fixture.Save("game", []); Known(fresh);
        updated = fixture.Store.Update(updated, "game", []); Known(updated);
        var explicitKnown = fixture.Store.Update(updated, "game", [], ["main-title.options"]); Known(explicitKnown, "shared.options");
        Known(fixture.Store.Update(explicitKnown, "game", []));
        var withBox = fixture.Store.Update(updated, "game", [Box("game.health")]);
        Known(fixture.Store.Update(withBox, "game", []), "game.health"); // Mac reads the previous box before committing this update.
        Known(fixture.Store.Update(new(updated.DirectoryPath, fixture.Store.LoadManifest(updated.DirectoryPath)), "game", []));
    }

    public static void TestStandardizedDrafts()
    {
        using var fixture = new Fixture(); var reversed = new LabelingExampleAnnotation(Guid.NewGuid(), "main-title.options", .75, .8, -.5, -.6, false);
        var saved = fixture.Save("options", [reversed]); var box = saved.Manifest.Annotations.Single();
        Check(box.Id == reversed.Id && box.ClassIdentifier == "shared.options" && box.X == .25 && Math.Abs(box.Y - .2) < 1e-9
            && box.Width == .5 && box.Height == .6 && box.IsNegative is null);
        var updated = fixture.Store.Update(saved, "options", [reversed with { IsNegative = true }]);
        Check(updated.Manifest.Annotations.Single() == (box with { IsNegative = true }));
        var before = File.ReadAllBytes(ManifestPath(updated));
        foreach (var bad in new[] { reversed with { Width = double.NaN }, reversed with { X = -1 }, reversed with { Height = 0 } })
            Throws<ArgumentException>(() => fixture.Store.Update(updated, "options", [bad]));
        Check(File.ReadAllBytes(ManifestPath(updated)).SequenceEqual(before));
    }

    public static void TestPortableManifest()
    {
        using var fixture = new Fixture(); fixture.Time = new DateTimeOffset(2026, 10, 21, 8, 9, 10, TimeSpan.FromHours(-3)).AddTicks(1234567);
        var saved = fixture.Store.Save(Fixture.Image, "game", [Box("game.mana"), Box("game.health", true)], languageIdentifier: "fr");
        using (var json = JsonDocument.Parse(File.ReadAllBytes(ManifestPath(saved))))
        {
            var root = json.RootElement;
            Check(root.GetProperty("createdAt").GetString() == "2026-10-21T11:09:10Z");
            Check(root.GetProperty("completion").GetString() == "draft" && root.GetProperty("languageIdentifier").GetString() == "fr");
            Check(!root.GetProperty("annotations")[0].TryGetProperty("isNegative", out _)
                && root.GetProperty("annotations")[1].GetProperty("isNegative").GetBoolean());
            Check(!root.TryGetProperty("effectiveKnownClassIdentifiers", out _) && !root.TryGetProperty("resolvedLanguageIdentifier", out _));
        }
        Check(saved.Manifest.CreatedAt == fixture.Time, "In-memory capture date must not be rounded.");
        Rewrite(saved, json => { json["createdAt"] = "2026-10-21T08:09:10.1234567-03:00"; json.Remove("languageIdentifier"); });
        var loaded = fixture.Store.LoadManifest(saved.DirectoryPath);
        Check(loaded.CreatedAt == fixture.Time && loaded.ResolvedLanguageIdentifier == "en");
        var pixels = File.ReadAllBytes(saved.ImagePath);
        var updated = fixture.Store.Update(new(saved.DirectoryPath, loaded), "game", loaded.Annotations);
        Check(updated.Manifest.LanguageIdentifier is null && File.ReadAllBytes(saved.ImagePath).SequenceEqual(pixels));
        using var rewritten = JsonDocument.Parse(File.ReadAllBytes(ManifestPath(saved)));
        Check(!rewritten.RootElement.TryGetProperty("languageIdentifier", out _));
        Check(rewritten.RootElement.GetProperty("createdAt").GetString() == "2026-10-21T11:09:10Z");
    }

    public static void TestCanonicalSnapshotPersistence()
    {
        using var fixture = new Fixture(); var saved = fixture.Save("game", [Box("game.mana")]);
        Rewrite(saved, json => json["knownClassIdentifiers"] = new JsonArray("game.mana", "game.health", "game.mana"));
        var loaded = fixture.Store.LoadManifest(saved.DirectoryPath);
        Check(loaded.KnownClassIdentifiers!.SequenceEqual(new[] { "game.health", "game.mana" }));
        using (var persisted = JsonDocument.Parse(File.ReadAllBytes(ManifestPath(saved))))
            Check(persisted.RootElement.GetProperty("knownClassIdentifiers").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "game.health", "game.mana" }));
        var stamp = new DateTime(2026, 10, 20, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ManifestPath(saved), stamp); var before = File.GetLastWriteTimeUtc(ManifestPath(saved));
        fixture.Store.LoadManifest(saved.DirectoryPath);
        Check(File.GetLastWriteTimeUtc(ManifestPath(saved)) == before, "No-op loads must not signal untrained changes.");
        foreach (var schema in new[] { 0, 6 })
        {
            Rewrite(saved, json => json["schemaVersion"] = schema); var bytes = File.ReadAllBytes(ManifestPath(saved));
            Throws<InvalidDataException>(() => fixture.Store.LoadManifest(saved.DirectoryPath));
            Check(File.ReadAllBytes(ManifestPath(saved)).SequenceEqual(bytes));
        }
    }

    public static void TestAtomicFailurePreservation()
    {
        using var fixture = new Fixture(); var saved = fixture.Save("game", [Box("game.mana")]);
        var manifest = File.ReadAllBytes(ManifestPath(saved)); var pixels = File.ReadAllBytes(saved.ImagePath);
        var colliding = new LabelingExampleStore(fixture.Store.RootDirectory, () => saved.Id, () => fixture.Time);
        Throws<IOException>(() => colliding.Save(Fixture.Image, "game", [Box("game.health")]));
        Check(Directory.GetDirectories(fixture.Store.RootDirectory).Length == 1);
        Check(File.ReadAllBytes(ManifestPath(saved)).SequenceEqual(manifest) && File.ReadAllBytes(saved.ImagePath).SequenceEqual(pixels));
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(ManifestPath(saved), FileAttributes.ReadOnly);
            try { Throws<UnauthorizedAccessException>(() => fixture.Store.Update(saved, "game", [Box("game.health")])); }
            finally { File.SetAttributes(ManifestPath(saved), FileAttributes.Normal); }
            Check(File.ReadAllBytes(ManifestPath(saved)).SequenceEqual(manifest) && File.ReadAllBytes(saved.ImagePath).SequenceEqual(pixels));
            Check(Directory.GetFiles(saved.DirectoryPath).Length == 2, "Failed atomic update left temporary files.");
        }
        Check(!Directory.EnumerateDirectories(fixture.Store.RootDirectory).Any(path => Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal)));
    }
}
