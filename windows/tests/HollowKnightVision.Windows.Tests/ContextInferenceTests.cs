using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class ContextInferenceTests
{
    private static void Check(bool condition, string message = "Context inference assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static LabelingExampleAnnotation Box(string id, bool negative = false) => new(Guid.NewGuid(), id, .1, .1, .2, .2, negative ? true : null);
    private static LabelingExampleAnnotation[] Brightness() =>
        new[] { "shared.brightness", "brightness.pointer", "shared.back", "shared.select-decoration", "shared.select-decoration" }.Select(id => Box(id)).ToArray();

    public static void TestClearMultiObjectMismatch()
    {
        var gameplay = new[] { "game.playable-knight", "game.mana", "game.health", "game.geo" }.Select(id => Box(id)).ToArray();
        Check(LabelingExampleContextInference.Infer("options", gameplay) == "game");
        Check(LabelingExampleContextInference.Infer("shared", [Box("shared.yes"), Box("shared.no")]) == "shared");
        Check(LabelingExampleContextInference.Infer("unknown", gameplay) == "game");
        Check(LabelingExampleContextInference.Infer("options", gameplay.Where(item => item.ClassIdentifier != "game.geo")) == "game");
        Check(LabelingExampleContextInference.Infer("options", [.. gameplay, Box("enemies.crawlid"), Box("enemies.crawlid")]) == "game");
    }

    public static void TestCompleteContractNotOverlap()
    {
        var complete = Brightness();
        Check(LabelingExampleContextInference.Infer("screen-scale", complete) == "brightness");
        Check(LabelingExampleContextInference.Infer("brightness", complete) == "brightness");
        Check(LabelingExampleContextInference.Infer("screen-scale", [Box("shared.back"), Box("shared.select-decoration"), Box("shared.select-decoration")]) == "screen-scale");
        Check(LabelingExampleContextInference.Infer("screen-scale", complete.Select(item => item.ClassIdentifier == "shared.back"
            ? item with { ClassIdentifier = "select-profile.back" } : item)) == "brightness");
    }

    public static void TestAmbiguityCountsAndNegatives()
    {
        var complete = Brightness(); var catalog = LabelingContractCatalog.Bundled;
        Check(LabelingExampleContextInference.Infer("screen-scale", [.. complete, Box("shared.back")]) == "screen-scale");
        Check(LabelingExampleContextInference.Infer("screen-scale", [.. complete, Box("custom.banner")]) == "screen-scale");
        Check(LabelingExampleContextInference.Infer("screen-scale", [.. complete, Box("custom.banner", true)]) == "brightness");
        Check(LabelingExampleContextInference.Infer("screen-scale", complete.Select(item => item with { IsNegative = true })) == "screen-scale");
        Check(LabelingExampleContextInference.Infer("screen-scale", complete.Skip(1).Append(complete[0] with { IsNegative = true })) == "screen-scale");
        Check(LabelingExampleContextInference.Infer("options", [Box("game.health"), Box("game.health", true)]) == "options");
        Check(LabelingExampleContextInference.Infer("options", [Box("game.health"), Box("game.health")]) == "gameplay-hud");
        var brightness = catalog.SetContract("brightness")!;
        Check(LabelingExampleContextInference.Infer("screen-scale", complete, catalog with { LabelSets = [brightness, brightness] }) == "screen-scale",
            "Tied exact fits must never migrate by catalog order.");
        Check(LabelingExampleContextInference.Infer("screen-scale", complete, catalog with { LabelSets = [brightness with { RequiresAllContextObjects = false }] }) == "screen-scale");
        Check(LabelingExampleContextInference.Infer("screen-scale", complete, catalog with { LabelSets = [new("unknown", true, null)] }) == "screen-scale");
    }

    public static void TestDurableStoreRepair()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-context-repair-" + Guid.NewGuid().ToString("N"));
        var store = new LabelingExampleStore(root);
        var frame = new BgraFrame(2, 2, 8, Enumerable.Repeat((byte)255, 16).ToArray(), 0, DateTimeOffset.UnixEpoch);
        try
        {
            var original = store.Save(frame, "screen-scale", [Box("shared.brightness")], languageIdentifier: "fr");
            var pixels = File.ReadAllBytes(original.ImagePath); var complete = Brightness();
            var updated = store.Update(original, "screen-scale", complete);
            Check(updated.Manifest.ContextIdentifier == "brightness" && store.LoadManifest(updated.DirectoryPath).ContextIdentifier == "brightness");
            Check(updated.Id == original.Id && updated.Manifest.ImageIdentifier == original.Manifest.ImageIdentifier
                && updated.Manifest.CaptureGroupIdentifier == original.Manifest.CaptureGroupIdentifier
                && updated.Manifest.CreatedAt == original.Manifest.CreatedAt && updated.Manifest.LanguageIdentifier == "fr"
                && updated.Manifest.Annotations.SequenceEqual(complete) && File.ReadAllBytes(original.ImagePath).SequenceEqual(pixels),
                "Context repair changed screenshot pixels, annotations, language or capture identity.");

            // Initial save retains explicit context; load migrates it atomically, as on Mac.
            var legacy = store.Save(frame, "screen-scale", complete);
            Check(legacy.Manifest.ContextIdentifier == "screen-scale");
            var manifestPath = Path.Combine(legacy.DirectoryPath, LabelingExampleStore.ManifestFilename);
            Check(store.LoadManifest(legacy.DirectoryPath).ContextIdentifier == "brightness");
            using (var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath)))
                Check(document.RootElement.GetProperty("contextIdentifier").GetString() == "brightness");
            var contents = File.ReadAllBytes(manifestPath); var timestamp = File.GetLastWriteTimeUtc(manifestPath);
            _ = store.LoadManifest(legacy.DirectoryPath);
            Check(File.ReadAllBytes(manifestPath).SequenceEqual(contents) && File.GetLastWriteTimeUtc(manifestPath) == timestamp,
                "Already-correct context was repeatedly rewritten.");
            Check(!Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.AllDirectories).Any());

            var partial = store.Save(frame, "main-title", [Box("main-title.start-game")]);
            Check(store.Update(partial, "options", partial.Manifest.Annotations).Manifest.ContextIdentifier == "main-title",
                "A selector change alone reclassified an existing incomplete example.");
            var empty = store.Save(frame, "main-title", []);
            Check(store.Update(empty, "options", [Box("shared.audio")]).Manifest.ContextIdentifier == "options");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
