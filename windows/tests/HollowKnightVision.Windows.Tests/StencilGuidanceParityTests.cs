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

    public static void TestDeferredProfileAndModelGuidance()
    {
        WithTemporaryDirectory(root =>
        {
            var store = new LabelingExampleStore(root);
            foreach (var context in StencilInitializationPolicy.Contexts(GameRuntimeMode.Normal).Where(item => StencilInitializationPolicy.Phase(item.Identifier) == 1))
                store.Save(Frame(), context.Identifier, context.Requirements.Where(item => item.Identifier != StencilInitializationPolicy.DeferredProfileClass)
                    .SelectMany(item => Enumerable.Range(0, item.Count).Select(_ => Box(item.Identifier))).ToArray());
            var coordinator = new StencilInitializationCoordinator(store, GameRuntimeMode.Normal);
            Check(!coordinator.IsScreenComplete("select-profile"), "Deferred Clear Save incorrectly earned a completion checkmark.");
            Check(coordinator.NavigationStep()?.ContextIdentifier == "gameplay-hud", "Deferred action blocked HUD setup.");
            coordinator.HasUsableHUD = true;
            Check(coordinator.IsFirstPhaseComplete && !coordinator.IsScreenComplete("select-profile"));
            Check(coordinator.NavigationStep()?.ContextIdentifier == "options");
            foreach (var context in StencilInitializationPolicy.Contexts(GameRuntimeMode.Normal).Where(item => StencilInitializationPolicy.Phase(item.Identifier) != 1))
                store.Save(Frame(), context.Identifier, context.Requirements.Where(item => !item.ModdedOnly)
                    .SelectMany(item => Enumerable.Range(0, item.Count).Select(_ => Box(item.Identifier))).ToArray());
            coordinator.Refresh();
            Check(!coordinator.IsComplete && coordinator.Steps.Single().ClassIdentifier == StencilInitializationPolicy.DeferredProfileClass);
            Check(coordinator.Guidance(null, null)!.Contains("Return to Select Profile"));
            Check(coordinator.LabelContextForCapture("select-profile", null, "game") == "select-profile");
            Check(coordinator.Guidance("select-profile", StencilInitializationPolicy.DeferredProfileClass) == "Draw a box around Clear Save.");
            Check(coordinator.ModelGuidance("Gameplay", true, 0, false, false) is null);
            var profile = coordinator.ExampleFor("select-profile")!;
            var completed = store.Update(profile, "select-profile", [.. profile.Manifest.Annotations, Box(StencilInitializationPolicy.DeferredProfileClass)]);
            coordinator.Refresh();
            Check(coordinator.IsComplete && coordinator.IsScreenComplete("select-profile") && coordinator.CompletedScreenCount == coordinator.ScreenCount);
            Check(coordinator.ModelGuidance("Gameplay", false, 0, false, false)!.Contains("Enter gameplay"));
            Check(coordinator.ModelGuidance("Gameplay", true, 0, false, false)!.Contains("Open Model"));
            Check(coordinator.ModelGuidance("Model", true, 2, false, false)!.Contains("2 screenshots"));
            Check(coordinator.ModelGuidance("Model", true, 0, false, false)!.StartsWith("Click Train"));
            Check(coordinator.ModelGuidance("Model", true, 0, false, true)!.StartsWith("Training your model"));
            Check(coordinator.ModelGuidance("Gameplay", true, 0, true, false)!.StartsWith("Model trained"));
            Check(coordinator.ModelGuidance("Label", true, 0, false, false) is null);
            store.Update(completed, "select-profile", profile.Manifest.Annotations);
            coordinator.Refresh();
            Check(!coordinator.IsScreenComplete("select-profile") && !coordinator.IsComplete && coordinator.IsFirstPhaseComplete,
                "Undo failed to restore deferred guidance or locked the already completed first phase.");
        });
    }

    public static void TestEmptySetupIncludesEveryRequiredScreen()
    {
        WithTemporaryDirectory(root =>
        {
            var coordinator = new StencilInitializationCoordinator(
                new LabelingExampleStore(root), GameRuntimeMode.Modded);
            Check(coordinator.TotalCount == 190);
            Check(coordinator.ScreenCount == 26);
            Check(coordinator.ButtonTitle == "Stencil (0 / 26)");
            Check(coordinator.CompletedScreenCount == 0);
            Check(coordinator.Guidance(null, null) == "Navigate to Main Title and press L.");
            Check(coordinator.Guidance("main-title", "main-title.start-game") ==
                "Draw a box around Start Game.");
            Check(coordinator.Guidance(null, null, "options") == "Navigate to Options and press L.");
        });
    }
    public static void TestPhaseTwoNavigationOpensTheGuidedScreenAndNextObject()
    {
        WithTemporaryDirectory(root =>
        {
            var store = new LabelingExampleStore(root);
            foreach (var mode in new[] { GameRuntimeMode.Normal, GameRuntimeMode.Modded })
            {
                foreach (var context in StencilInitializationPolicy.Contexts(mode)
                    .Where(item => StencilInitializationPolicy.Phase(item.Identifier) == 1))
                {
                    var labels = context.Requirements.Where(item => !item.ModdedOnly || mode == GameRuntimeMode.Modded)
                        .SelectMany(item => Enumerable.Range(0, item.Count).Select(_ => Box(item.Identifier))).ToArray();
                    store.Save(Frame(), context.Identifier, labels);
                }
                var coordinator = new StencilInitializationCoordinator(store, mode) { HasUsableHUD = true };
                Check(coordinator.IsFirstPhaseComplete);
                Check(coordinator.Guidance(null, null) == "Navigate to Options and press L.");
                var target = coordinator.LabelContextForCapture(null, null, "game");
                Check(target == "options", "Phase-two Label entry retained the previous Gameplay context.");
                var next = coordinator.Steps.First(item => item.ContextIdentifier == target);
                Check(next.ClassIdentifier == "shared.options");
                Check(coordinator.Guidance(target, next.ClassIdentifier) == "Draw a box around Options.");
                Check(coordinator.LabelContextForCapture(null, "audio", "game") == "audio",
                    "Explicit stencil selection did not override the default next screen.");
                Check(coordinator.LabelContextForCapture("game", null, "game") == "options",
                    "HUD visible behind an untrained Options menu overrode the navigation instruction.");
                Check(coordinator.LabelContextForCapture("game", "audio", "game") == "audio",
                    "A background HUD overrode an explicitly selected stencil.");

                var partial = store.Save(Frame(), "options", [Box("shared.options")]);
                coordinator.Refresh();
                Check(coordinator.LabelContextForCapture(null, null, "game") == "options");
                var missing = coordinator.Steps.First(item => item.ContextIdentifier == "options");
                Check(missing.ClassIdentifier != "shared.options"
                    && coordinator.Guidance("options", missing.ClassIdentifier) == coordinator.Instruction(missing),
                    "Reopening a partial stencil repeated an already labeled heading.");
                store.Delete(partial);
            }
            foreach (var context in StencilInitializationPolicy.Contexts(GameRuntimeMode.Modded)
                .Where(item => StencilInitializationPolicy.Phase(item.Identifier) != 1))
                store.Save(Frame(), context.Identifier, context.Requirements
                    .SelectMany(item => Enumerable.Range(0, item.Count).Select(_ => Box(item.Identifier))).ToArray());
            var complete = new StencilInitializationCoordinator(store, GameRuntimeMode.Modded) { HasUsableHUD = true };
            Check(complete.IsComplete && complete.Guidance(null, null) is null);
            Check(complete.LabelContextForCapture("game", null, "options") == "game",
                "Completed setup did not return to recognized Gameplay labeling.");
        });
    }
}
