using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class LabelingContractTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Labeling contract assertion failed."); }
    private static LabelingExampleAnnotation Annotation(string id, bool negative = false) => new(Guid.NewGuid(), id, .1, .1, .2, .2, negative);
    internal static SavedLabelingExample Example(string context, params string[] classes) => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        new(5, Guid.NewGuid(), Guid.NewGuid(), "image.png", 640, 360, Guid.NewGuid(), context,
            classes.Select(id => Annotation(id)).ToArray(), classes, LabelingExampleCompletion.Draft, DateTimeOffset.UtcNow));
    public static void TestCatalogAndIncompleteScreens()
    {
        var catalog = LabelingContractCatalog.Bundled;
        Check(catalog.Occurrence("main-title.select-decoration")!.AllowedCounts!.SequenceEqual(new[] { 0, 2 }));
        Check(catalog.Pairs.First().ClassIdentifiers.SequenceEqual(new[] { "shared.yes", "shared.no" }));
        Check(catalog.SetsContaining("shared.options").Any(set => set.ContextIdentifier == "main-title"));
        Check(catalog.SetsContaining("shared.options").Any(set => set.ContextIdentifier == "options"));
        var result = LabelingContractEvaluator.Evaluate(Example("main-title", "main-title.hollow-knight-logo", "shared.select-decoration"));
        Check(result.Single(item => item.Id == "set.main-title.main-title.start-game").IsBreach);
        Check(result.Single(item => item.Id == "set.main-title.shared.select-decoration").IsBreach);
        Check(LabelingContractEvaluator.Evaluate(Example("main-title")).Any(item => item.Id == "screenshot.has-label" && item.IsBreach));
        Check(LabelingContractCatalog.Contexts.Single(context => context.Identifier == "shared").Labels.Count == 23);
        Check(LabelingContractCatalog.Contexts.Single(context => context.Identifier == "enemies").Labels.Count == 3);
        Check(LabelingContractCatalog.Contexts.Single(context => context.Identifier == "world").Labels.Count == 3);
    }
    public static void TestCountsAndNegatives()
    {
        var labels = new[] { "screen-scale.screen-corner", "screen-scale.screen-corner", "screen-scale.scale", "shared.select-decoration", "shared.select-decoration", "shared.back" };
        Check(LabelingContractEvaluator.Evaluate(Example("screen-scale", labels)).All(item => !item.IsBreach));
        Check(LabelingContractEvaluator.Evaluate(Example("screen-scale", labels.Skip(1).ToArray())).Any(item => item.Id == "set.screen-scale.screen-scale.screen-corner" && item.IsBreach));
        Check(LabelingContractEvaluator.Evaluate(Example("screen-scale", labels.Where((_, index) => index != 3).ToArray())).Any(item => item.Id == "set.screen-scale.shared.select-decoration" && item.IsBreach));
        Check(!LabelingContractEvaluator.Evaluate(Example("game", "game.playable-knight", "game.mana", "game.health")).Any(item => item.Id == "set.game.game.geo" && item.IsBreach));
        var excess = Example("screen-scale", [.. labels, "shared.back"]);
        Check(LabelingContractEvaluator.Evaluate(excess).Any(item => item.Id == "set.screen-scale.shared.back" && item.IsBreach));
        excess = excess with { Manifest = excess.Manifest with { Annotations = excess.Manifest.Annotations.Select((item, index) => index == 6 ? item with { IsNegative = true } : item).ToArray() } };
        Check(LabelingContractEvaluator.Evaluate(excess).All(item => !item.IsBreach));
        Check(LabelingContractEvaluator.Evaluate(Example("unknown", "enemies.crawlid", "enemies.crawlid")).All(item => !item.IsBreach));
        Check(LabelingContractEvaluator.Evaluate(Example("unknown", "shared.select-decoration")).Single().IsBreach);
    }
    public static void TestAdvanceAndExemptions()
    {
        Check(!LabelingContractEvaluator.ShouldAdvance("shared.select-decoration", "main-title", [Annotation("main-title.select-decoration")]));
        Check(LabelingContractEvaluator.ShouldAdvance("shared.select-decoration", "main-title", [Annotation("main-title.select-decoration"), Annotation("shared.select-decoration")]));
        Check(LabelingContractEvaluator.ShouldAdvance("main-title.start-game", "main-title", [Annotation("main-title.start-game")]));
        Check(!LabelingContractEvaluator.ShouldAdvance("enemies.crawlid", "enemies", [Annotation("enemies.crawlid")]));
        Check(!LabelingContractEvaluator.ShouldAdvance("game.health", "gameplay-hud", [Annotation("game.health"), Annotation("game.health", true)]));
        var example = Example("quit-to-menu", "shared.yes");
        Check(LabelingContractEvaluator.Evaluate(example).Single(item => item.Id == "pair.yes-no").IsBreach);
        var pair = LabelingContractEvaluator.Evaluate(example, exemptions: new HashSet<string> { "pair.yes-no" }).Single(item => item.Id == "pair.yes-no");
        Check(!pair.IsBreach && pair.IsExempt && !pair.IsSatisfied);
        var directory = Path.Combine(Path.GetTempPath(), "hkv-contract-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            example = example with { DirectoryPath = directory }; var store = new LabelingContractExemptionStore();
            store.Save(["set.game.game.geo", "screenshot.has-label"], example);
            Check(store.Load(example).SetEquals(new[] { "screenshot.has-label" }));
            Check(JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, LabelingContractExemptionStore.Filename)))!.SequenceEqual(new[] { "screenshot.has-label" }));
        }
        finally { Directory.Delete(directory, true); }
    }
    public static void TestMenuAdmission()
    {
        string[] Labels(string context) => LabelingContractCatalog.Contexts.Single(item => item.Identifier == context).Labels.SelectMany(label =>
            Enumerable.Repeat(label.Identifier, LabelingContractCatalog.Bundled.SetContract(context)!.OccurrenceOverrides?.GetValueOrDefault(label.Identifier)?.Minimum ?? 1)).ToArray();
        var complete = Example("main-title", Labels("main-title"));
        Check(complete.Manifest.Completion == LabelingExampleCompletion.Draft && LabelingContractEvaluator.AcceptsMenuExample(complete));
        Check(!LabelingContractEvaluator.AcceptsMenuExample(Example("main-title", "main-title.start-game") with { Manifest = Example("main-title", "main-title.start-game").Manifest with { Completion = LabelingExampleCompletion.Ready } }));
        Check(!LabelingContractEvaluator.AcceptsMenuExample(Example("main-title", [.. Labels("main-title"), "main-title.start-game"])));
        var normal = Example("options", Labels("options").Where(id => id != "shared.mods").ToArray());
        Check(LabelingContractEvaluator.Evaluate(normal).Any(item => item.IsBreach) && LabelingContractEvaluator.AcceptsMenuExample(normal));
        Check(!LabelingContractEvaluator.AcceptsMenuExample(Example("options", normal.Manifest.Annotations.Select(item => item.ClassIdentifier).Where(id => id != "shared.back").ToArray())));
        Check(!LabelingContractEvaluator.AcceptsMenuExample(Example("unknown", "a")));
    }
}
