using HollowKnightVision.Windows.Core;
using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

internal static class MenuTrackerTests
{
    private static readonly byte[] Pattern = [238, 86, 214, 104, 72, 226, 98, 242, 206, 116, 232, 80, 94, 244, 122, 218];
    private static readonly byte[] ForeignPattern = [86, 214, 104, 238, 226, 98, 242, 72, 116, 232, 80, 206, 244, 122, 218, 94];
    private static readonly byte[] ThirdPattern = [218, 122, 244, 94, 80, 232, 116, 206, 242, 98, 226, 72, 104, 214, 86, 238];
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    internal static void TestCadence()
    {
        var cadence = new MenuStencilSearchCadence(.1);
        Check(cadence.ShouldSearch(1) && !cadence.ShouldSearch(1.05) && cadence.ShouldSearch(1.101) && !cadence.ShouldSearch(1.15), "Untracked search cadence differs from Mac.");
        cadence.Reset(); Check(cadence.ShouldSearch(1.151) && cadence.ShouldSearch(.5), "Reset/clock rollback did not reopen acquisition.");
        Check(cadence.ShouldSearch(double.NaN) && cadence.LastSearchTimestamp == .5, "Invalid timestamp poisoned cadence.");
        var tracker = new MenuStencilTracker(Catalog(), .1);
        var blank = Frame(72, 40);
        Check(tracker.Observe(blank, 1)?.IsMatch == false && tracker.Observe(blank, 1.05) is null, "Tracker bypassed untracked cadence.");
        tracker.Reset(); Check(tracker.Observe(Image(12, 20, 50, 20), 1.051)?.IsMatch == true, "Explicit reset did not acquire immediately.");
        Check(tracker.Observe(Image(12, 20, 50, 20), 1.052)?.Phase == MenuStencilPhase.Tracking, "Search cadence incorrectly throttled tracked frames.");
    }

    internal static void TestPairEvidence()
    {
        Equal(.696, MenuStencilTracker.QualifiedSelectorPairConfidence(.787, .605)!.Value);
        Near(.6215, MenuStencilTracker.QualifiedSelectorPairConfidence(.587, .656)!.Value);
        Check(.696 - .6215 > MenuStencilTracker.SelectorWinningMargin, "Winning margin changed.");
        Check(MenuStencilTracker.QualifiedSelectorPairConfidence(.9, .479) is null, "One strong side hid a weak side.");
        Check(MenuStencilTracker.QualifiedSelectorPairConfidence(.459, .599, 59, 59) is not null, "Foreground-supported pair rejected.");
        Check(MenuStencilTracker.QualifiedSelectorPairConfidence(.390, .430, 56, 56) is not null, "Both faint sides rejected.");
        Check(MenuStencilTracker.QualifiedSelectorPairConfidence(.299, .243, 39, 45) is not null, "Faded visible pair rejected.");
        Check(MenuStencilTracker.QualifiedSelectorPairConfidence(.299, .243, 35, 45) is null, "Faded pair without enough foreground accepted.");
        Check(MenuStencilTracker.QualifiedSelectorPairConfidence(.459, .599, 0, 59) is null, "Missing left foreground accepted.");
        var faded = new MenuStencilMatch("shared.select-decoration", "Back left", new(10, 10, 12, 10), .22, null);
        var result = new MenuStencilResult("select-profile", true, 1, "Back", [], [faded, faded], [], 0, 0, 1, MenuStencilPhase.Tracking, 0, 1, null);
        Check(result.SelectedPairAccepted, "Accepted faded pair was rendered as rejected.");
        Check(!(result with { SelectedOption = null }).SelectedPairAccepted && !(result with { SelectorCandidates = [faded] }).SelectedPairAccepted, "Unsolved/unilateral pair rendered as accepted.");
    }

    internal static void TestClearSaveOffsets()
    {
        Check(MenuStencilTracker.ClearSaveVerticalProbeOffsets(103, [103, 156, 208, 261]).SequenceEqual(new double[] { 0, 53, 105, 158 }), "First profile-row offsets differ.");
        Check(MenuStencilTracker.ClearSaveVerticalProbeOffsets(207, [103, 156, 208, 261]).SequenceEqual(new double[] { -105, -52, 0, 53 }), "Third profile-row offsets differ.");
        Check(MenuStencilTracker.ClearSaveVerticalProbeOffsets(207, []).SequenceEqual(new double[] { 0 }), "Empty profile rows require zero offset.");
    }

    internal static void TestSearchRegions()
    {
        var result = new MenuStencilTracker(Catalog()).Observe(Image(12, 20, 50, 20), 1)!;
        Check(result.IsMatch && result.SelectedOption == "Fixture Option", "Synthetic Mac selector pair was not acquired.");
        Equal(2, result.SelectorSearchRegions.Count); Equal(2, result.SelectorCandidates.Count);
        foreach (var region in result.SelectorSearchRegions)
        {
            Check(region.IsSolved, "Solved row is not highlighted."); Near(12, region.Rect.Width); Near(12, region.Rect.Height);
            Check(result.SelectorCandidates.Any(match => region.Rect.Intersect(match.Rect) == match.Rect), "Dashed search region misses solved cursor.");
        }
        var scaled = Scale2(Image(12, 20, 50, 20));
        var doubleResult = new MenuStencilTracker(Catalog()).Observe(scaled, 1)!;
        Equal(result.SelectedOption, doubleResult.SelectedOption);
        foreach (var (single, doubled) in result.SelectorCandidates.Zip(doubleResult.SelectorCandidates))
        { Near(single.Rect.X * 2, doubled.Rect.X); Near(single.Rect.Y * 2, doubled.Rect.Y); Near(single.Rect.Width * 2, doubled.Rect.Width); }
    }

    internal static void TestDifferentRowsRejected()
    {
        var result = new MenuStencilTracker(Catalog()).Observe(Image(12, 17, 50, 24), 1)!;
        Check(result.SelectedOption is null && result.SelectorCandidates.Count == 0 && result.SelectorSearchRegions.All(region => !region.IsSolved), "Arrows from different rows formed a false pair.");
    }

    internal static void TestRecalibrationFromImages()
    {
        using var fixture = new CalibrationFixture(); var tracker = fixture.Tracker();
        MenuStencilResult? first = null, second = null;
        for (var index = 0; index <= 10; index++) first = tracker.Observe(Image(12, 20, 50, 20), index * .25);
        for (var index = 0; index <= 9; index++) second = tracker.Observe(Image(15, 23, 53, 23), 3 + index * .25);
        var firstLeft = first!.SelectorSearchRegions.First(region => region.Side == MenuSelectorSide.Left);
        var secondLeft = second!.SelectorSearchRegions.First(region => region.Side == MenuSelectorSide.Left);
        Near(3, secondLeft.Rect.MidX - firstLeft.Rect.MidX);
        Near(3, secondLeft.Rect.MidY - firstLeft.Rect.MidY); // Windows remains top-origin; Mac's output asserts -3.
        var selector = MenuStencilCalibration.Load(fixture.Output)!.FindScene("main-title")!.Selectors.Single();
        Equal(2, selector.EvidenceCount); Near(15, selector.LeftRect.X); Near(23, selector.LeftRect.Y);
        Check(File.ReadAllBytes(fixture.Output).SequenceEqual(File.ReadAllBytes(fixture.Mirror)), "Image-driven calibration mirror differs.");
    }

    internal static void TestHighRateCalibrationFromImages()
    {
        using var fixture = new CalibrationFixture(); var tracker = fixture.Tracker(); var image = Image(10, 20, 48, 20);
        for (var frame = 0; frame < 120; frame++) _ = tracker.Observe(image, frame / 60.0);
        Check(!File.Exists(fixture.Output), "High-rate image observations bypassed stability time.");
        for (var frame = 120; frame <= 150; frame++) _ = tracker.Observe(image, frame / 60.0);
        Check(MenuStencilCalibration.Load(fixture.Output)?.FindScene("main-title")?.Selectors.Any(selector => selector.SelectedIdentifier == "fixture-option") == true, "High-rate image observations never settled.");
    }

    internal static void TestStaleCalibrationAndReviewedCorrections()
    {
        using var fixture = new CalibrationFixture(); File.WriteAllText(fixture.Output, "stale");
        var tracker = fixture.Tracker();
        for (var index = 0; index <= 9; index++) Equal("Fixture Option", tracker.Observe(Image(10, 20, 48, 20), index * .25)!.SelectedOption);
        Check(MenuStencilCalibration.Load(fixture.Output)?.FindScene("main-title") is not null, "Catalog geometry masqueraded as persisted data.");
        using var reviewed = new CalibrationFixture();
        var left = new StencilRect(12, 18, 4, 4); var right = new StencilRect(50, 18, 4, 4); var id = Guid.NewGuid();
        var catalog = Catalog() with { SelectorCorrections = [new("main-title", "fixture-option", "en", left, right, id)] };
        _ = reviewed.Tracker(catalog);
        var imported = MenuStencilCalibration.Load(reviewed.Output)!;
        Equal(new MenuSelectorBounds(left, right), MenuSelectorCalibrationTracker.PersistedBounds(imported, "main-title", "fixture-option", "en")!.Value);
        Check(File.ReadAllBytes(reviewed.Output).SequenceEqual(File.ReadAllBytes(reviewed.Mirror)), "Reviewed correction mirror differs.");
        var liveLeft = left.Offset(2, 1); var liveRight = right.Offset(2, 1);
        imported.ReplacingSelector("main-title", "fixture-option", liveLeft, liveRight, now: Now.AddSeconds(10)).Write(reviewed.Output);
        _ = reviewed.Tracker(catalog);
        Equal(new MenuSelectorBounds(liveLeft, liveRight), MenuSelectorCalibrationTracker.PersistedBounds(MenuStencilCalibration.Load(reviewed.Output)!, "main-title", "fixture-option", "en")!.Value);
    }

    internal static void TestRowTransitionsAndProbeGrace()
    {
        var catalog = Catalog(); var scene = catalog.Scenes[0]; var first = scene.Options[0];
        var second = first with { ClassIdentifier = "second", Name = "Second", Bounds = first.Bounds.Offset(0, 16),
            LeftSelector = first.LeftSelector! with { Bounds = first.LeftSelector.Bounds.Offset(0, 16) },
            RightSelector = first.RightSelector! with { Bounds = first.RightSelector.Bounds.Offset(0, 16) } };
        catalog = catalog with { ReferenceHeight = 52, BandEnd = 52, Scenes = [scene with { ReferenceHeight = 52, Options = [first, second] }] };
        var tracker = new MenuStencilTracker(catalog);
        var initial = Frame(72, 52, (2, 2, Pattern), (10, 20, Pattern), (48, 20, Pattern));
        Equal("Fixture Option", tracker.Observe(initial, 0)!.SelectedOption);
        var changed = Frame(72, 52, (10, 36, Pattern), (48, 36, Pattern));
        var grace = tracker.Observe(changed, .25)!;
        Check(grace.IsMatch && grace.SelectedOption == "Second", "One probe miss froze the previous selected row.");
        Check(tracker.Observe(changed, .5)?.IsMatch == false, "Scene survived beyond probe-loss grace.");
        var restored = Frame(72, 52, (2, 2, Pattern), (10, 36, Pattern), (48, 36, Pattern));
        Equal("Second", tracker.Observe(restored, .75)!.SelectedOption);
    }

    internal static void TestFailedSearchKeepsRegions()
    {
        var result = new MenuStencilTracker(Catalog()).Observe(Frame(72, 40), 1)!;
        Check(!result.IsMatch && result.Phase == MenuStencilPhase.Searching, "Blank frame acquired a menu.");
        Equal(2, result.SelectorSearchRegions.Count);
        Check(result.SelectorSearchRegions.All(region => !region.IsSolved && region.OptionIdentifier == "fixture-option"), "Failed search lost candidate regions.");
    }

    internal static void TestSceneCheckpoint()
    {
        var variant = Variant(); var foreign = Variant(ForeignPattern);
        var common = new Anchor("shared-probe", "Shared", new(2, 2, 4, 4), null, [variant], variant);
        var leftAnchor = new Anchor("audio-only", "Audio only", new(20, 2, 4, 4), null, [foreign], foreign);
        var rightAnchor = new Anchor("video-only", "Video only", new(48, 2, 4, 4), null, [foreign], foreign);
        var a = new Scene("audio", 72, 40, common, [common, leftAnchor], [], new Dictionary<string, IReadOnlyList<Variant>>(), null);
        var b = new Scene("video", 72, 40, common, [common, rightAnchor], [], new Dictionary<string, IReadOnlyList<Variant>>(), null);
        var tracker = new MenuStencilTracker(new([a, b], 72, 40, 0, 40, []));
        Equal("audio", tracker.Observe(Frame(72, 40, (2, 2, Pattern), (20, 2, ForeignPattern)), 0)!.ContextIdentifier);
        MenuStencilResult? result = null;
        for (var index = 1; index <= MenuStencilTracker.ValidationInterval; index++) result = tracker.Observe(Frame(72, 40, (2, 2, Pattern), (48, 2, ForeignPattern)), index * .25);
        Check(result?.IsMatch == true && result.ContextIdentifier == "video", "Shared heading trapped the tracker in the old menu.");
    }

    internal static void TestLanguageChangeAndCatalogRefresh()
    {
        var en = Variant(Pattern, "en"); var fr = Variant(ForeignPattern, "fr");
        var probe = new Anchor("probe", "Probe", new(2, 2, 4, 4), null, [en, fr], en, [en, fr]);
        var row = new Anchor("row", "Row", new(30, 15, 4, 4), null, [en, fr], en, [en, fr]);
        var scene = new Scene("audio", 72, 40, probe, [probe, row], [], new Dictionary<string, IReadOnlyList<Variant>>(), null);
        var catalog = new MenuStencilCatalog([scene], 72, 40, 0, 40, []); var tracker = new MenuStencilTracker(catalog);
        Equal("en", tracker.Observe(Frame(72, 40, (2, 2, Pattern), (30, 15, Pattern)), 0)!.LanguageIdentifier);
        var french = Frame(72, 40, (2, 2, ForeignPattern), (30, 15, ForeignPattern)); MenuStencilResult? result = null;
        for (var index = 1; index <= 20; index++) result = tracker.Observe(french, index * .25);
        Check(result?.IsMatch == true && result.LanguageIdentifier == "fr", "Old-language evidence blocked language reacquisition.");
        tracker.ReplaceCatalog(catalog); var refreshed = tracker.Observe(french, 6)!;
        var fresh = new MenuStencilTracker(catalog).Observe(french, 6)!;
        Equal("fr", refreshed.LanguageIdentifier);
        Check(refreshed.ComparisonCount < fresh.ComparisonCount, "Catalog refresh forgot the running game's language.");
    }

    internal static void TestTwoColumnRowBand()
    {
        var catalog = Catalog(); var scene = catalog.Scenes[0]; var first = scene.Options[0];
        var second = first with { ClassIdentifier = "second-column", Name = "Second Column", Bounds = new(95, 21, 24, 4),
            LeftSelector = new(new(83, 21, 4, 4), [Variant()]), RightSelector = new(new(121, 21, 4, 4), [Variant()]) };
        catalog = catalog with { ReferenceWidth = 140, Scenes = [scene with { ReferenceWidth = 140, Options = [first, second] }] };
        var result = new MenuStencilTracker(catalog).Observe(Frame(140, 40, (2, 2, Pattern), (10, 22, Pattern), (48, 22, Pattern)), 1)!;
        Equal("Fixture Option", result.SelectedOption);
        Equal(4, result.SelectorSearchRegions.Count); Equal(2, result.SelectorSearchRegions.Count(region => region.IsSolved));
    }

    internal static void TestModalTakesParentOwnership()
    {
        var variant = Variant(); var foreign = Variant(ForeignPattern);
        var parentProbe = new Anchor("profile-title", "Profile", new(2, 2, 4, 4), null, [variant], variant);
        var parentRow = new Anchor("profile-row", "Profile row", new(20, 2, 4, 4), null, [foreign], foreign);
        var slots = new[] { new Option("select-profile.slot-1", "Slot 1", new(22, 20, 24, 4), null, null, null),
            new Option("select-profile.slot-2", "Slot 2", new(22, 40, 24, 4), null, null, null) };
        var parent = new Scene("select-profile", 72, 60, parentProbe, [parentProbe, parentRow], slots, new Dictionary<string, IReadOnlyList<Variant>>(), null);
        var modalProbe = new Anchor("clear-title", "Clear Save", new(40, 12, 4, 4), null, [foreign], foreign);
        var modal = new Scene("clear-save", 72, 60, modalProbe, [modalProbe], [], new Dictionary<string, IReadOnlyList<Variant>>(), null);
        var tracker = new MenuStencilTracker(new([parent, modal], 72, 60, 0, 60, []));
        Equal("select-profile", tracker.Observe(Frame(72, 60, (2, 2, Pattern), (20, 2, ForeignPattern)), 0)!.ContextIdentifier);
        var result = tracker.Observe(Frame(72, 60, (2, 2, Pattern), (20, 2, ForeignPattern), (40, 32, ForeignPattern)), .25)!;
        Check(result.IsMatch && result.ContextIdentifier == "clear-save", "Visible parent heading prevented modal takeover.");
        Near(32, result.Anchors.Single().Rect.Y);
    }

    internal static void TestDialogPoseUsesStableRows()
    {
        var variant = Variant(); var foreign = Variant(ForeignPattern);
        var title = new Anchor("quit-game.quit-game", "Quit Game", new(2, 2, 4, 4), null, [variant], variant);
        var yes = new Anchor("shared.yes", "Yes", new(24, 20, 4, 4), null, [foreign], foreign);
        var no = new Anchor("shared.no", "No", new(24, 36, 4, 4), null, [foreign], foreign);
        Option Row(string id, string name, int y) => new(id, name, new(22, y, 24, 4), null,
            new(new(10, y, 4, 4), [variant]), new(new(48, y, 4, 4), [variant]));
        var scene = new Scene("quit-game", 72, 60, title, [title, yes, no], [Row("shared.yes", "Yes", 20), Row("shared.no", "No", 36)],
            new Dictionary<string, IReadOnlyList<Variant>>(), null);
        var tracker = new MenuStencilTracker(new([scene], 72, 60, 0, 60, []));
        foreach (var titlePosition in new[] { (X: 5, Y: 4), (X: 6, Y: 1) })
        {
            var result = tracker.Observe(Frame(72, 60, (titlePosition.X, titlePosition.Y, Pattern), (25, 23, ForeignPattern),
                (25, 39, ForeignPattern), (11, 39, Pattern), (49, 39, Pattern)), titlePosition.X) !;
            Check(result.IsMatch && result.SelectedOption == "No", "Dialog failed stable Yes/No selection.");
            foreach (var region in result.SelectorSearchRegions.Where(region => region.OptionIdentifier == "shared.yes"))
            { Near(25, region.Rect.MidY); Near(region.Side == MenuSelectorSide.Left ? 13 : 51, region.Rect.MidX); }
        }
    }

    internal static void TestStableSelectorMatchPersistsProductCalibration()
    {
        using var fixture = new CalibrationFixture();
        var tracker = fixture.Tracker();
        for (var index = 0; index <= 9; index++)
            Equal("Fixture Option", tracker.Observe(Image(10, 20, 48, 20), index * .25)!.SelectedOption);

        var persisted = MenuStencilCalibration.Load(fixture.Output)!;
        var selector = persisted.FindScene("main-title")!.Selectors.Single(item => item.SelectedIdentifier == "fixture-option");
        Check(selector.Measured, "Stable selector evidence was not marked measured.");
        Equal(1, selector.EvidenceCount);
        Near(10, selector.LeftRect.X); Near(20, selector.LeftRect.Y);
        Near(48, selector.RightRect.X); Near(20, selector.RightRect.Y);
    }

    internal static void TestIntermittentSelectorMatchesDoNotPersistCalibration()
    {
        using var fixture = new CalibrationFixture();
        var catalog = ThreeRowCatalog(2);
        var tracker = fixture.Tracker(catalog);
        for (var index = 0; index < 20; index++)
        {
            var row = index % 2;
            var result = tracker.Observe(RowFrame(row, 2), index * .25)!;
            Equal(row == 0 ? "First" : "Second", result.SelectedOption);
        }
        Check(!File.Exists(fixture.Output), "Alternating selector rows incorrectly settled calibration.");
    }

    internal static void TestLiveSelectorTransitionsCannotRemainStuckOnPreviousRow()
    {
        var catalog = ThreeRowCatalog(3);
        var transitions = 0;
        for (var from = 0; from < 3; from++) for (var to = 0; to < 3; to++)
        {
            if (from == to) continue;
            var tracker = new MenuStencilTracker(catalog);
            _ = tracker.Observe(RowFrame(from, 3), 1);
            var result = tracker.Observe(RowFrame(to, 3), 2)!;
            Equal(new[] { "First", "Second", "Third" }[to], result.SelectedOption);
            transitions++;
        }
        Equal(6, transitions);
    }

    internal static void TestTrackingUsesFarFewerComparisonsWhenRequested()
    {
        var scenes = Enumerable.Range(0, 8).Select(index => SingleOptionScene(
            $"comparison-{index}", 4 + index * 18, Pattern, $"Choice {index}", 54, "en")).ToArray();
        var catalog = new MenuStencilCatalog(scenes, 160, 72, 0, 72, []);
        var frame = Frame(160, 72, (4 + 6 * 18, 2, Pattern), (18, 54, Pattern), (74, 54, Pattern));
        var tracker = new MenuStencilTracker(catalog);
        var acquired = tracker.Observe(frame, 1)!;
        var tracked = tracker.Observe(frame, 2)!;
        Check(acquired.IsMatch && tracked.IsMatch, "Comparison fixture did not remain tracked.");
        Equal("comparison-6", tracked.ContextIdentifier);
        Check(tracked.ComparisonCount < acquired.ComparisonCount,
            $"Tracked scan used {tracked.ComparisonCount} comparisons after acquisition used {acquired.ComparisonCount}.");
        for (var index = 0; index < 24; index++) Check(tracker.Observe(frame, index + 3)!.IsMatch, "Repeated tracking lost its scene.");
    }

    internal static void TestTrackerReacquiresOnlyAfterTrackedProbeDisappearsWhenRequested()
    {
        var (catalog, optionsFrame, audioFrame) = DistinctSceneFixture();
        var tracker = new MenuStencilTracker(catalog);
        var acquired = tracker.Observe(optionsFrame, 1)!;
        var tracked = tracker.Observe(optionsFrame, 2)!;
        var grace = tracker.Observe(audioFrame, 3)!;
        var reacquired = tracker.Observe(audioFrame, 4)!;

        Equal("options", acquired.ContextIdentifier); Equal("options", tracked.ContextIdentifier);
        Equal("options", grace.ContextIdentifier); Equal("audio", reacquired.ContextIdentifier);
        Equal(MenuStencilPhase.Searching, reacquired.Phase);
        Check(tracked.ComparisonCount < reacquired.ComparisonCount, "Reacquisition did not require broader search.");
        Check(reacquired.Anchors.All(anchor => anchor.Reference?.IsValid == true), "Reacquired anchors lost valid references.");
        Check(reacquired.SelectorSearchRegions.Count > 0 && reacquired.SelectorSearchRegions.Count(region => region.IsSolved) == 2,
            "Reacquired selector regions were not solved bilaterally.");
        Equal("Master Volume", reacquired.SelectedOption);
    }

    internal static void TestTrackedSceneYieldsToStrongerValidatedSceneProbe()
    {
        var fixture = SharedProbeFixture([("audio", ForeignPattern, "Master Volume"), ("video", ThirdPattern, "Resolution")]);
        var tracker = new MenuStencilTracker(fixture.Catalog);
        Equal("audio", tracker.Observe(fixture.Frames[0], 0)!.ContextIdentifier);
        MenuStencilResult? result = null;
        for (var frame = 1; frame <= MenuStencilTracker.ValidationInterval; frame++)
            result = tracker.Observe(fixture.Frames[1], frame * .25);
        Check(result?.IsMatch == true && result.ContextIdentifier == "video" && result.SelectedOption == "Resolution",
            "Validated challenger did not displace the tracked scene.");
    }

    internal static void TestTrackedSceneCheckpointReleasesAcrossWeakProbeScreens()
    {
        var fixture = SharedProbeFixture([
            ("options", ForeignPattern, "Back"),
            ("game-options", ThirdPattern, "Language"),
            ("video-advanced-settings", Pattern.Reverse().ToArray(), "Particle Effects")]);
        for (var from = 0; from < fixture.Frames.Count; from++) for (var to = 0; to < fixture.Frames.Count; to++)
        {
            if (from == to) continue;
            var tracker = new MenuStencilTracker(fixture.Catalog);
            Equal(fixture.Catalog.Scenes[from].ContextIdentifier, tracker.Observe(fixture.Frames[from], 0)!.ContextIdentifier);
            MenuStencilResult? result = null;
            for (var frame = 1; frame <= MenuStencilTracker.ValidationInterval; frame++)
                result = tracker.Observe(fixture.Frames[to], frame * .25);
            Equal(fixture.Catalog.Scenes[to].ContextIdentifier, result!.ContextIdentifier);
            Equal(fixture.Catalog.Scenes[to].Options[0].Name, result.SelectedOption);
        }
    }

    internal static void TestLostSceneReacquiresUsingTrackedLanguageWithinGrace()
    {
        var fixture = LocalizedSceneFixture();
        var tracker = new MenuStencilTracker(fixture.Catalog);
        var primed = tracker.Observe(fixture.OptionsEnglish, 1)!;
        Equal("en", primed.LanguageIdentifier);
        Equal("options", tracker.Observe(fixture.AudioEnglish, 2)!.ContextIdentifier);
        var reacquired = tracker.Observe(fixture.AudioEnglish, 3)!;
        Check(reacquired.IsMatch, "Known-language reacquisition failed.");
        Equal("audio", reacquired.ContextIdentifier); Equal("Master Volume", reacquired.SelectedOption); Equal("en", reacquired.LanguageIdentifier);
        var fresh = new MenuStencilTracker(fixture.Catalog).Observe(fixture.AudioEnglish, 3)!;
        Check(reacquired.ComparisonCount < fresh.ComparisonCount, "Tracked language did not narrow reacquisition work.");
    }

    internal static void TestCaptureResetPreservesLanguageForNextScene()
    {
        var fixture = LocalizedSceneFixture();
        var tracker = new MenuStencilTracker(fixture.Catalog);
        Equal("en", tracker.Observe(fixture.OptionsEnglish, 1)!.LanguageIdentifier);
        tracker.Reset(preservingLanguage: true);
        var reacquired = tracker.Observe(fixture.AudioEnglish, 2)!;
        Check(reacquired.IsMatch, "Preserved-language reset failed to acquire the next scene.");
        Equal("audio", reacquired.ContextIdentifier); Equal("en", reacquired.LanguageIdentifier);
    }

    internal static void TestFailedTransitionFrameDoesNotDiscardTrackedLanguage()
    {
        var fixture = LocalizedSceneFixture();
        var tracker = new MenuStencilTracker(fixture.Catalog);
        Equal("en", tracker.Observe(fixture.OptionsEnglish, 1)!.LanguageIdentifier);
        _ = tracker.Observe(Frame(96, 64), 2);
        var failed = tracker.Observe(Frame(96, 64), 3);
        Check(failed?.IsMatch != true, "Blank transition frame remained a menu match.");
        var reacquired = tracker.Observe(fixture.AudioEnglish, 4)!;
        Check(reacquired.IsMatch, "Tracker failed to recover after blank transition frames.");
        Equal("audio", reacquired.ContextIdentifier); Equal("en", reacquired.LanguageIdentifier);
    }

    internal static void TestLanguageChangeReleasesTrackedForeignAdvancedScene()
    {
        var fixture = LanguageChangeFixture();
        var tracker = new MenuStencilTracker(fixture.Catalog);
        var primed = tracker.Observe(fixture.AdvancedChinese, 1)!;
        Equal("video-advanced-settings", primed.ContextIdentifier); Equal("zh-Hans", primed.LanguageIdentifier);
        for (var frame = 1; frame <= 16; frame++) _ = tracker.Observe(fixture.QuitChinese, 1 + frame / 30.0);
        MenuStencilResult? italian = null;
        for (var frame = 1; frame <= 24; frame++) italian = tracker.Observe(fixture.GameOptionsItalian, 2 + frame / 30.0);
        Check(italian?.IsMatch == true, "Italian menu was not released from the foreign advanced scene.");
        Equal("game-options", italian!.ContextIdentifier); Equal("Language", italian.SelectedOption); Equal("it", italian.LanguageIdentifier);

        var japaneseTracker = new MenuStencilTracker(fixture.Catalog);
        _ = japaneseTracker.Observe(fixture.AdvancedChinese, 1);
        MenuStencilResult? japanese = null;
        for (var frame = 1; frame <= 24; frame++) japanese = japaneseTracker.Observe(fixture.GameOptionsJapanese, 2 + frame / 30.0);
        Check(japanese?.IsMatch == true, "Japanese menu was not released from the foreign advanced scene.");
        Equal("game-options", japanese!.ContextIdentifier); Equal("Language", japanese.SelectedOption); Equal("ja", japanese.LanguageIdentifier);
    }

    internal static void TestVideoAdvancedReleasesToParentMenusAcrossLiveFrames()
    {
        var advanced = SingleOptionScene("video-advanced-settings", 4, Pattern, "Particle Effects", 52, "en");
        var options = SingleOptionScene("options", 28, ForeignPattern, "Back", 36, "en");
        var gameOptions = SingleOptionScene("game-options", 52, ThirdPattern, "Language", 20, "en");
        var catalog = new MenuStencilCatalog([advanced, options, gameOptions], 96, 64, 0, 64, []);
        var prime = Frame(96, 64, (4, 2, Pattern), (18, 52, Pattern), (74, 52, Pattern));
        var targets = new[]
        {
            (Frame: Frame(96, 64, (28, 2, ForeignPattern), (18, 36, Pattern), (74, 36, Pattern)), Context: "options", Selection: "Back"),
            (Frame: Frame(96, 64, (52, 2, ThirdPattern), (18, 20, Pattern), (74, 20, Pattern)), Context: "game-options", Selection: "Language")
        };
        foreach (var target in targets)
        {
            var tracker = new MenuStencilTracker(catalog);
            Equal("video-advanced-settings", tracker.Observe(prime, 1)!.ContextIdentifier);
            MenuStencilResult? result = null;
            for (var frame = 1; frame <= 16; frame++) result = tracker.Observe(target.Frame, 1 + frame / 60.0);
            Check(result?.IsMatch == true, $"{target.Context} did not match after advanced-video release.");
            Equal(target.Context, result!.ContextIdentifier); Equal(target.Selection, result.SelectedOption);
        }
    }

    private static MenuStencilCatalog Catalog()
    {
        var variant = Variant(); var probe = new Anchor("fixture-probe", "Fixture Probe", new(2, 2, 4, 4), null, [variant], variant);
        var option = new Option("fixture-option", "Fixture Option", new(22, 20, 24, 4), null,
            new(new(10, 20, 4, 4), [variant]), new(new(48, 20, 4, 4), [variant]));
        return new([new("main-title", 72, 40, probe, [probe], [option], new Dictionary<string, IReadOnlyList<Variant>>(), null)], 72, 40, 0, 40, []);
    }
    private static MenuStencilCatalog ThreeRowCatalog(int count)
    {
        var variant = Variant();
        var probe = new Anchor("fixture-probe", "Fixture Probe", new(2, 2, 4, 4), null, [variant], variant);
        var names = new[] { "First", "Second", "Third" };
        var options = Enumerable.Range(0, count).Select(index => RowOption($"row-{index}", names[index], 16 + index * 16)).ToArray();
        var height = 24 + count * 16;
        return new([new("main-title", 72, height, probe, [probe], options, new Dictionary<string, IReadOnlyList<Variant>>(), null)], 72, height, 0, height, []);
    }
    private static Option RowOption(string identifier, string name, int y)
    {
        var variant = Variant();
        return new(identifier, name, new(22, y, 24, 4), null,
            new(new(10, y, 4, 4), [variant]), new(new(48, y, 4, 4), [variant]));
    }
    private static BgraFrame RowFrame(int row, int rowCount)
    {
        var height = 24 + rowCount * 16; var y = 16 + row * 16;
        return Frame(72, height, (2, 2, Pattern), (10, y, Pattern), (48, y, Pattern));
    }
    private static Scene SingleOptionScene(string context, int probeX, byte[] probePattern, string optionName, int rowY,
        string? language, IReadOnlyList<Variant>? probeVariants = null, int referenceWidth = 96, int referenceHeight = 64)
    {
        var variants = probeVariants ?? [Variant(probePattern, language)];
        var probe = new Anchor(context + ".probe", context + " Probe", new(probeX, 2, 4, 4), null, variants, variants[0], variants);
        var selector = Variant();
        var option = new Option(context + ".option", optionName, new(34, rowY, 24, 4), null,
            new(new(18, rowY, 4, 4), [selector]), new(new(74, rowY, 4, 4), [selector]));
        return new(context, referenceWidth, referenceHeight, probe, [probe], [option], new Dictionary<string, IReadOnlyList<Variant>>(), null);
    }
    private static (MenuStencilCatalog Catalog, BgraFrame Options, BgraFrame Audio) DistinctSceneFixture()
    {
        var options = SingleOptionScene("options", 4, Pattern, "Back", 20, "en");
        var audio = SingleOptionScene("audio", 28, ForeignPattern, "Master Volume", 40, "en");
        var catalog = new MenuStencilCatalog([options, audio], 96, 64, 0, 64, []);
        return (catalog,
            Frame(96, 64, (4, 2, Pattern), (18, 20, Pattern), (74, 20, Pattern)),
            Frame(96, 64, (28, 2, ForeignPattern), (18, 40, Pattern), (74, 40, Pattern)));
    }
    private static (MenuStencilCatalog Catalog, IReadOnlyList<BgraFrame> Frames) SharedProbeFixture(
        IReadOnlyList<(string Context, byte[] UniquePattern, string Option)> definitions)
    {
        var commonVariant = Variant();
        var scenes = definitions.Select((definition, index) =>
        {
            var common = new Anchor("shared-heading", "Shared Heading", new(2, 2, 4, 4), null, [commonVariant], commonVariant);
            var uniqueVariant = Variant(definition.UniquePattern);
            var unique = new Anchor(definition.Context + ".unique", definition.Context + " Unique", new(34, 2, 4, 4), null,
                [uniqueVariant], uniqueVariant);
            var option = RowOption(definition.Context + ".option", definition.Option, 20 + index * 16);
            return new Scene(definition.Context, 96, 72, common, [common, unique], [option],
                new Dictionary<string, IReadOnlyList<Variant>>(), null);
        }).ToArray();
        var frames = definitions.Select((definition, index) => Frame(96, 72, (2, 2, Pattern), (34, 2, definition.UniquePattern),
            (10, 20 + index * 16, Pattern), (48, 20 + index * 16, Pattern))).ToArray();
        return (new(scenes, 96, 72, 0, 72, []), frames);
    }
    private sealed record LocalizedFixture(MenuStencilCatalog Catalog, BgraFrame OptionsEnglish, BgraFrame AudioEnglish);
    private static LocalizedFixture LocalizedSceneFixture()
    {
        IReadOnlyList<Variant> Languages() => [Variant(Pattern, "en"), Variant(ForeignPattern, "fr")];
        var options = SingleOptionScene("options", 4, Pattern, "Back", 20, "en", Languages());
        var audio = SingleOptionScene("audio", 28, Pattern, "Master Volume", 40, "en", Languages());
        var catalog = new MenuStencilCatalog([options, audio], 96, 64, 0, 64, []);
        return new(catalog,
            Frame(96, 64, (4, 2, Pattern), (18, 20, Pattern), (74, 20, Pattern)),
            Frame(96, 64, (28, 2, Pattern), (18, 40, Pattern), (74, 40, Pattern)));
    }
    private sealed record LanguageFixture(MenuStencilCatalog Catalog, BgraFrame AdvancedChinese, BgraFrame QuitChinese,
        BgraFrame GameOptionsItalian, BgraFrame GameOptionsJapanese);
    private static LanguageFixture LanguageChangeFixture()
    {
        IReadOnlyList<Variant> Languages() => [Variant(ForeignPattern, "zh-Hans"), Variant(Pattern, "it"), Variant(ThirdPattern, "ja")];
        var advanced = SingleOptionScene("video-advanced-settings", 4, ForeignPattern, "Particle Effects", 52, "zh-Hans", Languages());
        var quit = SingleOptionScene("quit-game", 28, ForeignPattern, "Yes", 36, "zh-Hans", Languages());
        var gameOptions = SingleOptionScene("game-options", 52, Pattern, "Language", 20, "it", Languages());
        var catalog = new MenuStencilCatalog([advanced, quit, gameOptions], 96, 64, 0, 64, []);
        return new(catalog,
            Frame(96, 64, (4, 2, ForeignPattern), (18, 52, Pattern), (74, 52, Pattern)),
            Frame(96, 64, (28, 2, ForeignPattern), (18, 36, Pattern), (74, 36, Pattern)),
            Frame(96, 64, (52, 2, Pattern), (18, 20, Pattern), (74, 20, Pattern)),
            Frame(96, 64, (52, 2, ThirdPattern), (18, 20, Pattern), (74, 20, Pattern)));
    }
    private static Variant Variant(byte[]? values = null, string? language = null) => MakeVariant("fixture-selector", new(4, 4, (values ?? Pattern).SelectMany(value => new[] { value, value, value }).ToArray()), language: language);
    private static BgraFrame Image(int leftX, int leftY, int rightX, int rightY) => Frame(72, 40, (2, 2, Pattern), (leftX, leftY, Pattern), (rightX, rightY, Pattern));
    private static BgraFrame Frame(int width, int height, params (int X, int Y, byte[] Pixels)[] stamps)
    {
        var bytes = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++) { bytes[pixel * 4] = 62; bytes[pixel * 4 + 1] = 24; bytes[pixel * 4 + 2] = 12; bytes[pixel * 4 + 3] = 255; }
        foreach (var stamp in stamps) for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++)
        { var offset = ((stamp.Y + y) * width + stamp.X + x) * 4; var value = stamp.Pixels[y * 4 + x]; bytes[offset] = value; bytes[offset + 1] = value; bytes[offset + 2] = value; }
        return new(width, height, width * 4, bytes, 0, Now);
    }
    private static BgraFrame Scale2(BgraFrame image)
    {
        var width = image.Width * 2; var height = image.Height * 2; var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) image.Row(y / 2).Slice(x / 2 * 4, 4).CopyTo(bytes.AsSpan((y * width + x) * 4, 4));
        return new(width, height, width * 4, bytes, 0, Now);
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Near(double expected, double actual) { if (Math.Abs(expected - actual) > .000001) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class CalibrationFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("hkv-menu-tracker-");
        internal string Output => Path.Combine(directory.FullName, MenuStencilCalibration.Filename);
        internal string Mirror => Path.Combine(directory.FullName, "mirror.json");
        internal MenuStencilTracker Tracker(MenuStencilCatalog? catalog = null) => new(catalog ?? Catalog(), calibration:
            new(Output, Mirror, new(1, 72, 40, Now, [])));
        public void Dispose() => directory.Delete(recursive: true);
    }
}
