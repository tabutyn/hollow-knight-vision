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

    public static void TestExemptionsAndTraining()
    {
        using var fixture = new Fixture(); var good = fixture.Save(ValidBoxes); var bad = fixture.Save(Box("game.playable-knight"), Box("game.playable-knight"), Box("world.sign"), Box("world.sign"));
        var session = new LabelingScreenshotReviewSession(fixture.Store); session.Reload();
        Check(session.BreachCount == 1 && !session.CanTrainModel() && !session.CanTrainSelectedClass("game.playable-knight"));
        Check(session.Outline(good.Id) == LabelingScreenshotOutline.Selected && session.Outline(bad.Id) == LabelingScreenshotOutline.Breach);
        var satisfied = session.Evaluations(good.Id).First(evaluation => evaluation.IsSatisfied).Id;
        Check(!session.ToggleExemption(good.Id, satisfied) && !session.ToggleExemption(good.Id, "unknown"));
        Check(!File.Exists(Path.Combine(good.DirectoryPath, LabelingContractExemptionStore.Filename)), "A disabled contract wrote an exemption.");
        var failed = session.Evaluations(bad.Id).Where(evaluation => evaluation.IsBreach).Select(evaluation => evaluation.Id).ToArray();
        Check(failed.Length >= 2);
        session.Select(bad.Id); session.ToggleExemption(bad.Id, failed[0]);
        Check(session.Outline(bad.Id) == LabelingScreenshotOutline.Breach, "Selection or exemption hid another breach.");
        foreach (var id in failed.Skip(1)) Check(session.ToggleExemption(bad.Id, id));
        Check(session.Outline(bad.Id) == LabelingScreenshotOutline.Exempt && session.BreachCount == 0);
        Check(session.CanTrainModel() && !session.CanTrainModel(training: true) && !session.CanTrainSelectedClass("shared.options"),
            "Global model training incorrectly depends on the leftover menu selection.");
        Check(session.CanTrainSelectedClass("game.playable-knight") && !session.CanTrainSelectedClass("custom.absent")
            && !session.CanTrainSelectedClass("game.playable-knight", training: true) && !session.CanTrainSelectedClass(" "));
        session.Reload(); Check(session.BreachCount == 0 && session.Selected?.Id == bad.Id);
        Check(new LabelingContractExemptionStore().Load(bad).SetEquals(failed));
        Check(session.ToggleExemption(bad.Id, failed[0]) && session.BreachCount == 1 && !session.CanTrainSelectedClass("game.playable-knight"));
        // A now-satisfied check that was exempted remains removable.
        new LabelingContractExemptionStore().Save([satisfied], good); session.Reload();
        Check(session.ToggleExemption(good.Id, satisfied) && new LabelingContractExemptionStore().Load(good).Count == 0);
    }

    public static void TestFailedAndStaleExemptionWrites()
    {
        using var fixture = new Fixture(); var example = fixture.Save(Box("game.playable-knight"), Box("game.playable-knight"), Box("world.sign"), Box("world.sign"));
        var session = new LabelingScreenshotReviewSession(fixture.Store); session.Reload();
        var rule = session.Evaluations(example.Id).First(evaluation => evaluation.IsBreach).Id;
        var blocked = Path.Combine(example.DirectoryPath, LabelingContractExemptionStore.Filename); Directory.CreateDirectory(blocked);
        try { session.ToggleExemption(example.Id, rule); throw new Exception("Blocked exemption write succeeded."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Check(session.Evaluations(example.Id).Single(evaluation => evaluation.Id == rule).IsBreach && session.BreachCount == 1);
        Check(Directory.Exists(blocked) && !Directory.EnumerateFiles(example.DirectoryPath, "*.tmp-*").Any());
        Directory.Delete(blocked); ChangeIdentity(example);
        try { session.ToggleExemption(example.Id, rule); throw new Exception("Replaced screenshot was edited."); }
        catch (InvalidOperationException) { }
        Check(!File.Exists(blocked) && session.BreachCount == 1);
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
        Check(session.Selected?.Id != first.Id && session.Evaluations(first.Id).Count == 0 && session.Examples.Count == 3);
        var missing = session.DeleteConfirmed([first.Id]); Check(missing.Deleted.Count == 0 && missing.Failures.Count == 0);
        session.SetExamples([survivor]); Check(session.DeleteConfirmed([survivor.Id]).Deleted.Count == 1 && session.Selected is null);
    }

    public static void TestKnownEmptyNegativeAndCanonicalTraining()
    {
        using var fixture = new Fixture(); var negative = fixture.Save([.. ValidBoxes, Box("custom.wisp", true), Box("main-title.options")]);
        var empty = fixture.Save(); var session = new LabelingScreenshotReviewSession(fixture.Store); session.Reload();
        Check(session.BreachCount == 1 && session.Evaluations(empty.Id).Any(item => item.Id == "screenshot.has-label" && item.IsBreach));
        foreach (var id in session.Evaluations(empty.Id).Where(item => item.IsBreach).Select(item => item.Id).ToArray()) session.ToggleExemption(empty.Id, id);
        Check(!session.CanTrainSelectedClass("custom.wisp") && session.CanTrainSelectedClass("main-title.options") && session.CanTrainSelectedClass("shared.options"));
        session.SetExamples([empty]); Check(!session.CanTrainModel() && !session.CanTrainSelectedClass("game.playable-knight"), "Exempt empty screenshots invented positive examples.");
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
