using HollowKnightVision.Windows.Core;
using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

internal static class MenuCatalogTests
{
    private const string Decoration = "shared.select-decoration";
    private static readonly (string Id, StencilRect Bounds)[] MainLabels =
    [ ("main-title.start-game", new(50, 40, 30, 8)), ("shared.options", new(55, 65, 20, 8)), ("quit-game.quit-game", new(53, 90, 24, 8)) ];
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    internal static void TestHumanCatalog()
    {
        var fixture = new Fixture();
        var first = fixture.Example("main-title", WithSelection(MainLabels, 0));
        var second = fixture.Example("main-title", WithSelection(MainLabels, 1));
        var builder = fixture.Builder(Context("main-title", MainLabels));
        var catalog = builder.Build([second, first], null);
        var scene = catalog.Scenes.Single();
        Equal("main-title.start-game", scene.Probe.ClassIdentifier);
        Equal(3, scene.Anchors.Count); Equal(3, scene.Options.Count);
        Equal(26, catalog.BandStart); Equal(112, catalog.BandEnd);
        Equal(8.0, scene.Geometry!.LeftWidth); Equal(7.0, scene.Geometry.LeftGap);
        var inferred = scene.Options.Single(option => option.ClassIdentifier == "quit-game.quit-game");
        Equal(new StencilRect(38, 90, 8, 8), inferred.LeftSelector!.Bounds);
        Equal(new StencilRect(84, 90, 8, 8), inferred.RightSelector!.Bounds);
        var pixels = new ImageStencilPixels(fixture.Images[first.ImagePath], 160, 120);
        foreach (var anchor in scene.Anchors)
        {
            Check(anchor.Variants.All(variant => variant.Reference.Mask is not null && variant.Kernel.Samples.Count >= 8), "Catalog did not retain sparse text masks.");
            Check(pixels.Compare(anchor.Variants[0].Kernel, anchor.Bounds)!.Confidence(.82, 105) > .99, "Reference no longer matches its source text.");
        }
        Equal("main-title.start-game", MenuStencilCatalogBuilder.SelectedIdentifier(first, 160, 120));
        Equal("shared.options", MenuStencilCatalogBuilder.SelectedIdentifier(second, 160, 120));
        var tracked = new MenuStencilTracker(catalog).Observe(fixture.Images[first.ImagePath], 1);
        Check(tracked?.IsMatch == true && tracked.SelectedOption == "main-title.start-game", "Built human catalog failed end-to-end pixel recognition.");
        Equal(0, catalog.ContainingOnly("absent").Scenes.Count);
        Equal(1, catalog.ContainingOnly("main-title").Scenes.Count);
    }

    internal static void TestFocusedCorrections()
    {
        var fixture = new Fixture(); var complete = fixture.Example("main-title", WithSelection(MainLabels, 1));
        var corrected = new StencilRect(52, 40, 31, 8);
        var draft = fixture.Example("main-title", [(MainLabels[0].Id, corrected)], completion: LabelingExampleCompletion.Draft, age: 1);
        var markers = fixture.Example("main-title", [(Decoration, new(10, 10, 8, 8)), (Decoration, new(30, 10, 8, 8))], age: 2);
        var catalog = fixture.Builder(Context("main-title", MainLabels)).Build([markers, draft, complete], null);
        var scene = catalog.Scenes.Single(); Equal(3, scene.Anchors.Count);
        Equal(corrected, scene.Probe.Bounds);
        Check(scene.Probe.Variants.Any(variant => variant.Kernel.Kind.Contains("human-box.", StringComparison.Ordinal)), "Focused draft not incorporated.");
        Check(scene.Probe.Probe.Reference.Width == 31, "Latest focused reference did not drive the ordinary scene probe.");
        Equal(0, catalog.SelectorCorrections.Count);
        Check(MenuStencilCatalogBuilder.IsSelectorCorrection(markers), "Two decoration-only boxes were treated as a complete scene.");
    }

    internal static void TestForeignText()
    {
        var fixture = new Fixture(); var english = fixture.Example("main-title", WithSelection(MainLabels, 1));
        var frenchLabels = MainLabels.Select(item => item.Id == MainLabels[0].Id ? (item.Id, new StencilRect(40, 40, 50, 8)) : item).ToArray();
        var french = fixture.Example("main-title", WithSelection(frenchLabels, 1), "fr", age: 1);
        var scene = fixture.Builder(Context("main-title", MainLabels)).Build([french, english], null).Scenes.Single();
        Equal("en", scene.Probe.Variants[0].LanguageIdentifier);
        var translated = scene.Probe.Variants.Single(variant => variant.Kernel.Kind.EndsWith(".human.fr", StringComparison.Ordinal));
        Check(translated.NominalBounds is { } bounds && bounds.X < 50 && bounds.Right > 80, "Translated text clipped or scaled into English width.");
        Check(scene.Probe.SearchProbeVariants.Any(variant => variant.LanguageIdentifier == "fr" && variant.NominalBounds is not null), "Foreign language probe missing.");
        var pixels = new ImageStencilPixels(fixture.Images[french.ImagePath], 160, 120);
        Check(pixels.Compare(translated.Kernel, translated.NominalBounds!.Value)!.Confidence(.82, 105) > .99, "Foreign glyph reference does not match actual pixels.");
    }

    internal static void TestLiveLimitsAndHoldouts()
    {
        var fixture = new Fixture(); var example = fixture.Example("main-title", WithSelection(MainLabels, 1));
        var calibration = new MenuStencilCalibration(1, 160, 120, Now, []).ReplacingSelector("main-title", MainLabels[0].Id, new(35, 40, 8, 8), new(87, 40, 8, 8));
        var live = Enumerable.Range(0, 12).Select(index => fixture.Live("main-title", MainLabels[0].Id, "fr", WithSelection(MainLabels, 0))).ToArray();
        var builder = fixture.Builder(Context("main-title", MainLabels));
        var held = builder.Build([example], calibration, live, live.Select(item => item.Id).ToHashSet()).Scenes.Single();
        Equal(8, live.Sum(source => fixture.Loads.GetValueOrDefault(source.ImagePath)));
        Check(held.Anchors.SelectMany(anchor => anchor.Variants).All(variant => variant.LanguageIdentifier != "fr"), "Held-out captures leaked into positive text references.");
        var heldOption = held.Options.Single(option => option.ClassIdentifier == MainLabels[0].Id);
        Check(heldOption.LeftSelector!.Variants.All(variant => variant.LanguageIdentifier != "fr"), "Held-out captures leaked into selector references.");
        fixture.Loads.Clear();
        var positive = builder.Build([example], calibration, live).Scenes.Single();
        Equal(8, live.Sum(source => fixture.Loads.GetValueOrDefault(source.ImagePath)));
        Check(positive.Probe.Variants.Any(variant => variant.Kernel.Kind.EndsWith(".live.fr", StringComparison.Ordinal)), "Positive live text consensus missing.");
        Check(positive.Options.Single(option => option.ClassIdentifier == MainLabels[0].Id).LeftSelector!.Variants.Any(variant => variant.LanguageIdentifier == "fr"), "Selected live arrows missing.");
        fixture.Loads.Clear(); builder.Build([example], null, live);
        Equal(0, live.Sum(source => fixture.Loads.GetValueOrDefault(source.ImagePath)));
    }

    internal static void TestProbeAndSelectablePolicies()
    {
        var fixture = new Fixture();
        (string Id, StencilRect Bounds)[] labels = [("shared.options", new(50, 10, 30, 8)), ("video.full-screen", new(50, 55, 30, 8)),
            ("video.heading", new(50, 30, 30, 8)), ("video.pointer", new(50, 80, 30, 8)), ("video.poster", new(5, 30, 20, 20))];
        var example = fixture.Example("video", WithSelection(labels, 1));
        var scene = fixture.Builder(Context("video", labels), Context("options", [labels[0]])).Build([example], null).Scenes.Single();
        Equal("video.full-screen", scene.Probe.ClassIdentifier);
        Check(scene.Anchors.All(anchor => anchor.ClassIdentifier != "video.poster"), "Poster became a menu text anchor.");
        Equal("video.full-screen", scene.Options.Single().ClassIdentifier);
        (string Id, StencilRect Bounds)[] dialog = [("quit-game.quit-game", new(50, 15, 30, 8)), ("shared.yes", new(50, 65, 30, 8)), ("shared.no", new(50, 90, 30, 8))];
        var first = fixture.Example("quit-game", WithSelection(dialog, 2), paint: WithSelection(dialog.Select((item, index) => index == 0 ? (item.Id, item.Bounds with { Width = 37 }) : item).ToArray(), 2));
        var focused = fixture.Example("quit-game", [(dialog[0].Id, new(52, 15, 30, 8))], completion: LabelingExampleCompletion.Draft, age: 1);
        var quit = fixture.Builder(Context("quit-game", dialog)).Build([focused, first], null).Scenes.Single();
        Equal(37.0, quit.Probe.Bounds.Width); Equal(2, quit.Options.Count);
        Check(quit.Options.All(option => option.ClassIdentifier is "shared.yes" or "shared.no"), "Dialog heading became selectable.");
        Check(!ReferenceEquals(quit.Probe.Probe.Reference, quit.Probe.Variants.Single(variant => variant.Kernel.Kind.Contains("human-box.", StringComparison.Ordinal)).Reference), "Focused title replaced the dialog's disambiguating consensus probe.");
    }

    internal static void TestClearSaveRows()
    {
        var fixture = new Fixture();
        (string Id, StencilRect Bounds)[] labels = [("select-profile.heading", new(50, 10, 30, 8)),
            ("select-profile.slot-1", new(50, 30, 30, 8)), ("select-profile.slot-2", new(50, 50, 30, 8)),
            ("select-profile.slot-3", new(50, 70, 30, 8)), ("select-profile.slot-4", new(50, 90, 30, 8)),
            ("shared.back", new(55, 105, 20, 8))];
        var withClear = fixture.Example("select-profile", [.. WithSelection(labels, 2), ("select-profile.clear-save", new(118, 52, 16, 6))]);
        var withoutClear = fixture.Example("select-profile", WithSelection(labels, 2), age: 1);
        var scene = fixture.Builder(Context("select-profile", labels)).Build([withoutClear, withClear], null).Scenes.Single();
        var rows = scene.Options.Where(option => option.ClassIdentifier.StartsWith("select-profile.clear-save-", StringComparison.Ordinal)).OrderBy(option => option.ClassIdentifier).ToArray();
        Equal(4, rows.Length);
        var singleReference = fixture.Builder(Context("select-profile", labels)).Build([withClear], null).Scenes.Single();
        Check(!singleReference.Options.Any(option => option.ClassIdentifier == "select-profile.clear-save")
            && singleReference.Options.Count(option => option.ClassIdentifier.StartsWith("select-profile.clear-save-", StringComparison.Ordinal)) == 4,
            "A completed single profile reference created a duplicate Clear Save selector candidate.");
        for (var index = 0; index < rows.Length; index++)
        {
            Equal($"Clear Save {index + 1}", rows[index].Name);
            Equal(new StencilRect(94, 32 + index * 20, 15, 12), rows[index].LeftSelector!.Bounds);
            Equal(new StencilRect(145, 32 + index * 20, 15, 12), rows[index].RightSelector!.Bounds);
        }
    }

    internal static void TestClearSaveMovesBetweenSlots()
    {
        var fixture = new Fixture();
        (string Id, StencilRect Bounds)[] slots = [("select-profile.heading", new(50, 4, 30, 8)),
            ("select-profile.slot-1", new(25, 26, 12, 8)), ("select-profile.slot-2", new(25, 46, 12, 8)),
            ("select-profile.slot-3", new(25, 66, 12, 8)), ("select-profile.slot-4", new(25, 86, 12, 8))];
        (string Id, string Name, StencilRect Bounds)[] dialog = [("clear-save.heading", "Clear Save", new(10, 46, 40, 8)),
            ("shared.yes", "Yes", new(80, 46, 15, 8)), ("shared.no", "No", new(125, 46, 15, 8))];
        var profile = fixture.Example("select-profile", WithSelection(slots, 1));
        var source = fixture.Example("clear-save", WithSelection(Plain(dialog), 2));
        var stale = new MenuStencilCalibration(1, 160, 120, Now, [])
            .ReplacingSelector("clear-save", "shared.yes", new(65, 26, 8, 8), new(102, 26, 8, 8))
            .ReplacingSelector("clear-save", "shared.no", new(110, 26, 8, 8), new(147, 26, 8, 8));
        var catalog = fixture.Builder(Context("select-profile", slots), Context("clear-save", dialog)).Build([source, profile], stale);
        foreach (var offset in new[] { 0, 20, 40, -20 })
        {
            var moved = fixture.Example("clear-save", WithSelection(Plain(dialog).Select(item => (item.Id, item.Bounds.Offset(0, offset))).ToArray(), 2));
            var result = new MenuStencilTracker(catalog).Observe(fixture.Images[moved.ImagePath], 0);
            Check(result?.IsMatch == true && result.ContextIdentifier == "clear-save", $"Dialog not recognized at offset {offset}.");
            Equal("No", result!.SelectedOption);
            Check(result.SelectorSearchRegions.Count == 4 && result.SelectorSearchRegions.All(region => Math.Abs(region.Rect.MidY - (50 + offset)) < 8),
                $"Selector search stayed on another slot at offset {offset}.");
        }
    }

    internal static void TestReviewedSelectorCorrections()
    {
        var fixture = new Fixture();
        (string Id, StencilRect Bounds)[] labels = [("shared.keyboard", new(50, 10, 30, 8)), ("keyboard.right", new(50, 80, 30, 8)), ("shared.jump", new(50, 100, 30, 8))];
        var baseline = fixture.Example("keyboard", WithSelection(labels, 1));
        var id = Guid.Parse("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F");
        var left = new StencilRect(30, 80, 8, 8); var right = new StencilRect(95, 80, 8, 8);
        // Reviewed correction screenshots can be stored under Main Title even
        // though the review explicitly targets another scene and language.
        var correction = fixture.Example("main-title", [(Decoration, left), (Decoration, right)], "ko", id: id, age: 1);
        var builder = fixture.Builder(Context("keyboard", labels), Context("main-title", MainLabels));
        var original = builder.Build([baseline], null).Scenes.Single().Options.Single(option => option.ClassIdentifier == "keyboard.right");
        var catalog = builder.Build([correction, baseline], null);
        var option = catalog.Scenes.Single().Options.Single(item => item.ClassIdentifier == "keyboard.right");
        Equal(original.LeftSelector!.Bounds, option.LeftSelector!.Bounds);
        Equal(left, option.LeftSelector.BoundsFor("ko")); Equal(right, option.RightSelector!.BoundsFor("ko"));
        Check(option.LeftSelector.Variants.Any(variant => variant.Kernel.Kind == "human-selector-correction.ko"), "Reviewed correction pixels missing.");
        var placement = catalog.SelectorCorrections.Single(); Equal(id, placement.SourceExampleIdentifier); Equal("keyboard", placement.ContextIdentifier);
        var directory = Directory.CreateTempSubdirectory("hkv-catalog-correction-");
        try
        {
            var output = Path.Combine(directory.FullName, MenuStencilCalibration.Filename);
            var tracker = new MenuSelectorCalibrationTracker(output, fallbackCalibration: new(1, 160, 120, Now, []));
            tracker.SynchronizeCorrections(catalog.SelectorCorrections, Now);
            Equal(new MenuSelectorBounds(left, right), MenuSelectorCalibrationTracker.PersistedBounds(MenuStencilCalibration.Load(output)!, "keyboard", "keyboard.right", "ko")!.Value);
        }
        finally { directory.Delete(recursive: true); }
    }

    internal static void TestInvalidAndIncompleteScenes()
    {
        var fixture = new Fixture(); var builder = fixture.Builder(Context("main-title", MainLabels));
        var incomplete = fixture.Example("main-title", [MainLabels[0]], completion: LabelingExampleCompletion.Draft);
        Throws(() => builder.Build([incomplete], null));
        var complete = fixture.Example("main-title", WithSelection(MainLabels, 0));
        Throws(() => builder.Build([complete], null, includedContexts: new HashSet<string> { "absent" }));
        var corruptDecoder = new MenuStencilCatalogBuilder([Context("main-title", MainLabels)],
            _ => throw new FormatException("Invalid PNG."), _ => true);
        Throws(() => corruptDecoder.Build([complete], null));
        fixture.Images.Remove(complete.ImagePath); Throws(() => builder.Build([complete], null));
    }

    // Fixture-adapted port: the private workstation screenshots are replaced by
    // deterministic captures, while the production UUID routing table is the
    // only code allowed to resolve each reviewed correction.
    internal static void TestReviewedContractBreachSelectorsCorrectActualLocalizedRows()
    {
        foreach (var expected in ReviewedCorrectionCases())
        {
            var fixture = new Fixture();
            var labels = SyntheticRows(expected.ContextIdentifier, expected.OptionIdentifier, expected.OptionName);
            var baseline = fixture.Example(expected.ContextIdentifier, WithSelection(Plain(labels), 4), expected.LanguageIdentifier);
            var target = labels[4].Bounds;
            var left = new StencilRect(target.X - 15, target.Y, 8, 8);
            var right = new StencilRect(target.Right + 7, target.Y, 8, 8);
            var correction = fixture.Example("main-title", [(Decoration, left), (Decoration, right)],
                expected.LanguageIdentifier, id: expected.Identifier, age: 1,
                paint: WithSelection(Plain(labels), 4));

            var catalog = fixture.Builder(Context(expected.ContextIdentifier, labels)).Build([correction, baseline], null);
            var option = catalog.Scenes.Single().Options.Single(item => item.ClassIdentifier == expected.OptionIdentifier);
            Check(option.LeftSelector?.LocalizedBounds?.ContainsKey(expected.LanguageIdentifier) == true,
                $"Missing localized left selector for {expected.Identifier}.");
            Check(option.RightSelector?.LocalizedBounds?.ContainsKey(expected.LanguageIdentifier) == true,
                $"Missing localized right selector for {expected.Identifier}.");
            var deployed = new[]
            {
                option.LeftSelector!.BoundsFor(expected.LanguageIdentifier),
                option.RightSelector!.BoundsFor(expected.LanguageIdentifier)
            };
            foreach (var annotation in new[] { left, right })
                Check(deployed.Any(candidate => Math.Abs(candidate.MidX - annotation.MidX) < .001
                    && Math.Abs(candidate.MidY - annotation.MidY) < .001),
                    $"Reviewed correction missed {expected.Identifier} {expected.ContextIdentifier}/{expected.OptionIdentifier}.");
            var placement = catalog.SelectorCorrections.Single();
            Equal(expected.Identifier, placement.SourceExampleIdentifier);
            Equal(expected.ContextIdentifier, placement.ContextIdentifier);
            Equal(expected.OptionIdentifier, placement.OptionIdentifier);
            Equal(expected.LanguageIdentifier, placement.LanguageIdentifier);
        }
    }

    // Fixture-adapted port of the unavailable human Clear Save capture.
    internal static void TestHumanClearSaveCaptureBuildsBothSelectorRows()
    {
        var fixture = new Fixture();
        (string Id, string Name, StencilRect Bounds)[] labels =
        [
            ("clear-save.heading", "Clear Save", new(50, 15, 40, 8)),
            ("shared.no", "No", new(60, 65, 20, 8)),
            ("shared.yes", "Yes", new(60, 90, 20, 8))
        ];
        var source = fixture.Example("clear-save", WithSelection(Plain(labels), 2));
        var catalog = fixture.Builder(Context("clear-save", labels)).Build([source], null);
        var scene = catalog.Scenes.Single();
        Equal("No|Yes", string.Join('|', scene.Options.Select(option => option.Name)));
        Check(scene.Options.All(option => option.LeftSelector is not null && option.RightSelector is not null),
            "Clear Save did not retain both selector pairs.");
        var result = new MenuStencilTracker(catalog).Observe(fixture.Images[source.ImagePath], 0);
        Check(result?.IsMatch == true, "Synthetic Clear Save capture did not match production tracker.");
        Equal("clear-save", result!.ContextIdentifier);
        Equal("Yes", result.SelectedOption);
        Equal(4, result.SelectorSearchRegions.Count);
    }

    // Fixture-adapted port of the unavailable human Inventory capture.
    internal static void TestHumanInventoryCaptureBuildsStaticSceneWithoutSelectors()
    {
        var fixture = new Fixture();
        (string Id, string Name, StencilRect Bounds)[] labels =
        [ ("inventory.inventory", "Inventory", new(50, 40, 50, 8)) ];
        var source = fixture.Example("inventory", Plain(labels));
        var catalog = fixture.Builder(Context("inventory", labels)).Build([source], null);
        var scene = catalog.Scenes.Single();
        Equal("inventory.inventory", scene.Probe.ClassIdentifier);
        Equal(0, scene.Options.Count);
        var result = new MenuStencilTracker(catalog).Observe(fixture.Images[source.ImagePath], 0);
        Check(result?.IsMatch == true, "Synthetic Inventory capture did not match production tracker.");
        Equal("inventory", result!.ContextIdentifier);
        Equal<string?>(null, result.SelectedOption);
        Equal(0, result.SelectorSearchRegions.Count);
    }

    // Fixture-adapted port: fixed IDs exercise the real focused-draft variant path.
    internal static void TestFocusedDraftBoxesBecomeObjectStencilVariantsWhenAvailable()
    {
        (Guid Identifier, string ContextIdentifier, string OptionIdentifier)[] expected =
        [
            (Guid.Parse("F43481C4-3F4E-4E02-B52A-15B5BE20803F"), "game-options", "game-options.show-achievements"),
            (Guid.Parse("F43481C4-3F4E-4E02-B52A-15B5BE20803F"), "game-options", "game-options.backer-credits"),
            (Guid.Parse("90E5D3BE-F45F-488A-B376-B63664A7D75E"), "audio", "audio.sound-volume"),
            (Guid.Parse("E7B8D5DC-BA90-472F-875B-66A728435EF8"), "video", "shared.advanced-settings"),
            (Guid.Parse("AA8CCF3B-0069-4DBD-8638-E051DF298529"), "controller", "shared.advanced-settings")
        ];
        foreach (var item in expected)
        {
            var fixture = new Fixture();
            var labels = SyntheticRows(item.ContextIdentifier, item.OptionIdentifier, item.OptionIdentifier);
            var complete = fixture.Example(item.ContextIdentifier, WithSelection(Plain(labels), 4));
            var focusedBounds = labels[4].Bounds with { X = labels[4].Bounds.X + 1, Width = labels[4].Bounds.Width + 2 };
            var focused = fixture.Example(item.ContextIdentifier, [(item.OptionIdentifier, focusedBounds)],
                completion: LabelingExampleCompletion.Draft, id: item.Identifier, age: 1,
                paint: Plain(labels));
            var scene = fixture.Builder(Context(item.ContextIdentifier, labels)).Build([focused, complete], null).Scenes.Single();
            var anchor = scene.Anchors.Single(candidate => candidate.ClassIdentifier == item.OptionIdentifier);
            Check(anchor.Variants.Any(variant => variant.Kernel.Kind.EndsWith(
                $".human-box.{item.Identifier:D}".ToLowerInvariant(), StringComparison.Ordinal)),
                $"Focused draft variant missing for {item.ContextIdentifier}/{item.OptionIdentifier}.");
        }
    }

    internal static void TestBundledGeometryContainsLatestReportedSelectorCorrections()
    {
        var calibration = MenuStencilCalibration.LoadBundled() ?? throw new Exception("Bundled menu calibration missing.");
        MenuStencilCalibration.Selector Selector(string context, string identifier) =>
            calibration.FindScene(context)?.Selectors.SingleOrDefault(item => item.SelectedIdentifier == identifier)
            ?? throw new Exception($"Missing bundled selector {context}/{identifier}.");

        var profileBack = Selector("select-profile", "shared.back");
        Equal(new StencilRect(282, 294, 16, 14), profileBack.LeftRect.Bounds);
        Equal(new StencilRect(345, 293, 15, 14), profileBack.RightRect.Bounds);
        Equal(new StencilRect(459, 101, 12, 10), Selector("audio", "audio.master-volume").RightRect.Bounds);
        var controllerAdvanced = Selector("controller", "shared.advanced-settings");
        Equal(new StencilRect(240, 297, 11, 8), controllerAdvanced.LeftRect.Bounds);
        Equal(new StencilRect(387, 297, 11, 8), controllerAdvanced.RightRect.Bounds);
        var resetDefaults = Selector("controller-advanced-settings", "shared.reset-defaults");
        Equal(new StencilRect(252, 282, 11, 10), resetDefaults.LeftRect.Bounds);
        Equal(new StencilRect(373, 282, 12, 10), resetDefaults.RightRect.Bounds);
    }

    // Fixture-adapted port of the unavailable latest focused draft.
    internal static void TestLatestFocusedDraftDefinesFullHumanStencilBounds()
    {
        var fixture = new Fixture();
        const string context = "controller-advanced-settings", target = "controller-advanced.native-input";
        var labels = SyntheticRows(context, target, target);
        var complete = fixture.Example(context, WithSelection(Plain(labels), 4));
        var identifier = Guid.Parse("EC3C8664-2AD6-4BBB-BC5B-549392476E39");
        var expected = new StencilRect(47.25, 78.5, 38.5, 10.25);
        var focused = fixture.Example(context, [(target, expected)], completion: LabelingExampleCompletion.Draft,
            id: identifier, age: 1, paint: [(target, expected)]);
        var scene = fixture.Builder(Context(context, labels)).Build([focused, complete], null).Scenes.Single();
        var anchor = scene.Anchors.Single(item => item.ClassIdentifier == target);
        Equal(expected, anchor.Bounds);
        var suffix = $".human-box.{identifier:D}".ToLowerInvariant();
        var variant = anchor.Variants.Single(item => item.Kernel.Kind.EndsWith(suffix, StringComparison.Ordinal));
        var sampledWidth = (int)Math.Ceiling(expected.Right) - (int)Math.Floor(expected.X);
        var sampledHeight = (int)Math.Ceiling(expected.Bottom) - (int)Math.Floor(expected.Y);
        Equal(sampledWidth, variant.Reference.Width);
        Equal(sampledHeight, variant.Reference.Height);
        Check(variant.Reference.Mask is { } mask && mask.Count(value => value != 0) < sampledWidth * sampledHeight,
            "Focused draft reference lost its sparse interior mask.");
    }

    // Fixture-adapted ports of four unavailable localized correction screenshots.
    internal static void TestItalianKeyboardCorrectionBeatsEmptyAudioPair() =>
        AssertCorrectionSelects(Guid.Parse("01A82890-192D-4CD7-BB55-E0D4B27C3CB9"), "options",
            "shared.keyboard", "Keyboard", "it", "shared.audio", "Audio");

    internal static void TestItalianAdvancedSettingsCorrectionSelectsLabeledRow() =>
        AssertCorrectionSelects(Guid.Parse("FC571AB9-9AB2-4EE9-BEC0-7257EFF93C1B"), "video",
            "shared.advanced-settings", "Advanced Settings", "it");

    internal static void TestItalianGameCorrectionSelectsLabeledRow() =>
        AssertCorrectionSelects(Guid.Parse("973AB0CA-EEF2-4902-8B53-E0765479567E"), "options",
            "options.game", "Game", "it");

    internal static void TestKoreanGrimmCorrectionRejectsEmptyCreditsPair() =>
        AssertCorrectionSelects(Guid.Parse("144A8CFB-9C77-4D92-A621-D7780B96335F"), "extras",
            "extras.the-grimm-troupe", "The Grimm Troupe", "ko", "extras.credits", "Credits");

    // Fixture-adapted port: exact IDs and production correction placement drive three synthetic captures.
    internal static void TestNewestKoreanSelectorCorrectionsSelectLabeledRows()
    {
        var (fixture, catalog, cases) = NewestKoreanCatalog();
        var keys = catalog.SelectorCorrections.Select(item =>
            $"{item.ContextIdentifier}:{item.OptionIdentifier}:{item.LanguageIdentifier}").ToHashSet(StringComparer.Ordinal);
        foreach (var expected in cases)
        {
            Check(keys.Contains($"{expected.ContextIdentifier}:{expected.OptionIdentifier}:ko"),
                $"Missing Korean correction placement for {expected.OptionIdentifier}.");
            var image = fixture.Images[expected.ImagePath];
            var result = new MenuStencilTracker(catalog.ContainingOnly(expected.ContextIdentifier)).Observe(image, 1);
            Check(result?.IsMatch == true, $"Synthetic Korean correction did not match: {expected.Identifier}.");
            Equal(expected.ContextIdentifier, result!.ContextIdentifier);
            Equal(expected.OptionName, result.SelectedOption);
            Equal(2, result.SelectorCandidates.Count);
            Check(Math.Abs(result.SelectorCandidates[0].Rect.MidY - result.SelectorCandidates[1].Rect.MidY) <= 3,
                $"Korean selector pair is not row-aligned: {expected.Identifier}.");
        }
    }

    internal static void TestNewestKoreanCorrectionsPersistIntoCalibration()
    {
        var (_, catalog, _) = NewestKoreanCatalog();
        var directory = Directory.CreateTempSubdirectory("hkv-newest-korean-corrections-");
        try
        {
            var output = Path.Combine(directory.FullName, MenuStencilCalibration.Filename);
            var mirror = Path.Combine(directory.FullName, "project-menu-stencils.json");
            var stale = new MenuStencilCalibration(1, 160, 120, Now.AddMinutes(-1), [])
                .ReplacingSelector("keyboard", "shared.super-dash",
                    new(30, 73, 10, 8), new(110, 92.5, 10, 8), "ko", Now.AddMinutes(-1));
            stale.Write(output);
            var tracker = new MenuSelectorCalibrationTracker(output, mirror, stale);
            _ = new MenuStencilTracker(catalog, calibration: tracker);

            var persisted = MenuStencilCalibration.Load(output) ?? throw new Exception("Persisted Korean calibration did not load.");
            var right = MenuSelectorCalibrationTracker.PersistedBounds(persisted, "keyboard", "keyboard.right", "ko")
                ?? throw new Exception("Korean Right correction was not persisted.");
            var superDash = MenuSelectorCalibrationTracker.PersistedBounds(persisted, "keyboard", "shared.super-dash", "ko")
                ?? throw new Exception("Korean Super Dash correction was not persisted.");
            Check(Math.Abs(right.Left.MidY - right.Right.MidY) <= 3, "Persisted Korean Right pair is not row-aligned.");
            Check(Math.Abs(superDash.Left.MidY - superDash.Right.MidY) <= 3, "Persisted Korean Super Dash pair is not row-aligned.");
            Check(File.ReadAllBytes(mirror).SequenceEqual(File.ReadAllBytes(output)), "Calibration mirror differs from primary output.");
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed record CorrectionCase(Guid Identifier, string ContextIdentifier, string OptionIdentifier,
        string OptionName, string LanguageIdentifier);
    private sealed record SyntheticCorrectionCapture(Guid Identifier, string ContextIdentifier, string OptionIdentifier,
        string OptionName, string ImagePath);

    private static CorrectionCase[] ReviewedCorrectionCases() =>
    [
        Correction("5EF5867D-16EC-4202-B9A7-0B273A4CCE12", "options", "options.game", "fr"),
        Correction("A343B40F-10B9-4992-808E-A4DF90DD60DE", "game-options", "shared.back", "fr"),
        Correction("7BDBE122-1CF4-48A0-AA47-0DF9DA22E8BD", "game-options", "shared.reset-defaults", "fr"),
        Correction("9AF0F76E-969F-4A39-BE1F-85DCBFC43FDA", "extras", "extras.lifeblood", "es"),
        Correction("BA6281F5-61BD-4FDE-A88F-84AF957A0668", "extras", "extras.the-grimm-troupe", "es"),
        Correction("62D3E3C4-A574-4946-B26D-A646EE6666AE", "extras", "extras.hidden-dreams", "es"),
        Correction("F44121EB-E362-4ADB-A314-48ECAAE61E28", "keyboard", "shared.attack", "es"),
        Correction("3ABEEF76-A4E8-4BDA-9C0C-5B7F6B503AE2", "options", "shared.controller", "es"),
        Correction("8F82F042-B421-4FC4-9BC4-A7CD23D30A52", "video", "video.full-screen", "es"),
        Correction("F1EE8587-0830-4A26-A3DA-C526267A98CE", "game-options", "shared.reset-defaults", "es"),
        Correction("FCFC1ADD-C60D-4B30-AC31-E9E42E5C020F", "quit-game", "shared.no", "pt-BR"),
        Correction("6E4C3D9F-73CA-49E4-B2F0-C30D3A48DBEE", "keyboard", "shared.super-dash", "pt-BR"),
        Correction("997CB3D1-0546-4B2A-9FD4-396DF6CDFC6D", "keyboard", "shared.attack", "pt-BR"),
        Correction("7B18DA08-52B2-40DD-904D-E68412C5DE10", "keyboard", "inventory.inventory", "pt-BR"),
        Correction("804959CD-CEDE-47EC-B5EC-99B5C1DB24BB", "video", "shared.advanced-settings", "pt-BR"),
        Correction("08B7D411-2B22-4BA1-83DD-D35519E8CC21", "video", "video.full-screen", "pt-BR"),
        Correction("23177404-3D79-4AD0-A2CE-67F6C8CA69D1", "options", "options.game", "pt-BR"),
        Correction("35F5BDEE-F9C8-4B35-95B2-40AAE1E4EB24", "extras", "extras.lifeblood", "ko"),
        Correction("577B177F-29D8-4D26-AA5C-EAD1A325841C", "extras", "extras.hidden-dreams", "ko"),
        Correction("69FA0F82-93F4-49C8-8BE1-2CDDD89748A9", "extras", "extras.the-grimm-troupe", "ko"),
        Correction("E8FC52A8-3E99-407C-9A83-1D88E7143BF5", "options", "options.game", "es"),
        Correction("04D40700-0C53-4E5D-B2D8-0DA5C134774D", "options", "options.game", "fr"),
        Correction("0D565524-8EC3-442C-B8F3-3152A0A0382A", "options", "options.game", "it"),
        Correction("01A82890-192D-4CD7-BB55-E0D4B27C3CB9", "options", "shared.keyboard", "it"),
        Correction("FC571AB9-9AB2-4EE9-BEC0-7257EFF93C1B", "video", "shared.advanced-settings", "it"),
        Correction("973AB0CA-EEF2-4902-8B53-E0765479567E", "options", "options.game", "it"),
        Correction("911E9057-3FE0-452C-BE9C-2D68AD922154", "keyboard", "shared.attack", "it"),
        Correction("AE1F5C8D-6D41-427C-B6BE-DDCC911964BD", "keyboard", "shared.super-dash", "it"),
        Correction("D57CC45F-DE6A-4BA6-A25F-FDB67B3ADEEA", "keyboard", "inventory.inventory", "it"),
        Correction("FA0753AB-7E1F-4024-A1EF-F5E651F51B93", "options", "options.game", "ja"),
        Correction("5393B426-F329-4E86-80D2-5E104C36A746", "extras", "extras.hidden-dreams", "ja"),
        Correction("7B00F837-FC48-46FC-9AF2-9C19C471ED3E", "extras", "extras.the-grimm-troupe", "ja"),
        Correction("07A600CE-FC9A-42FD-AA72-6E54D0412971", "extras", "extras.lifeblood", "ja"),
        Correction("D3BF03A8-98EC-4CEA-9696-B647C674221E", "quit-game", "shared.no", "ja"),
        Correction("00B9FE92-B22D-400A-947C-03848590DBBF", "video", "shared.advanced-settings", "ja"),
        Correction("AA6813FA-8B96-4B2C-B499-0A3F46F745C6", "keyboard", "shared.attack", "ko"),
        Correction("3224CB73-47A1-4F4B-B4AD-21EE272B4B81", "keyboard", "shared.attack", "ko"),
        Correction("16A25BAA-201A-40AD-ABC2-28F97683D505", "extras", "extras.lifeblood", "ko"),
        Correction("144A8CFB-9C77-4D92-A621-D7780B96335F", "extras", "extras.the-grimm-troupe", "ko"),
        Correction("66CCC048-781A-4D48-8D80-6D58A1CA9530", "video", "shared.advanced-settings", "ko"),
        Correction("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F", "keyboard", "keyboard.right", "ko"),
        Correction("BB7C33FD-0000-41D3-A25F-525B663772D2", "keyboard", "shared.super-dash", "ko")
    ];

    private static CorrectionCase Correction(string identifier, string context, string option, string language) =>
        new(Guid.Parse(identifier), context, option, option, language);

    private static (string Id, string Name, StencilRect Bounds)[] SyntheticRows(
        string context, string targetIdentifier, string targetName) =>
    [
        ($"{context}.heading", $"{context} heading", new(50, 10, 45, 8)),
        ($"{context}.fixture-a", "Fixture A", new(50, 30, 40, 8)),
        ($"{context}.fixture-b", "Fixture B", new(50, 45, 40, 8)),
        ($"{context}.fixture-c", "Fixture C", new(50, 60, 40, 8)),
        (targetIdentifier, targetName, new(50, 80, 40, 8))
    ];

    private static void AssertCorrectionSelects(Guid identifier, string contextIdentifier, string optionIdentifier,
        string optionName, string languageIdentifier, string? emptyIdentifier = null, string? emptyName = null)
    {
        var fixture = new Fixture();
        var labels = SyntheticRows(contextIdentifier, optionIdentifier, optionName);
        if (emptyIdentifier is not null)
            labels[3] = (emptyIdentifier, emptyName ?? emptyIdentifier, labels[3].Bounds);
        var plain = Plain(labels);
        var baseline = fixture.Example(contextIdentifier, WithSelection(plain, 4), languageIdentifier);
        var target = labels[4].Bounds;
        var left = new StencilRect(target.X - 15, target.Y, 8, 8);
        var right = new StencilRect(target.Right + 7, target.Y, 8, 8);
        var correction = fixture.Example("main-title", [(Decoration, left), (Decoration, right)], languageIdentifier,
            id: identifier, age: 1, paint: WithSelection(plain, 4));
        var catalog = fixture.Builder(Context(contextIdentifier, labels)).Build([correction, baseline], null);
        var result = new MenuStencilTracker(catalog.ContainingOnly(contextIdentifier)).Observe(fixture.Images[correction.ImagePath], 1);
        Check(result?.IsMatch == true, $"Synthetic localized correction did not match: {identifier}.");
        Equal(contextIdentifier, result!.ContextIdentifier);
        Equal(languageIdentifier, result.LanguageIdentifier);
        Equal(optionName, result.SelectedOption);
    }

    private static (Fixture Fixture, MenuStencilCatalog Catalog, SyntheticCorrectionCapture[] Cases) NewestKoreanCatalog()
    {
        var fixture = new Fixture();
        var video = SyntheticRows("video", "shared.advanced-settings", "Advanced Settings");
        (string Id, string Name, StencilRect Bounds)[] keyboard =
        [
            ("keyboard.heading", "Keyboard", new(50, 10, 45, 8)),
            ("keyboard.fixture-a", "Fixture A", new(50, 35, 40, 8)),
            ("keyboard.fixture-b", "Fixture B", new(50, 55, 40, 8)),
            ("keyboard.right", "Right", new(50, 80, 40, 8)),
            ("shared.super-dash", "Super Dash", new(50, 100, 40, 8))
        ];
        var videoPlain = Plain(video); var keyboardPlain = Plain(keyboard);
        var baselines = new[]
        {
            fixture.Example("video", WithSelection(videoPlain, 4), "ko"),
            fixture.Example("keyboard", WithSelection(keyboardPlain, 3), "ko"),
            fixture.Example("keyboard", WithSelection(keyboardPlain, 4), "ko", age: 1)
        };
        var definitions = new[]
        {
            (Guid.Parse("66CCC048-781A-4D48-8D80-6D58A1CA9530"), "video", "shared.advanced-settings", "Advanced Settings", videoPlain, 4),
            (Guid.Parse("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F"), "keyboard", "keyboard.right", "Right", keyboardPlain, 3),
            (Guid.Parse("BB7C33FD-0000-41D3-A25F-525B663772D2"), "keyboard", "shared.super-dash", "Super Dash", keyboardPlain, 4)
        };
        var corrections = definitions.Select((definition, index) =>
        {
            var selected = definition.Item5[definition.Item6].Bounds;
            var pair = new (string Id, StencilRect Bounds)[]
            {
                (Decoration, new(selected.X - 15, selected.Y, 8, 8)),
                (Decoration, new(selected.Right + 7, selected.Y, 8, 8))
            };
            return fixture.Example("main-title", pair, "ko", id: definition.Item1, age: index + 2,
                paint: WithSelection(definition.Item5, definition.Item6));
        }).ToArray();
        var catalog = fixture.Builder(Context("video", video), Context("keyboard", keyboard))
            .Build([.. corrections, .. baselines], null);
        var cases = definitions.Select((definition, index) => new SyntheticCorrectionCapture(definition.Item1,
            definition.Item2, definition.Item3, definition.Item4, corrections[index].ImagePath)).ToArray();
        return (fixture, catalog, cases);
    }

    private static (string Id, StencilRect Bounds)[] Plain(
        IEnumerable<(string Id, string Name, StencilRect Bounds)> labels) =>
        labels.Select(item => (item.Id, item.Bounds)).ToArray();

    private static MenuStencilContext Context(string id, IEnumerable<(string Id, StencilRect Bounds)> labels) =>
        new(id, labels.Select(item => new MenuStencilLabel(item.Id, item.Id)).ToArray());
    private static MenuStencilContext Context(string id, IEnumerable<(string Id, string Name, StencilRect Bounds)> labels) =>
        new(id, labels.Select(item => new MenuStencilLabel(item.Id, item.Name)).ToArray());
    private static (string Id, StencilRect Bounds)[] WithSelection(IReadOnlyList<(string Id, StencilRect Bounds)> labels, int selected)
    {
        var bounds = labels[selected].Bounds;
        return [.. labels, (Decoration, new(bounds.X - 15, bounds.Y, 8, 8)), (Decoration, new(bounds.Right + 7, bounds.Y, 8, 8))];
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Unusable scene accepted."); }

    private sealed class Fixture
    {
        internal Dictionary<string, BgraFrame> Images { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> Loads { get; } = new(StringComparer.Ordinal);
        internal MenuStencilCatalogBuilder Builder(params MenuStencilContext[] contexts) => new(contexts, path =>
        { Loads[path] = Loads.GetValueOrDefault(path) + 1; return Images.GetValueOrDefault(path); }, example => example.Manifest.Completion == LabelingExampleCompletion.Ready);
        internal SavedLabelingExample Example(string context, IReadOnlyList<(string Id, StencilRect Bounds)> annotations, string language = "en",
            LabelingExampleCompletion completion = LabelingExampleCompletion.Ready, Guid? id = null, int age = 0,
            IReadOnlyList<(string Id, StencilRect Bounds)>? paint = null)
        {
            var identifier = id ?? Guid.NewGuid(); var directory = Path.Combine(Path.GetTempPath(), "hkv-virtual-catalog", identifier.ToString("D"));
            var manifest = new LabelingExampleManifest(5, identifier, identifier, "image.png", 160, 120, identifier, context,
                annotations.Select(item => new LabelingExampleAnnotation(Guid.NewGuid(), item.Id, item.Bounds.X / 160, item.Bounds.Y / 120, item.Bounds.Width / 160, item.Bounds.Height / 120)).ToArray(),
                [], completion, Now.AddSeconds(age), language);
            var example = new SavedLabelingExample(directory, manifest); Images[example.ImagePath] = Paint(paint ?? annotations); return example;
        }
        internal MenuStencilLiveSource Live(string context, string selected, string language, IReadOnlyList<(string Id, StencilRect Bounds)> paint)
        {
            var id = Guid.NewGuid(); var path = Path.Combine(Path.GetTempPath(), "hkv-virtual-live", id.ToString("D"), "image.png");
            Images[path] = Paint(paint); return new(id, context, selected, language, 160, 120, path);
        }
        private static BgraFrame Paint(IReadOnlyList<(string Id, StencilRect Bounds)> objects)
        {
            var bytes = new byte[160 * 120 * 4];
            for (var index = 0; index < 160 * 120; index++) { bytes[index * 4] = 62; bytes[index * 4 + 1] = 24; bytes[index * 4 + 2] = 12; bytes[index * 4 + 3] = 255; }
            foreach (var item in objects)
            {
                var bounds = item.Bounds;
                for (var y = Math.Max(0, (int)bounds.Y + 1); y < Math.Min(120, bounds.Bottom - 1); y++)
                    for (var x = Math.Max(0, (int)bounds.X + 1); x < Math.Min(160, bounds.Right - 1); x++)
                    {
                        if (item.Id == Decoration ? x > bounds.X + 2 && y < bounds.Bottom - 3 : (x - (int)bounds.X) % 5 == 0 && y > bounds.Y + 2) continue;
                        var gray = (byte)(180 + (x - (int)bounds.X + 2 * (y - (int)bounds.Y)) % 60);
                        var offset = (y * 160 + x) * 4; bytes[offset] = gray; bytes[offset + 1] = gray; bytes[offset + 2] = gray;
                    }
            }
            return new(160, 120, 160 * 4, bytes, 0, Now);
        }
    }
}
