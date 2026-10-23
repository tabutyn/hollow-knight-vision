namespace HollowKnightVision.Windows.Core;

public sealed record LabelingClassReviewSummary(string ClassIdentifier, int TruePositives, int FalsePositives,
    int FalseNegatives, double MeanIntersectionOverUnion, IReadOnlySet<Guid> FailingExampleIdentifiers)
{
    public bool HasFailures => FalsePositives > 0 || FalseNegatives > 0;
}

public sealed record LabelingObjectPrioritySummary(string ClassIdentifier, int RegistrationCount,
    int FalsePositives, int FalseNegatives, int FailingFrameCount, bool HasModelReview)
{
    public int ErrorCount => FalsePositives + FalseNegatives;
}

public static class LabelingPredictionReviewAnalyzer
{
    public static IReadOnlyList<LabelingClassReviewSummary> Summaries(LabelingPredictionReview review,
        IEnumerable<string>? classIdentifiers = null, double? confidenceThreshold = null, double? intersectionOverUnionThreshold = null)
    {
        var confidence = confidenceThreshold ?? review.Metrics.ConfidenceThreshold;
        var overlapThreshold = intersectionOverUnionThreshold ?? review.Metrics.IntersectionOverUnionThreshold;
        var identifiers = (classIdentifiers ?? []).Concat(review.Examples.SelectMany(example => example.Human.Concat(example.Predictions).Select(box => box.Label)))
            .Select(LabelingClassIdentity.CanonicalIdentifier).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var summaries = new List<LabelingClassReviewSummary>();
        foreach (var identifier in identifiers)
        {
            var truePositives = 0; var falsePositives = 0; var falseNegatives = 0; var overlapSum = 0.0; var failures = new HashSet<Guid>();
            foreach (var example in review.Examples)
            {
                var human = example.Human.Where(box => Canonical(box.Label) == identifier).ToArray();
                var predictions = example.Predictions.Where(box => Canonical(box.Label) == identifier && box.Confidence >= confidence)
                    .OrderByDescending(box => box.Confidence);
                var unmatched = Enumerable.Repeat(true, human.Length).ToArray(); var exampleFalsePositives = 0;
                foreach (var prediction in predictions)
                {
                    var match = -1; var bestOverlap = double.NegativeInfinity;
                    for (var index = 0; index < human.Length; index++)
                    {
                        if (!unmatched[index]) continue;
                        var overlap = Standardized(prediction).IntersectionOverUnion(Standardized(human[index]));
                        // Stable lowest-index tie breaking; Swift's Set iteration does not specify ties.
                        if (overlap > bestOverlap) { match = index; bestOverlap = overlap; }
                    }
                    if (match >= 0 && bestOverlap >= overlapThreshold)
                    { truePositives++; overlapSum += bestOverlap; unmatched[match] = false; }
                    else { falsePositives++; exampleFalsePositives++; }
                }
                var missed = unmatched.Count(value => value); falseNegatives += missed;
                if (exampleFalsePositives > 0 || missed > 0) failures.Add(example.ExampleIdentifier);
            }
            summaries.Add(new(identifier, truePositives, falsePositives, falseNegatives,
                truePositives == 0 ? 0 : overlapSum / truePositives, failures));
        }
        return summaries.OrderByDescending(value => value.HasFailures).ThenByDescending(value => value.FalsePositives + value.FalseNegatives)
            .ThenBy(value => value.ClassIdentifier, StringComparer.Ordinal).ToArray();
    }

    private static string Canonical(string identifier) => LabelingClassIdentity.CanonicalIdentifier(identifier);
    private static NormalizedBox Standardized(LabelingReviewBox box) => new(Math.Min(box.X, box.X + box.Width),
        Math.Min(box.Y, box.Y + box.Height), Math.Abs(box.Width), Math.Abs(box.Height));
}

public static class LabelingObjectPriorityAnalyzer
{
    public static IReadOnlyList<LabelingObjectPrioritySummary> Summaries(IEnumerable<SavedLabelingExample> examples,
        LabelingPredictionReview? review, IEnumerable<string> classIdentifiers, IEnumerable<string> reviewedClassIdentifiers)
    {
        var identifiers = classIdentifiers.Select(LabelingClassIdentity.CanonicalIdentifier).Distinct(StringComparer.Ordinal).ToArray();
        var counts = examples.SelectMany(example => example.Manifest.Annotations).Where(annotation => !annotation.IsHardNegative)
            .GroupBy(annotation => LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var summaries = (review is null ? [] : LabelingPredictionReviewAnalyzer.Summaries(review, identifiers))
            .ToDictionary(value => value.ClassIdentifier, StringComparer.Ordinal);
        var reviewed = reviewedClassIdentifiers.Select(LabelingClassIdentity.CanonicalIdentifier).ToHashSet(StringComparer.Ordinal);
        return identifiers.Select(identifier =>
        {
            var value = summaries.GetValueOrDefault(identifier);
            return new LabelingObjectPrioritySummary(identifier, counts.GetValueOrDefault(identifier), value?.FalsePositives ?? 0,
                value?.FalseNegatives ?? 0, value?.FailingExampleIdentifiers.Count ?? 0, reviewed.Contains(identifier));
        }).ToArray();
    }
    public static IReadOnlyList<LabelingObjectPrioritySummary> LeastRegistered(IEnumerable<LabelingObjectPrioritySummary> summaries) =>
        summaries.OrderBy(value => value.RegistrationCount).ThenByDescending(value => value.ErrorCount)
            .ThenBy(value => value.ClassIdentifier, StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<LabelingObjectPrioritySummary> MostErrors(IEnumerable<LabelingObjectPrioritySummary> summaries) =>
        summaries.Where(value => value.ErrorCount > 0).OrderByDescending(value => value.ErrorCount).ThenByDescending(value => value.FalseNegatives)
            .ThenBy(value => value.RegistrationCount).ThenBy(value => value.ClassIdentifier, StringComparer.Ordinal).ToArray();
}

public static class LabelingFailureReviewNavigator
{
    public static IReadOnlyList<Guid> OrderedExampleIdentifiers(string classIdentifier, LabelingPredictionReview review)
    {
        var canonical = LabelingClassIdentity.CanonicalIdentifier(classIdentifier);
        var failures = LabelingPredictionReviewAnalyzer.Summaries(review, [canonical]).First(value => value.ClassIdentifier == canonical).FailingExampleIdentifiers;
        return review.Examples.Select(example => example.ExampleIdentifier).Where(failures.Contains).Distinct().ToArray();
    }
    public static Guid? MovedIdentifier(Guid? current, int offset, IReadOnlyList<Guid> identifiers)
    {
        if (identifiers.Count == 0) return null;
        var index = current is { } identifier ? Math.Max(0, Array.IndexOf(identifiers.ToArray(), identifier)) : 0;
        return identifiers[(int)(((long)index + offset % identifiers.Count + identifiers.Count) % identifiers.Count)];
    }
}
