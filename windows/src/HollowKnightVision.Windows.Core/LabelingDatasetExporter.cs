using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record ObjectCoordinates(double X, double Y, double Width, double Height);
public sealed record ObjectAnnotation(
    string Label,
    ObjectCoordinates Coordinates,
    bool? IsNegative = null);
public sealed record ImageAnnotations(
    string Image,
    IReadOnlyList<ObjectAnnotation> Annotations);

[JsonConverter(typeof(JsonStringEnumConverter<LabelingDatasetSplit>))]
public enum LabelingDatasetSplit
{
    Training,
    Validation
}

public sealed record LabelingDatasetItem(
    Guid ExampleIdentifier,
    Guid ImageIdentifier,
    Guid CaptureGroupIdentifier,
    string ImageDigest,
    string ImageFilename,
    int AnnotationCount,
    IReadOnlyList<string>? KnownClassIdentifiers,
    LabelingDatasetSplit Split);

public sealed record LabelingDatasetManifest(
    int SchemaVersion,
    Guid Id,
    string ClassIdentifier,
    IReadOnlyList<string>? ClassIdentifiers,
    DateTimeOffset CreatedAt,
    IReadOnlyList<LabelingDatasetItem> Items,
    bool IsPreliminary)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record LabelingDatasetSnapshot(
    string DirectoryPath,
    LabelingDatasetManifest Manifest);

/// <summary>
/// Exports trainer-ready mac-compatible datasets. Capture groups and duplicate
/// images stay together, explicit hard negatives stay in training, and each
/// positive class retains training data.
/// </summary>
public sealed class LabelingDatasetExporter
{
    public const string ManifestFilename = "dataset.json";
    public const string AnnotationsFilename = "annotations.json";
    public const string SharedObjectModelIdentifier = "shared-object-model";
    private static readonly JsonSerializerOptions JsonOptions =
        LabelingExampleStore.CreateJsonOptions();
    private readonly Func<Guid> createId;
    private readonly Func<DateTimeOffset> now;

    public LabelingDatasetExporter(
        string rootDirectory,
        Func<Guid>? createId = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        this.createId = createId ?? Guid.NewGuid;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string RootDirectory { get; }

    public LabelingDatasetSnapshot Export(
        string modelIdentifier,
        IEnumerable<string> includedClassIdentifiers,
        IEnumerable<SavedLabelingExample> examples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentifier);
        ArgumentNullException.ThrowIfNull(includedClassIdentifiers);
        ArgumentNullException.ThrowIfNull(examples);
        var included = includedClassIdentifiers
            .Select(LabelingClassIdentity.CanonicalIdentifier)
            .ToHashSet(StringComparer.Ordinal);
        if (included.Count == 0) throw new ArgumentException("Include at least one class.");

        var candidates = examples.Where(example =>
            example.Manifest.Annotations.Any(annotation => included.Contains(
                LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier)))
            || example.Manifest.EffectiveKnownClassIdentifiers.Any(included.Contains)).ToArray();
        if (!candidates.Any(example => example.Manifest.Annotations.Any(annotation =>
                !annotation.IsHardNegative
                && included.Contains(LabelingClassIdentity.CanonicalIdentifier(
                    annotation.ClassIdentifier)))))
        {
            throw new InvalidOperationException(
                $"No labeled instances exist for {modelIdentifier}.");
        }

        var records = candidates.Select(example => SourceRecord.Create(example, included)).ToArray();
        var validationIds = ValidationExampleIdentifiers(records, modelIdentifier);
        var items = records.Select(record => new LabelingDatasetItem(
            record.Example.Id,
            record.Example.Manifest.ImageIdentifier,
            record.Example.Manifest.CaptureGroupIdentifier,
            record.ImageDigest,
            $"{record.Example.Id:D}.png".ToLowerInvariant(),
            record.Annotations.Count(annotation => !annotation.IsHardNegative),
            record.Example.Manifest.EffectiveKnownClassIdentifiers,
            validationIds.Contains(record.Example.Id)
                ? LabelingDatasetSplit.Validation
                : LabelingDatasetSplit.Training)).ToArray();
        var id = createId();
        var manifest = new LabelingDatasetManifest(
            LabelingDatasetManifest.CurrentSchemaVersion,
            id,
            modelIdentifier,
            included.Order(StringComparer.Ordinal).ToArray(),
            now(),
            items,
            items.Length < 10 || validationIds.Count == 0);

        Directory.CreateDirectory(RootDirectory);
        var finalDirectory = Path.Combine(RootDirectory, id.ToString("D").ToLowerInvariant());
        var stagingDirectory = Path.Combine(
            RootDirectory,
            $".staging-{id:D}-{Guid.NewGuid():N}".ToLowerInvariant());
        if (Directory.Exists(finalDirectory)) throw new IOException("Dataset destination exists.");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            WriteSplit(LabelingDatasetSplit.Training, records, items, stagingDirectory);
            WriteSplit(LabelingDatasetSplit.Validation, records, items, stagingDirectory);
            AtomicFile.Write(
                Path.Combine(stagingDirectory, ManifestFilename),
                JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            Directory.Move(stagingDirectory, finalDirectory);
        }
        catch
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
        return new LabelingDatasetSnapshot(finalDirectory, manifest);
    }

    private static void WriteSplit(
        LabelingDatasetSplit split,
        IReadOnlyList<SourceRecord> records,
        IReadOnlyList<LabelingDatasetItem> items,
        string rootDirectory)
    {
        var splitName = split == LabelingDatasetSplit.Training ? "training" : "validation";
        var splitDirectory = Path.Combine(rootDirectory, splitName);
        Directory.CreateDirectory(splitDirectory);
        var recordsById = records.ToDictionary(record => record.Example.Id);
        var annotations = new List<ImageAnnotations>();
        foreach (var item in items.Where(item => item.Split == split))
        {
            var record = recordsById[item.ExampleIdentifier];
            File.Copy(record.Example.ImagePath, Path.Combine(splitDirectory, item.ImageFilename));
            var imageWidth = record.Example.Manifest.ImageWidth;
            var imageHeight = record.Example.Manifest.ImageHeight;
            annotations.Add(new ImageAnnotations(
                item.ImageFilename,
                record.Annotations.Select(annotation => new ObjectAnnotation(
                    annotation.ClassIdentifier,
                    new ObjectCoordinates(
                        (annotation.X + annotation.Width / 2) * imageWidth,
                        (annotation.Y + annotation.Height / 2) * imageHeight,
                        annotation.Width * imageWidth,
                        annotation.Height * imageHeight),
                    annotation.IsHardNegative ? true : null)).ToArray()));
        }
        AtomicFile.Write(
            Path.Combine(splitDirectory, AnnotationsFilename),
            JsonSerializer.SerializeToUtf8Bytes(annotations, JsonOptions));
    }

    private HashSet<Guid> ValidationExampleIdentifiers(
        IReadOnlyList<SourceRecord> records,
        string modelIdentifier)
    {
        if (records.Count <= 1) return [];
        var parents = Enumerable.Range(0, records.Count).ToArray();
        int Root(int index)
        {
            var current = index;
            while (parents[current] != current)
            {
                parents[current] = parents[parents[current]];
                current = parents[current];
            }
            return current;
        }
        void Join(int first, int second)
        {
            var firstRoot = Root(first);
            var secondRoot = Root(second);
            if (firstRoot != secondRoot) parents[secondRoot] = firstRoot;
        }
        for (var first = 0; first < records.Count; first++)
        {
            for (var second = first + 1; second < records.Count; second++)
            {
                if (records[first].Example.Manifest.CaptureGroupIdentifier
                        == records[second].Example.Manifest.CaptureGroupIdentifier
                    || records[first].ImageDigest == records[second].ImageDigest)
                {
                    Join(first, second);
                }
            }
        }
        var grouped = Enumerable.Range(0, records.Count)
            .GroupBy(Root)
            .Select(group => SplitGroup.Create(group.ToArray(), records))
            .OrderBy(group => group.AnnotationCount)
            .ThenBy(group => group.StableKey, StringComparer.Ordinal)
            .ToArray();
        if (grouped.Length <= 1) return [];

        var ledger = LoadSplitLedger();
        ledger.Assignments.TryGetValue(modelIdentifier, out var previous);
        previous ??= new Dictionary<string, LabelingDatasetSplit>(StringComparer.Ordinal);
        var validationGroups = new List<SplitGroup>();
        var unassignedGroups = new List<SplitGroup>();
        foreach (var group in grouped)
        {
            if (group.HasHardNegative) continue;
            var assignments = group.Indices
                .Select(index => records[index].Example.Id.ToString("D").ToLowerInvariant())
                .Where(previous.ContainsKey)
                .Select(identifier => previous[identifier])
                .ToArray();
            if (assignments.Contains(LabelingDatasetSplit.Validation))
            {
                validationGroups.Add(group);
            }
            else if (!assignments.Contains(LabelingDatasetSplit.Training))
            {
                unassignedGroups.Add(group);
            }
        }

        var totalAnnotations = grouped.Sum(group => group.AnnotationCount);
        var target = Math.Max(1, (int)Math.Round(totalAnnotations * 0.2,
            MidpointRounding.AwayFromZero));
        var selectedCount = validationGroups.Sum(group => group.AnnotationCount);
        var maximumValidationGroups = Math.Max(1, grouped.Length - 1);
        foreach (var group in unassignedGroups)
        {
            if (selectedCount >= target || validationGroups.Count >= maximumValidationGroups) break;
            validationGroups.Add(group);
            selectedCount += group.AnnotationCount;
        }
        if (validationGroups.Count == 0)
        {
            var first = grouped.FirstOrDefault(group => !group.HasHardNegative);
            if (first is not null) validationGroups.Add(first);
        }

        var validationKeys = validationGroups.Select(group => group.StableKey)
            .ToHashSet(StringComparer.Ordinal);
        var positiveCounts = grouped.ToDictionary(
            group => group.StableKey,
            group => group.Indices.SelectMany(index => records[index].Annotations)
                .Where(annotation => !annotation.IsHardNegative)
                .GroupBy(annotation => annotation.ClassIdentifier)
                .ToDictionary(classGroup => classGroup.Key, classGroup => classGroup.Count(),
                    StringComparer.Ordinal),
            StringComparer.Ordinal);
        var positiveClasses = positiveCounts.Values.SelectMany(counts => counts.Keys)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var classIdentifier in positiveClasses.Order(StringComparer.Ordinal))
        {
            var classGroups = grouped.Where(group =>
                positiveCounts[group.StableKey].GetValueOrDefault(classIdentifier) > 0).ToArray();
            var totalPositive = classGroups.Sum(group =>
                positiveCounts[group.StableKey].GetValueOrDefault(classIdentifier));
            var validationTarget = Math.Min(
                Math.Max(1, (int)Math.Round(totalPositive * 0.2,
                    MidpointRounding.AwayFromZero)),
                Math.Max(0, totalPositive - 1));
            var validationPositive = classGroups.Where(group => validationKeys.Contains(group.StableKey))
                .Sum(group => positiveCounts[group.StableKey].GetValueOrDefault(classIdentifier));
            while (validationPositive > validationTarget)
            {
                var trainGroup = classGroups.Where(group => validationKeys.Contains(group.StableKey))
                    .OrderBy(group => positiveCounts[group.StableKey]
                        .GetValueOrDefault(classIdentifier))
                    .ThenBy(group => group.AnnotationCount)
                    .ThenBy(group => group.StableKey, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (trainGroup is null) break;
                validationKeys.Remove(trainGroup.StableKey);
                validationPositive -= positiveCounts[trainGroup.StableKey]
                    .GetValueOrDefault(classIdentifier);
            }
        }
        validationGroups = validationGroups.Where(group => validationKeys.Contains(group.StableKey))
            .ToList();
        var validationIds = validationGroups.SelectMany(group => group.Indices)
            .Select(index => records[index].Example.Id)
            .ToHashSet();
        var updated = new Dictionary<string, LabelingDatasetSplit>(previous, StringComparer.Ordinal);
        foreach (var record in records)
        {
            updated[record.Example.Id.ToString("D").ToLowerInvariant()] =
                validationIds.Contains(record.Example.Id)
                    ? LabelingDatasetSplit.Validation
                    : LabelingDatasetSplit.Training;
        }
        ledger.Assignments[modelIdentifier] = updated;
        SaveSplitLedger(ledger);
        return validationIds;
    }

    private string SplitLedgerPath => Path.Combine(
        Directory.GetParent(RootDirectory)?.FullName ?? RootDirectory,
        "split-assignments.json");

    private SplitLedger LoadSplitLedger()
    {
        if (!File.Exists(SplitLedgerPath)) return new SplitLedger();
        return JsonSerializer.Deserialize<SplitLedger>(File.ReadAllBytes(SplitLedgerPath), JsonOptions)
            ?? new SplitLedger();
    }

    private void SaveSplitLedger(SplitLedger ledger) => AtomicFile.Write(
        SplitLedgerPath,
        JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions));

    private sealed record SourceRecord(
        SavedLabelingExample Example,
        string ImageDigest,
        IReadOnlyList<LabelingExampleAnnotation> Annotations)
    {
        internal static SourceRecord Create(
            SavedLabelingExample example,
            IReadOnlySet<string> included)
        {
            var data = File.ReadAllBytes(example.ImagePath);
            if (data.Length == 0) throw new InvalidDataException(
                $"The saved image is empty: {example.ImagePath}");
            return new SourceRecord(
                example,
                Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                example.Manifest.Annotations.Select(annotation => annotation.Canonicalized())
                    .Where(annotation => included.Contains(annotation.ClassIdentifier))
                    .ToArray());
        }
    }

    private sealed record SplitGroup(
        IReadOnlyList<int> Indices,
        int AnnotationCount,
        bool HasHardNegative,
        string StableKey)
    {
        internal static SplitGroup Create(
            IReadOnlyList<int> indices,
            IReadOnlyList<SourceRecord> records) => new(
                indices,
                indices.Sum(index => records[index].Annotations.Count(annotation =>
                    !annotation.IsHardNegative)),
                indices.Any(index => records[index].Annotations.Any(annotation =>
                    annotation.IsHardNegative)),
                indices.Select(index => records[index].Example.Id.ToString("D").ToLowerInvariant())
                    .Min(StringComparer.Ordinal) ?? "");
    }

    private sealed class SplitLedger
    {
        public int SchemaVersion { get; init; } = 1;
        public Dictionary<string, Dictionary<string, LabelingDatasetSplit>> Assignments { get; init; }
            = new(StringComparer.Ordinal);
    }
}
