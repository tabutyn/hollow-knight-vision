namespace HollowKnightVision.Windows.Core;

public sealed record LabelingCampaignProgress(string Identifier, int Count);
public sealed record LabelingCampaignPoint(Guid Id, int Run, double Accuracy, int ValidationCount);

public static class LabelingCampaignPolicy
{
    public static Dictionary<string, int> Counts(IEnumerable<SavedLabelingExample> examples)
    {
        var groups = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var example in examples.Where(example => example.Manifest.ContextIdentifier == "game"))
        foreach (var annotation in example.Manifest.Annotations.Where(annotation => !annotation.IsHardNegative))
        {
            var identifier = LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier);
            if (!groups.TryGetValue(identifier, out var captures)) groups[identifier] = captures = [];
            captures.Add(example.Manifest.CaptureGroupIdentifier);
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
