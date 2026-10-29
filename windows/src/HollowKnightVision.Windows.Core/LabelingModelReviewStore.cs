using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record LabelingTrainingMetricSummary
{
    public bool IsValid { get; init; }
    public double? MeanAveragePrecision { get; init; }
    public double? MeanAveragePrecisionAt50PercentIOU { get; init; }
    public Dictionary<string, double> AveragePrecisionByClass { get; init; } = [];
    public Dictionary<string, double> AveragePrecisionAt50PercentIOUByClass { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record LabelingReviewMetrics(double ConfidenceThreshold, double IntersectionOverUnionThreshold,
    int TruePositives, int FalsePositives, int FalseNegatives, double Precision, double Recall,
    double MeanIntersectionOverUnion);
public sealed record LabelingReviewBox(string Label, double X, double Y, double Width, double Height, double Confidence);
public sealed record LabelingReviewExample(Guid ExampleIdentifier, string ImageFilename, string Split,
    IReadOnlyList<LabelingReviewBox> Human, IReadOnlyList<LabelingReviewBox> Predictions);
public sealed record LabelingPredictionReview(IReadOnlyList<LabelingReviewExample> Examples,
    LabelingReviewMetrics Metrics, IReadOnlyList<LabelingReviewExample>? BaselineExamples = null,
    LabelingReviewMetrics? BaselineMetrics = null);

public sealed record LabelingTrainingRunSummary
{
    [JsonConverter(typeof(TrainingRunIdentifierConverter))]
    public required Guid Id { get; init; }
    public required Guid DatasetIdentifier { get; init; }
    public required string ClassIdentifier { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required string ModelFilename { get; init; }
    public string? ModelFormat { get; init; }
    public string? CheckpointFilename { get; init; }
    public string? PredictionsFilename { get; init; }
    public string? BaseRunIdentifier { get; init; }
    public string[]? ClassIdentifiers { get; init; }
    public LabelingTrainingMetricSummary? TrainingMetrics { get; init; }
    public LabelingTrainingMetricSummary? ValidationMetrics { get; init; }
    public LabelingReviewMetrics? ReviewMetrics { get; init; }
    // Preserve the complete shared Python/Swift manifest when making a durable ONNX run.
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}

// The Python trainer uses the output directory name as its run identifier.
// Earlier Windows runs used compact UUIDs; retain those completed models while
// writing the canonical hyphenated format for new durable metadata.
internal sealed class TrainingRunIdentifierConverter : JsonConverter<Guid>
{
    public TrainingRunIdentifierConverter() { }
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && (Guid.TryParseExact(reader.GetString(), "D", out var identifier)
                || Guid.TryParseExact(reader.GetString(), "N", out identifier))) return identifier;
        throw new JsonException("Training run identifier must be a UUID.");
    }
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed record LabelingModelCandidate(string RunDirectory, string DatasetDirectory,
    LabelingTrainingRunSummary Summary, LabelingPredictionReview? PredictionReview)
{
    [JsonIgnore] public Guid Id => Summary.Id;
}

public sealed class LabelingModelReviewStore(string runsRoot, string datasetsRoot)
{
    public IReadOnlyList<LabelingModelCandidate> LoadCandidates(string? classIdentifier = null) =>
        !Directory.Exists(runsRoot) ? [] : Directory.EnumerateDirectories(runsRoot)
            .Where(path => !Path.GetFileName(path).StartsWith('.')
                && (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) == 0)
            // Interrupted training/staging directories are not completed candidates.
            .Where(path => File.Exists(Path.Combine(path, "training.json")))
            .Select(LoadCandidate).OfType<LabelingModelCandidate>()
            .Where(candidate => classIdentifier is null || candidate.Summary.ClassIdentifier == classIdentifier)
            .OrderBy(candidate => candidate.Summary.CompletedAt).ThenBy(candidate => candidate.Id).ToArray();

    public LabelingModelCandidate? LoadLatestCandidate(string? classIdentifier = null) => LoadCandidates(classIdentifier).LastOrDefault();

    public LabelingModelCandidate? LoadCandidate(string runDirectory)
    {
        if (!Directory.Exists(runDirectory)) return null;
        var summary = ModelStoreJson.Read<LabelingTrainingRunSummary>(ModelStoreJson.Child(runDirectory, "training.json"));
        ModelStoreJson.Component(summary.ClassIdentifier);
        ModelStoreJson.Component(summary.ModelFilename);
        if (summary.CheckpointFilename is { } checkpoint) ModelStoreJson.Component(checkpoint);
        var review = summary.PredictionsFilename is { } predictions
            ? ModelStoreJson.Read<LabelingPredictionReview>(ModelStoreJson.Child(runDirectory, predictions)) : null;
        return new(Path.GetFullPath(runDirectory), Path.Combine(Path.GetFullPath(datasetsRoot),
            summary.DatasetIdentifier.ToString("D")), summary, review);
    }
}

internal static class ModelStoreJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string Component(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\')
            || value.Contains(':') || Path.IsPathRooted(value))
            throw new InvalidDataException($"Invalid model path component: {value}");
        var stem = value.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Reserved Windows model filename.");
        return value;
    }

    internal static string Child(string root, params string[] components)
    {
        var path = Path.GetFullPath(root);
        RejectLinks(path);
        foreach (var component in components)
        {
            path = Path.Combine(path, Component(component));
            RejectLinks(path);
        }
        return path;
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Model storage must not follow symbolic links or junctions.");
    }

    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options)
        ?? throw new InvalidDataException($"Empty model metadata: {path}");

    internal static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
