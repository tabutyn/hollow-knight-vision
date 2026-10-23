using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class MenuCalibrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);
    private static readonly MenuSelectorBounds Pair = new(new(10, 20, 4, 4), new(48, 20, 4, 4));
    private static MenuStencilCalibration Empty() => new(1, 640, 360, Now, []);

    internal static void TestBundledCalibration()
    {
        var calibration = MenuStencilCalibration.LoadBundled() ?? throw new Exception("Mac calibration resource missing.");
        Equal(640, calibration.ReferenceWidth); Equal(360, calibration.ReferenceHeight);
        // The committed resource now includes Clear Save (two learned rows, no
        // generator states). Mac's old 23/104 assertions predate that addition.
        Equal(24, calibration.Scenes.Count);
        var selectors = calibration.Scenes.SelectMany(scene => scene.Selectors).ToArray();
        var states = calibration.Scenes.SelectMany(scene => scene.States).ToArray();
        Equal(106, selectors.Length); Equal(104, states.Length);
        Check(selectors.All(item => item.Measured) && states.All(item => item.Measured), "Unmeasured deployed selector.");
        Check(selectors.All(item => item.LeftRect.Width > 0 && item.RightRect.Width > 0), "Empty selector geometry.");
        Check(!selectors.Any(item => item.SelectedIdentifier is "brightness.pointer" or "controller-advanced.mfi"), "Non-selector control included.");
        Check(calibration.Scenes.All(scene => scene.States.All(state => scene.Selectors.Any(selector => selector.SelectedIdentifier == state.SelectedIdentifier))), "State has no selector pair.");
        Equal(2, calibration.FindScene("clear-save")!.Selectors.Count);
        Equal(0, calibration.FindScene("clear-save")!.States.Count);
    }

    internal static void TestNewScene()
    {
        // Ensure this is genuinely the first learned Clear Save pair even though
        // current deployed JSON already contains that scene.
        var bundled = MenuStencilCalibration.LoadBundled()!;
        var original = bundled with { Scenes = bundled.Scenes.Where(scene => scene.ContextIdentifier != "clear-save").ToArray() };
        var left = new StencilRect(280, 100, 12, 11); var right = new StencilRect(336, 100, 12, 11);
        var updated = original.ReplacingSelector("clear-save", "shared.yes", left, right, now: Now);
        var scene = updated.FindScene("clear-save")!;
        Equal(1, scene.Selectors.Count); Equal("shared.yes", scene.Selectors[0].SelectedIdentifier);
        Equal(1, scene.Selectors[0].EvidenceCount); Equal(1, scene.Selectors[0].Placement("en")!.EvidenceCount);
        Equal(left, scene.Selectors[0].LeftRect.Bounds); Equal(right, scene.Selectors[0].RightRect.Bounds);
        Equal(0, scene.Anchors.Count); Equal(0, scene.States.Count); Equal(Now, updated.GeneratedAt);
        Check(original.FindScene("clear-save") is null, "Immutable source was changed.");
        Check(original.ReviewedCorrectionIdentifiers!.SequenceEqual(updated.ReviewedCorrectionIdentifiers!), "Correction IDs lost.");
        Check(updated.Scenes.Select(item => item.ContextIdentifier).SequenceEqual(updated.Scenes.Select(item => item.ContextIdentifier).Order(StringComparer.Ordinal)), "New scene insertion is not sorted.");
        var nonEnglish = Empty().ReplacingSelector("clear-save", "shared.no", left, right, "ja", Now);
        Equal(0, nonEnglish.FindScene("clear-save")!.Selectors[0].EvidenceCount);
        Equal(1, nonEnglish.FindScene("clear-save")!.Selectors[0].Placement("ja")!.EvidenceCount);
    }

    internal static void TestIndependentLanguages()
    {
        var original = MenuStencilCalibration.LoadBundled()!;
        var before = original.FindScene("options")!.Selectors.Single(item => item.SelectedIdentifier == "options.game");
        var russianLeft = new StencilRect(240, 100, 13, 11); var russianRight = new StencilRect(387, 100, 13, 11);
        var japaneseLeft = new StencilRect(274, 100, 13, 11); var japaneseRight = new StencilRect(353, 100, 13, 11);
        var updated = original.ReplacingSelector("options", "options.game", russianLeft, russianRight, "ru", Now)
            .ReplacingSelector("options", "options.game", japaneseLeft, japaneseRight, "ja", Now);
        var selector = updated.FindScene("options")!.Selectors.Single(item => item.SelectedIdentifier == "options.game");
        Equal(before.LeftRect, selector.LeftRect); Equal(before.RightRect, selector.RightRect); Equal(before.EvidenceCount, selector.EvidenceCount);
        Equal(russianLeft, selector.Placement("ru")!.LeftRect.Bounds); Equal(japaneseRight, selector.Placement("ja")!.RightRect.Bounds);
        Equal((before.Placement("ru")?.EvidenceCount ?? 0) + 1, selector.Placement("ru")!.EvidenceCount);
        Equal((before.Placement("ja")?.EvidenceCount ?? 0) + 1, selector.Placement("ja")!.EvidenceCount);
        Check(original.FindScene("options")!.States.SequenceEqual(updated.FindScene("options")!.States), "Localized update overwrote English generator states.");
        Check(MenuSelectorCalibrationTracker.PersistedBounds(Empty().ReplacingSelector("x", "y", Pair.Left, Pair.Right), "x", "y", "ru") is null, "English geometry masquerades as persisted Russian evidence.");
        var english = updated.ReplacingSelector("options", "options.game", Pair.Left, Pair.Right, now: Now).FindScene("options")!;
        var englishSelector = english.Selectors.Single(item => item.SelectedIdentifier == "options.game");
        Equal(before.EvidenceCount + 1, englishSelector.EvidenceCount);
        Equal((before.Placement("en")?.EvidenceCount ?? before.EvidenceCount) + 1, englishSelector.Placement("en")!.EvidenceCount);
        Equal(japaneseRight, englishSelector.Placement("ja")!.RightRect.Bounds);
        foreach (var oldState in original.FindScene("options")!.States)
        {
            var state = english.States.Single(item => item.SelectedIdentifier == oldState.SelectedIdentifier);
            Equal(oldState.SourceExampleIdentifier, state.SourceExampleIdentifier);
            for (var index = 0; index < oldState.Matches.Count; index++)
            {
                var prior = oldState.Matches[index]; var match = state.Matches[index];
                if (oldState.SelectedIdentifier != "options.game" || prior.Role is not ("selector-left" or "selector-right")) Equal(prior, match);
                else
                {
                    Equal(prior.Confidence, match.Confidence); Equal(0.0, match.CenterError); Check(match.Accepted, "Updated selector still rejected.");
                    Equal(prior.Role == "selector-left" ? Pair.Left : Pair.Right, match.Rect.Bounds);
                }
            }
        }
    }

    internal static void TestExplicitPaths()
    {
        using var fixture = new Fixture();
        Check(MenuStencilCalibration.ProjectMirrorPath([]) is null, "Implicit project write target.");
        Equal(fixture.Mirror, MenuStencilCalibration.ProjectMirrorPath(["Vision", "--menu-stencil-project-calibration=" + fixture.Mirror]));
        Check(MenuStencilCalibration.ProjectMirrorPath(["--menu-stencil-project-calibration=", "--menu-stencil-project-calibration=" + fixture.Mirror]) is null, "First explicit argument should determine mirror policy.");
        Check(MenuSelectorCalibrationTracker.ResolveCalibrationPath(true, null) is null && MenuSelectorCalibrationTracker.ResolveCalibrationPath(false, null) is null, "Implicit deployed calibration writes enabled.");
        Equal(fixture.Output, MenuSelectorCalibrationTracker.ResolveCalibrationPath(true, fixture.Output));
        Equal(Path.Combine(fixture.Root, "labeling-v1", MenuStencilCalibration.Filename), MenuStencilCalibration.WritablePath(Path.Combine(fixture.Root, "labeling-v1", "examples")));
        var disabled = new MenuSelectorCalibrationTracker(mirrorPath: fixture.Mirror, fallbackCalibration: Empty());
        Feed(disabled, Pair, 0); Equal("disabled", disabled.Diagnostic); Check(!File.Exists(fixture.Mirror), "Mirror-only input enabled implicit persistence.");
    }

    internal static void TestPortableSerialization()
    {
        using var fixture = new Fixture();
        var first = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"); var second = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");
        var value = Empty().ReplacingSelector("main-title", "fixture-option", Pair.Left, Pair.Right, now: Now.AddMilliseconds(123))
            .RecordingReviewedCorrections([second, first, first], Now.AddMilliseconds(123));
        value.Write(fixture.Output);
        var loaded = MenuStencilCalibration.Load(fixture.Output)!;
        Equal(Now, loaded.GeneratedAt); Check(loaded.ReviewedCorrectionIdentifiers!.SequenceEqual(new[] { first, second }), "UUIDs not deduplicated and sorted.");
        var json = JsonNode.Parse(File.ReadAllText(fixture.Output))!;
        Equal("2026-10-21T20:00:00Z", json["generatedAt"]!.GetValue<string>());
        var selector = json["scenes"]![0]!["selectors"]![0]!;
        Equal(4, selector["leftRect"]!.AsObject().Count); Check(selector["leftRect"]!["bounds"] is null, "Computed geometry leaked into portable JSON.");
        // Original schema-1 files did not contain either optional field.
        json.AsObject().Remove("reviewedCorrectionIdentifiers"); selector.AsObject().Remove("localizedPlacements");
        File.WriteAllText(fixture.Output, json.ToJsonString());
        loaded = MenuStencilCalibration.Load(fixture.Output)!;
        Check(loaded.ReviewedCorrectionIdentifiers is null && loaded.Scenes[0].Selectors[0].LocalizedPlacements is null, "Legacy optional fields not supported.");
        Equal(Pair, MenuSelectorCalibrationTracker.PersistedBounds(loaded, "main-title", "fixture-option", "en")!.Value);
        loaded.Write(fixture.Output); Check(!Directory.EnumerateFiles(fixture.Root, "*.tmp-*").Any(), "Atomic write leaked temporary files.");
        foreach (var mutate in new Action<JsonNode>[]
        {
            node => node["generatedAt"] = "not-a-date",
            node => node["generatedAt"] = 123,
            node => node["scenes"]![0]!["anchors"] = null,
            node => node["scenes"]![0]!["selectors"]![0] = null,
            node => node["scenes"]![0]!["selectors"]![0]!["leftRect"]!["x"] = null,
            node => node["scenes"]![0]!["selectors"]![0]!["selectedIdentifier"] = null,
            node => node["reviewedCorrectionIdentifiers"] = new JsonArray("not-a-uuid")
        })
        {
            var invalid = json.DeepClone(); mutate(invalid);
            File.WriteAllText(fixture.Output, invalid.ToJsonString());
            Check(MenuStencilCalibration.Load(fixture.Output) is null, "Invalid nested schema escaped corrupt-file handling.");
        }
        foreach (var corrupt in new[] { "stale", "{}", "{\"schemaVersion\":1}", "{\"schemaVersion\":9,\"referenceWidth\":640,\"referenceHeight\":360,\"generatedAt\":\"2026-10-21T20:00:00Z\",\"scenes\":[]}" })
        { File.WriteAllText(fixture.Output, corrupt); Check(MenuStencilCalibration.Load(fixture.Output) is null, "Corrupt or unsupported calibration accepted."); }
        json["scenes"]![0]!["selectors"]![0]!.AsObject().Remove("measured");
        File.WriteAllText(fixture.Output, json.ToJsonString()); Check(MenuStencilCalibration.Load(fixture.Output) is null, "Missing nested required field silently defaulted.");
        json["scenes"] = null;
        File.WriteAllText(fixture.Output, json.ToJsonString()); Check(MenuStencilCalibration.Load(fixture.Output) is null, "Null non-optional scenes accepted.");
        Equal(24, MenuStencilCalibration.LoadDefault(fixture.Output)!.Scenes.Count);
        Check(MenuStencilCalibration.Load(Path.Combine(fixture.Root, "absent.json")) is null, "Missing file should not load.");
    }

    internal static void TestStabilityAndRecalibration()
    {
        using var fixture = new Fixture();
        var tracker = fixture.Tracker();
        for (var frame = 0; frame < 120; frame++) tracker.RecordMatch("main-title", "fixture-option", "en", Pair, Pair, frame / 60.0, Now);
        Check(!File.Exists(fixture.Output), "High frame rate bypassed two-second stability window.");
        for (var frame = 120; frame <= 150; frame++) tracker.RecordMatch("main-title", "fixture-option", "en", Pair, Pair, frame / 60.0, Now);
        var first = MenuStencilCalibration.Load(fixture.Output)!;
        Equal(1, first.FindScene("main-title")!.Selectors[0].EvidenceCount);
        Check(File.ReadAllBytes(fixture.Output).SequenceEqual(File.ReadAllBytes(fixture.Mirror)), "Explicit mirror differs from persisted calibration.");
        var shifted = new MenuSelectorBounds(Pair.Left.Offset(3, 3), Pair.Right.Offset(3, 3));
        tracker.Reset(); Feed(tracker, shifted, 3);
        Equal(2, tracker.Calibration!.FindScene("main-title")!.Selectors[0].EvidenceCount);
        Equal(shifted, tracker.CurrentBounds("main-title", "fixture-option", "en", Pair)!.Value);
        tracker.Reset(); Equal(0, tracker.PendingSampleCount);
        Equal(shifted, tracker.CurrentBounds("main-title", "fixture-option", "en", Pair)!.Value);
        var bytes = File.ReadAllBytes(fixture.Output);
        Feed(tracker, shifted, 6, now: Now.AddMinutes(1)); Equal("stored", tracker.Diagnostic);
        Check(bytes.SequenceEqual(File.ReadAllBytes(fixture.Output)), "Unchanged persisted pair was rewritten.");
    }

    internal static void TestUnstableAndDiscontinuousEvidence()
    {
        using var fixture = new Fixture(); var tracker = fixture.Tracker();
        for (var index = 0; index < 20; index++) tracker.RecordMatch("main-title", index % 2 == 0 ? "a" : "b", "en", Pair, Pair, index * .25, Now);
        Check(!File.Exists(fixture.Output), "Alternating selections were accumulated together.");
        tracker.Reset();
        for (var index = 0; index < 20; index++) tracker.RecordMatch("main-title", "fixture-option", index % 2 == 0 ? "ru" : "ja", Pair, Pair, index * .25, Now);
        Check(!File.Exists(fixture.Output), "Language hypotheses were accumulated together.");
        tracker.Reset();
        for (var index = 0; index < 20; index++)
        {
            var shifted = index % 2 == 0 ? Pair : new MenuSelectorBounds(Pair.Left.Offset(8, 0), Pair.Right.Offset(8, 0));
            tracker.RecordMatch("main-title", "fixture-option", "en", Pair, shifted, index * .25, Now);
        }
        Check(!File.Exists(fixture.Output) && tracker.Diagnostic!.StartsWith("unstable "), "Split position clusters were persisted.");
        tracker.RecordMatch("main-title", "fixture-option", "en", Pair, Pair, 10, Now); Equal(1, tracker.PendingSampleCount);
        tracker.RecordMatch("main-title", "fixture-option", "en", Pair, Pair, 9, Now); Equal(1, tracker.PendingSampleCount);
        tracker.RecordMatch("main-title", "fixture-option", "en", Pair, Pair, double.NaN, Now); Equal("invalid-sample", tracker.Diagnostic);
        var outside = new MenuSelectorBounds(Pair.Left.Offset(41, 0), Pair.Right);
        tracker.RecordMatch("main-title", "fixture-option", "en", Pair, outside, 9.25, Now); Equal("outside-recovery", tracker.Diagnostic);
        tracker.RecordMatch("main-title", "fixture-option", "en", null, Pair, 9.25, Now); Equal("missing-bounds", tracker.Diagnostic);
        Check(!File.Exists(fixture.Output), "Invalid/discontinuous samples caused a write.");
    }

    internal static void TestDominantClusterAndMissingPhases()
    {
        using var fixture = new Fixture(); var tracker = fixture.Tracker();
        for (var index = 0; index <= 10; index++)
        {
            var actual = index == 4 ? new MenuSelectorBounds(Pair.Left.Offset(8, 0), Pair.Right.Offset(8, 0))
                : new MenuSelectorBounds(Pair.Left.Offset(index % 3 == 0 ? 1 : 0, 0), Pair.Right.Offset(index % 3 == 0 ? 1 : 0, 0));
            tracker.RecordMatch("main-title", "fixture-option", "en", Pair, actual, index * .25, Now);
            if (index == 3) tracker.WaitingForMatch();
        }
        var loaded = MenuStencilCalibration.Load(fixture.Output) ?? throw new Exception("One animation outlier prevented stable calibration.");
        Equal(Pair, MenuSelectorCalibrationTracker.PersistedBounds(loaded, "main-title", "fixture-option", "en")!.Value);
        // Correct catalog geometry is not the same as an on-disk entry.
        using var stale = new Fixture(); File.WriteAllText(stale.Output, "stale");
        Feed(stale.Tracker(), Pair, 0);
        Check(MenuStencilCalibration.Load(stale.Output)!.FindScene("main-title") is not null, "Existing stale file hid an unpersisted selector.");
    }

    internal static void TestReviewedCorrections()
    {
        using var fixture = new Fixture(); var tracker = fixture.Tracker();
        var correction = new MenuSelectorCorrection("main-title", "fixture-option", "en", Pair.Left, Pair.Right, Guid.NewGuid());
        tracker.SynchronizeCorrections([correction], Now);
        Equal("imported 1 human corrections", tracker.Diagnostic);
        Check(tracker.Calibration!.ReviewedCorrectionIdentifiers!.Contains(correction.SourceExampleIdentifier), "Human correction was not recorded.");
        var shifted = new MenuSelectorBounds(Pair.Left.Offset(2, 1), Pair.Right.Offset(2, 1));
        Feed(tracker, shifted, 0, now: Now.AddSeconds(10));
        var reopened = fixture.Tracker(); reopened.SynchronizeCorrections([correction], Now.AddSeconds(20));
        Equal(shifted, MenuSelectorCalibrationTracker.PersistedBounds(reopened.Calibration!, "main-title", "fixture-option", "en")!.Value);
        var count = reopened.Calibration!.FindScene("main-title")!.Selectors[0].EvidenceCount;
        var sameGeometry = correction with { Left = shifted.Left, Right = shifted.Right, SourceExampleIdentifier = Guid.NewGuid() };
        reopened.SynchronizeCorrections([sameGeometry, sameGeometry], Now.AddSeconds(30));
        Equal(count, reopened.Calibration!.FindScene("main-title")!.Selectors[0].EvidenceCount);
        Equal(2, reopened.Calibration.ReviewedCorrectionIdentifiers!.Count);
        Check(File.ReadAllBytes(fixture.Output).SequenceEqual(File.ReadAllBytes(fixture.Mirror)), "Correction mirror not updated.");
    }

    internal static void TestWriteFailureRecovery()
    {
        using var fixture = new Fixture(); var original = Empty(); original.Write(fixture.Output);
        var bytes = File.ReadAllBytes(fixture.Output); var tracker = fixture.Tracker();
        using (var locked = new FileStream(fixture.Output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Feed(tracker, Pair, 0); Equal("write-failed", tracker.Diagnostic);
            Equal(0, tracker.Calibration!.Scenes.Count);
            Check(bytes.SequenceEqual(File.ReadAllBytes(fixture.Output)), "Failed atomic replacement damaged original JSON.");
            Check(!Directory.EnumerateFiles(fixture.Root, "*.tmp-*").Any(), "Failed write leaked a temporary file.");
        }
        tracker.RecordMatch("main-title", "fixture-option", "en", Pair, Pair, 2.25, Now);
        Check(tracker.Diagnostic!.StartsWith("saved ") && MenuStencilCalibration.Load(fixture.Output)!.Scenes.Count == 1, "Write failure did not recover on next stable sample.");
    }

    private static void Feed(MenuSelectorCalibrationTracker tracker, MenuSelectorBounds bounds, double start, DateTimeOffset? now = null)
    { for (var index = 0; index <= 8; index++) tracker.RecordMatch("main-title", "fixture-option", "en", Pair, bounds, start + index * .25, now ?? Now); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("hkv-menu-calibration-").FullName;
        internal string Output => Path.Combine(Root, MenuStencilCalibration.Filename);
        internal string Mirror => Path.Combine(Root, "mirror.json");
        internal MenuSelectorCalibrationTracker Tracker() => new(Output, Mirror, Empty());
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
