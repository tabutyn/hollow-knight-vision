namespace HollowKnightVision.Windows.Core;

public sealed record LabelingModelSemanticVersion(int Major, int Minor)
{
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => $"v{Major}.{Minor}";
}
public sealed record LabelingModelRestorePoint(string ClassIdentifier, LabelingModelSemanticVersion Version,
    Guid SourceRunIdentifier, DateTimeOffset CreatedAt, string ModelFilename, string? CheckpointFilename = null);
public sealed record LabelingActiveModelArtifact(string ClassIdentifier, LabelingModelSemanticVersion Version, string ModelPath);

public static class LabelingDevelopmentCandidatePolicy
{
    public const double MaximumPerClassRegression = .10;
    public const double MaximumMeanRegression = .02;

    public static bool PreservesBase(LabelingModelCandidate candidate, LabelingModelCandidate baseline)
    {
        if (candidate.Summary.ValidationMetrics is not { IsValid: true } current
            || baseline.Summary.ValidationMetrics is not { IsValid: true } previous) return true;
        var classes = baseline.Summary.ClassIdentifiers ?? previous.AveragePrecisionAt50PercentIOUByClass.Keys.ToArray();
        var comparisons = classes.Where(id => previous.AveragePrecisionAt50PercentIOUByClass.ContainsKey(id)
                && current.AveragePrecisionAt50PercentIOUByClass.ContainsKey(id))
            .Select(id => (Previous: previous.AveragePrecisionAt50PercentIOUByClass[id], Current: current.AveragePrecisionAt50PercentIOUByClass[id])).ToArray();
        return comparisons.Length == 0 || (comparisons.All(pair => pair.Current + MaximumPerClassRegression >= pair.Previous)
            && comparisons.Average(pair => pair.Current) + MaximumMeanRegression >= comparisons.Average(pair => pair.Previous));
    }
}

/// <summary>Mac-compatible candidate versions, durable promotions and incremental checkpoint lineage.</summary>
public sealed class LabelingModelVersionStore(string rootDirectory)
{
    public const string SharedObjectModel = "shared-object-model";
    private readonly object gate = new();
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
    private sealed class Registry
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, ObjectRecord> Objects { get; set; } = [];
    }
    private sealed class ObjectRecord
    {
        public Dictionary<Guid, LabelingModelSemanticVersion> CandidateVersions { get; set; } = [];
        public List<LabelingModelRestorePoint> RestorePoints { get; set; } = [];
        public int? ActiveMajorVersion { get; set; }
        public Guid? DevelopmentRunIdentifier { get; set; }
    }

    private Registry Load()
    {
        var path = ModelStoreJson.Child(RootDirectory, "registry.json");
        var registry = File.Exists(path) ? ModelStoreJson.Read<Registry>(path) : new Registry();
        if (registry.SchemaVersion != 1) throw new InvalidDataException("Unsupported model registry schema.");
        return registry;
    }
    private void Save(Registry registry)
    {
        var path = ModelStoreJson.Child(RootDirectory, "registry.json");
        Directory.CreateDirectory(RootDirectory);
        ModelStoreJson.Write(path, registry);
    }
    private static ObjectRecord Record(Registry registry, string identifier)
    {
        ModelStoreJson.Component(identifier);
        if (!registry.Objects.TryGetValue(identifier, out var record)) registry.Objects[identifier] = record = new();
        return record;
    }
    private string PointDirectory(LabelingModelRestorePoint point) => ModelStoreJson.Child(RootDirectory,
        point.ClassIdentifier, point.Version.DisplayName);

    public void SynchronizeCandidates(IReadOnlyList<LabelingModelCandidate> candidates)
    {
        lock (gate)
        {
            var registry = Load(); var changed = false;
            foreach (var group in candidates.GroupBy(candidate => candidate.Summary.ClassIdentifier))
            {
                var record = Record(registry, group.Key);
                var major = record.RestorePoints.Select(point => point.Version.Major).DefaultIfEmpty(0).Max();
                var minor = record.CandidateVersions.Values.Where(version => version.Major == major)
                    .Select(version => version.Minor).DefaultIfEmpty(0).Max() + 1;
                foreach (var candidate in group.OrderBy(value => value.Summary.CompletedAt).ThenBy(value => value.Id))
                    if (record.CandidateVersions.TryAdd(candidate.Id, new(major, minor))) { minor++; changed = true; }
            }
            if (changed) Save(registry);
        }
    }
    public LabelingModelSemanticVersion CandidateVersion(LabelingModelCandidate candidate)
    {
        lock (gate) return Record(Load(), candidate.Summary.ClassIdentifier).CandidateVersions.TryGetValue(candidate.Id, out var version)
            ? version : throw new InvalidOperationException($"Candidate version is missing for run {candidate.Id}.");
    }
    public IReadOnlyList<LabelingModelRestorePoint> RestorePoints(string classIdentifier)
    {
        lock (gate) return Record(Load(), classIdentifier).RestorePoints.OrderBy(point => point.Version.Major).ToArray();
    }
    // null is Default. Promoted selections identify the major version (minor is always zero).
    public int? ActiveSelection(string classIdentifier)
    {
        lock (gate)
        {
            var record = Record(Load(), classIdentifier);
            return record.RestorePoints.Any(point => point.Version.Major == record.ActiveMajorVersion) ? record.ActiveMajorVersion : null;
        }
    }
    public void Activate(string classIdentifier, int? major)
    {
        lock (gate)
        {
            var registry = Load(); var record = Record(registry, classIdentifier);
            var point = major is null ? null : record.RestorePoints.SingleOrDefault(point => point.Version.Major == major)
                ?? throw new InvalidOperationException($"Model restore point v{major}.0 is unavailable.");
            record.ActiveMajorVersion = major;
            record.DevelopmentRunIdentifier = point?.SourceRunIdentifier;
            Save(registry);
        }
    }
    public LabelingModelRestorePoint Promote(LabelingModelCandidate candidate, DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var registry = Load(); var record = Record(registry, candidate.Summary.ClassIdentifier);
            if (!record.CandidateVersions.ContainsKey(candidate.Id)) throw new InvalidOperationException("Synchronize the candidate before promotion.");
            if (record.RestorePoints.FirstOrDefault(point => point.SourceRunIdentifier == candidate.Id) is { } existing)
            {
                record.ActiveMajorVersion = existing.Version.Major; record.DevelopmentRunIdentifier = candidate.Id;
                Save(registry); return existing;
            }
            var summary = candidate.Summary;
            var source = ModelStoreJson.Child(candidate.RunDirectory, summary.ModelFilename);
            if (!File.Exists(source)) throw new FileNotFoundException("Candidate model is missing.", source);
            var filenames = new[] { summary.ModelFilename, summary.CheckpointFilename, summary.PredictionsFilename,
                "training.json", "restore-point.json" }.OfType<string>().ToArray();
            if (filenames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != filenames.Length)
                throw new InvalidDataException("Model artifact filenames must be distinct.");
            foreach (var filename in filenames) ModelStoreJson.Component(filename);
            var version = new LabelingModelSemanticVersion(record.RestorePoints.Select(point => point.Version.Major).DefaultIfEmpty(0).Max() + 1, 0);
            var point = new LabelingModelRestorePoint(summary.ClassIdentifier, version, candidate.Id,
                now ?? DateTimeOffset.UtcNow, summary.ModelFilename, summary.CheckpointFilename);
            var destination = PointDirectory(point);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException($"Model restore point {version.DisplayName} already exists.");
            var staging = ModelStoreJson.Child(RootDirectory, summary.ClassIdentifier, $".staging-{version.DisplayName}-{Guid.NewGuid():N}");
            var moved = false;
            try
            {
                Directory.CreateDirectory(staging);
                File.Copy(source, ModelStoreJson.Child(staging, summary.ModelFilename));
                if (summary.CheckpointFilename is { } checkpoint && File.Exists(ModelStoreJson.Child(candidate.RunDirectory, checkpoint)))
                    File.Copy(ModelStoreJson.Child(candidate.RunDirectory, checkpoint), ModelStoreJson.Child(staging, checkpoint));
                // ONNX inference needs class metadata alongside the model; retain review evidence too.
                ModelStoreJson.Write(ModelStoreJson.Child(staging, "training.json"), summary);
                if (summary.PredictionsFilename is { } predictions && candidate.PredictionReview is { } review)
                    ModelStoreJson.Write(ModelStoreJson.Child(staging, predictions), review);
                ModelStoreJson.Write(ModelStoreJson.Child(staging, "restore-point.json"), point);
                Directory.Move(staging, destination); moved = true;
                record.RestorePoints.Add(point); record.ActiveMajorVersion = version.Major; record.DevelopmentRunIdentifier = candidate.Id;
                Save(registry); return point;
            }
            catch
            {
                // Only remove the unique directory created by this failed promotion; never a prior restore point.
                var ownedPath = moved ? destination : staging;
                if (Directory.Exists(ownedPath)) Directory.Delete(ownedPath, recursive: true);
                throw;
            }
        }
    }
    public void RecordDevelopmentCandidate(LabelingModelCandidate candidate)
    {
        if (candidate.Summary.CheckpointFilename is null) return;
        lock (gate)
        {
            var registry = Load(); Record(registry, candidate.Summary.ClassIdentifier).DevelopmentRunIdentifier = candidate.Id;
            Save(registry);
        }
    }
    public void ReconcileDevelopmentCandidates(IReadOnlyList<LabelingModelCandidate> candidates)
    {
        lock (gate)
        {
            var registry = Load(); var changed = false;
            foreach (var group in candidates.GroupBy(candidate => candidate.Summary.ClassIdentifier))
            {
                var record = Record(registry, group.Key);
                if (record.ActiveMajorVersion is not null) continue;
                var ordered = group.Where(candidate => candidate.Summary.CheckpointFilename is not null)
                    .OrderBy(candidate => candidate.Summary.CompletedAt).ThenBy(candidate => candidate.Id).ToArray();
                var accepted = ordered.FirstOrDefault(candidate => candidate.Summary.BaseRunIdentifier is null);
                if (accepted is null) continue;
                foreach (var candidate in ordered)
                    if (candidate.Id != accepted.Id && Guid.TryParse(candidate.Summary.BaseRunIdentifier, out var baseId)
                        && baseId == accepted.Id && LabelingDevelopmentCandidatePolicy.PreservesBase(candidate, accepted)) accepted = candidate;
                if (record.DevelopmentRunIdentifier != accepted.Id) { record.DevelopmentRunIdentifier = accepted.Id; changed = true; }
            }
            if (changed) Save(registry);
        }
    }
    public string? TrainingCheckpointPath(string classIdentifier, IReadOnlyList<LabelingModelCandidate> candidates)
    {
        lock (gate)
        {
            var record = Record(Load(), classIdentifier);
            if (record.DevelopmentRunIdentifier is not { } run) return null;
            var candidate = candidates.FirstOrDefault(value => value.Id == run && value.Summary.ClassIdentifier == classIdentifier);
            if (candidate?.Summary.CheckpointFilename is { } filename)
            {
                var path = ModelStoreJson.Child(candidate.RunDirectory, filename);
                if (File.Exists(path)) return path;
            }
            var point = record.RestorePoints.FirstOrDefault(value => value.SourceRunIdentifier == run);
            if (point?.CheckpointFilename is not { } checkpoint) return null;
            var restored = ModelStoreJson.Child(PointDirectory(point), checkpoint);
            return File.Exists(restored) ? restored : null;
        }
    }
    public string? ModelPath(string classIdentifier, int? major)
    {
        if (major is null) return null;
        lock (gate)
        {
            var point = Record(Load(), classIdentifier).RestorePoints.SingleOrDefault(point => point.Version.Major == major)
                ?? throw new InvalidOperationException($"Model restore point v{major}.0 is unavailable.");
            return ModelStoreJson.Child(PointDirectory(point), point.ModelFilename);
        }
    }
    public IReadOnlyList<LabelingActiveModelArtifact> ActiveModelArtifacts()
    {
        lock (gate)
        {
            var artifacts = Load().Objects.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.RestorePoints.FirstOrDefault(point => point.Version.Major == pair.Value.ActiveMajorVersion))
                .OfType<LabelingModelRestorePoint>().Select(point => new LabelingActiveModelArtifact(point.ClassIdentifier,
                    point.Version, ModelStoreJson.Child(PointDirectory(point), point.ModelFilename))).ToArray();
            return artifacts.FirstOrDefault(artifact => artifact.ClassIdentifier == SharedObjectModel) is { } shared ? [shared] : artifacts;
        }
    }
    public bool HasTrainedGameplayModel(IReadOnlySet<Guid> exampleIds, IReadOnlyList<LabelingModelCandidate> candidates)
    {
        if (exampleIds.Count == 0) return false;
        lock (gate) return ActiveModelArtifacts().Any(artifact => File.Exists(artifact.ModelPath)
            && RestorePoints(artifact.ClassIdentifier).FirstOrDefault(point => point.Version == artifact.Version) is { } point
            && candidates.FirstOrDefault(candidate => candidate.Id == point.SourceRunIdentifier)?.PredictionReview?.Examples
                .Any(example => exampleIds.Contains(example.ExampleIdentifier)) == true);
    }
}
