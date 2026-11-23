using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

/// <summary>Proves enabled classes from a completed run or its original positive training annotations.</summary>
internal static class LabelingTrainingEvidence
{
    private sealed record Image(Annotation?[]? Annotations);
    private sealed record Annotation(string? Label, Coordinates? Coordinates, bool? IsNegative);
    private sealed record Coordinates(double Width, double Height);

    internal static IReadOnlySet<string> Canonical(IEnumerable<string?> identifiers) => identifiers
        .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
        .Select(identifier => LabelingClassIdentity.CanonicalIdentifier(identifier!))
        .ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlySet<string>? PositiveClasses(string datasetDirectory)
    {
        try
        {
            var path = ModelStoreJson.Child(datasetDirectory, "training", LabelingDatasetExporter.AnnotationsFilename);
            return Canonical(ModelStoreJson.Read<Image?[]>(path).SelectMany(image => image?.Annotations ?? []).OfType<Annotation>()
                .Where(item => item.IsNegative != true
                    && item.Coordinates is { Width: > 0 and < double.PositiveInfinity, Height: > 0 and < double.PositiveInfinity })
                .Select(item => item.Label));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    internal static IReadOnlySet<string>? FromManifest(JsonElement summary, string? trainingRoot)
    {
        if (summary.ValueKind != JsonValueKind.Object) return null;
        if (summary.TryGetProperty("trainedClassIdentifiers", out var evidence) && evidence.ValueKind != JsonValueKind.Null)
        {
            return evidence.ValueKind == JsonValueKind.Array
                ? Canonical(evidence.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()))
                : Canonical([]);
        }
        return trainingRoot is not null && summary.TryGetProperty("datasetIdentifier", out var dataset)
            && dataset.ValueKind == JsonValueKind.String && dataset.TryGetGuid(out var identifier)
            ? PositiveClasses(Path.Combine(trainingRoot, "datasets", identifier.ToString("D")))
            : null;
    }

    internal static IReadOnlySet<string>? FromRun(string directory, string trainingRoot)
    {
        try
        {
            var path = ModelStoreJson.Child(directory, "training.json");
            using var summary = JsonDocument.Parse(File.ReadAllBytes(path));
            return FromManifest(summary.RootElement, trainingRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
