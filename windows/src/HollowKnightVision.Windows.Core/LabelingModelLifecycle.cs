namespace HollowKnightVision.Windows.Core;

/// <summary>The dashboard's model lifecycle, independent of WPF and live game state.</summary>
public sealed class LabelingModelLifecycle(LabelingModelReviewStore reviews, LabelingModelVersionStore versions)
{
    public IReadOnlyList<LabelingModelCandidate> Candidates { get; private set; } = [];
    public void Refresh()
    {
        var candidates = reviews.LoadCandidates();
        versions.SynchronizeCandidates(candidates);
        versions.ReconcileDevelopmentCandidates(candidates);
        Candidates = candidates;
    }
    public LabelingModelCandidate? Latest(string classIdentifier = LabelingModelVersionStore.SharedObjectModel) =>
        Candidates.LastOrDefault(candidate => candidate.Summary.ClassIdentifier == classIdentifier);

    // Mac's dashboard does this on appearance and after a completed training run.
    public void PromoteLatest(string classIdentifier = LabelingModelVersionStore.SharedObjectModel)
    {
        if (Latest(classIdentifier) is { } candidate) Promote(candidate);
    }
    public void Promote(LabelingModelCandidate candidate)
    {
        if (!string.Equals(candidate.Summary.ModelFormat, "onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows requires an ONNX export; a Core ML model cannot be activated here.");
        versions.Promote(candidate);
    }
    public string ActiveVersionName(string classIdentifier = LabelingModelVersionStore.SharedObjectModel) =>
        versions.ActiveSelection(classIdentifier) is { } major ? $"v{major}.0" : "Default";
    public string? TrainingCheckpointPath(string classIdentifier = LabelingModelVersionStore.SharedObjectModel) =>
        versions.TrainingCheckpointPath(classIdentifier, Candidates);
    public string[] ActiveRunDirectories() => versions.ActiveModelArtifacts()
        .Select(artifact => Path.GetDirectoryName(artifact.ModelPath)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<LabelingActiveModelArtifact> ActiveArtifacts() => versions.ActiveModelArtifacts();
    public bool HasTrainedGameplayModel(IReadOnlySet<Guid> exampleIds) => versions.HasTrainedGameplayModel(exampleIds, Candidates);
}
