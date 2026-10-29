using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record LabelingOccurrenceBounds(int? Minimum, int? Maximum);
public sealed record LabelingOccurrenceContract(string ClassIdentifier, IReadOnlyList<int>? AllowedCounts);
public sealed record LabelingPairContract(string Identifier, IReadOnlyList<string> ClassIdentifiers);
public sealed record LabelingSetContract(string ContextIdentifier, bool RequiresAllContextObjects,
    IReadOnlyDictionary<string, LabelingOccurrenceBounds>? OccurrenceOverrides);
public sealed record LabelingContractCatalog(int SchemaVersion, int DefaultMaximum,
    IReadOnlyList<LabelingOccurrenceContract> Occurrences, IReadOnlyList<LabelingPairContract> Pairs,
    IReadOnlyList<LabelingSetContract> LabelSets)
{
    private sealed record ObjectDefinition(string Id, string Name);
    private sealed record ContextDefinition(string ContextIdentifier, string Name, ObjectDefinition[] Objects);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static T Resource<T>(string name)
    {
        using var stream = typeof(LabelingContractCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Missing {name}.");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException($"Invalid {name}.");
    }
    public static LabelingContractCatalog Bundled { get; } = Resource<LabelingContractCatalog>("labeling-contracts.json");
    public static IReadOnlyList<MenuStencilContext> Contexts { get; } = LoadContexts();
    private static readonly IReadOnlyDictionary<string, string> ContextNames = Resource<ContextDefinition[]>("labeling-contexts.json")
        .ToDictionary(context => context.ContextIdentifier, context => context.Name, StringComparer.Ordinal);
    public static string ContextName(string identifier) => ContextNames.GetValueOrDefault(identifier, identifier);
    private static IReadOnlyList<MenuStencilContext> LoadContexts()
    {
        var contexts = Resource<ContextDefinition[]>("labeling-contexts.json").Select(context =>
            new MenuStencilContext(context.ContextIdentifier, context.Objects.Select(item => new MenuStencilLabel(item.Id, item.Name)).ToArray())).ToList();
        // These three object-library contexts live in LabelingEditorView.swift,
        // outside the exported screen catalog. Contracts can evaluate them,
        // but they are not screens and must not alter menu membership weights.
        var labels = contexts.SelectMany(context => context.Labels).DistinctBy(item => item.Identifier).ToArray();
        foreach (var group in new[] { "enemies", "world", "shared" })
            contexts.Add(new(group, labels.Where(item => item.Identifier.StartsWith(group + ".", StringComparison.Ordinal)).ToArray()));
        return contexts;
    }
    public static LabelingContractCatalog Load(string path) => JsonSerializer.Deserialize<LabelingContractCatalog>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("Invalid label contracts.");
    public static IReadOnlyList<MenuStencilContext> ScreenContexts => Contexts.Where(context =>
        context.Identifier is not ("enemies" or "world" or "shared")).ToArray();
    public LabelingOccurrenceContract? Occurrence(string identifier) => Occurrences.FirstOrDefault(item =>
        LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) == LabelingClassIdentity.CanonicalIdentifier(identifier));
    public LabelingSetContract? SetContract(string context) => LabelSets.FirstOrDefault(item => item.ContextIdentifier == context);
    public IReadOnlyList<LabelingSetContract> SetsContaining(string identifier) => LabelSets.Where(set =>
        Contexts.FirstOrDefault(context => context.Identifier == set.ContextIdentifier)?.Labels.Any(label =>
            LabelingClassIdentity.CanonicalIdentifier(label.Identifier) == LabelingClassIdentity.CanonicalIdentifier(identifier)) == true).ToArray();
}

public sealed record LabelingContractEvaluation(string Id, string Title, bool IsSatisfied, bool IsExempt)
{
    public bool IsBreach => !IsSatisfied && !IsExempt;
}

/// <summary>Mac occurrence, pair and screen contracts. Completion/Draft is not a validity test.</summary>
public static class LabelingContractEvaluator
{
    public static Dictionary<string, int> PositiveCounts(IEnumerable<LabelingExampleAnnotation> annotations) => annotations
        .Where(annotation => !annotation.IsHardNegative).GroupBy(annotation => LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    public static IReadOnlyList<LabelingContractEvaluation> Evaluate(SavedLabelingExample example,
        LabelingContractCatalog? catalog = null, IReadOnlySet<string>? exemptions = null)
    {
        catalog ??= LabelingContractCatalog.Bundled;
        var counts = PositiveCounts(example.Manifest.Annotations.Where(annotation =>
            LabelingHudStencilPolicy.IsLabelable(example.Manifest.ContextIdentifier, annotation.ClassIdentifier)));
        var result = new List<LabelingContractEvaluation>();
        var context = LabelingContractCatalog.Contexts.FirstOrDefault(item => item.Identifier == example.Manifest.ContextIdentifier);
        var set = catalog.SetContract(example.Manifest.ContextIdentifier);
        var contextIds = (context?.Labels ?? []).Select(item => Canonical(item.Identifier)).ToHashSet(StringComparer.Ordinal);
        void Add(string id, string title, bool satisfied) => result.Add(new(id, title, satisfied, exemptions?.Contains(id) == true));
        if (example.Manifest.Annotations.Count == 0) Add("screenshot.has-label", "Screenshot has at least one label", false);
        if (set?.RequiresAllContextObjects == true && context is not null)
            foreach (var label in context.Labels.Where(label => LabelingHudStencilPolicy.IsLabelable(context.Identifier, label.Identifier)))
            {
                var id = Canonical(label.Identifier); var bounds = set.OccurrenceOverrides?.GetValueOrDefault(id);
                var minimum = bounds?.Minimum ?? 1; var maximum = bounds?.Maximum ?? RequiredMaximum(id, catalog);
                var count = counts.GetValueOrDefault(id);
                var range = maximum is null ? $"{minimum}+" : minimum == maximum ? $"{minimum}" : $"{minimum}…{maximum}";
                Add($"set.{set.ContextIdentifier}.{id}", $"{label.Name}: {count}, expected {range}", count >= minimum && (maximum is null || count <= maximum));
            }
        foreach (var (id, count) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Where(pair => !contextIds.Contains(pair.Key)))
        {
            var occurrence = catalog.Occurrence(id);
            var satisfied = occurrence?.AllowedCounts is { } allowed ? allowed.Contains(count) : occurrence is not null || count <= catalog.DefaultMaximum;
            var expected = occurrence?.AllowedCounts is { } values ? string.Join(" or ", values) : occurrence is not null ? "any" : $"0…{catalog.DefaultMaximum}";
            Add($"occurrence.{id}", $"{ObjectName(id)}: {count}, expected {expected}", satisfied);
        }
        foreach (var pair in catalog.Pairs)
        {
            var ids = pair.ClassIdentifiers.Select(Canonical).ToArray(); var any = ids.Any(id => counts.GetValueOrDefault(id) > 0);
            if (!any && !ids.Any(contextIds.Contains)) continue;
            Add($"pair.{pair.Identifier}", string.Join(" + ", pair.ClassIdentifiers.Select(ObjectName)), !any || ids.All(id => counts.GetValueOrDefault(id) > 0));
        }
        return result;
    }

    public static bool ShouldAdvance(string classIdentifier, string contextIdentifier,
        IEnumerable<LabelingExampleAnnotation> rectangles, LabelingContractCatalog? catalog = null)
    {
        catalog ??= LabelingContractCatalog.Bundled;
        var id = Canonical(classIdentifier); var count = PositiveCounts(rectangles).GetValueOrDefault(id);
        if (catalog.SetContract(contextIdentifier)?.OccurrenceOverrides?.GetValueOrDefault(id) is { } bounds)
            return bounds.Maximum is { } maximum && count >= maximum;
        if (catalog.Occurrence(id) is { } occurrence)
            return PositiveMaximum(occurrence.AllowedCounts) is { } target && count >= target;
        return count >= catalog.DefaultMaximum;
    }

    public static bool AcceptsMenuExample(SavedLabelingExample example, LabelingContractCatalog? catalog = null)
    {
        catalog ??= LabelingContractCatalog.Bundled;
        var context = LabelingContractCatalog.Contexts.FirstOrDefault(item => item.Identifier == example.Manifest.ContextIdentifier);
        if (context is null) return false;
        if (Evaluate(example, catalog).All(item => item.IsSatisfied)) return true;
        // Normal Hollow Knight's Options screen has no Mods row. Mac permits
        // this screen using normal-mode minimum counts, not the Ready checkbox.
        if (context.Identifier != "options") return false;
        var counts = PositiveCounts(example.Manifest.Annotations); var set = catalog.SetContract(context.Identifier);
        return context.Labels.Where(label => Canonical(label.Identifier) != "shared.mods").All(label =>
            counts.GetValueOrDefault(Canonical(label.Identifier)) >= (set?.OccurrenceOverrides?.GetValueOrDefault(Canonical(label.Identifier))?.Minimum
                ?? (set?.RequiresAllContextObjects == true ? 1 : 0)));
    }
    private static string Canonical(string id) => LabelingClassIdentity.CanonicalIdentifier(id);
    private static int? PositiveMaximum(IReadOnlyList<int>? counts) => counts?.Where(value => value > 0).Select(value => (int?)value).Max();
    private static int? RequiredMaximum(string id, LabelingContractCatalog catalog) => catalog.Occurrence(id) is { } occurrence
        ? PositiveMaximum(occurrence.AllowedCounts) : catalog.DefaultMaximum;
    private static string ObjectName(string id) => LabelingContractCatalog.Contexts.SelectMany(context => context.Labels)
        .FirstOrDefault(label => Canonical(label.Identifier) == Canonical(id))?.Name ?? id;
}

public sealed class LabelingContractExemptionStore
{
    public const string Filename = "contract-exemptions.json";
    public IReadOnlySet<string> Load(SavedLabelingExample example)
    {
        try
        {
            var result = (JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(example.DirectoryPath, Filename))) ?? []).ToHashSet(StringComparer.Ordinal);
            if (result.Remove("set.game.game.geo"))
                try { Save(result, example); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new HashSet<string>(); }
    }
    public void Save(IEnumerable<string> exemptions, SavedLabelingExample example) =>
        ModelStoreJson.Write(Path.Combine(example.DirectoryPath, Filename), exemptions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
}
