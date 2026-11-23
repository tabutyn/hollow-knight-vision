using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class ScreenshotReviewTests
{
    private static void Check(bool condition, string message = "Screenshot review assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static LabelingExampleAnnotation Box(string id, bool negative = false) => new(Guid.NewGuid(), id, .1, .1, .2, .2, negative);
    private static LabelingExampleAnnotation[] ValidBoxes => [Box("game.playable-knight"), Box("game.mana"), Box("game.health")];
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hkv-review-test-" + Guid.NewGuid().ToString("N"));
        public LabelingExampleStore Store { get; }
        private int tick;
        public Fixture() { Store = new(Path.Combine(Root, "examples") + Path.DirectorySeparatorChar, now: () => DateTimeOffset.UnixEpoch.AddSeconds(++tick)); }
        public SavedLabelingExample Save(params LabelingExampleAnnotation[] boxes) => Store.Save(new(2, 2, 8, new byte[16], 0, DateTimeOffset.UnixEpoch), "game", boxes);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static void ChangeIdentity(SavedLabelingExample example)
    {
        var path = Path.Combine(example.DirectoryPath, LabelingExampleStore.ManifestFilename);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!; manifest["id"] = Guid.NewGuid(); File.WriteAllText(path, manifest.ToJsonString());
    }

    public static void TestSelectionAndOrdering()
    {
        using var fixture = new Fixture(); var first = fixture.Save(ValidBoxes); var last = fixture.Save(ValidBoxes);
        var menu = LabelingContractTests.Example("main-title", "main-title.hollow-knight-logo");
        var session = new LabelingScreenshotReviewSession(fixture.Store); session.SetExamples([last, menu, first]);
        Check(session.Examples.Select(item => item.Id).SequenceEqual(new[] { first.Id, last.Id }) && session.Selected?.Id == first.Id);
        Check(session.Select(last.Id) && !session.Select(menu.Id)); session.SetExamples([last, first]); Check(session.Selected?.Id == last.Id);
        session.Move(int.MaxValue); Check(session.Selected?.Id == last.Id); session.Move(int.MinValue); Check(session.Selected?.Id == first.Id);
        session.Select(last.Id); session.SetExamples([first]); Check(session.Selected?.Id == first.Id);
        session.SetExamples([]); session.Move(1); Check(session.Selected is null && session.SelectedIdentifier is null);
        session.Reload(); Check(session.Examples.Count == 2 && session.Selected?.Id == first.Id);
    }

    public static void TestSparseGameplayExamplesAndLegacyExemptions()
    {
        using var fixture = new Fixture();
        var empty = fixture.Save();
        var sign = fixture.Save(Box("world.sign"), Box("world.sign"));
        var exemptionPath = Path.Combine(empty.DirectoryPath, LabelingContractExemptionStore.Filename);
        const string previousExemption = "[\"set.game.game.geo\"]";
        File.WriteAllText(exemptionPath, previousExemption);

        var session = new LabelingScreenshotReviewSession(fixture.Store);
        session.Reload();
        Check(session.Selected?.Id == empty.Id && session.Examples.Count == 2,
            "Blank gameplay screenshots remain available for review.");
        Check(LabelingSharedModelPolicy.HasPositiveGameplayEvidence(session.Examples, LabelingSharedModelPolicy.ClassIdentifiers()),
            "Positive gameplay labels enable training without a Knight or complete screenshot.");
        Check(File.ReadAllText(exemptionPath) == previousExemption,
            "Reviewing existing screenshots leaves the archived exemption file intact.");
        Check(!File.Exists(Path.Combine(sign.DirectoryPath, LabelingContractExemptionStore.Filename)),
            "Positive examples need no exemption metadata.");
    }

    public static void TestConfirmedDeletionAndFailures()
    {
        using var fixture = new Fixture(); var first = fixture.Save(ValidBoxes); var changed = fixture.Save(ValidBoxes); var survivor = fixture.Save(ValidBoxes);
        using var foreign = new Fixture(); var outside = foreign.Save(ValidBoxes);
        new LabelingContractExemptionStore().Save(["unknown-preserved-rule"], first);
        var firstDirectory = first.DirectoryPath; var session = new LabelingScreenshotReviewSession(fixture.Store);
        session.SetExamples([first, changed, survivor, outside]); session.Select(first.Id); ChangeIdentity(changed);
        var result = session.DeleteConfirmed([first.Id, changed.Id, outside.Id, Guid.NewGuid(), first.Id]);
        Check(result.Deleted.SequenceEqual(new[] { first.Id }) && result.Failures.Count == 2);
        Check(!Directory.Exists(firstDirectory) && Directory.Exists(changed.DirectoryPath) && File.Exists(outside.ImagePath)
            && File.Exists(survivor.ImagePath), "Deletion escaped its confirmed owned examples.");
        Check(session.Selected?.Id != first.Id && session.Examples.Count == 3);
        var missing = session.DeleteConfirmed([first.Id]); Check(missing.Deleted.Count == 0 && missing.Failures.Count == 0);
        session.SetExamples([survivor]); Check(session.DeleteConfirmed([survivor.Id]).Deleted.Count == 1 && session.Selected is null);
    }

    public static void TestTrainingUsesPositiveGameplayVocabulary()
    {
        using var fixture = new Fixture();
        var image = new BgraFrame(2, 2, 8, new byte[16], 0, DateTimeOffset.UnixEpoch);
        var empty = fixture.Store.Save(image, "game", [], knownClassIdentifiers: ["world.sign"]);
        var negative = fixture.Save(Box("world.sign", negative: true));
        var hud = fixture.Save(Box("game.health"), Box("game.mana"));
        var misplacedMenuLabel = fixture.Save(Box("main-title.options"));
        var misplacedGameplayLabel = LabelingContractTests.Example("main-title", "world.sign");
        var custom = fixture.Save(Box("custom.wisp"));
        var builtIn = LabelingSharedModelPolicy.ClassIdentifiers();
        var customClasses = LabelingSharedModelPolicy.ClassIdentifiers([new("custom.wisp", "Wisp")]);

        Check(!LabelingSharedModelPolicy.HasPositiveGameplayEvidence([], builtIn));
        Check(!LabelingSharedModelPolicy.HasPositiveGameplayEvidence(
            [empty, negative, hud, misplacedMenuLabel, misplacedGameplayLabel, custom], builtIn),
            "Only positive examples within the gameplay model vocabulary enable training.");
        Check(LabelingSharedModelPolicy.HasPositiveGameplayEvidence([custom], customClasses),
            "A registered custom label may be the only positive gameplay label.");
        Check(fixture.Store.LoadTrainingExamplesInContext("game").Any(example => example.Id == empty.Id),
            "An explicitly empty gameplay screenshot remains usable as training evidence.");
    }

    public static void TestVisualIdentity()
    {
        var ids = LabelingContractCatalog.Contexts.SelectMany(context => context.Labels).Select(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var palette = new LabelingVisualIdentity(); var colors = ids.Select(palette.Rgb).ToArray();
        Check(palette.Rgb("game.mana").Red == palette.Rgb("game.mana").Red && palette.Rgb("game.mana").Red != palette.Rgb("game.health").Red);
        Check(colors.Distinct().Count() == ids.Length && colors.All(color => new[] { color.Red, color.Green, color.Blue }.All(channel => channel >= .219999 && channel <= 1)));
        Check(colors[0] == new LabelingRgb(1, 1 - .78, 1 - .78));
        Check(palette.Rgb("main-title.options") == palette.Rgb("shared.options") && LabelingVisualIdentity.DisplayName("main-title.options") == "Options");
        var custom = new LabelingVisualIdentity(["custom.🔵-λ", "custom.sign"]);
        var reverse = new LabelingVisualIdentity(["custom.sign", "custom.🔵-λ", "custom.sign"]);
        Check(custom.Rgb("custom.🔵-λ") == reverse.Rgb("custom.🔵-λ") && custom.Rgb("custom.sign") != custom.Rgb("custom.🔵-λ"));
        Check(palette.Rgb("custom.🔵-λ") == new LabelingVisualIdentity().Rgb("custom.🔵-λ") && LabelingVisualIdentity.DisplayName("custom.sign") == "custom.sign");
        // UTF-8 DJB2 with signed-64 overflow, then Int.max masking, as in Swift.
        var hash = System.Numerics.BigInteger.Parse("5381");
        foreach (var value in System.Text.Encoding.UTF8.GetBytes("custom.🔵-λ")) hash = hash * 33 + value;
        var index = (int)((hash & long.MaxValue) % ids.Length);
        Check(palette.Rgb("custom.🔵-λ") == colors[index], "Fallback was not Swift's 64-bit UTF-8 hash.");
    }
}
