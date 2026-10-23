using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class ReviewAnalysisTests
{
    private static void Check(bool condition, string message = "Review analysis assertion failed.") { if (!condition) throw new Exception(message); }
    private static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < 1e-9, $"Expected {expected}; got {actual}.");
    private static LabelingReviewBox Box(string label, double x = .1, double confidence = 1) => new(label, x, .2, .1, .1, confidence);
    private static LabelingReviewExample Example(Guid id, LabelingReviewBox[] human, LabelingReviewBox[] predictions) => new(id, $"{id}.png", "validation", human, predictions);
    private static LabelingReviewMetrics Metrics => new(.5, .5, 0, 0, 0, 0, 0, 0);
    private static LabelingModelCandidate Candidate(LabelingPredictionReview? review, params string[] classes) => new("test-run", "test-dataset",
        new() { Id = Guid.NewGuid(), DatasetIdentifier = Guid.NewGuid(), ClassIdentifier = LabelingModelVersionStore.SharedObjectModel,
            CompletedAt = DateTimeOffset.UnixEpoch, ModelFilename = "model.onnx", PredictionsFilename = "predictions.json", ClassIdentifiers = classes }, review);

    public static void TestSummariesByClass()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var review = new LabelingPredictionReview([
            Example(first, [Box("decoration", .1), Box("decoration", .7)], [Box("decoration", .1, .9)]),
            Example(second, [Box("title", .2)], [Box("title", .2, .8), Box("title", .6, .7), Box("decoration", .7, .2)])], Metrics);
        var summaries = LabelingPredictionReviewAnalyzer.Summaries(review, ["known-but-absent"]);
        Check(summaries.Select(value => value.ClassIdentifier).SequenceEqual(new[] { "decoration", "title", "known-but-absent" }));
        Check(summaries[0].TruePositives == 1 && summaries[0].FalseNegatives == 1 && summaries[0].FalsePositives == 0 && summaries[0].FailingExampleIdentifiers.SetEquals([first]));
        Check(summaries[1].TruePositives == 1 && summaries[1].FalsePositives == 1 && summaries[1].FailingExampleIdentifiers.SetEquals([second]));
        Check(!summaries[2].HasFailures && summaries[2].TruePositives == 0); Near(1, summaries[0].MeanIntersectionOverUnion);
    }

    public static void TestPriorityRanking()
    {
        var saved = LabelingContractTests.Example("enemies", "enemies.crawlid", "enemies.crawlid");
        var review = new LabelingPredictionReview([Example(saved.Id, [Box("enemies.crawlid")], [Box("enemies.crawlid", .6), Box("enemies.vengfly", .7)])], Metrics);
        var values = LabelingObjectPriorityAnalyzer.Summaries([saved], review, ["enemies.crawlid", "enemies.vengfly", "enemies.shade"], ["enemies.crawlid", "enemies.vengfly"]);
        Check(LabelingObjectPriorityAnalyzer.LeastRegistered(values).Select(value => value.ClassIdentifier).SequenceEqual(new[] { "enemies.vengfly", "enemies.shade", "enemies.crawlid" }));
        Check(LabelingObjectPriorityAnalyzer.MostErrors(values).Select(value => value.ClassIdentifier).SequenceEqual(new[] { "enemies.crawlid", "enemies.vengfly" }));
        Check(values.Single(value => value.ClassIdentifier == "enemies.crawlid").RegistrationCount == 2 && !values.Single(value => value.ClassIdentifier == "enemies.shade").HasModelReview);
    }

    public static void TestFailureNavigation()
    {
        var first = Guid.NewGuid(); var passing = Guid.NewGuid(); var last = Guid.NewGuid();
        var review = new LabelingPredictionReview([Example(first, [Box("target")], []),
            Example(passing, [Box("target", .2)], [Box("target", .2)]), Example(last, [], [Box("target", .7)])], Metrics);
        var identifiers = LabelingFailureReviewNavigator.OrderedExampleIdentifiers("target", review);
        Check(identifiers.SequenceEqual(new[] { first, last }));
        Check(LabelingFailureReviewNavigator.MovedIdentifier(first, 1, identifiers) == last && LabelingFailureReviewNavigator.MovedIdentifier(first, -1, identifiers) == last);
        Check(LabelingFailureReviewNavigator.MovedIdentifier(last, int.MaxValue, identifiers) == first && LabelingFailureReviewNavigator.MovedIdentifier(first, int.MinValue, identifiers) == first);
        Check(LabelingFailureReviewNavigator.MovedIdentifier(null, 0, [first, Guid.Empty]) == first && LabelingFailureReviewNavigator.MovedIdentifier(Guid.Empty, 0, [first, Guid.Empty]) == Guid.Empty);
        Check(LabelingFailureReviewNavigator.MovedIdentifier(Guid.NewGuid(), 1, identifiers) == last && LabelingFailureReviewNavigator.MovedIdentifier(null, 1, []) is null);
        var duplicates = review with { Examples = [review.Examples[0], review.Examples[2], review.Examples[0]] };
        Check(LabelingFailureReviewNavigator.OrderedExampleIdentifiers("target", duplicates).SequenceEqual(new[] { first, last }));
    }

    public static void TestConfidenceOrderedOneToOne()
    {
        var human = new[] { Box("target", 0) with { Width = .2 }, Box("target", .15) with { Width = .2 } };
        var predictions = new[] { Box("target", 0, .6) with { Width = .2 }, Box("target", .06, .9) with { Width = .2 } };
        var review = new LabelingPredictionReview([Example(Guid.NewGuid(), human, predictions)], Metrics with { IntersectionOverUnionThreshold = .3 });
        var summary = LabelingPredictionReviewAnalyzer.Summaries(review).Single();
        Check(summary.TruePositives == 1 && summary.FalsePositives == 1 && summary.FalseNegatives == 1, "Review used input order or reused a human box instead of confidence-ordered greedy matching.");
        Near(.14 / .26, summary.MeanIntersectionOverUnion);
        summary = LabelingPredictionReviewAnalyzer.Summaries(review, confidenceThreshold: .95).Single();
        Check(summary.TruePositives == 0 && summary.FalsePositives == 0 && summary.FalseNegatives == 2); Near(0, summary.MeanIntersectionOverUnion);
        summary = LabelingPredictionReviewAnalyzer.Summaries(review, intersectionOverUnionThreshold: .55).Single();
        Check(summary.TruePositives == 1 && summary.FalsePositives == 1 && summary.FalseNegatives == 1); Near(1, summary.MeanIntersectionOverUnion);
        var exact = new LabelingPredictionReview([Example(Guid.NewGuid(), [Box("target", 0) with { Width = .75 }],
            [Box("target", .25, .5) with { Width = .75 }, Box("target", .25, .49) with { Width = .75 }])], Metrics);
        summary = LabelingPredictionReviewAnalyzer.Summaries(exact).Single();
        Check(summary.TruePositives == 1 && !summary.HasFailures); Near(.5, summary.MeanIntersectionOverUnion);
    }

    public static void TestCanonicalGeometryAndBaselines()
    {
        var reversed = Box("main-title.options", .2) with { Width = -.1, Y = .3, Height = -.1 };
        var review = new LabelingPredictionReview([Example(Guid.NewGuid(), [Box("shared.options")], [reversed, reversed with { Confidence = .7 }, Box("hidden", .1, .2)])], Metrics,
            [Example(Guid.NewGuid(), [Box("baseline-only")], [])], Metrics);
        var summaries = LabelingPredictionReviewAnalyzer.Summaries(review, ["shared.options", "main-title.options", "absent"]);
        Check(summaries.Count == 3 && summaries[0].ClassIdentifier == "shared.options" && summaries[0].TruePositives == 1 && summaries[0].FalsePositives == 1 && summaries[0].FalseNegatives == 0);
        Near(1, summaries[0].MeanIntersectionOverUnion);
        Check(summaries.All(value => value.ClassIdentifier != "baseline-only") && summaries.Single(value => value.ClassIdentifier == "hidden").TruePositives == 0);
        var invalid = review with { Examples = [Example(Guid.NewGuid(), [Box("target")], [Box("target") with { X = double.NaN }, Box("target") with { Confidence = double.NaN }])] };
        var rejected = LabelingPredictionReviewAnalyzer.Summaries(invalid).Single();
        Check(rejected.TruePositives == 0 && rejected.FalsePositives == 1 && rejected.FalseNegatives == 1 && double.IsFinite(rejected.MeanIntersectionOverUnion));
    }

    public static void TestPriorityTiesAndNegatives()
    {
        LabelingObjectPrioritySummary[] values = [new("b", 0, 1, 1, 1, true), new("a", 0, 1, 1, 1, true), new("misses", 1, 0, 2, 1, true), new("empty", 0, 0, 0, 0, false)];
        Check(LabelingObjectPriorityAnalyzer.MostErrors(values).Select(value => value.ClassIdentifier).SequenceEqual(new[] { "misses", "a", "b" }));
        Check(LabelingObjectPriorityAnalyzer.LeastRegistered(values).Select(value => value.ClassIdentifier).SequenceEqual(new[] { "a", "b", "empty", "misses" }));
        var saved = LabelingContractTests.Example("game", "main-title.options", "shared.options", "ignored");
        saved = saved with { Manifest = saved.Manifest with { Annotations = saved.Manifest.Annotations.Select((box, index) => box with { IsNegative = index == 1 }).ToArray() } };
        var priorities = LabelingObjectPriorityAnalyzer.Summaries([saved], null, ["shared.options", "main-title.options", "empty"], ["main-title.options"]);
        Check(priorities.Count == 2 && priorities.Single(value => value.ClassIdentifier == "shared.options") is { RegistrationCount: 1, HasModelReview: true }
            && priorities.Single(value => value.ClassIdentifier == "empty").RegistrationCount == 0 && LabelingObjectPriorityAnalyzer.MostErrors(priorities).Count == 0);
    }

    public static void TestSummarySession()
    {
        var first = LabelingContractTests.Example("game", "game.playable-knight"); var second = LabelingContractTests.Example("game", "game.mana");
        var menu = LabelingContractTests.Example("main-title", "game.playable-knight"); var deleted = Guid.NewGuid();
        var review = new LabelingPredictionReview([Example(deleted, [Box("game.playable-knight")], []), Example(second.Id, [], [Box("game.playable-knight"), Box("game.mana"), Box("game.playable-knight", .2, .2)]),
            Example(first.Id, [Box("game.playable-knight")], []), Example(menu.Id, [Box("game.playable-knight")], [])], Metrics);
        var session = new LabelingModelSummarySession(); var candidate = Candidate(review, "game.playable-knight");
        string[] classes = ["game.playable-knight", "game.mana", "custom.bench"];
        session.SetContent([first, second, menu], candidate, classes, "game.playable-knight");
        Check(session.ReviewClassIdentifier is null && session.SelectedFailure is null && session.PositionText == "0 / 0");
        Check(session.SelectPriority("game.playable-knight") && !session.SelectPriority("unknown"));
        Check(session.FailureExamples.Select(example => example.Id).SequenceEqual(new[] { second.Id, first.Id }) && session.PositionText == "1 / 2" && session.SelectedPredictions.Count == 1);
        session.Move(1); Check(session.SelectedFailure?.Id == first.Id && session.PositionText == "2 / 2" && session.SelectedPredictions.Count == 0);
        session.SetContent([first, second], candidate, classes, "game.mana");
        Check(session.SelectedFailure?.Id == first.Id && session.ReviewClassIdentifier == "game.playable-knight" && session.SelectedClassIdentifier == "game.mana");
        session.SetContent([second], candidate, classes, "game.mana"); Check(session.SelectedFailure?.Id == second.Id && session.PositionText == "1 / 1");
        session.SetContent([second], null, classes, "game.mana");
        Check(session.FailureExamples.Count == 0 && session.SelectedPredictions.Count == 0 && session.MostErrors.Count == 0 && session.LeastRegistered.Count == 3);
        session.SetContent([second], candidate, classes, "game.mana"); Check(session.SelectedFailure?.Id == second.Id);
    }

    public static void TestPersistedReviewAndAtomicFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-review-analysis-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var saved = LabelingContractTests.Example("game", "game.playable-knight");
            var review = new LabelingPredictionReview([Example(saved.Id, [Box("game.playable-knight")], [])], Metrics);
            var candidate = Candidate(review, "game.playable-knight"); var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(Path.Combine(root, "training.json"), JsonSerializer.Serialize(candidate.Summary, json));
            File.WriteAllText(Path.Combine(root, "predictions.json"), JsonSerializer.Serialize(review, json));
            var loaded = new LabelingModelReviewStore(root, Path.Combine(root, "datasets")).LoadCandidate(root)!;
            var session = new LabelingModelSummarySession(); session.SetContent([saved], loaded, ["game.playable-knight"], "game.playable-knight"); session.SelectPriority("game.playable-knight");
            Check(session.MostErrors.Single().FalseNegatives == 1 && session.SelectedFailure?.Id == saved.Id);
            var old = session.LeastRegistered;
            try { session.SetContent([], candidate with { PredictionReview = review with { Examples = null! } }, [], null); throw new Exception("Malformed review unexpectedly succeeded."); }
            catch (ArgumentNullException) { }
            Check(ReferenceEquals(old, session.LeastRegistered) && session.SelectedFailure?.Id == saved.Id, "Failed analysis partially published its new state.");
        }
        finally { Directory.Delete(root, true); }
    }
}
