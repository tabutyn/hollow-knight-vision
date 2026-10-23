using System.Text.Json;
using System.Text.RegularExpressions;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class StencilGuidanceParityTests
{
    private static readonly string[] Languages =
    [
        "en", "es", "fr", "it", "ja", "ko", "pt-BR", "ru", "zh-Hans", "zh-Hant", "de"
    ];

    private sealed record ResourceObject(string Id, string Name);
    private sealed record ResourceContext(string ContextIdentifier, string Name, ResourceObject[] Objects);

    private static void Check(
        bool condition,
        string message = "Stencil guidance parity assertion failed.")
    {
        if (!condition) throw new Exception(message);
    }

    private static Dictionary<string, Dictionary<string, string>> GuidanceCatalog() =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "stencil-guidance.json")))
        ?? throw new InvalidDataException("Missing stencil-guidance.json.");

    private static LabelingExampleAnnotation Box(string identifier) =>
        new(Guid.NewGuid(), identifier, .1, .1, .2, .1);

    private static BgraFrame Frame() => new(
        640,
        360,
        640 * BgraFrame.BytesPerPixel,
        new byte[640 * 360 * BgraFrame.BytesPerPixel],
        0,
        DateTimeOffset.UnixEpoch);

    private static void WithTemporaryDirectory(Action<string> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-stencil-guidance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            run(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void TestVisibleButtonsAndCampaignInstructionsHaveTranslations()
    {
        var catalog = GuidanceCatalog();
        var keys = new[]
        {
            "Gameplay", "Label", "Model", "Add", "Modify", "Negative", "Save State", "New", "Delete", "Load",
            "Free Fly", "Current", "Fit", "Object Detection", "Stencil Detection", "Left Select Decoration",
            "Right Select Decoration", "Summary", "Objects", "Screenshots", "Train", "Stop Training", "Recent Frames",
            "profilePhaseDone", "navigateHUD", "drawHealthFirst", "drawHealthSecond", "phase1", "phase2", "phase3",
            "stencilLocked", "modelLocked", "resumeStencil", "deleteScreenshot", "runtimeHelp", "Max Health"
        };

        Check(StencilInitializationCoordinator.SupportedLanguages.Select(item => item.Identifier)
            .SequenceEqual(Languages, StringComparer.Ordinal), "The Windows language list differs from Mac.");
        foreach (var language in Languages)
        {
            Check(catalog.TryGetValue(language, out var localized), $"Missing guidance language {language}.");
            foreach (var key in keys)
                Check(localized!.ContainsKey(key), $"Missing guidance translation {language}:{key}.");
        }
    }

    public static void TestAllLanguagesHaveGuidanceAndMatchingPlaceholders()
    {
        var catalog = GuidanceCatalog();
        var english = catalog["en"];
        static HashSet<string> Placeholders(string value) =>
            Regex.Matches(value, "\\{[^}]+\\}").Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

        foreach (var language in Languages)
        {
            Check(catalog.TryGetValue(language, out var localized), $"Missing guidance language {language}.");
            Check(localized!.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(english.Keys),
                $"Guidance key set differs for {language}.");
            foreach (var (key, value) in english)
            {
                Check(localized.TryGetValue(key, out var translation) && !string.IsNullOrEmpty(translation),
                    $"Empty guidance translation {language}:{key}.");
                Check(Placeholders(translation!).SetEquals(Placeholders(value)),
                    $"Guidance placeholders differ for {language}:{key}.");
            }
        }
    }

    public static void TestProgressUsesOneReferenceAndRejectsWrongLanguageOrSize()
    {
        var requirements = StencilInitializationPolicy.RequiredContexts
            .Single(context => context.Identifier == "main-title").Requirements;
        var labels = requirements.SelectMany(requirement =>
            Enumerable.Range(0, requirement.Count).Select(_ => Box(requirement.Identifier))).ToArray();

        SavedLabelingExample Example(
            IReadOnlyList<LabelingExampleAnnotation> annotations,
            string language = "en",
            int width = 640)
        {
            var id = Guid.NewGuid();
            return new SavedLabelingExample("unused", new LabelingExampleManifest(
                LabelingExampleManifest.CurrentSchemaVersion,
                id,
                Guid.NewGuid(),
                "image.png",
                width,
                360,
                Guid.NewGuid(),
                "main-title",
                annotations,
                null,
                LabelingExampleCompletion.Draft,
                DateTimeOffset.UnixEpoch,
                language));
        }

        var first = Example(labels.Take(3).ToArray());
        var second = Example(labels.Skip(3).ToArray());
        var completeFrench = Example(labels, "fr");
        var wrongSize = Example(labels, width: 1280);
        SavedLabelingExample[] examples = [first, second, completeFrench, wrongSize];

        Check(StencilInitializationPolicy.RepresentativeExample(
                examples, "main-title", "en", GameRuntimeMode.Modded)?.Id == second.Id,
            "Partial screenshots were combined or the strongest compatible reference was not selected.");
        var missing = StencilInitializationPolicy.MissingSteps(examples, "en", GameRuntimeMode.Modded);
        Check(missing.Count(step => step.ContextIdentifier == "main-title") == 3,
            "Partial screenshots incorrectly combined into a completed stencil.");

        var completed = StencilInitializationPolicy.MissingSteps(
            [.. examples, Example(labels)], "en", GameRuntimeMode.Modded);
        Check(completed.First().ContextIdentifier == "select-profile"
            && !completed.Any(step => step.ContextIdentifier == "main-title"),
            "A complete compatible reference did not advance to Select Profile.");
    }

    public static void TestRequiredScreensFollowContractsAndWindowsCatalogMatches()
    {
        var expected = LabelingContractCatalog.Bundled.LabelSets
            .Where(contract => contract.RequiresAllContextObjects && contract.ContextIdentifier != "game")
            .Select(contract => contract.ContextIdentifier).ToHashSet(StringComparer.Ordinal);
        Check(StencilInitializationPolicy.RequiredContexts.Select(context => context.Identifier)
            .ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            "Required stencil screens do not follow the bundled contracts.");
        Check(StencilInitializationPolicy.Contexts(GameRuntimeMode.Normal).Length == 25
            && !StencilInitializationPolicy.Contexts(GameRuntimeMode.Normal)
                .Any(context => context.Identifier == "mods"),
            "Normal mode did not omit the Mods screen.");

        var contexts = JsonSerializer.Deserialize<ResourceContext[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "labeling-contexts.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing labeling-contexts.json.");
        Check(contexts.Select(context => context.ContextIdentifier).ToHashSet(StringComparer.Ordinal)
            .SetEquals(LabelingContractCatalog.ScreenContexts.Select(context => context.Identifier)),
            "The Windows context catalog differs from the shipped context resource.");

        var catalog = GuidanceCatalog();
        foreach (var item in contexts)
        {
            var context = LabelingContractCatalog.ScreenContexts.Single(value =>
                value.Identifier == item.ContextIdentifier);
            Check(LabelingContractCatalog.ContextName(item.ContextIdentifier) == item.Name,
                $"Context name differs for {item.ContextIdentifier}.");
            Check(context.Labels.Select(label => label.Identifier)
                    .SequenceEqual(item.Objects.Select(value => value.Id), StringComparer.Ordinal)
                && context.Labels.Select(label => label.Name)
                    .SequenceEqual(item.Objects.Select(value => value.Name), StringComparer.Ordinal),
                $"Context objects differ for {item.ContextIdentifier}.");

            if (!expected.Contains(item.ContextIdentifier)) continue;
            foreach (var language in Languages)
            {
                Check(catalog[language].ContainsKey(item.Name),
                    $"Missing screen translation {language}:{item.Name}.");
                foreach (var value in item.Objects)
                    Check(catalog[language].ContainsKey(value.Name),
                        $"Missing object translation {language}:{value.Name}.");
            }
        }
    }

    public static void TestPartialReferenceResumesEditingAndSavedBackCounts()
    {
        WithTemporaryDirectory(root =>
        {
            var store = new LabelingExampleStore(root);
            var example = store.Save(Frame(), "select-profile", [Box("shared.back")]);
            var coordinator = new StencilInitializationCoordinator(store, GameRuntimeMode.Modded);

            Check(coordinator.Guidance(null, null, "select-profile") ==
                "Click Stencil and Edit Labels for Select Profile.");
            Check(!coordinator.Steps.Any(step => step.ContextIdentifier == "select-profile"
                && step.ClassIdentifier == "shared.back"));
            Check(coordinator.Guidance("select-profile", "shared.back") != "Draw a box around Back.",
                "A saved Back label was requested again while editing.");

            store.Delete(example);
            coordinator.Refresh();
            Check(coordinator.Guidance(null, null, "select-profile") ==
                "Select Start Game to navigate to Select Profile and press L.");
        });
    }

    public static void TestEmptySetupIncludesEveryRequiredScreen()
    {
        WithTemporaryDirectory(root =>
        {
            var coordinator = new StencilInitializationCoordinator(
                new LabelingExampleStore(root), GameRuntimeMode.Modded);
            Check(coordinator.TotalCount == 189);
            Check(coordinator.ScreenCount == 26);
            Check(coordinator.ButtonTitle == "Stencil (0 / 26)");
            Check(coordinator.CompletedScreenCount == 0);
            Check(coordinator.Guidance(null, null) == "Navigate to Main Title and press L.");
            Check(coordinator.Guidance("main-title", "main-title.start-game") ==
                "Draw a box around Start Game.");
            Check(coordinator.Guidance(null, null, "options") == "Navigate to Options and press L.");
        });
    }
}
