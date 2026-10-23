namespace HollowKnightVision.Windows.Core;

/// <summary>Summary-specific selection survives tab/view refreshes, independently of the selected labeling object.</summary>
public sealed class LabelingModelSummarySession
{
    private IReadOnlyList<SavedLabelingExample> examples = [];
    private LabelingPredictionReview? review;
    private IReadOnlyDictionary<string, LabelingClassReviewSummary> summaries = new Dictionary<string, LabelingClassReviewSummary>();
    private HashSet<string> classes = new(StringComparer.Ordinal);
    public IReadOnlyList<LabelingObjectPrioritySummary> LeastRegistered { get; private set; } = [];
    public IReadOnlyList<LabelingObjectPrioritySummary> MostErrors { get; private set; } = [];
    public string? SelectedClassIdentifier { get; private set; }
    public string? ReviewClassIdentifier { get; private set; }
    public Guid? SelectedFailureIdentifier { get; private set; }
    public IReadOnlyList<SavedLabelingExample> FailureExamples { get; private set; } = [];
    public SavedLabelingExample? SelectedFailure => FailureExamples.FirstOrDefault(example => example.Id == SelectedFailureIdentifier) ?? FailureExamples.FirstOrDefault();
    public string PositionText => SelectedFailure is { } selected
        ? $"{Array.FindIndex(FailureExamples.ToArray(), example => example.Id == selected.Id) + 1} / {FailureExamples.Count}" : "0 / 0";
    public IReadOnlyList<LabelingReviewBox> SelectedPredictions => SelectedFailure is { } selected && ReviewClassIdentifier is { } identifier && review is not null
        ? review.Examples.FirstOrDefault(example => example.ExampleIdentifier == selected.Id)?.Predictions
            .Where(box => LabelingClassIdentity.CanonicalIdentifier(box.Label) == identifier && box.Confidence >= review.Metrics.ConfidenceThreshold).ToArray() ?? [] : [];

    public void SetContent(IEnumerable<SavedLabelingExample> values, LabelingModelCandidate? candidate,
        IEnumerable<string> classIdentifiers, string? selectedClassIdentifier)
    {
        var nextExamples = LabelingScreenshotReviewSession.ModelExamples(values);
        var nextClasses = classIdentifiers.Select(LabelingClassIdentity.CanonicalIdentifier).ToHashSet(StringComparer.Ordinal);
        var nextReview = candidate?.PredictionReview;
        var nextSummaries = (nextReview is null ? [] : LabelingPredictionReviewAnalyzer.Summaries(nextReview, nextClasses))
            .ToDictionary(value => value.ClassIdentifier, StringComparer.Ordinal);
        var priorities = LabelingObjectPriorityAnalyzer.Summaries(nextExamples, nextReview, nextClasses, candidate?.Summary.ClassIdentifiers ?? []);
        examples = nextExamples; classes = nextClasses; review = nextReview; summaries = nextSummaries;
        LeastRegistered = LabelingObjectPriorityAnalyzer.LeastRegistered(priorities); MostErrors = LabelingObjectPriorityAnalyzer.MostErrors(priorities);
        SetSelectedClass(selectedClassIdentifier); ReconcileFailures();
    }
    public void SetSelectedClass(string? identifier) => SelectedClassIdentifier = string.IsNullOrWhiteSpace(identifier)
        ? null : LabelingClassIdentity.CanonicalIdentifier(identifier);
    public int TruePositiveCount(string identifier) => summaries.GetValueOrDefault(LabelingClassIdentity.CanonicalIdentifier(identifier))?.TruePositives ?? 0;
    public bool SelectPriority(string identifier)
    {
        var canonical = LabelingClassIdentity.CanonicalIdentifier(identifier);
        if (!classes.Contains(canonical)) return false;
        SelectedClassIdentifier = ReviewClassIdentifier = canonical; SelectedFailureIdentifier = null;
        ReconcileFailures(); return true;
    }
    public void Move(int offset) => SelectedFailureIdentifier = LabelingFailureReviewNavigator.MovedIdentifier(
        SelectedFailure?.Id, offset, FailureExamples.Select(example => example.Id).ToArray());
    private void ReconcileFailures()
    {
        if (ReviewClassIdentifier is not { } identifier || review is null) { FailureExamples = []; SelectedFailureIdentifier = null; return; }
        var available = examples.ToDictionary(example => example.Id);
        var failures = summaries.GetValueOrDefault(identifier)?.FailingExampleIdentifiers;
        FailureExamples = review.Examples.Select(example => example.ExampleIdentifier).Distinct()
            .Where(id => failures?.Contains(id) == true && available.ContainsKey(id)).Select(id => available[id]).ToArray();
        if (!FailureExamples.Any(example => example.Id == SelectedFailureIdentifier)) SelectedFailureIdentifier = FailureExamples.FirstOrDefault()?.Id;
    }
}
