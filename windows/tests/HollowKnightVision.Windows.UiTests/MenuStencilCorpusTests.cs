using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.ExceptionServices;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class MenuStencilCorpusTests
{
    private const string AuditRootVariable = "HKV_MENU_STENCIL_AUDIT";
    private static readonly string[] FrontEndContexts =
    [
        "main-title", "options", "game-options", "audio", "video", "screen-scale",
        "brightness", "video-advanced-settings", "controller", "remap-controller",
        "controller-advanced-settings", "keyboard", "mods", "achievements", "extras",
        "hidden-dreams", "grimm-troupe", "lifeblood", "godmaster", "select-profile",
        "clear-save", "quit-game"
    ];
    private static readonly MethodInfo WpfLoadFrame = typeof(WindowsMenuStencilRecognizer)
        .GetMethod("LoadFrame", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The production WPF menu-frame decoder is unavailable.");

    internal static void Run()
    {
        Case("testCurrentReportedMenuStatesRecognizeWithoutLiveSelfReference", TestCurrentReportedMenuStatesRecognizeWithoutLiveSelfReference);
        Case("testEveryCapturedLanguageRecognizesGameOptionsAndLanguageSelection", TestEveryCapturedLanguageRecognizesGameOptionsAndLanguageSelection);
        Case("testRequestedLanguagesRecognizeEveryCapturedFrontEndScene", TestRequestedLanguagesRecognizeEveryCapturedFrontEndScene);
        Case("testRequestedLanguagesRecognizeEveryFrontEndSelection", TestRequestedLanguagesRecognizeEveryFrontEndSelection);
        Case("testAllHumanMenuExamplesWhenRequested", TestAllHumanMenuExamplesWhenRequested);
        Case("testLatestLiveCaptureForEveryMeasuredSelectionWhenAvailable", TestLatestLiveCaptureForEveryMeasuredSelectionWhenAvailable);
        Case("testLatestMultilingualSelectorCorrectionsSelectLabeledRows", TestLatestMultilingualSelectorCorrectionsSelectLabeledRows);
        Case("testKeyboardTwoColumnRowRecognizesFadedFocusCaptureWithoutSelfReference", TestKeyboardTwoColumnRowRecognizesFadedFocusCaptureWithoutSelfReference);
        Case("testReportedMenuFailuresRecognizeWithoutSelfReference", TestReportedMenuFailuresRecognizeWithoutSelfReference);
        Case("testQuitGameDialogIsNotConfusedWithQuitToMenuAndKeepsSearchBoxes", TestQuitGameDialogIsNotConfusedWithQuitToMenuAndKeepsSearchBoxes);
        Case("testLatestLiveCaptureAcquiresItsSceneFromTheFullCatalog", TestLatestLiveCaptureAcquiresItsSceneFromTheFullCatalog);
        Case("testProblematicExtrasReferenceWhenRequested", TestProblematicExtrasReferenceWhenRequested);
        Case("testExternalMenuFrameWhenRequested", TestExternalMenuFrameWhenRequested);
        Case("testItalianOptionsWinsFullCatalog", TestItalianOptionsWinsFullCatalog);
    }

    private static void TestCurrentReportedMenuStatesRecognizeWithoutLiveSelfReference()
    {
        Dictionary<Guid, (string Context, string Selection)> expected = new()
        {
            [Id("0C12A259-3910-4CA4-BF43-B1EC73738FF8")] = ("select-profile", "Back"),
            [Id("A5D1697C-189B-4F73-BFED-70ED4392C1EA")] = ("game-options", "Show Achievements"),
            [Id("BB033DA5-3C96-4ABF-A0F2-73ADC29465EF")] = ("game-options", "Backer Credits"),
            [Id("CFD659AC-19A6-48E2-9929-9F87CE29D059")] = ("audio", "Sound Volume"),
            [Id("C46E9CB8-7434-4AEB-9D47-C95BED51C91A")] = ("audio", "Master Volume"),
            [Id("F676CB9B-42A6-4266-9019-602DD3552EEE")] = ("controller-advanced-settings", "Reset Defaults"),
            [Id("47AC5304-6490-4B92-B121-F4EDF05655C8")] = ("controller", "Advanced Settings")
        };
        var (root, examples, captures) = DefaultCorpus();
        var audited = captures.Where(source => expected.ContainsKey(source.Capture.Id)).ToArray();
        RequireCompleteOrSkip(examples, audited.Length == expected.Count,
            "Current reported menu captures are unavailable");
        var catalog = Build(root, examples, [], Calibration());
        foreach (var source in audited)
        {
            var wanted = expected[source.Capture.Id];
            var result = Observe(catalog, source.ImagePath);
            Check(result.IsMatch, source.Capture.Id.ToString());
            Equal(wanted.Context, result.ContextIdentifier, source.Capture.Id.ToString());
            Equal(wanted.Selection, result.SelectedOption, source.Capture.Id.ToString());
            var scene = catalog.Scenes.Single(item => item.ContextIdentifier == wanted.Context);
            Equal(scene.Options.Count * 2, result.SelectorSearchRegions.Count, source.Capture.Id.ToString());
        }
    }

    private static void TestEveryCapturedLanguageRecognizesGameOptionsAndLanguageSelection()
    {
        var (root, examples, captures) = DefaultCorpus();
        var latest = captures.Where(source => source.Capture.ContextIdentifier == "game-options"
                && source.Capture.SelectedIdentifier == "game-options.language")
            .GroupBy(source => source.Capture.ResolvedLanguageIdentifier, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        RequireCompleteOrSkip(examples,
            latest.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(MenuStencilLiveCaptureStore.SupportedLanguages),
            "Complete live language-cycle captures are unavailable");
        var catalog = Build(root, examples, captures, Calibration(), included: Set("game-options"));
        var scene = catalog.Scenes.Single(item => item.ContextIdentifier == "game-options");
        Check(scene.Probe.SearchProbeVariants.Select(item => item.LanguageIdentifier).Where(item => item is not null)
                .Select(item => item!).ToHashSet(StringComparer.Ordinal).SetEquals(MenuStencilLiveCaptureStore.SupportedLanguages),
            "The game-options probe does not contain every captured language.");
        foreach (var language in MenuStencilLiveCaptureStore.SupportedLanguages.Order(StringComparer.Ordinal))
        {
            var result = Observe(catalog, latest[language].ImagePath);
            Check(result.IsMatch, language);
            Equal("game-options", result.ContextIdentifier, language);
            Equal("Language", result.SelectedOption, language);
        }
    }

    private static void TestRequestedLanguagesRecognizeEveryCapturedFrontEndScene()
    {
        var requestedContexts = RequestedContexts();
        var languages = RequestedLanguages("es");
        var requestedIds = RequestedCaptureIds();
        var (root, examples, captures) = DefaultCorpus();
        RequireRequestedCorpus(examples, captures, "Front-end scene capture corpus is unavailable");
        if (requestedIds is not null) RequireIdsPresent(captures, requestedIds);
        var catalog = Build(root, examples, captures, Calibration(), included: requestedContexts);
        foreach (var language in languages)
        {
            var byContext = captures.Where(source => source.Capture.ResolvedLanguageIdentifier == language
                    && (requestedIds?.Contains(source.Capture.Id) ?? true)
                    && requestedContexts.Contains(source.Capture.ContextIdentifier))
                .GroupBy(source => source.Capture.ContextIdentifier, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            Check(byContext.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(requestedContexts),
                $"Incomplete front-end capture route for {language}.");
            foreach (var context in requestedContexts.Order(StringComparer.Ordinal))
            {
                var available = byContext.GetValueOrDefault(context)
                    ?? throw new Exception($"Missing {language} {context}.");
                var sources = requestedIds is null ? available.Take(3).ToArray() : available;
                Check(sources.Length >= (requestedIds is null ? 3 : 1), $"Insufficient {language} {context} animation phases.");
                foreach (var source in sources)
                {
                    var result = Observe(catalog, source.ImagePath);
                    var label = $"{language} {context} {source.Capture.Id}";
                    Check(result.IsMatch, label);
                    Equal(context, result.ContextIdentifier, label);
                    Equal(source.Capture.SelectedName, result.SelectedOption, label);
                }
            }
        }
    }

    private static void TestRequestedLanguagesRecognizeEveryFrontEndSelection()
    {
        var contexts = RequestedContexts();
        var languages = RequestedLanguages("en");
        var requestedIds = RequestedCaptureIds();
        var (root, examples, captures) = DefaultCorpus();
        RequireRequestedCorpus(examples, captures, "Front-end selection capture corpus is unavailable");
        if (requestedIds is not null) RequireIdsPresent(captures, requestedIds);
        var full = Build(root, examples, captures, Calibration(), included: contexts);
        var expectedKeys = full.Scenes.Where(scene => contexts.Contains(scene.ContextIdentifier))
            .SelectMany(scene => scene.Options.Select(option => Key(scene.ContextIdentifier, option.ClassIdentifier)))
            .ToHashSet(StringComparer.Ordinal);
        var holdoutLatest = new Dictionary<string, Dictionary<string, SavedMenuStencilCapture>>(StringComparer.Ordinal);
        var selectedLatest = new Dictionary<string, Dictionary<string, SavedMenuStencilCapture>>(StringComparer.Ordinal);
        foreach (var language in languages)
        {
            var latest = LatestByKey(captures.Where(item => item.Capture.ResolvedLanguageIdentifier == language), expectedKeys);
            holdoutLatest[language] = latest;
            if (requestedIds is null)
            {
                Check(latest.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys),
                    $"Incomplete front-end selection sweep for {language}.");
                selectedLatest[language] = latest;
            }
            else
            {
                selectedLatest[language] = latest.Where(pair => requestedIds.Contains(pair.Value.Capture.Id))
                    .ToDictionary(StringComparer.Ordinal);
                Check(selectedLatest[language].Count > 0, $"Requested capture IDs contain no {language} front-end selection.");
            }
        }
        var holdouts = holdoutLatest.Values.SelectMany(map => map.Values).Select(item => item.Capture.Id).ToHashSet();
        var catalog = Build(root, examples, captures, Calibration(), holdouts, contexts);
        foreach (var language in languages)
        {
            var latest = selectedLatest[language];
            var keys = requestedIds is null ? expectedKeys : latest.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var key in keys.Order(StringComparer.Ordinal))
            {
                var source = latest.GetValueOrDefault(key) ?? throw new Exception($"Missing {language} {key}.");
                var result = Observe(catalog.ContainingOnly(source.Capture.ContextIdentifier), source.ImagePath);
                var evidence = string.Join(',', result.SelectorCandidates.Select(item => $"{item.Name}={item.Confidence:F3}"));
                var detail = $"{language} {key} {source.Capture.Id} actual={result.SelectedOption ?? "nil"} language={result.LanguageIdentifier ?? "nil"} selectors=[{evidence}]";
                Check(result.IsMatch, detail);
                Equal(source.Capture.ContextIdentifier, result.ContextIdentifier, detail);
                Equal(source.Capture.SelectedName, result.SelectedOption, detail);
            }
        }
    }

    private static void TestAllHumanMenuExamplesWhenRequested()
    {
        var root = AuditRoot();
        var examples = LoadExamples(root);
        Check(examples.Count > 0, $"{AuditRootVariable} contains no labeling examples.");
        var catalog = Build(root, examples, [], calibration: null);
        Check(catalog.Scenes.Count >= 20, $"Expected at least 20 menu scenes, found {catalog.Scenes.Count}.");
        var contexts = catalog.Scenes.Select(scene => scene.ContextIdentifier).ToHashSet(StringComparer.Ordinal);
        var requested = RequestedExampleIds();
        if (requested is not null)
        {
            var present = examples.Select(item => item.Id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(requested.IsSubsetOf(present), "HKV_MENU_STENCIL_ONLY requested an example that is absent.");
        }
        var truePositive = 0; var falseNegative = 0; var falseContext = 0;
        var trueNegative = 0; var falsePositive = 0; var selectionMismatch = 0;
        var comparisons = new List<int>(); var visited = 0;
        HashSet<string> visuallyQuitGame = new(StringComparer.OrdinalIgnoreCase)
        { "d958e9f5-da0d-40f5-a8e7-07f4931f5abf", "dc544c8b-448c-4ce7-8d88-f9ba0b68b032" };
        foreach (var example in examples)
        {
            if (requested is not null && !requested.Contains(example.Id.ToString("D"))) continue;
            visited++;
            var expected = visuallyQuitGame.Contains(example.Id.ToString("D")) ? "quit-game" : example.Manifest.ContextIdentifier;
            var image = LoadFrame(example.ImagePath);
            var result = new MenuStencilTracker(catalog).Observe(image, 1);
            if (contexts.Contains(expected))
            {
                if (result?.IsMatch == true && result.ContextIdentifier == expected)
                {
                    truePositive++;
                    if (ExpectedSelection(example, expected) is { } selection && result.SelectedOption != selection)
                    {
                        selectionMismatch++;
                        Console.WriteLine($"MENU_STENCIL selection={example.Id} context={expected} expected={selection} actual={result.SelectedOption ?? "nil"} candidates=[{Scores(result.SelectorCandidates)}]");
                    }
                }
                else if (result?.IsMatch == true)
                {
                    falseContext++;
                    Console.WriteLine($"MENU_STENCIL wrong={example.Id} expected={expected} actual={result.ContextIdentifier} confidence={result.Confidence} anchors=[{Scores(result.Anchors)}]");
                    PrintExpectedCandidate(catalog, expected, image);
                }
                else
                {
                    falseNegative++;
                    Console.WriteLine($"MENU_STENCIL missed={example.Id} expected={expected} best={result?.ContextIdentifier ?? "nil"} confidence={result?.Confidence ?? 0}");
                    PrintExpectedCandidate(catalog, expected, image);
                }
            }
            else if (result?.IsMatch == true)
            {
                falsePositive++;
                Console.WriteLine($"MENU_STENCIL falsePositive={example.Id} expected={expected} actual={result.ContextIdentifier} confidence={result.Confidence}");
            }
            else trueNegative++;
            if (result is not null) comparisons.Add(result.ComparisonCount);
        }
        Check(visited > 0, "The requested human menu example filter selected no examples.");
        Console.WriteLine($"MENU_STENCIL scenes={catalog.Scenes.Count} tp={truePositive} fn={falseNegative} wrong={falseContext} tn={trueNegative} fp={falsePositive} selectionWrong={selectionMismatch} maxComparisons={comparisons.DefaultIfEmpty().Max()}");
        if (requested is null)
        {
            Equal(0, falseNegative, "Human corpus false negatives");
            Equal(0, falseContext, "Human corpus wrong contexts");
            Equal(0, falsePositive, "Human corpus false positives");
            Equal(0, selectionMismatch, "Human corpus selection mismatches");
        }
    }

    private static void TestLatestLiveCaptureForEveryMeasuredSelectionWhenAvailable()
    {
        var (root, examples, captures) = DefaultCorpus();
        if ((examples.Count == 0 || captures.Count == 0)
            && Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_GEOMETRY_LOG") is not null)
            throw new Exception("No live menu calibration captures. HKV_MENU_STENCIL_GEOMETRY_LOG was explicitly requested.");
        RequireCompleteOrSkip(examples, captures.Count > 0, "No live menu calibration captures");
        var catalog = Build(root, examples, captures, Calibration());
        var validKeys = CatalogKeys(catalog);
        var latest = LatestByKey(captures, validKeys);
        var failures = new List<string>();
        foreach (var source in latest.Values)
        {
            if (!catalog.Scenes.Any(scene => scene.ContextIdentifier == source.Capture.ContextIdentifier)) continue;
            var image = LoadFrame(source.ImagePath);
            var result = new MenuStencilTracker(catalog.ContainingOnly(source.Capture.ContextIdentifier)).Observe(image, 1);
            if (result?.IsMatch != true || result.SelectedOption != source.Capture.SelectedName)
            {
                failures.Add($"{source.Capture.ContextIdentifier}/{source.Capture.SelectedName} actual={result?.SelectedOption ?? "nil"} [{(result is null ? "" : Scores(result.SelectorCandidates))}]");
                continue;
            }
            var scene = catalog.Scenes.Single(item => item.ContextIdentifier == source.Capture.ContextIdentifier);
            Equal(scene.Options.Count * 2, result.SelectorSearchRegions.Count, source.Capture.ContextIdentifier);
            Equal(result.SelectorSearchRegions.Count, result.SelectorSearchRegions
                .Select(region => $"{region.Side}:{region.Rect.MidX}:{region.Rect.MidY}").Distinct(StringComparer.Ordinal).Count(), source.Capture.ContextIdentifier);
            var solved = result.SelectorSearchRegions.Where(region => region.IsSolved).ToArray();
            Equal(2, solved.Length, source.Capture.SelectedName);
            foreach (var region in solved)
                Check(result.SelectorCandidates.Any(candidate => Contains(region.Rect, candidate.Rect)), source.Capture.SelectedName);
            var scaleX = image.Width / (double)scene.ReferenceWidth;
            var scaleY = image.Height / (double)scene.ReferenceHeight;
            var offsets = result.SelectorSearchRegions.Where(region => !region.IsSolved).Select(region =>
            {
                var option = scene.Options.Single(item => item.ClassIdentifier == region.OptionIdentifier);
                var configured = (region.Side == MenuSelectorSide.Left ? option.LeftSelector : option.RightSelector)?.BoundsFor(source.Capture.ResolvedLanguageIdentifier)
                    ?? throw new Exception($"Missing configured {region.Side} selector for {region.OptionIdentifier}.");
                return (X: region.Rect.MidX / scaleX - configured.MidX, Y: region.Rect.MidY / scaleY - configured.MidY);
            }).ToArray();
            foreach (var offset in offsets.Skip(1))
            {
                Near(offsets[0].X, offset.X, .001, source.Capture.ContextIdentifier);
                Near(offsets[0].Y, offset.Y, .001, source.Capture.ContextIdentifier);
            }
            if (Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_GEOMETRY_LOG") == "1")
                Console.WriteLine($"MENU_SELECTOR_GEOMETRY {source.Capture.ContextIdentifier}/{source.Capture.SelectedIdentifier} offsets=[{string.Join(',', offsets)}]");
        }
        if (failures.Count > 0) Console.WriteLine("LIVE_MENU_STENCIL failures=" + string.Join(" | ", failures));
        Equal(validKeys.Count, latest.Count, "Every measured menu selection must have a latest capture.");
        Check(failures.Count == 0, "Latest measured menu selections did not all recognize.");
    }

    private static void TestLatestMultilingualSelectorCorrectionsSelectLabeledRows()
    {
        (string Id, string Context, string Selection)[] expected =
        [
            ("911E9057-3FE0-452C-BE9C-2D68AD922154", "keyboard", "Attack"),
            ("AE1F5C8D-6D41-427C-B6BE-DDCC911964BD", "keyboard", "Super Dash"),
            ("D57CC45F-DE6A-4BA6-A25F-FDB67B3ADEEA", "keyboard", "Inventory"),
            ("FA0753AB-7E1F-4024-A1EF-F5E651F51B93", "options", "Game"),
            ("152AE0A8-198B-4596-A705-DED146B7883E", "extras", "Hidden Dreams"),
            ("E5616482-3FF6-4842-B313-DFB50C7783BC", "extras", "The Grimm Troupe"),
            ("6D028D16-970D-4755-845B-34DCBF91A247", "extras", "Lifeblood"),
            ("34021979-272E-4680-84E7-2D5BDD35555C", "video", "Advanced Settings"),
            ("5393B426-F329-4E86-80D2-5E104C36A746", "extras", "Hidden Dreams"),
            ("7B00F837-FC48-46FC-9AF2-9C19C471ED3E", "extras", "The Grimm Troupe"),
            ("07A600CE-FC9A-42FD-AA72-6E54D0412971", "extras", "Lifeblood"),
            ("D3BF03A8-98EC-4CEA-9696-B647C674221E", "quit-game", "No"),
            ("00B9FE92-B22D-400A-947C-03848590DBBF", "video", "Advanced Settings"),
            ("AA6813FA-8B96-4B2C-B499-0A3F46F745C6", "keyboard", "Attack"),
            ("3224CB73-47A1-4F4B-B4AD-21EE272B4B81", "keyboard", "Attack"),
            ("16A25BAA-201A-40AD-ABC2-28F97683D505", "extras", "Lifeblood"),
            ("144A8CFB-9C77-4D92-A621-D7780B96335F", "extras", "The Grimm Troupe"),
            ("66CCC048-781A-4D48-8D80-6D58A1CA9530", "video", "Advanced Settings"),
            ("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F", "keyboard", "Right"),
            ("BB7C33FD-0000-41D3-A25F-525B663772D2", "keyboard", "Super Dash")
        ];
        var (root, examples, captures) = DefaultCorpus();
        var byId = examples.ToDictionary(item => item.Id);
        var available = expected.Where(item => byId.ContainsKey(Id(item.Id))).ToArray();
        if (available.Length == 0) Skip("Latest multilingual corrections are unavailable");
        var catalog = Build(root, examples, captures, DefaultCalibration());
        foreach (var item in available)
        {
            var example = byId[Id(item.Id)];
            var result = Observe(catalog.ContainingOnly(item.Context), example.ImagePath);
            Check(result.IsMatch, item.Id);
            Equal(item.Context, result.ContextIdentifier, item.Id);
            Equal(item.Selection, result.SelectedOption, item.Id);
        }
    }

    private static void TestKeyboardTwoColumnRowRecognizesFadedFocusCaptureWithoutSelfReference()
    {
        var captureId = Id("B3126000-F082-4D1A-A5F2-DBF97CA8BF30");
        var (root, examples, captures) = DefaultCorpus();
        var source = captures.FirstOrDefault(item => item.Capture.Id == captureId);
        if (source is null) Skip("Faded Keyboard regression capture is unavailable");
        RequireExamples(examples);
        var catalog = Build(root, examples, captures.Where(item => item.Capture.Id != captureId).ToArray(), Calibration()).ContainingOnly("keyboard");
        var result = Observe(catalog, source.ImagePath);
        Check(result.IsMatch, captureId.ToString());
        Equal("Focus / Cast", result.SelectedOption, captureId.ToString());
    }

    private static void TestReportedMenuFailuresRecognizeWithoutSelfReference()
    {
        Dictionary<Guid, (string Context, string Selection)> expected = new()
        {
            [Id("A5D1697C-189B-4F73-BFED-70ED4392C1EA")] = ("game-options", "Show Achievements"),
            [Id("C2BF6275-0A46-4F57-9BF8-0F889C2DD88F")] = ("video", "Advanced Settings"),
            [Id("3E46B7D6-5670-4824-B4A2-1515973F9F27")] = ("video-advanced-settings", "Particle Effects"),
            [Id("D87C6496-EA21-4E98-9222-E4EF4F309444")] = ("achievements", "Back"),
            [Id("B3126000-F082-4D1A-A5F2-DBF97CA8BF30")] = ("keyboard", "Focus / Cast"),
            [Id("7F7B6D9F-0112-4BAC-875E-83C2FBB0F647")] = ("select-profile", "Clear Save 1"),
            [Id("CF3E3014-A724-4BCB-A8EF-B7CBFE4A7D10")] = ("controller", "Advanced Settings")
        };
        var (root, examples, captures) = DefaultCorpus();
        var audited = captures.Where(source => expected.ContainsKey(source.Capture.Id)).ToArray();
        RequireCompleteOrSkip(examples, audited.Length == expected.Count, "Reported menu regression captures are unavailable");
        var catalog = Build(root, examples, captures.Where(source => !expected.ContainsKey(source.Capture.Id)).ToArray(), Calibration());
        foreach (var source in audited)
        {
            var wanted = expected[source.Capture.Id];
            var result = Observe(catalog, source.ImagePath);
            Check(result.IsMatch, source.Capture.Id.ToString());
            Equal(wanted.Context, result.ContextIdentifier, source.Capture.Id.ToString());
            Equal(wanted.Selection, result.SelectedOption, source.Capture.Id.ToString());
            Equal(catalog.Scenes.Single(scene => scene.ContextIdentifier == wanted.Context).Options.Count * 2,
                result.SelectorSearchRegions.Count, source.Capture.Id.ToString());
        }
    }

    private static void TestQuitGameDialogIsNotConfusedWithQuitToMenuAndKeepsSearchBoxes()
    {
        Dictionary<Guid, string> expected = new()
        {
            [Id("17D7F253-8BB2-43D3-8054-258E902FF9D8")] = "quit-game",
            [Id("AB625546-6B69-41BC-AF99-83E70238A7F1")] = "quit-to-menu"
        };
        var (root, examples, captures) = DefaultCorpus();
        var audited = captures.Where(source => expected.ContainsKey(source.Capture.Id)).ToArray();
        RequireCompleteOrSkip(examples, audited.Length == expected.Count, "Quit dialog regression captures are unavailable");
        var catalog = Build(root, examples, captures.Where(source => !expected.ContainsKey(source.Capture.Id)).ToArray(),
            Calibration(), included: Set("quit-game", "quit-to-menu"));
        foreach (var source in audited)
        {
            var image = LoadFrame(source.ImagePath); var tracker = new MenuStencilTracker(catalog);
            var result = Required(tracker.Observe(image, 1), source.Capture.Id.ToString());
            Console.WriteLine($"QUIT_DIALOG_REGRESSION context={result.ContextIdentifier} match={result.IsMatch} selected={result.SelectedOption ?? "nil"} confidence={result.Confidence} regions={result.SelectorSearchRegions.Count}");
            AssertDialog(result, expected[source.Capture.Id], 1);
            for (var frame = 2; frame <= 12; frame++)
                AssertDialog(Required(tracker.Observe(image, 1 + (frame - 1) / 30d), $"frame {frame}"), expected[source.Capture.Id], frame);
        }
    }

    private static void TestLatestLiveCaptureAcquiresItsSceneFromTheFullCatalog()
    {
        var (root, examples, captures) = DefaultCorpus();
        RequireCompleteOrSkip(examples, captures.Count > 0, "No live menu calibration captures");
        var catalog = Build(root, examples, captures, Calibration());
        var validKeys = CatalogKeys(catalog); var latest = LatestByKey(captures, validKeys);
        var failures = new List<string>();
        foreach (var source in latest.Values)
        {
            var result = new MenuStencilTracker(catalog).Observe(LoadFrame(source.ImagePath), 1);
            if (result?.IsMatch != true || result.ContextIdentifier != source.Capture.ContextIdentifier || result.SelectedOption != source.Capture.SelectedName)
                failures.Add($"{source.Capture.ContextIdentifier}/{source.Capture.SelectedName} actual={result?.ContextIdentifier ?? "nil"}/{result?.SelectedOption ?? "nil"}");
        }
        if (failures.Count > 0) Console.WriteLine("LIVE_MENU_ACQUISITION failures=" + string.Join(" | ", failures));
        Equal(validKeys.Count, latest.Count, "Every catalog selection must have a latest capture.");
        Check(failures.Count == 0, "Latest live captures did not all acquire from the full catalog.");
    }

    private static void TestProblematicExtrasReferenceWhenRequested()
    {
        var root = AuditRoot(); var examples = LoadExamples(root);
        Check(examples.Count > 0, $"{AuditRootVariable} contains no labeling examples.");
        var example = examples.FirstOrDefault(item => item.Id == Id("11766cc0-fbca-4349-9415-47740f1efbcd"))
            ?? throw new Exception("The requested problematic Extras example is absent.");
        var catalog = Build(root, examples, DefaultCorpus().Captures, Calibration()).ContainingOnly("extras");
        var result = Observe(catalog, example.ImagePath);
        Console.WriteLine($"EXTRAS_DIAGNOSTIC match={result.IsMatch} confidence={result.Confidence} anchors=[{Scores(result.Anchors)}]");
    }

    private static void TestExternalMenuFrameWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_FRAME");
        if (path is null)
        {
            if (Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_EXPECTED_CONTEXT") is not null
                || Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_EXCLUDE_AUDITED_CAPTURE") is not null
                || Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_HUMAN_ONLY") is not null)
                throw new Exception("HKV_MENU_STENCIL_FRAME is required by the explicit external-frame options.");
            Skip("Set HKV_MENU_STENCIL_FRAME to audit an external frame");
        }
        Check(!string.IsNullOrWhiteSpace(path), "HKV_MENU_STENCIL_FRAME is empty.");
        var fullPath = Path.GetFullPath(path!);
        Check(File.Exists(fullPath), $"HKV_MENU_STENCIL_FRAME does not exist: {fullPath}");
        var image = LoadFrame(fullPath);
        var (root, examples, captures) = DefaultCorpus();
        Check(examples.Count > 0, "Default menu labeling examples are unavailable for the explicitly requested external frame.");
        var sources = Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_EXCLUDE_AUDITED_CAPTURE") == "1"
            ? captures.Where(item => !string.Equals(Path.GetFullPath(item.ImagePath), fullPath, StringComparison.OrdinalIgnoreCase)).ToArray()
            : captures;
        var calibration = Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_HUMAN_ONLY") == "1" ? null : Calibration();
        var fullCatalog = Build(root, examples, sources, calibration);
        var expected = Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_EXPECTED_CONTEXT");
        if (!string.IsNullOrWhiteSpace(expected))
            Check(LabelingContractCatalog.ScreenContexts.Any(item => item.Identifier == expected), $"Unknown HKV_MENU_STENCIL_EXPECTED_CONTEXT: {expected}");
        var catalog = string.IsNullOrWhiteSpace(expected) ? fullCatalog : fullCatalog.ContainingOnly(expected!);
        var tracker = new MenuStencilTracker(catalog);
        if (!string.IsNullOrWhiteSpace(expected) && captures.FirstOrDefault(item => item.Capture.ContextIdentifier == expected) is { } prime)
            _ = tracker.Observe(LoadFrame(prime.ImagePath), 1);
        var result = Required(tracker.Observe(image, 2), "External frame produced no menu diagnostic result.");
        Console.WriteLine($"EXTERNAL_MENU_STENCIL context={result.ContextIdentifier} match={result.IsMatch} confidence={result.Confidence} selected={result.SelectedOption ?? "nil"} anchors=[{Scores(result.Anchors)}] selectors=[{Scores(result.SelectorCandidates)}]");
        if (!string.IsNullOrWhiteSpace(expected))
        {
            Check(result.IsMatch, "Expected external frame did not match.");
            Equal(expected!, result.ContextIdentifier, "External frame context");
        }
    }

    private static void TestItalianOptionsWinsFullCatalog()
    {
        var captureId = Id("DA25905D-4E14-41D7-9B38-4471B78492EB");
        var (root, examples, captures) = DefaultCorpus();
        var source = captures.FirstOrDefault(item => item.Capture.Id == captureId);
        if (source is null) Skip("Italian Options regression capture unavailable");
        RequireExamples(examples);
        var result = Observe(Build(root, examples, captures, Calibration()), source.ImagePath);
        Check(result.IsMatch, captureId.ToString());
        Equal("options", result.ContextIdentifier, captureId.ToString());
        Equal("Game", result.SelectedOption, captureId.ToString());
        Equal("it", result.LanguageIdentifier, captureId.ToString());
    }

    private static void AssertDialog(MenuStencilResult result, string context, int frame)
    {
        Check(result.IsMatch, $"frame {frame}"); Equal(context, result.ContextIdentifier, $"frame {frame}");
        Equal("No", result.SelectedOption, $"frame {frame}"); Equal(4, result.SelectorSearchRegions.Count, $"frame {frame}");
        var yes = result.Anchors.FirstOrDefault(item => item.ClassIdentifier == "shared.yes")
            ?? throw new Exception($"Missing Yes anchor at frame {frame}.");
        var searches = result.SelectorSearchRegions.Where(item => item.OptionIdentifier == "shared.yes").ToArray();
        Equal(2, searches.Length, $"Yes searches at frame {frame}");
        foreach (var search in searches) Near(yes.Rect.MidY, search.Rect.MidY, 3, $"Yes alignment at frame {frame}");
        if (context != "quit-game") return;
        var title = result.Anchors.FirstOrDefault(item => item.ClassIdentifier == "quit-game.quit-game")
            ?? throw new Exception($"Missing complete Quit Game title at frame {frame}.");
        var reference = title.Reference ?? throw new Exception($"Quit Game title has no reference at frame {frame}.");
        var verified = reference.VerifiedBounds(title.Rect);
        Check(title.Rect.Right >= 361 && verified.Right >= 360, $"Quit Game title is clipped at frame {frame}.");
    }

    private static void PrintExpectedCandidate(MenuStencilCatalog catalog, string expected, BgraFrame image)
    {
        if (!catalog.Scenes.Any(scene => scene.ContextIdentifier == expected)) return;
        var result = new MenuStencilTracker(catalog.ContainingOnly(expected)).Observe(image, 1);
        if (result is not null) Console.WriteLine($"MENU_STENCIL expectedCandidate confidence={result.Confidence} matched={result.IsMatch} anchors=[{Scores(result.Anchors)}]");
    }

    private static string? ExpectedSelection(SavedLabelingExample example, string contextIdentifier)
    {
        var context = LabelingContractCatalog.ScreenContexts.FirstOrDefault(item => item.Identifier == contextIdentifier);
        if (context is null) return null;
        var selectors = example.Manifest.Annotations.Where(item => !item.IsHardNegative
            && LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) == "shared.select-decoration").ToArray();
        if (selectors.Length < 2) return null;
        var selectorY = selectors.Average(item => item.Y + item.Height / 2);
        var selectorX = selectors.Select(item => item.X + item.Width / 2).Order().ToArray();
        var nearest = example.Manifest.Annotations.Where(item => !item.IsHardNegative
                && LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) != "shared.select-decoration"
                && item.Width * item.Height <= 5000d / (example.Manifest.ImageWidth * example.Manifest.ImageHeight)
                && item.X > selectorX[0] && item.X + item.Width < selectorX[^1])
            .MinBy(item => Math.Abs(item.Y + item.Height / 2 - selectorY));
        if (nearest is null) return null;
        var canonical = LabelingClassIdentity.CanonicalIdentifier(nearest.ClassIdentifier);
        return context.Labels.FirstOrDefault(item => LabelingClassIdentity.CanonicalIdentifier(item.Identifier) == canonical)?.Name;
    }

    private static MenuStencilCatalog Build(string root, IReadOnlyList<SavedLabelingExample> examples,
        IReadOnlyList<SavedMenuStencilCapture> captures, MenuStencilCalibration? calibration,
        IReadOnlySet<Guid>? holdouts = null, IReadOnlySet<string>? included = null) =>
        new MenuStencilCatalogBuilder(LabelingContractCatalog.ScreenContexts, TryLoadFrame,
            item => LabelingContractEvaluator.AcceptsMenuExample(item)).Build(examples, calibration,
                captures.Select(item => item.Source).ToArray(), holdouts, included);

    private static (string Root, IReadOnlyList<SavedLabelingExample> Examples, IReadOnlyList<SavedMenuStencilCapture> Captures) DefaultCorpus()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HollowKnightVision", "labeling-v1", "examples");
        return (root, LoadExamples(root), new MenuStencilLiveCaptureStore(MenuStencilLiveCaptureStore.RootForExamples(root)).Load());
    }

    private static IReadOnlyList<SavedLabelingExample> LoadExamples(string root) => new LabelingExampleStore(root).Load();
    private static BgraFrame? TryLoadFrame(string path)
    {
        try { return LoadFrame(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or FormatException) { return null; }
    }
    private static BgraFrame LoadFrame(string path)
    {
        try
        {
            return (BgraFrame)(WpfLoadFrame.Invoke(null, [Path.GetFullPath(path)])
                ?? throw new InvalidDataException($"WPF could not decode {path}."));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    private static MenuStencilResult Observe(MenuStencilCatalog catalog, string path) =>
        Required(new MenuStencilTracker(catalog).Observe(LoadFrame(path), 1), path);
    private static MenuStencilResult Required(MenuStencilResult? result, string label) => result ?? throw new Exception($"No menu stencil result: {label}");
    private static MenuStencilCalibration Calibration() => MenuStencilCalibration.LoadBundled()
        ?? throw new Exception("Bundled menu stencil calibration is unavailable.");
    private static MenuStencilCalibration DefaultCalibration() => MenuStencilCalibration.LoadDefault()
        ?? throw new Exception("Default menu stencil calibration is unavailable.");
    private static Guid Id(string value) => Guid.Parse(value);
    private static string Key(string context, string selection) => context + ":" + selection;
    private static HashSet<string> Set(params string[] values) => values.ToHashSet(StringComparer.Ordinal);
    private static HashSet<string> CatalogKeys(MenuStencilCatalog catalog) => catalog.Scenes
        .SelectMany(scene => scene.Options.Select(option => Key(scene.ContextIdentifier, option.ClassIdentifier))).ToHashSet(StringComparer.Ordinal);
    private static Dictionary<string, SavedMenuStencilCapture> LatestByKey(IEnumerable<SavedMenuStencilCapture> captures, IReadOnlySet<string> valid) =>
        captures.Select(item => (Key: Key(item.Capture.ContextIdentifier, item.Capture.SelectedIdentifier), Item: item))
            .Where(item => valid.Contains(item.Key)).GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Item, StringComparer.Ordinal);

    private static HashSet<string> RequestedContexts()
    {
        var raw = Environment.GetEnvironmentVariable("HKV_MENU_FRONT_END_CONTEXTS");
        if (raw is null) return FrontEndContexts.ToHashSet(StringComparer.Ordinal);
        var requested = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        Check(requested.Count > 0 && requested.IsSubsetOf(FrontEndContexts.ToHashSet(StringComparer.Ordinal)),
            "HKV_MENU_FRONT_END_CONTEXTS contains an unknown or empty context filter.");
        return requested;
    }

    private static string[] RequestedLanguages(string fallback)
    {
        var raw = Environment.GetEnvironmentVariable("HKV_MENU_FRONT_END_LANGUAGES");
        if (raw is null) return [fallback];
        var languages = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Check(languages.Length > 0 && languages.All(MenuStencilLiveCaptureStore.SupportedLanguages.Contains),
            "HKV_MENU_FRONT_END_LANGUAGES contains an unknown or empty language filter.");
        return languages;
    }

    private static HashSet<Guid>? RequestedCaptureIds()
    {
        var raw = Environment.GetEnvironmentVariable("HKV_MENU_CAPTURE_IDS");
        if (raw is null) return null;
        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Check(values.Length > 0 && values.All(value => Guid.TryParse(value, out _)), "HKV_MENU_CAPTURE_IDS contains an invalid or empty UUID filter.");
        return values.Select(Guid.Parse).ToHashSet();
    }

    private static HashSet<string>? RequestedExampleIds()
    {
        var raw = Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_ONLY");
        if (raw is null) return null;
        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Check(values.Length > 0 && values.All(value => Guid.TryParse(value, out _)), "HKV_MENU_STENCIL_ONLY contains an invalid or empty UUID filter.");
        return values.Select(value => Guid.Parse(value).ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string AuditRoot()
    {
        var raw = Environment.GetEnvironmentVariable(AuditRootVariable);
        if (raw is null)
        {
            if (Environment.GetEnvironmentVariable("HKV_MENU_STENCIL_ONLY") is not null)
                throw new Exception($"{AuditRootVariable} is required by HKV_MENU_STENCIL_ONLY.");
            Skip($"Set {AuditRootVariable} to the examples directory");
        }
        Check(!string.IsNullOrWhiteSpace(raw), $"{AuditRootVariable} is empty.");
        var root = Path.GetFullPath(raw!);
        Check(Directory.Exists(root), $"{AuditRootVariable} does not exist: {root}");
        return root;
    }

    private static void RequireIdsPresent(IReadOnlyList<SavedMenuStencilCapture> captures, IReadOnlySet<Guid> requested)
    {
        var present = captures.Select(item => item.Capture.Id).ToHashSet();
        Check(requested.IsSubsetOf(present), "HKV_MENU_CAPTURE_IDS requested a live capture that is absent.");
    }

    private static void RequireRequestedCorpus(IReadOnlyList<SavedLabelingExample> examples,
        IReadOnlyList<SavedMenuStencilCapture> captures, string message)
    {
        if (examples.Count > 0 && captures.Count > 0) return;
        if (Environment.GetEnvironmentVariable("HKV_MENU_FRONT_END_CONTEXTS") is not null
            || Environment.GetEnvironmentVariable("HKV_MENU_FRONT_END_LANGUAGES") is not null
            || Environment.GetEnvironmentVariable("HKV_MENU_CAPTURE_IDS") is not null)
            throw new Exception(message + " An explicit corpus filter was requested.");
        Skip(message);
    }

    private static void RequireCompleteOrSkip(IReadOnlyList<SavedLabelingExample> examples, bool complete, string message)
    { if (examples.Count == 0 || !complete) Skip(message); }
    private static void RequireExamples(IReadOnlyList<SavedLabelingExample> examples)
    { if (examples.Count == 0) Skip("Default menu labeling examples are unavailable"); }
    private static bool Contains(StencilRect outer, StencilRect inner) => inner.X >= outer.X && inner.Y >= outer.Y
        && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    private static string Scores(IEnumerable<MenuStencilMatch> values) => string.Join(',', values.Select(item => $"{item.Name}={item.Confidence:F3}"));
    private static void Near(double expected, double actual, double accuracy, string label) =>
        Check(Math.Abs(expected - actual) <= accuracy, $"{label}: expected {expected} ± {accuracy}, got {actual}.");
    private static void Equal<T>(T expected, T actual, string label) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{label}: expected {expected}, got {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [DoesNotReturn]
    private static void Skip(string message) => throw new SkipCase(message);
    private static void Case(string name, Action body)
    {
        try { body(); Console.WriteLine("PASS MenuStencilTests." + name); }
        catch (SkipCase skip) { Console.WriteLine("SKIP MenuStencilTests." + name + ": " + skip.Message); }
    }
    private sealed class SkipCase(string message) : Exception(message);
}
