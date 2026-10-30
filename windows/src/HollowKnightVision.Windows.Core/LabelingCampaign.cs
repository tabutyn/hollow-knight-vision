using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record LabelingCampaignProgress(string Identifier, int Count, int CompletedCount)
{
    public int Target => Math.Max(10, (CompletedCount / 10 + 1) * 10);
    public int Remaining => Math.Max(0, Target - Count);
    public bool NeedsTraining => Count >= Target;
    public string GuidanceKey(bool labeling, bool inModel) => NeedsTraining
        ? inModel ? "campaignTrainHere" : "campaignTrain" : labeling ? "campaignDraw" : "campaignCollect";
}
public sealed record LabelingCampaignPoint(Guid Id, int Run, double Accuracy, int ValidationCount);

public static class LabelingCampaignPolicy
{
    public static Dictionary<string, int> Counts(IEnumerable<SavedLabelingExample> examples)
    {
        var groups = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var example in examples.Where(example => example.Manifest.ContextIdentifier == "game"))
            foreach (var annotation in example.Manifest.Annotations.Where(annotation => !annotation.IsHardNegative))
                Add(groups, annotation.ClassIdentifier, example.Manifest.CaptureGroupIdentifier);
        return groups.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
    }
    private static void Add(Dictionary<string, HashSet<Guid>> groups, string identifier, Guid capture)
    {
        identifier = LabelingClassIdentity.CanonicalIdentifier(identifier);
        if (!groups.TryGetValue(identifier, out var captures)) groups[identifier] = captures = [];
        captures.Add(capture);
    }
    public static Dictionary<string, int> CompletedCounts(string datasetDirectory)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<LabelingDatasetManifest>(
            File.ReadAllText(Path.Combine(datasetDirectory, "dataset.json")), options)
            ?? throw new InvalidDataException("Missing campaign dataset.");
        var groups = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var split in new[] { LabelingDatasetSplit.Training, LabelingDatasetSplit.Validation })
        {
            var items = manifest.Items.Where(item => item.Split == split).ToDictionary(item => item.ImageFilename);
            if (items.Count == 0) continue;
            var images = JsonSerializer.Deserialize<ImageAnnotations[]>(File.ReadAllText(Path.Combine(
                datasetDirectory, split.ToString().ToLowerInvariant(), "annotations.json")), options)
                ?? throw new InvalidDataException("Missing campaign annotations.");
            foreach (var image in images)
                if (items.TryGetValue(image.Image, out var item))
                    foreach (var annotation in image.Annotations.Where(annotation => annotation.IsNegative != true))
                        Add(groups, annotation.Label, item.CaptureGroupIdentifier);
        }
        return groups.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
    }
    public static IReadOnlyList<LabelingCampaignPoint> History(IEnumerable<LabelingModelCandidate> candidates) => candidates
        .Where(candidate => candidate.Summary.ClassIdentifier == LabelingSharedModelPolicy.ModelIdentifier)
        .OrderBy(candidate => candidate.Summary.CompletedAt)
        .Select((candidate, index) => (candidate, index))
        .Where(item => item.candidate.Summary.ValidationAnnotationCount > 0
            && item.candidate.Summary.ValidationMetrics is { IsValid: true, MeanAveragePrecisionAt50PercentIOU: >= 0 and <= 1 })
        .Select(item => new LabelingCampaignPoint(item.candidate.Summary.Id, item.index + 1,
            item.candidate.Summary.ValidationMetrics!.MeanAveragePrecisionAt50PercentIOU!.Value,
            item.candidate.Summary.ValidationAnnotationCount)).ToArray();
}
