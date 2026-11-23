using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record LabelingScreenshotDeletionResult(IReadOnlyList<Guid> Deleted, IReadOnlyDictionary<Guid, string> Failures);

/// <summary>Selection and deletion for gameplay screenshots in the Model workspace.</summary>
public sealed class LabelingScreenshotReviewSession(LabelingExampleStore store)
{
    public string RootDirectory => store.RootDirectory;
    public IReadOnlyList<SavedLabelingExample> Examples { get; private set; } = [];
    public Guid? SelectedIdentifier { get; private set; }
    public SavedLabelingExample? Selected => Examples.FirstOrDefault(example => example.Id == SelectedIdentifier) ?? Examples.FirstOrDefault();

    public static IReadOnlyList<SavedLabelingExample> ModelExamples(IEnumerable<SavedLabelingExample> examples) =>
        examples.Where(example => example.Manifest.ContextIdentifier == "game").OrderBy(example => example.Manifest.CreatedAt).ToArray();

    public void Reload() => SetExamples(store.Load());
    public void SetExamples(IEnumerable<SavedLabelingExample> values)
    {
        Examples = ModelExamples(values);
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
            throw new InvalidOperationException("Screenshot identity changed. Reload examples before deleting it.");
    }
}
