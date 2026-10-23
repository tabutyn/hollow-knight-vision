using HollowKnightVision.Windows.Core;

internal static class RecentFrameTests
{
    private static void Check(bool condition, string message = "Recent frame assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static BgraFrame Image(long id) => new(2, 2, 8,
        Enumerable.Range(0, 16).Select(index => (byte)(index % 4 == 3 ? 255 : 32)).ToArray(),
        id, DateTimeOffset.UnixEpoch.AddSeconds(id));
    private static RecentCapturedFrame Frame(long id, long generation = 1, params ObjectDetection[] detections) =>
        new(generation, id, Image(id), detections);
    private static ObjectDetection Detection(string identifier, long id = 8, double x = .2) =>
        new(identifier, new(x, .2, .1, .1), .9f) { SourceFrameIdentifier = id, ModelVersion = new(1, 0) };
    private static LabelingExampleAnnotation Box(string identifier, bool negative = false) =>
        new(Guid.NewGuid(), identifier, .1, .2, .1, .1, negative);
    private static LabelingExampleManifest Manifest(string context, params LabelingExampleAnnotation[] boxes) =>
        new(5, Guid.NewGuid(), Guid.NewGuid(), "image.png", 2, 2, Guid.NewGuid(), context, boxes,
            boxes.Select(box => box.ClassIdentifier).ToArray(), LabelingExampleCompletion.Draft, DateTimeOffset.UnixEpoch);

    public static void TestCapacityAndOrdering()
    {
        var buffer = new RecentCapturedFrameBuffer();
        for (var id = 1; id <= 70; id++) buffer.Append(Frame(id));
        Check(buffer.Capacity == 64 && buffer.Frames.Select(frame => frame.SourceFrameIdentifier)
            .SequenceEqual(Enumerable.Range(7, 64).Reverse().Select(id => (long)id)));
        var duplicate = Frame(10, 1, Detection("game.mana", 10)); buffer.Append(duplicate);
        Check(buffer.Frames.Count == 64 && buffer.Frames.First().Id == duplicate.Id
            && buffer.Frames.Count(frame => frame.Id == duplicate.Id) == 1, "Duplicate captures did not move to the front.");
        var clamped = new RecentCapturedFrameBuffer(0); clamped.Append(Frame(1)); clamped.Append(Frame(2));
        Check(clamped.Capacity == 1 && clamped.Frames.Single().SourceFrameIdentifier == 2);
    }

    public static void TestCaptureGeneration()
    {
        var buffer = new RecentCapturedFrameBuffer();
        buffer.Append(Frame(10)); buffer.Append(Frame(1, 2)); buffer.Append(Frame(11));
        Check(buffer.Frames.Single().Id == new RecentCapturedFrameIdentifier(2, 1));
        buffer.ReplaceDetections([Detection("game.mana", 1)], 1, 1);
        Check(buffer.Frames.Single().Detections.Count == 0, "Old session changed a reused frame ID.");
        buffer.RemoveAll(); Check(buffer.Frames.Count == 0);
        buffer.Append(Frame(1)); Check(buffer.Frames.Single().CaptureGeneration == 1, "Clear retained a generation floor.");
    }

    public static void TestExactPredictionReplacement()
    {
        var buffer = new RecentCapturedFrameBuffer(2); var first = Frame(7);
        buffer.Append(first); buffer.Append(Frame(8)); var frozen = buffer.Frames;
        var replacement = new[] { Detection("game.mana", 7) };
        buffer.ReplaceDetections(replacement, 1, 7);
        Check(buffer.Frames.Select(frame => frame.SourceFrameIdentifier).SequenceEqual(new long[] { 8, 7 }));
        Check(buffer.Frames[1].Detections.Single() == replacement[0] && ReferenceEquals(first.Image, buffer.Frames[1].Image));
        replacement[0] = Detection("game.health");
        Check(buffer.Frames[1].Detections.Single().ClassIdentifier == "game.mana" && frozen[1].Detections.Count == 0,
            "Prediction replacement mutated an already-open picker or retained the caller's array.");
        buffer.Append(Frame(9)); buffer.ReplaceDetections(replacement, 1, 7);
        Check(buffer.Frames.Count == 2 && buffer.Frames.All(frame => frame.SourceFrameIdentifier != 7), "Evicted frame resurrected.");
        var supplied = new[] { Detection("game.geo") }; buffer.Append(Frame(10, 1, supplied)); supplied[0] = Detection("game.mana");
        Check(buffer.Frames[0].Detections.Single().ClassIdentifier == "game.geo");
        buffer.ReplaceDetections([], 1, 10); Check(buffer.Frames[0].Detections.Count == 0, "Empty exact result retained false predictions.");
    }

    public static void TestCapturedRepeatedPredictions()
    {
        var captured = Frame(8, 1, Detection("main-title.hollow-knight-logo"),
            Detection("main-title.select-decoration", x: .4), Detection("main-title.select-decoration", x: .58),
            Detection("main-title.options", x: .46), Detection("game.mana", x: .05));
        var state = RecentCapturedFrameLabelingPolicy.StateForOpening(captured, "game", "game.health");
        Check(state.ContextIdentifier == "main-title" && state.SelectedClassIdentifier == "main-title.hollow-knight-logo");
        Check(state.Rectangles.Count == 5 && state.Rectangles.Count(box => box.ClassIdentifier == "shared.select-decoration") == 2);
        Check(state.Rectangles.Any(box => box.ClassIdentifier == "game.mana") && state.Rectangles.All(box => !box.IsHardNegative));
        Check(state.Rectangles.Select(box => box.Id).Distinct().Count() == 5);
        Check(state.Rectangles[1].X == .4 && state.Rectangles[2].X == .58, "Repeated instances lost their independent geometry.");
        Check(captured.Detections[1].ClassIdentifier == "main-title.select-decoration", "Opening mutated capture metadata.");
    }

    public static void TestCapturedFallbackAndPriority()
    {
        var empty = RecentCapturedFrameLabelingPolicy.StateForOpening(Frame(9), "game", "game.health");
        Check(empty.ContextIdentifier == "game" && empty.SelectedClassIdentifier == "game.health" && empty.Rectangles.Count == 0);
        var hud = new[] { Detection("game.health"), Detection("game.mana"), Detection("game.geo") };
        LabelingFrameOpenState Open(params ObjectDetection[] detections) =>
            RecentCapturedFrameLabelingPolicy.StateForOpening(Frame(9, 1, detections), "options", "options.audio");
        Check(Open(hud).ContextIdentifier == "game");
        Check(Open(hud[..2]).ContextIdentifier == "options", "Partial HUD overrode the current screen.");
        Check(Open(hud[..2]).SelectedClassIdentifier == "shared.audio", "Fallback selection was not canonicalized.");
        Check(Open([.. hud, Detection("select-profile.heading")]).ContextIdentifier == "select-profile");
        Check(Open([.. hud, Detection("select-profile.heading"), Detection("main-title.hollow-knight-logo")]).ContextIdentifier == "main-title");
        var custom = RecentCapturedFrameLabelingPolicy.StateForOpening(Frame(9, 1,
            Detection("custom.bench"), Detection("not-a-known-object")), "game", "game.health", ["custom.bench"]);
        Check(custom.SelectedClassIdentifier == "custom.bench" && custom.Rectangles.Single().ClassIdentifier == "custom.bench");
        Check(Open(Detection("not-a-known-object")).Rectangles.Count == 0);
    }

    public static void TestSavedScreenOwnsContext()
    {
        foreach (var context in LabelingContractCatalog.ScreenContexts.Where(context => context.Identifier != "game"))
        {
            var first = context.Labels.First().Identifier; var box = Box(first);
            var state = LabelingFrameOpeningPolicy.StateForOpening(Manifest(context.Identifier, box), "game.playable-knight")!;
            Check(state.ContextIdentifier == context.Identifier && state.SelectedClassIdentifier == LabelingClassIdentity.CanonicalIdentifier(first));
            Check(state.Rectangles.Single().Id == box.Id, "Opening a stencil discarded its rectangle identity.");
        }
        Check(LabelingFrameOpeningPolicy.StateForOpening(Manifest("unsupported-context")) is null);
    }

    public static void TestSavedObjectSelection()
    {
        Check(LabelingFrameOpeningPolicy.ClassIdentifierForOpening("game", ["game.mana", "game.playable-knight"]) == "game.mana");
        Check(LabelingFrameOpeningPolicy.ClassIdentifierForOpening("options", ["game.mana", "shared.audio"]) == "shared.audio");
        var options = Box("main-title.options");
        var state = LabelingFrameOpeningPolicy.StateForOpening(Manifest("main-title", options), "shared.select-decoration")!;
        Check(state.ContextIdentifier == "main-title" && state.SelectedClassIdentifier == "shared.select-decoration");
        Check(state.Rectangles.Single().Id == options.Id && state.Rectangles.Single().ClassIdentifier == "shared.options");
        var first = LabelingFrameOpeningPolicy.ContextClasses("options").First();
        Check(LabelingFrameOpeningPolicy.ClassIdentifierForOpening("options", [], "") == first);
    }

    public static void TestSavedRectanglesAndLegacyGroups()
    {
        var knight = Box("game.playable-knight"); var mana = Box("game.mana", negative: true);
        var state = LabelingFrameOpeningPolicy.StateForOpening(Manifest("game", knight, mana))!;
        Check(state.ContextIdentifier == "game" && state.SelectedClassIdentifier == "game.playable-knight"
            && state.Rectangles.SequenceEqual(new[] { knight, mana }));
        var enemy = Box("enemies.crawlid"); var world = Box("world.sign");
        state = LabelingFrameOpeningPolicy.StateForOpening(Manifest("world", enemy, world), "enemies.crawlid")!;
        Check(state.ContextIdentifier == "game" && state.SelectedClassIdentifier == "enemies.crawlid"
            && state.Rectangles.SequenceEqual(new[] { enemy, world }));
        state = LabelingFrameOpeningPolicy.StateForOpening(Manifest("shared", Box("main-title.options")))!;
        Check(state.ContextIdentifier == "main-title" && state.SelectedClassIdentifier == "shared.options");
        state = LabelingFrameOpeningPolicy.StateForOpening(Manifest("world", world), "custom.bench", ["custom.bench"])!;
        Check(state.ContextIdentifier == "game" && state.SelectedClassIdentifier == "custom.bench" && state.Rectangles.Single() == world);
    }

    public static void TestLabeledFrameEligibility()
    {
        var annotated = new SavedLabelingExample("unused", Manifest("game", Box("game.mana", negative: true)));
        var empty = new SavedLabelingExample("unused", Manifest("game") with { CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(3) });
        var known = new SavedLabelingExample("unused", Manifest("game") with {
            KnownClassIdentifiers = ["game.health"], CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(2) });
        var recent = RecentCapturedFrameLabelingPolicy.RecentLabeledFrames([annotated, empty, known]);
        Check(recent.Select(item => item.Id).SequenceEqual(new[] { known.Id, annotated.Id }),
            "Picker lost known-empty/negative training examples or included an unlabeled capture.");
    }

    public static void TestDraftPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-recent-draft-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LabelingExampleStore(root); var group = Guid.NewGuid(); var empty = Frame(1);
            var state = RecentCapturedFrameLabelingPolicy.StateForOpening(empty, "game", "game.health");
            Check(RecentCapturedFrameLabelingPolicy.SavePredictedDraft(store, empty, state, group, "fr") is null
                && !Directory.Exists(root), "Opening an empty capture wrote to disk.");
            var frame = Frame(2, 1, Detection("game.health", 2), Detection("game.health", 2, .4));
            state = RecentCapturedFrameLabelingPolicy.StateForOpening(frame, "game", "game.health");
            var saved = RecentCapturedFrameLabelingPolicy.SavePredictedDraft(store, frame, state, group, "fr")!;
            var loaded = store.Load().Single();
            Check(loaded.Id == saved.Id && loaded.Manifest.CaptureGroupIdentifier == group && loaded.Manifest.ResolvedLanguageIdentifier == "fr");
            Check(loaded.Manifest.Completion == LabelingExampleCompletion.Draft && loaded.Manifest.Annotations.SequenceEqual(state.Rectangles));
            Check(File.Exists(loaded.ImagePath) && loaded.Manifest.ImageWidth == 2 && loaded.Manifest.ImageHeight == 2);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
