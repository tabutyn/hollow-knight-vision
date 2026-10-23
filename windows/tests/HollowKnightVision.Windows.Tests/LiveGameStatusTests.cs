using HollowKnightVision.Windows.Core;

internal static class LiveGameStatusTests
{
    private static void Check(bool condition, string message = "Live game status assertion failed.") { if (!condition) throw new Exception(message); }
    private static ObjectDetection Detection(string id, float confidence = .9f) => new(id, new(.2, .2, .2, .025), confidence);
    private static ObjectDetection[] Complete(string context) => LabelingContractCatalog.ScreenContexts.Single(item => item.Identifier == context)
        .Labels.Where(label => label.Identifier != "shared.select-decoration").Select(label => Detection(label.Identifier))
        .Concat(new[] { Detection("shared.select-decoration"), Detection("shared.select-decoration") }).ToArray();
    private static MenuStencilResult Menu(string context, string? selected = null) => new(context, true, .9, selected, [], [], [], 0, 0, 1, MenuStencilPhase.Tracking, 1, 1, null);
    private static HudStencilResult Hud(bool mana = true) => new([new(0, "full", default, .9), new(1, "full", default, .9)],
        mana ? new(0, "main", new(40, 20, 58, 40), .9) : null, [], [], [], default, false, 1);
    private static LiveGameStatus Game() => LiveGameStatusResolver.Resolve([], hudStencil: Hud())!;
    private static LiveGameStatus MenuStatus(string context, string? selected = null) => LiveGameStatusResolver.Resolve([], Menu(context, selected))!;

    public static void TestIgnoresMenuModelObjects()
    {
        Check(LiveGameStatusResolver.Resolve(Complete("main-title")) is null);
        var selected = Complete("main-title").Select(item => item.ClassIdentifier == "main-title.start-game"
            ? item with { Box = new(.4, .5, .2, .025) } : item.ClassIdentifier == "shared.select-decoration"
            ? item with { Box = new(.35, .5, .02, .025) } : item).ToArray();
        Check(LiveGameStatusResolver.Resolve(selected) is null);
        Check(LiveGameStatusResolver.Resolve([Detection("main-title.start-game"), Detection("main-title.select-decoration"), Detection("main-title.select-decoration")]) is null);
        Check(LiveGameStatusResolver.Resolve([Detection("shared.back"), Detection("shared.select-decoration"), Detection("shared.select-decoration")]) is null);
        Check(LiveGameStatusResolver.Resolve([Detection("main-title.hollow-knight-logo"), Detection("main-title.start-game"), Detection("shared.options")]) is null);
        Check(LiveGameStatusResolver.Resolve(Complete("options")) is null);
    }
    public static void TestMenuSelectionAndPriority()
    {
        Check(MenuStatus("main-title", "Options").DisplayText == "Main Title - Options");
        foreach (var name in new[] { "1", "4", "Clear Save", "Back" })
        { var status = MenuStatus("select-profile", name); Check(status.ContextIdentifier == "select-profile" && status.DisplayText == $"Select Profile - {name}"); }
        Check(LiveGameStatusResolver.Resolve([], Menu("select-profile"), Hud())?.DisplayText == "Select Profile");
        Check(LiveGameStatusResolver.Resolve([], Menu("main-title"), Hud())?.ContextIdentifier == "main-title");
        var stale = new[] { Detection("game.health"), Detection("game.mana"), Detection("game.playable-knight"), Detection("enemies.crawlid") };
        foreach (var (context, selection, display) in new[] { ("options", "Audio", "Options - Audio"), ("main-title", "Start Game", "Main Title - Start Game"), ("select-profile", "1.", "Select Profile - 1.") })
        { var status = LiveGameStatusResolver.Resolve(stale, Menu(context, selection)); Check(status?.ContextIdentifier == context && status.DisplayText == display); }
        foreach (var context in LabelingContractCatalog.ScreenContexts.Where(item => item.Identifier != "game"))
            Check(LiveGameStatusResolver.Resolve(stale, Menu(context.Identifier))?.ContextIdentifier == context.Identifier);
        Check((MenuStatus("main-title", "Options") with { SelectionPresentation = GameSelectionPresentation.SelectedSuffix }).DisplayText == "Main Title · Options selected");
    }
    public static void TestGameplayObjectEvidence()
    {
        Check(LiveGameStatusResolver.Resolve([], hudStencil: Hud(false))?.IsGameplay == true);
        var health = Detection("game.health"); var mana = Detection("game.mana");
        Check(LiveGameStatusResolver.Resolve([health]) is null && LiveGameStatusResolver.Resolve([health, mana]) is null);
        Check(LiveGameStatusResolver.Resolve([health, mana], hudStencil: Hud())?.IsGameplay == true);
        Check(SceneRegions.FromObjectDetections([health, mana], new(0, 0, 1000, 562)).Geo.IsEmpty);
        foreach (var pair in new[] { new[] { "game.playable-knight", "enemies.crawlid" }, new[] { "enemies.crawlid", "world.geo-deposit" }, new[] { "enemies.vengfly", "world.sign" } })
            Check(LiveGameStatusResolver.Resolve(pair.Select(id => Detection(id)).ToArray())?.IsGameplay == true);
        Check(LiveGameStatusResolver.Resolve([Detection("game.playable-knight")]) is null);
        Check(LiveGameStatusResolver.Resolve([Detection("game.playable-knight", .49f), Detection("enemies.crawlid", .49f)]) is null);
        Check(LiveGameStatusResolver.Resolve([Detection("game.playable-knight", .5f), Detection("enemies.crawlid", .5f)])?.IsGameplay == true);
        Check(LiveGameStatusResolver.Resolve([Detection("world.unknown"), Detection("world.unknown")]) is null);
    }
    public static void TestMissingCueGrace()
    {
        var tracker = new LiveGameStatusTracker();
        Check(tracker.Observe(MenuStatus("select-profile"), 1)?.ContextIdentifier == "select-profile");
        Check(tracker.Observe(null, 1.5)?.ContextIdentifier == "select-profile");
        Check(tracker.Observe(null, 1.9) is null);
        Check(tracker.Observe(MenuStatus("main-title"), 2)?.ContextIdentifier == "main-title");
        tracker.Reset(); Check(tracker.Current is null);
        tracker.Observe(MenuStatus("select-profile"), 0);
        Check(tracker.Observe(null, LiveGameStatusTracker.MissingCueGrace) is not null);
        Check(tracker.Observe(null, LiveGameStatusTracker.MissingCueGrace + .001) is null);
    }
    public static void TestGameplayLatch()
    {
        var tracker = new LiveGameStatusTracker();
        Check(tracker.Observe(Game(), 1)?.IsGameplay == true);
        Check(tracker.Observe(null, 2)?.IsGameplay == true && tracker.Observe(null, 100)?.IsGameplay == true);
        tracker.Reset(true); Check(tracker.Observe(null, 0)?.IsGameplay == true);
        Check(tracker.Observe(MenuStatus("main-title"), 1)?.ContextIdentifier == "main-title");
        Check(tracker.Observe(null, 2) is null);
        tracker.Observe(Game(), 3); Check(tracker.Observe(MenuStatus("select-profile"), 4)?.ContextIdentifier == "select-profile");
        Check(tracker.Observe(null, 5) is null);
        tracker.Observe(Game(), 6);
        var weak = LiveGameStatusResolver.Resolve([Detection("main-title.hollow-knight-logo", .49f)]);
        Check(weak is null && tracker.Observe(weak, 7)?.IsGameplay == true);
        tracker.Reset(); Check(tracker.Current is null);
        tracker.Observe(MenuStatus("options"), 1); tracker.Reset(true); Check(tracker.Current is null);
    }
    public static void TestClockOrdering()
    {
        var tracker = new LiveGameStatusTracker(); tracker.Observe(Game(), 10);
        foreach (var invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, 9d })
            Check(tracker.Observe(MenuStatus("main-title"), invalid)?.IsGameplay == true);
        Check(tracker.Observe(MenuStatus("main-title"), 10)?.ContextIdentifier == "main-title");
        tracker.Reset(); Check(tracker.Observe(MenuStatus("select-profile"), 0)?.ContextIdentifier == "select-profile");
    }
    public static void TestCreditsNavigation()
    {
        var credits = LiveGameStatusResolver.Resolve([], creditsLikely: true)!; var tracker = new LiveGameStatusTracker();
        Check(credits.ContextIdentifier == "credits" && tracker.Observe(credits, 1) is null);
        tracker.Observe(Game(), 2); Check(tracker.Observe(credits, 3)?.IsGameplay == true);
        tracker.Observe(MenuStatus("extras"), 4); Check(tracker.Observe(credits, 5)?.ContextIdentifier == "credits");
        Check(tracker.Observe(credits, 6)?.ContextIdentifier == "credits");
        Check(tracker.Observe(null, 7) is null && tracker.Observe(credits, 8) is null);
    }
    public static void TestInferenceAdmission()
    {
        Check(LiveGameStatusEvidencePolicy.ShouldRunObjectInference(null));
        Check(LiveGameStatusEvidencePolicy.ShouldRunObjectInference(Game()));
        Check(LiveGameStatusEvidencePolicy.ShouldRunObjectInference(LiveGameStatusResolver.Resolve([], creditsLikely: true)));
        Check(!LiveGameStatusEvidencePolicy.ShouldRunObjectInference(MenuStatus("main-title")));
    }
    public static void TestLabelSetScoring()
    {
        var complete = Complete("audio");
        LiveLabelSetScore Audio(IEnumerable<ObjectDetection> values) => LiveLabelSetScorer.Scores(values.ToArray()).Single(item => item.ContextIdentifier == "audio");
        var clean = Audio(complete); var noisy = Audio(complete.Append(Detection("game.health")));
        Check(clean.MatchedObjectCount == 8 && clean.DistinctiveObjectCount > 0 && clean.MissingObjectCount == 0 && clean.UnexpectedObjectCount == 0);
        Check(noisy.Score < clean.Score && noisy.UnexpectedObjectCount == 1);
        Check(Math.Abs(clean.Score - noisy.Score - LiveLabelSetScorer.EvidenceWeight("game.health")) < .0001);
        var missing = Audio(complete.Where(item => item.ClassIdentifier != "audio.music-volume"));
        var surplus = Audio(complete.Append(Detection("shared.select-decoration")));
        Check(missing.MatchedObjectCount == 7 && missing.MissingObjectCount == 1 && missing.Score < clean.Score);
        Check(surplus.UnexpectedObjectCount == 1 && surplus.Score < clean.Score);
        Check(Math.Abs(clean.Score - missing.Score - 1.2 * LiveLabelSetScorer.EvidenceWeight("audio.music-volume")) < .0001);
        Check(LiveLabelSetScorer.EvidenceWeight("main-title.start-game") > LiveLabelSetScorer.EvidenceWeight("shared.back"));
        Check(LiveLabelSetScorer.EvidenceWeight("main-title.hollow-knight-logo") > LiveLabelSetScorer.EvidenceWeight("shared.select-decoration"));
        Check(LiveLabelSetScorer.EvidenceWeight("main-title.options") == LiveLabelSetScorer.EvidenceWeight("shared.options"));
        Check(LiveLabelSetScorer.EvidenceWeight("world.unknown") == .25);
        Check(LiveLabelSetScorer.BestMatch(complete)?.ContextIdentifier == "audio");
        Check(LiveLabelSetScorer.BestMatch([]) is null && LiveLabelSetScorer.BestMatch([Detection("shared.back"), Detection("shared.select-decoration")]) is null);
    }
    public static void TestQuitIntent()
    {
        Check(QuitGameTerminationPolicy.RecognizesExitIntent("quit-game", "YES"));
        Check(!QuitGameTerminationPolicy.RecognizesExitIntent("quit-to-menu", "Yes"));
        Check(QuitGameTerminationPolicy.ShouldAwaitGameExit("quit-game", "Yes", "leftUp", true));
        foreach (var (context, selection, kind, accepted) in new[] { ("quit-game", "No", "leftUp", true), ("quit-game", "Yes", "leftDown", true), ("quit-game", "Yes", "leftUp", false) })
            Check(!QuitGameTerminationPolicy.ShouldAwaitGameExit(context, selection, kind, accepted));
        Check(QuitGameTerminationPolicy.ShouldAwaitGameExit("quit-game", "Yes", RecordedGameButton.Z, true, true));
        Check(!QuitGameTerminationPolicy.ShouldAwaitGameExit("quit-game", "Yes", RecordedGameButton.Z, false, true));
        Check(!QuitGameTerminationPolicy.ShouldAwaitGameExit("quit-game", "No", RecordedGameButton.Z, true, true));
        Check(!QuitGameTerminationPolicy.ShouldAwaitGameExit("quit-game", "Yes", RecordedGameButton.X, true, true));
        Check(!QuitGameTerminationPolicy.ShouldAwaitGameExit("quit-game", "Yes", RecordedGameButton.Z, true, false));
    }
    public static void TestFlashingMenuGate()
    {
        var gate = new GameplayMenuEvidenceGate();
        Check(!gate.Admits("keyboard", true, 5, 1)); Check(!gate.Admits(null, true, 0, 1.1));
        Check(!gate.Admits("keyboard", true, 0, 1.2)); Check(!gate.Admits(null, true, 0, 1.5));
        Check(!gate.Admits("keyboard", true, 0, 1.6));
    }
    public static void TestStableMenuGate()
    {
        var gate = new GameplayMenuEvidenceGate();
        Check(!gate.Admits(null, true, 5, 1)); Check(!gate.Admits("pause", true, 0, 1.4));
        Check(gate.Admits("pause", true, 0, 1.45)); Check(gate.Admits("pause", true, 0, 1.5));
        Check(!gate.Admits("options", true, 0, 1.6)); Check(gate.Admits("options", true, 0, 1.7));
        Check(!gate.Admits("options", true, 2, 2)); Check(!gate.Admits("options", true, 0, 2.3));
        gate.Reset(); Check(gate.Admits("main-title", false, 0, 1));
        Check(!gate.Admits("main-title", false, 0, double.NaN)); Check(!gate.Admits(null, false, 0, 1.1));
    }
    public static void TestPipelineHandoff()
    {
        var pipeline = new LiveGameStatusPipeline(); var calls = 0;
        IReadOnlyList<ObjectDetection> Inference() { calls++; return []; }
        var first = pipeline.Observe(null, Hud(), 1, inferObjects: Inference);
        Check(first.Status?.IsGameplay == true && first.RecognizedStatus?.IsGameplay == true && first.RanObjectInference);
        var flash = pipeline.Observe(Menu("keyboard"), Hud(), 1.1, inferObjects: Inference);
        Check(flash.Menu is null && flash.Status?.IsGameplay == true && flash.RanObjectInference);
        var missing = pipeline.Observe(null, null, 1.2, inferObjects: Inference);
        Check(missing.Status?.IsGameplay == true && missing.RecognizedStatus is null && missing.Menu is null);
        Check(pipeline.Observe(Menu("pause"), null, 1.5, inferObjects: Inference).Status?.IsGameplay == true);
        var pause = pipeline.Observe(Menu("pause", "Options"), null, 1.6, inferObjects: Inference);
        Check(pause.Status?.ContextIdentifier == "pause" && pause.Menu?.ContextIdentifier == "pause" && !pause.RanObjectInference && calls == 4);
        var grace = pipeline.Observe(null, null, 1.7, inferObjects: Inference);
        Check(grace.Status?.SelectedMenuOption == "Options" && grace.RecognizedStatus is null && grace.Menu is null && grace.RanObjectInference);
        Check(pipeline.Observe(null, null, 2.5).Status is null);
        pipeline.Observe(null, Hud(), 3); pipeline.Reset(true);
        Check(pipeline.Observe(null, null, 0).Status?.IsGameplay == true);
        pipeline.Reset(); Check(pipeline.Observe(null, null, 0).Status is null);
        Check(pipeline.Observe(Menu("main-title"), null, 1).Status?.ContextIdentifier == "main-title");
    }
    public static void TestCreditsInferenceRecovery()
    {
        var pipeline = new LiveGameStatusPipeline();
        pipeline.Observe(Menu("extras"), null, 1);
        var credits = pipeline.Observe(null, null, 2, creditsLikely: true, inferObjects: () => []);
        Check(credits.Status?.ContextIdentifier == "credits" && credits.RanObjectInference);
        var gameplay = pipeline.Observe(null, null, 3, creditsLikely: true,
            inferObjects: () => [Detection("game.playable-knight"), Detection("enemies.crawlid")]);
        Check(gameplay.Status?.IsGameplay == true && gameplay.RanObjectInference);
        Check(pipeline.Observe(null, null, 4, creditsLikely: true).Status?.IsGameplay == true);
    }
    public static void TestCreditsPixels()
    {
        BgraFrame Image(byte center, byte ring, byte corner, int width = 16, int height = 9)
        {
            var bytes = new byte[width * height * 4];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var gx = x * 16 / width; var gy = y * 9 / height;
                var level = gx is >= 6 and <= 9 && gy is >= 3 and <= 5 ? center
                    : gx is 3 or 12 && gy is 2 or 6 ? ring : corner;
                var index = (y * width + x) * 4; bytes[index] = bytes[index + 1] = bytes[index + 2] = level; bytes[index + 3] = 255;
            }
            return new(width, height, width * 4, bytes, 1, DateTimeOffset.UnixEpoch);
        }
        Check(CreditsVisualSignature.IsLikelyCredits(Image(180, 80, 0)));
        Check(CreditsVisualSignature.IsLikelyCredits(Image(180, 80, 0, 640, 360)));
        Check(CreditsVisualSignature.IsLikelyCredits(Image(180, 80, 0, 640, 540)));
        Check(!CreditsVisualSignature.IsLikelyCredits(Image(20, 10, 0)));
        Check(!CreditsVisualSignature.IsLikelyCredits(Image(180, 80, 40)));
        Check(!CreditsVisualSignature.IsLikelyCredits(Image(180, 160, 0)));
        Check(!CreditsVisualSignature.IsLikelyCredits(Image(180, 0, 0)));
        Check(!CreditsVisualSignature.IsLikelyCredits(Image(0, 0, 0)) && !CreditsVisualSignature.IsLikelyCredits(Image(255, 255, 255)));
    }
}
