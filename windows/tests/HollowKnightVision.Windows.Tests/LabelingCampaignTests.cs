using HollowKnightVision.Windows.Core;

internal static class LabelingCampaignTests
{
    public static void TestBatchesAndValidationHistory()
    {
        static void Check(bool condition) { if (!condition) throw new Exception("Campaign progress regression."); }
        var first = new LabelingCampaignProgress("knight", 9, 0);
        Check(first.Target == 10 && first.Remaining == 1 && !first.NeedsTraining);
        var ready = new LabelingCampaignProgress("knight", 14, 2);
        Check(ready.Target == 10 && ready.NeedsTraining && ready.GuidanceKey(false, true) == "campaignTrainHere");
        var trained = new LabelingCampaignProgress("knight", 14, 14);
        Check(trained.Target == 20 && trained.Remaining == 6 && trained.GuidanceKey(true, false) == "campaignDraw");
        LabelingModelCandidate Candidate(int index, int count, double? accuracy, bool valid = true) => new("unused", "unused", new()
        {
            Id = Guid.NewGuid(), DatasetIdentifier = Guid.NewGuid(), ClassIdentifier = LabelingSharedModelPolicy.ModelIdentifier,
            CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(index), ModelFilename = "Detector.onnx",
            ValidationAnnotationCount = count, ValidationMetrics = accuracy is null ? null : new()
                { IsValid = valid, MeanAveragePrecisionAt50PercentIOU = accuracy }
        }, null);
        var history = LabelingCampaignPolicy.History([Candidate(1, 0, 1), Candidate(2, 3, 0), Candidate(3, 3, .6),
            Candidate(4, 3, 1.5), Candidate(5, 3, 1, false)]);
        Check(history.Select(point => point.Run).SequenceEqual([2, 3]));
        Check(history.Select(point => point.Accuracy).SequenceEqual([0.0, .6]));
    }

    public static void TestCaptureCountsAndTrainingSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-campaign-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LabelingExampleStore(Path.Combine(root, "examples"));
            var examples = new List<SavedLabelingExample>(); var group = Guid.NewGuid();
            LabelingExampleAnnotation Box(string identifier, bool negative = false) => new(Guid.NewGuid(), identifier, .1, .2, .2, .3, negative);
            for (var index = 0; index < 11; index++)
            {
                var pixels = new byte[64 * 36 * 4]; Array.Fill(pixels, (byte)(index * 10));
                examples.Add(store.Save(new BgraFrame(64, 36, 256, pixels, 0, DateTimeOffset.UnixEpoch), "game",
                    [Box("game.playable-knight"), Box("game.playable-knight"), Box("enemies.crawlid", true)],
                    captureGroupIdentifier: index < 2 ? group : Guid.NewGuid()));
            }
            var counts = LabelingCampaignPolicy.Counts(examples);
            if (counts.GetValueOrDefault("game.playable-knight") != 10 || counts.ContainsKey("enemies.crawlid"))
                throw new Exception("Repeated and negative boxes inflated capture count.");
            var snapshot = new LabelingDatasetExporter(Path.Combine(root, "datasets"))
                .ExportSharedModel(["game.playable-knight", "enemies.crawlid"], examples, LabelingSharedModelPolicy.ModelIdentifier);
            if (LabelingCampaignPolicy.CompletedCounts(snapshot.DirectoryPath).GetValueOrDefault("game.playable-knight") != 10)
                throw new Exception("Immutable training snapshot count differs.");
            if (LabelingCampaignPolicy.Counts(examples.Take(2)).GetValueOrDefault("game.playable-knight") != 1)
                throw new Exception("Repeated capture counted twice.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
