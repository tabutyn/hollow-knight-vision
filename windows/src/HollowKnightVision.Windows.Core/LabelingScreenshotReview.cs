using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public enum LabelingScreenshotOutline { None, Selected, Exempt, Breach }
public sealed record LabelingScreenshotDeletionResult(IReadOnlyList<Guid> Deleted, IReadOnlyDictionary<Guid, string> Failures);

/// <summary>Model-workspace screenshot selection and contract review. Only gameplay examples belong here.</summary>
public sealed class LabelingScreenshotReviewSession(LabelingExampleStore store)
{
    private readonly LabelingContractExemptionStore exemptionStore = new();
    private readonly Dictionary<Guid, HashSet<string>> exemptions = [];
    private readonly Dictionary<Guid, IReadOnlyList<LabelingContractEvaluation>> evaluations = [];
    public string RootDirectory => store.RootDirectory;
    public IReadOnlyList<SavedLabelingExample> Examples { get; private set; } = [];
    public Guid? SelectedIdentifier { get; private set; }
    public SavedLabelingExample? Selected => Examples.FirstOrDefault(example => example.Id == SelectedIdentifier) ?? Examples.FirstOrDefault();
    public int BreachCount => evaluations.Values.Count(values => values.Any(value => value.IsBreach));

    public static IReadOnlyList<SavedLabelingExample> ModelExamples(IEnumerable<SavedLabelingExample> examples) =>
        examples.Where(example => example.Manifest.ContextIdentifier == "game").OrderBy(example => example.Manifest.CreatedAt).ToArray();

    public void Reload() => SetExamples(store.Load());
    public void SetExamples(IEnumerable<SavedLabelingExample> values)
    {
        Examples = ModelExamples(values); exemptions.Clear(); evaluations.Clear();
        foreach (var example in Examples)
        {
            exemptions[example.Id] = exemptionStore.Load(example).ToHashSet(StringComparer.Ordinal);
            Evaluate(example);
        }
        ReconcileSelection();
    }
    private void ReconcileSelection()
    { if (!Examples.Any(example => example.Id == SelectedIdentifier)) SelectedIdentifier = Examples.FirstOrDefault()?.Id; }
    public bool Select(Guid identifier)
    {
        if (!Examples.Any(example => example.Id == identifier)) return false;
        SelectedIdentifier = identifier; return true;
    }
    public void Move(int offset)
    {
        if (Examples.Count == 0) return;
        var current = Math.Max(0, Array.FindIndex(Examples.ToArray(), example => example.Id == SelectedIdentifier));
        SelectedIdentifier = Examples[(int)Math.Clamp((long)current + offset, 0, Examples.Count - 1)].Id;
    }
    public IReadOnlyList<LabelingContractEvaluation> Evaluations(Guid identifier) => evaluations.GetValueOrDefault(identifier) ?? [];
    private void Evaluate(SavedLabelingExample example) => evaluations[example.Id] = LabelingContractEvaluator.Evaluate(example, exemptions: exemptions[example.Id]);
    public LabelingScreenshotOutline Outline(Guid identifier) => Evaluations(identifier).Any(value => value.IsBreach) ? LabelingScreenshotOutline.Breach
        : exemptions.GetValueOrDefault(identifier)?.Count > 0 ? LabelingScreenshotOutline.Exempt
        : SelectedIdentifier == identifier ? LabelingScreenshotOutline.Selected : LabelingScreenshotOutline.None;

    public bool CanTrainSelectedClass(string identifier, bool training = false)
    {
        if (training || BreachCount > 0 || string.IsNullOrWhiteSpace(identifier)) return false;
        var canonical = LabelingClassIdentity.CanonicalIdentifier(identifier);
        return Examples.Any(example => example.Manifest.Annotations.Any(annotation => !annotation.IsHardNegative
            && LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier) == canonical));
    }
    public bool ToggleExemption(Guid identifier, string rule)
    {
        var example = Examples.FirstOrDefault(item => item.Id == identifier);
        var evaluation = Evaluations(identifier).FirstOrDefault(item => item.Id == rule);
        if (example is null || evaluation is null || evaluation.IsSatisfied && !evaluation.IsExempt) return false;
        EnsureCurrentOwned(example);
        var next = exemptions[identifier].ToHashSet(StringComparer.Ordinal);
        if (!next.Remove(rule)) next.Add(rule);
        exemptionStore.Save(next, example); // Commit succeeds before UI/admission state changes.
        exemptions[identifier] = next; Evaluate(example); return true;
    }
    public LabelingScreenshotDeletionResult DeleteConfirmed(IEnumerable<Guid> identifiers)
    {
        var requested = identifiers.ToHashSet(); var deleted = new List<Guid>(); var failures = new Dictionary<Guid, string>();
        foreach (var example in Examples.Where(example => requested.Contains(example.Id)).ToArray())
        {
            try { EnsureCurrentOwned(example); store.Delete(example); deleted.Add(example.Id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException)
            { failures[example.Id] = error.Message; }
        }
        var removed = deleted.ToHashSet(); Examples = Examples.Where(example => !removed.Contains(example.Id)).ToArray();
        foreach (var identifier in removed) { exemptions.Remove(identifier); evaluations.Remove(identifier); }
        ReconcileSelection(); return new(deleted, failures);
    }
    private void EnsureCurrentOwned(SavedLabelingExample example)
    {
        var path = Path.GetFullPath(example.DirectoryPath);
        if (!string.Equals(Path.GetDirectoryName(path), store.RootDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Screenshot no longer belongs to this example store.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(path, LabelingExampleStore.ManifestFilename)));
        if (manifest.RootElement.GetProperty("id").GetGuid() != example.Id)
            throw new InvalidOperationException("Screenshot identity changed. Reload examples before editing or deleting it.");
    }
}
