using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class LabelingSelectionTests
{
    private static void Check(bool condition, string message = "Label selection assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static LabelingExampleAnnotation Box(string identifier, bool negative = false) => new(Guid.NewGuid(), identifier, .1, .1, .2, .2, negative);
    private static BgraFrame Frame(byte color = 32) => new(2, 2, 8,
        Enumerable.Range(0, 16).Select(index => index % 4 == 3 ? (byte)255 : color).ToArray(), color, DateTimeOffset.UnixEpoch);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hkv-selection-" + Guid.NewGuid().ToString("N"));
        public string PreferencesPath => Path.Combine(Root, "selection.json");
        public LabelingSelectionPreferences Preferences => new(PreferencesPath);
        public LabelingExampleStore Store => new(Path.Combine(Root, "examples"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    public static void TestSelectionReload()
    {
        using var fixture = new Fixture(); var preferences = fixture.Preferences;
        Check(preferences.Load().ContextIdentifier == "game" && !Directory.Exists(fixture.Root));
        preferences.SaveContext("main-title"); preferences.SaveClassIdentifier("main-title.start-game");
        Check(fixture.Preferences.Load() == new LabelingSelection("main-title", "main-title.start-game"));
        var initial = File.ReadAllBytes(fixture.PreferencesPath); var modified = File.GetLastWriteTimeUtc(fixture.PreferencesPath);
        preferences.Save("main-title", "main-title.start-game");
        Check(File.ReadAllBytes(fixture.PreferencesPath).SequenceEqual(initial) && File.GetLastWriteTimeUtc(fixture.PreferencesPath) == modified,
            "Unchanged selection rewrote preferences.");
    }

    public static void TestSharedSelectionStaysInSavedSet()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(fixture.PreferencesPath, JsonSerializer.Serialize(new Dictionary<string, string> {
            [LabelingSelectionPreferences.ContextKey] = "main-title",
            [LabelingSelectionPreferences.ClassIdentifierKey] = "main-title.select-decoration" }));
        Check(fixture.Preferences.Load() == new LabelingSelection("main-title", "shared.select-decoration"));
        Check(LabelingSelectionPreferences.Resolve("options", "main-title.select-decoration") == new LabelingSelection("options", "shared.select-decoration"));
        Check(LabelingSelectionPreferences.Resolve("game", "game.health") == new LabelingSelection("game", "game.health"));
        Check(LabelingSelectionPreferences.Resolve("gameplay-hud", "game.health") == new LabelingSelection("gameplay-hud", "game.health"));
    }

    public static void TestSelectionMovesToContainingSet()
    {
        using var fixture = new Fixture(); var preferences = fixture.Preferences;
        preferences.SaveContext("main-title"); preferences.SaveClassIdentifier("game.mana");
        Check(fixture.Preferences.Load() == new LabelingSelection("game", "game.mana"));
        Check(LabelingSelectionPreferences.Resolve("main-title", "game.health") == new LabelingSelection("gameplay-hud", "game.health"));
        Check(LabelingSelectionPreferences.Resolve("world", "enemies.crawlid") == new LabelingSelection("game", "enemies.crawlid"));
        Check(LabelingSelectionPreferences.Resolve("shared", "main-title.options") == new LabelingSelection("main-title", "shared.options"));
        Check(LabelingSelectionPreferences.Resolve("main-title", "custom.bench", ["custom.bench"]) == new LabelingSelection("game", "custom.bench"));
    }

    public static void TestInvalidSelectionAndWriteRecovery()
    {
        using var fixture = new Fixture(); var preferences = fixture.Preferences;
        Check(LabelingSelectionPreferences.Resolve("unknown", "unknown").ContextIdentifier == "game");
        Check(LabelingSelectionPreferences.Resolve("options", "unknown").ClassIdentifier == LabelingFrameOpeningPolicy.ContextClasses("options").First());
        preferences.Save("main-title", ""); Check(preferences.Load().ContextIdentifier == "main-title");
        File.WriteAllText(fixture.PreferencesPath, "not-json"); Check(preferences.Load().ContextIdentifier == "game");
        preferences.Save("game", "game.mana"); Check(preferences.Load().ClassIdentifier == "game.mana");
        var blocked = Path.Combine(fixture.Root, "blocked.json"); Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "sentinel.txt"), "preserve");
        try { new LabelingSelectionPreferences(blocked).Save("game", "game.mana"); throw new Exception("Expected blocked atomic commit."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Check(File.ReadAllText(Path.Combine(blocked, "sentinel.txt")) == "preserve"
            && !Directory.EnumerateFiles(fixture.Root, "*.tmp").Any(), "Failed preference write damaged data or leaked staging files.");
    }

    public static void TestContextSwitchPreservesSavedScreens()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var title = store.Save(Frame(), "main-title", [Box("main-title.start-game"), Box("main-title.options")], languageIdentifier: "fr");
        var options = store.Save(Frame(80), "options", [Box("shared.audio"), Box("shared.back", true)], languageIdentifier: "fr");
        var titleManifest = File.ReadAllBytes(Path.Combine(title.DirectoryPath, LabelingExampleStore.ManifestFilename));
        var titleImage = File.ReadAllBytes(title.ImagePath); var frozen = Frame(120);
        var destination = LabelingContextTransitionPolicy.Resolve("options", title, frozen, context => context == "options" ? options : null)!;
        Check(destination.Example?.Id == options.Id && destination.Capture is null && destination.CaptureGroupIdentifier == options.Manifest.CaptureGroupIdentifier);
        Check(destination.Rectangles.SequenceEqual(options.Manifest.Annotations) && destination.Rectangles[1].IsHardNegative);
        var updated = store.Update(destination.Example!, destination.ContextIdentifier, [.. destination.Rectangles, Box("shared.video")], destination.KnownClasses);
        Check(updated.Id == options.Id && updated.Manifest.ResolvedLanguageIdentifier == "fr");
        Check(File.ReadAllBytes(Path.Combine(title.DirectoryPath, LabelingExampleStore.ManifestFilename)).SequenceEqual(titleManifest)
            && File.ReadAllBytes(title.ImagePath).SequenceEqual(titleImage), "Switching context reclassified or rewrote the previous screenshot.");
        var returnToTitle = LabelingContextTransitionPolicy.Resolve("main-title", updated, frozen, _ => title)!;
        Check(returnToTitle.Rectangles.SequenceEqual(title.Manifest.Annotations) && returnToTitle.Example?.Id == title.Id);
    }

    public static void TestMissingContextAndGameplayStartFresh()
    {
        using var fixture = new Fixture(); var store = fixture.Store; var frozen = Frame(120);
        var title = store.Save(Frame(), "main-title", [Box("main-title.start-game")]);
        var oldGameplay = store.Save(Frame(80), "game", [Box("game.playable-knight")]);
        var gameplay = LabelingContextTransitionPolicy.Resolve("game", title, frozen, _ => throw new Exception("Gameplay must not restore an arbitrary training image."))!;
        Check(gameplay.Example is null && ReferenceEquals(gameplay.Capture, frozen) && gameplay.Rectangles.Count == 0 && gameplay.KnownClasses.Count == 0);
        var fresh = store.Save(gameplay.Capture!, "game", [Box("game.mana")], captureGroupIdentifier: gameplay.CaptureGroupIdentifier);
        Check(fresh.Id != oldGameplay.Id && fresh.Manifest.CaptureGroupIdentifier != title.Manifest.CaptureGroupIdentifier);
        Check(store.Load().Single(item => item.Id == oldGameplay.Id).Manifest.Annotations.Single().ClassIdentifier == "game.playable-knight");
        var audio = LabelingContextTransitionPolicy.Resolve("audio", title, frozen, _ => null)!;
        Check(audio.Example is null && ReferenceEquals(audio.Capture, frozen) && audio.Rectangles.Count == 0
            && audio.CaptureGroupIdentifier != gameplay.CaptureGroupIdentifier);
        Check(LabelingContextTransitionPolicy.Resolve("main-title", title, frozen, _ => throw new Exception("Unchanged context reloaded.")) is null);
        try { LabelingContextTransitionPolicy.Resolve("audio", title, frozen, _ => title); throw new Exception("Mismatched reference accepted."); }
        catch (InvalidOperationException) { }
    }

    public static void TestSelectionUpdateGuard()
    {
        var guard = new LabelingSelectionUpdateGuard();
        Check(!guard.IsUpdating);
        guard.Run(() => { Check(guard.IsUpdating); guard.Run(() => Check(guard.IsUpdating)); Check(guard.IsUpdating); });
        Check(!guard.IsUpdating);
        try { guard.Run(() => guard.Run(() => throw new IOException("isolated load failure"))); }
        catch (IOException) { }
        Check(!guard.IsUpdating, "Failed image load permanently suppressed future user selection.");
    }
}
