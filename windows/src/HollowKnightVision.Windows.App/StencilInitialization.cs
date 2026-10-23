using System.IO;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed record StencilInitializationStep(
    string ContextIdentifier,
    string ContextName,
    string ClassIdentifier,
    string ClassName,
    int Occurrence,
    int RequiredCount)
{
    internal string Instruction => RequiredCount == 1
        ? $"Draw a box around {ClassName}."
        : $"Draw box {Occurrence} of {RequiredCount} around {ClassName}.";
}

internal static class StencilInitializationPolicy
{
    internal sealed record Requirement(string Identifier, string Name, int Count = 1, bool ModdedOnly = false);
    internal sealed record ContextRequirements(string Identifier, string Name, Requirement[] Requirements);

    private sealed record ObjectDefinition(string Id, string Name);
    private sealed record ContextDefinition(string ContextIdentifier, string Name, ObjectDefinition[] Objects);
    private sealed record Occurrence(int? Minimum, int? Maximum);
    private sealed record SetContract(string ContextIdentifier, bool RequiresAllContextObjects,
        Dictionary<string, Occurrence>? OccurrenceOverrides);
    private sealed record Contracts(SetContract[] LabelSets);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static T Resource<T>(string name) => JsonSerializer.Deserialize<T>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)), JsonOptions)
        ?? throw new InvalidDataException($"Missing {name}.");

    internal static int Phase(string context) => context is "main-title" or "select-profile" or "gameplay-hud" ? 1
        : context == "clear-save" ? 3 : 2;

    internal static readonly ContextRequirements[] AllContexts = LoadContexts();
    internal static readonly ContextRequirements[] RequiredContexts = AllContexts
        .Where(context => context.Identifier != "game" && context.Requirements.Any(item => item.Count > 0))
        .OrderBy(context => Phase(context.Identifier))
        .ThenBy(context => context.Identifier == "main-title" ? 0 : context.Identifier == "select-profile" ? 1 : 2)
        .ToArray();

    private static ContextRequirements[] LoadContexts()
    {
        var contracts = Resource<Contracts>("labeling-contracts.json");
        return Resource<ContextDefinition[]>("labeling-contexts.json").Select(context =>
        {
            var contract = contracts.LabelSets.First(item => item.ContextIdentifier == context.ContextIdentifier);
            return new ContextRequirements(context.ContextIdentifier, context.Name,
                context.Objects.Select(item => new Requirement(item.Id, item.Name,
                    contract.OccurrenceOverrides?.GetValueOrDefault(item.Id)?.Minimum
                        ?? (contract.RequiresAllContextObjects ? 1 : 0),
                    context.ContextIdentifier == "mods" || item.Id == "shared.mods")).ToArray());
        }).ToArray();
    }

    internal static ContextRequirements[] Contexts(GameRuntimeMode mode) => RequiredContexts
        .Where(context => mode == GameRuntimeMode.Modded || context.Identifier != "mods").ToArray();

    internal static IReadOnlyList<StencilInitializationStep> MissingSteps(
        IReadOnlyList<SavedLabelingExample> examples,
        string languageIdentifier,
        GameRuntimeMode mode)
    {
        var missing = new List<StencilInitializationStep>();
        foreach (var context in Contexts(mode))
        {
            var requirements = context.Requirements
                .Where(item => !item.ModdedOnly || mode == GameRuntimeMode.Modded).ToArray();
            var representative = RepresentativeExample(examples, context.Identifier, languageIdentifier, mode);
            var bestCounts = representative is null ? new Dictionary<string, int>(StringComparer.Ordinal)
                : PositiveCounts(representative);
            foreach (var requirement in requirements)
            {
                var present = Math.Min(requirement.Count,
                    bestCounts.GetValueOrDefault(requirement.Identifier));
                for (var occurrence = present + 1; occurrence <= requirement.Count; occurrence++)
                {
                    missing.Add(new StencilInitializationStep(
                        context.Identifier, context.Name, requirement.Identifier,
                        requirement.Name, occurrence, requirement.Count));
                }
            }
        }
        return missing;
    }

    internal static SavedLabelingExample? RepresentativeExample(
        IReadOnlyList<SavedLabelingExample> examples, string context, string languageIdentifier, GameRuntimeMode mode)
    {
        var requirements = RequiredContexts.First(item => item.Identifier == context).Requirements
            .Where(item => !item.ModdedOnly || mode == GameRuntimeMode.Modded).ToArray();
        return examples.Where(example => NormalizeContext(example.Manifest.ContextIdentifier) == context
                && example.Manifest.ResolvedLanguageIdentifier == languageIdentifier
                && example.Manifest.ImageWidth == 640 && example.Manifest.ImageHeight == 360)
            .OrderByDescending(example =>
            {
                var counts = PositiveCounts(example);
                return requirements.Sum(item => Math.Min(item.Count, counts.GetValueOrDefault(item.Identifier)));
            })
            .ThenByDescending(example => example.Manifest.CreatedAt)
            .ThenBy(example => example.Id)
            .FirstOrDefault();
    }

    private static Dictionary<string, int> PositiveCounts(SavedLabelingExample example) =>
        example.Manifest.Annotations.Where(annotation => !annotation.IsHardNegative)
            .GroupBy(annotation => LabelingClassIdentity.CanonicalIdentifier(annotation.ClassIdentifier), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    internal static int RequiredCount(GameRuntimeMode mode) => Contexts(mode).Sum(context =>
        context.Requirements.Where(item => !item.ModdedOnly || mode == GameRuntimeMode.Modded)
            .Sum(item => item.Count));

    internal static string NormalizeContext(string context) => context.Trim().ToLowerInvariant() switch
    {
        "main title" => "main-title", "select profile" => "select-profile",
        "quit to menu" => "quit-to-menu", "gameplay" => "game",
        _ => context.Trim().ToLowerInvariant().Replace(' ', '-')
    };

    // ComboBox values must use the catalog's exact spelling (not title case:
    // "Gameplay Hud" does not match "Gameplay HUD").
    internal static string ContextDisplayName(string identifier) =>
        AllContexts.FirstOrDefault(context => context.Identifier == NormalizeContext(identifier))?.Name
        ?? identifier;
}

internal sealed class StencilInitializationCoordinator
{
    private readonly LabelingExampleStore store;
    private readonly GameRuntimeMode mode;
    private readonly string languagePath;

    internal StencilInitializationCoordinator(LabelingExampleStore store, GameRuntimeMode mode)
    {
        this.store = store;
        this.mode = mode;
        languagePath = Path.Combine(
            Directory.GetParent(store.RootDirectory)?.Parent?.FullName
                ?? Path.GetDirectoryName(store.RootDirectory)!,
            "initialization-language-v1.txt");
        LanguageIdentifier = LoadLanguage();
        Refresh();
    }

    internal string LanguageIdentifier { get; private set; }
    internal IReadOnlyList<StencilInitializationStep> Steps { get; private set; } = [];
    internal int TotalCount => StencilInitializationPolicy.RequiredCount(mode);
    internal int CompletedCount => Math.Max(0, TotalCount - Steps.Count);
    internal bool IsComplete => Steps.Count == 0 && HasUsableHUD;
    internal bool HasUsableHUD { get; set; }
    internal bool IsFirstPhaseComplete => HasUsableHUD && !Steps.Any(step => StencilInitializationPolicy.Phase(step.ContextIdentifier) == 1);
    internal GameRuntimeMode Mode => mode;
    internal StencilInitializationPolicy.ContextRequirements[] Contexts => StencilInitializationPolicy.Contexts(mode);
    internal int ScreenCount => Contexts.Length;
    internal int CompletedScreenCount => Contexts.Count(
        context => !Steps.Any(step => step.ContextIdentifier == context.Identifier)
            && (context.Identifier != "gameplay-hud" || HasUsableHUD));
    internal string Text(string key, params (string Key, string Value)[] values) =>
        StencilGuidanceText.Text(LanguageIdentifier, key, values);
    internal string ScreenName(string name) => Text(name);
    internal string ButtonTitle => IsComplete ? Text("stencil")
        : $"{Text("stencil")} ({CompletedScreenCount} / {ScreenCount})";
    internal SavedLabelingExample? ExampleFor(string context) =>
        StencilInitializationPolicy.RequiredContexts.Any(item => item.Identifier == context)
            ? StencilInitializationPolicy.RepresentativeExample(store.Load(), context, LanguageIdentifier, mode) : null;
    internal string Instruction(StencilInitializationStep step)
    {
        if (step.ContextIdentifier == "gameplay-hud")
            return Text(step.Occurrence == 1 ? "drawHealthFirst" : "drawHealthSecond");
        if (step.ClassIdentifier == "shared.select-decoration")
            return Text("draw", ("object", Text(step.Occurrence == 1 ? "leftDecoration" : "rightDecoration")));
        return Text(step.RequiredCount == 1 ? "draw" : "drawRepeated",
            ("object", Text(step.ClassName).TrimEnd('.')), ("count", step.Occurrence.ToString()),
            ("total", step.RequiredCount.ToString()));
    }
    internal string? Guidance(string? context, string? selected, string? navigationContext = null)
    {
        if (context is not null && StencilInitializationPolicy.RequiredContexts.Any(item => item.Identifier == context))
        {
            if (context == "select-profile" && IsFirstPhaseComplete && selected == "select-profile.clear-save"
                && ExampleFor(context) is { } example
                && !example.Manifest.Annotations.Any(annotation => !annotation.IsHardNegative
                    && annotation.ClassIdentifier == "select-profile.clear-save"))
                return Text("draw", ("object", Text("Clear Save")));
            var remaining = Steps.Where(step => step.ContextIdentifier == context).ToArray();
            var next = remaining.FirstOrDefault(step => step.ClassIdentifier == selected) ?? remaining.FirstOrDefault();
            var name = StencilInitializationPolicy.RequiredContexts.First(item => item.Identifier == context).Name;
            if (next is null && context == "gameplay-hud" && !HasUsableHUD) return Text("invalidHUD");
            return next is null ? context == "select-profile" ? Text("profilePhaseDone")
                : Text("screenDone", ("screen", ScreenName(name))) : Instruction(next);
        }
        if (Contexts.FirstOrDefault(item => item.Identifier == navigationContext) is { } target
            && !Steps.Any(item => item.ContextIdentifier == navigationContext))
            return Text("screenReady", ("screen", ScreenName(target.Name)));
        if (navigationContext is null && !HasUsableHUD
            && !Steps.Any(item => item.ContextIdentifier is "main-title" or "select-profile"))
            return ExampleFor("gameplay-hud") is null ? Text("navigateHUD")
                : Text("resumeStencil", ("screen", ScreenName("Gameplay HUD")));
        var step = Steps.FirstOrDefault(item => item.ContextIdentifier == navigationContext) ?? Steps.FirstOrDefault();
        if (step is not null && ExampleFor(step.ContextIdentifier) is not null)
            return Text("resumeStencil", ("screen", ScreenName(step.ContextName)));
        return step is null ? null : step.ContextIdentifier == "gameplay-hud" ? Text("navigateHUD")
            : step.ContextIdentifier == "clear-save" ? Text("navigateClearSave") : step.ContextIdentifier == "select-profile"
            ? Text("selectProfile") : Text("navigateShort", ("screen", ScreenName(step.ContextName)));
    }

    internal void SetLanguage(string identifier)
    {
        LanguageIdentifier = SupportedLanguages.Any(item => item.Identifier == identifier)
            ? identifier : "en";
        Directory.CreateDirectory(Path.GetDirectoryName(languagePath)!);
        File.WriteAllText(languagePath, LanguageIdentifier);
        Refresh();
    }

    internal void Refresh() => Steps = StencilInitializationPolicy.MissingSteps(
        store.Load(), LanguageIdentifier, mode);

    private string LoadLanguage()
    {
        if (!File.Exists(languagePath)) return "en";
        var saved = File.ReadAllText(languagePath).Trim();
        return SupportedLanguages.Any(item => item.Identifier == saved) ? saved : "en";
    }

    internal static readonly (string Identifier, string Name)[] SupportedLanguages =
        new[] { "en", "es", "fr", "it", "ja", "ko", "pt-BR", "ru", "zh-Hans", "zh-Hant", "de" }
            .Select(id => (id, StencilGuidanceText.Text(id, "nativeName"))).ToArray();
}

internal static class TrainedModelReadiness
{
    internal static bool IsReady(string directory, IReadOnlySet<Guid> exampleIDs)
    {
        if (exampleIDs.Count == 0 || !File.Exists(Path.Combine(directory, "Detector.onnx"))) return false;
        try {
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "training.json")));
            if (!summary.RootElement.TryGetProperty("predictionsFilename", out var filename)
                || string.IsNullOrWhiteSpace(filename.GetString())) return false;
            using var review = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, filename.GetString()!)));
            return review.RootElement.GetProperty("examples").EnumerateArray().Any(item =>
                item.TryGetProperty("exampleIdentifier", out var id) && id.TryGetGuid(out var value) && exampleIDs.Contains(value));
        } catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException) {
            return false;
        }
    }
}
