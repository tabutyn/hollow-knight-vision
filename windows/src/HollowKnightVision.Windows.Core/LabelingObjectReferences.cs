using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record LabelingObjectReference(SavedLabelingExample Example, LabelingExampleAnnotation Annotation);

/// <summary>
/// Complete labeling vocabulary. Menu labels remain available to build and
/// inspect stencils even though they are no longer object-model classes.
/// </summary>
public static class LabelingCatalogPolicy
{
    public static IReadOnlySet<string> ClassIdentifiers(
        IReadOnlyList<MenuStencilLabel>? custom = null) =>
        LabelingContractCatalog.Contexts
            .SelectMany(context => context.Labels)
            .Concat(custom ?? [])
            .Select(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier))
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Fixed screen-space HUD art is recognized by the HUD stencil tracker, not by
/// the learned object detector. These identifiers remain in the labeling
/// catalog so source examples and stencil authoring remain available.
/// </summary>
public static class LabelingHudStencilPolicy
{
    public static IReadOnlySet<string> ClassIdentifiers { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "game.health",
            "game.mana",
            "game.geo"
        };
}

public static class LabelingSharedModelPolicy
{
    public const string ModelIdentifier = LabelingDatasetExporter.SharedObjectModelIdentifier;
    public const string ModelName = "Shared Object Model";

    public static IReadOnlySet<string> ClassIdentifiers(
        IReadOnlyList<MenuStencilLabel>? custom = null)
    {
        var identifiers = LabelingContractCatalog.Contexts
            .Single(context => context.Identifier == "game")
            .Labels
            .Select(label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier))
            .ToHashSet(StringComparer.Ordinal);
        identifiers.ExceptWith(LabelingHudStencilPolicy.ClassIdentifiers);
        identifiers.UnionWith((custom ?? []).Select(
            label => LabelingClassIdentity.CanonicalIdentifier(label.Identifier)));
        return identifiers;
    }
}

public static class LabelingObjectReferencePolicy
{
    public static IReadOnlyList<LabelingObjectReference> References(IEnumerable<SavedLabelingExample> examples, string identifier)
    {
        var canonical = LabelingClassIdentity.CanonicalIdentifier(identifier);
        return examples.OrderBy(example => example.Manifest.CreatedAt).SelectMany(example => example.Manifest.Annotations
            .Where(annotation => !annotation.IsHardNegative && LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier) == canonical)
            .Select(annotation => new LabelingObjectReference(example, annotation))).ToArray();
    }

    public static LabelingObjectReference? Preferred(IEnumerable<SavedLabelingExample> examples, string identifier, Guid? preferred)
    {
        var references = References(examples, identifier);
        return references.FirstOrDefault(reference => reference.Example.Id == preferred) ?? references.FirstOrDefault();
    }

    public static IReadOnlyList<MenuStencilLabel> ModelClasses(IReadOnlyList<MenuStencilLabel>? custom = null)
    {
        var identifiers = LabelingSharedModelPolicy.ClassIdentifiers(custom);
        return LabelingContractCatalog.Contexts
            .Single(context => context.Identifier == "game")
            .Labels
            .Concat(custom ?? [])
            .Select(item => item with
            {
                Identifier = LabelingClassIdentity.CanonicalIdentifier(item.Identifier)
            })
            .Where(item => identifiers.Contains(item.Identifier))
            .DistinctBy(item => item.Identifier, StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed class LabelingObjectReferenceStore(string path)
{
    private readonly string path = Path.GetFullPath(path);
    private readonly object gate = new();
    public Guid? ExampleIdentifier(string identifier)
    {
        var values = ExampleIdentifiers([identifier]);
        return values.TryGetValue(LabelingClassIdentity.CanonicalIdentifier(identifier), out var value) ? value : null;
    }

    public IReadOnlyDictionary<string, Guid> ExampleIdentifiers(IEnumerable<string> identifiers)
    {
        lock (gate)
        {
            var values = Read(); var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var identifier in identifiers.Select(LabelingClassIdentity.CanonicalIdentifier).Distinct(StringComparer.Ordinal))
                if (values.TryGetValue(identifier, out var text) && Guid.TryParse(text, out var value)) result[identifier] = value;
            return result;
        }
    }

    public void SetExampleIdentifier(string identifier, Guid example)
    {
        lock (gate)
        {
            var values = Read(); var canonical = LabelingClassIdentity.CanonicalIdentifier(identifier);
            var value = example.ToString("D").ToUpperInvariant();
            if (values.GetValueOrDefault(canonical) == value) return;
            values[canonical] = value;
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(values));
        }
    }

    private Dictionary<string, string> Read()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path)) ?? [] : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}
