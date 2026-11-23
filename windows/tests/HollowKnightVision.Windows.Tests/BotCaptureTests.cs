using HollowKnightVision.Windows.Core;

internal static class BotCaptureTests
{
    private const double Start = 30;
    private const long Generation = 12;
    private static readonly Guid BlueEpoch = Guid.Parse("8f76cac5-bf61-40d9-9aeb-a47dcad636ca");
    private static readonly Guid EnemyEpoch = Guid.Parse("bbc5a743-fd5d-4abe-9bfc-db806add7d44");

    internal static void TestExactHackerSourcesStaySeparateFromVisualSources()
    {
        var input = Input(0);
        var source = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(ReceiverWireCodec.EncodeLine(input.Mod));
        Check(source.Actors?.GeoCaches?.Single().Id == 3 && source.Actors.EnemyOutcomes?.Epoch == EnemyEpoch
            && source.BlueHealthOutcomes?.Epoch == BlueEpoch && source.AtlasGameplayActive == true,
            "The Windows receiver must preserve the frame's actor and outcome payload.");
        var hacker = Hacker(input, source)!;
        var gameplay = Visual(input);
        Check(hacker.Issues.Count == 0 && gameplay.Issues.Count == 0, "Complete dual-domain evidence should be usable.");
        Check(hacker.DetectedTerrain is null && hacker.AuthoredTerrain is { Floors.Count: 1, Walls.Count: 1, Doors.Count: 1, Spikes.Count: 1 }
            && hacker.AuthoredTerrain.Walls[0].SolidSide == 0 && hacker.GeoCaches?.Single().X == .625
            && hacker.LifebloodCocoons?.Count == 1, "Hacker must use authored markings and the mod's actor boxes.");
        Check(gameplay.AuthoredTerrain is null && gameplay.DetectedTerrain is { Floors.Count: 1, Walls.Count: 1 }
            && gameplay.DetectedTerrain.Walls[0].SolidSide == 1 && gameplay.GeoCaches?.Count == 0
            && Math.Abs(gameplay.Features[0] - .45) < 1e-9 && gameplay.Features[144] == 1,
            "Game Play must use pixel detections and terrain in bottom-left pixels.");
        Check(hacker.Features[0] == .5 && hacker.Features[96] > 0 && gameplay.Features[96] < 0,
            "Each observation must use its own Knight and enemy sources.");
        Check(Hacker(input, source with { UnityFrame = 8 }) is null && Hacker(input, source with { AtlasGameplayActive = false }) is null,
            "Stale rendered-frame markers and menu samples must not enter the Hacker clip.");
        var bad = source with { Actors = source.Actors! with { Enemies = [source.Actors.Enemies![0], source.Actors.Enemies[0]] } };
        Check(Hacker(input, bad) is { CanTrain: false }, "Duplicate mod identities must not produce valid observations.");
    }

    internal static void TestFreshTerrainMakesOccludedKnightUsable()
    {
        var input = Input(0);
        var hidden = Visual(input with { Detections = input.Detections with { Detections = [] } });
        Check(hidden.Issues.SequenceEqual(new[] { "missing-knight" }) && hidden.CanTrain,
            "A missed Knight with fresh detections and visible terrain should retain the motion evidence.");
        var stale = Visual(input with { Detections = input.Detections with { SourceTimestamp = input.Detections.SourceTimestamp - .21 } });
        Check(!stale.CanTrain && stale.Issues.Contains("stale-or-missing-detection"), "Stale detector output must remain ineligible.");
    }

    internal static void TestPairedEvidencePublishesOnceAndHazardClipsAreDiscarded()
    {
        WithLibrary(root =>
        {
            var result = WriteFixture(root);
            Check(result.Published.Count == 2 && result.Discarded == 0, "Both valid domains should publish.");
            var clips = BotCaptureSession.Library(root);
            Check(clips.Count == 2 && clips[0].PairedClipId == clips[1].Id && clips[1].PairedClipId == clips[0].Id
                && clips[0].ReplayId == clips[1].ReplayId && clips.All(c => c.ObservationCount == 6 && c.InputEventCount == 6),
                "A single run should publish two reciprocal manifests with the exact same input edges.");
            var pending = Directory.GetDirectories(root, ".pending-demo-*");
            Check(pending.Length == 0 && clips.All(clip => File.Exists(Path.Combine(root,
                BotCaptureSession.DirectoryName(clip.Id), "frames", "0.png"))), "Saved PNG evidence should be readable immediately.");

            var input = Input(0);
            var insufficient = new BotCaptureSession(root, "normal", "visual", Generation, Start, Labels());
            var invalid = Visual(input with { Detections = input.Detections with { CaptureGeneration = -1 } });
            Check(insufficient.TryRecord(input.Frame, invalid, null), "The recorder should log the hazard during capture.");
            var failure = insufficient.FinishAsync(Start + 1, null, completed: true).GetAwaiter().GetResult();
            Check(failure.Published.Count == 0 && failure.Discarded == 1 && BotCaptureSession.Library(root).Count == 2,
                "A clip with no eligible observations should disappear without entering Bot Summary.");
        });
    }

    internal static void TestNativeControlsAndInterruptedCapture()
    {
        WithLibrary(root =>
        {
            var input = Input(0);
            var active = new BotCaptureSession(root, "normal", "visual", Generation, Start, Labels());
            active.ObserveNativeInput(InputButtons.Right | InputButtons.ActionX, Start);
            active.ObserveNativeInput(InputButtons.Right, Start + .18);
            Check(active.TryRecord(input.Frame, Visual(input), null), "A visual-only clip should be accepted.");
            Check(!active.TryRecord(input.Frame, Visual(input), null), "Duplicate capture frames should not be appended.");
            Check(!active.TryRecord(input.Frame, Visual(input) with { Generation = Generation + 1 }, null),
                "Previous capture work must not cross a live generation.");
            var result = active.FinishAsync(Start + 1, null, completed: true).GetAwaiter().GetResult();
            var clip = result.Published.Single();
            Check(clip is { InputEventCount: 3, BotDomain: "gameplay", ReplayId: null, ObservationCount: 1 },
                "Native held keys should be stored as explicit input edges.");
            var interrupted = new BotCaptureSession(root, "normal", "visual", Generation, Start, Labels());
            interrupted.TryRecord(input.Frame, Visual(input), null);
            Check(interrupted.FinishAsync(Start + 1, null, completed: false).GetAwaiter().GetResult().Discarded == 1,
                "Interrupted captures should be discarded.");
        });
    }

    internal static void TestReviewUsesTheSavedDomainAndSafeLocalImage()
    {
        WithLibrary(root =>
        {
            var pair = WriteFixture(root);
            foreach (var clip in pair.Published)
            {
                var folder = Path.Combine(root, BotCaptureSession.DirectoryName(clip.Id));
                var frames = BotCaptureSession.Review(folder);
                var frame = frames.Single();
                Check(frame.Authored == (clip.BotDomain == "hacker") && frame.Features.Count == BotRecordingContract.FeatureNames.Count
                    && File.Exists(frame.ImageFile) && frame.Offset > 0,
                    "Review should use the image and source domain stored in its own clip.");
                Check(frame.Terrain?.Walls.Single().SolidSide == (frame.Authored ? 0 : 1),
                    "Review must show the saved wall source for its domain.");
                File.AppendAllText(Path.Combine(folder, "events.jsonl"), """{"imagePath":"../outside.png"}""" + "\n");
                Check(BotCaptureSession.Review(folder).Count == 1, "Review should only open paths inside the capture.");
            }
        });
    }

    internal static BotCaptureResult WriteFixture(string root)
    {
        var id = Guid.NewGuid();
        var labels = Labels();
        var record = new InputPathRecorder();
        record.Start(Start, id: id);
        record.RecordInput(RecordedGameButton.Right, true, Start);
        record.RecordInput(RecordedGameButton.X, true, Start + .08);
        record.RecordInput(RecordedGameButton.X, false, Start + .36);
        record.RecordInput(RecordedGameButton.X, true, Start + .39);
        var active = new BotCaptureSession(root, "modded", "visual", Generation, Start, labels, id, paired: true);
        for (var i = 0; i < 6; i++)
        {
            var input = Input(i);
            Check(active.TryRecord(input.Frame, Visual(input), Hacker(input, input.Mod)), "A current paired sample was dropped.");
        }
        // Keep attack held through stop and release right independently. The path recorder supplies the final releases.
        record.RecordInput(RecordedGameButton.Right, false, Start + .72);
        var path = record.Stop(Start + .9);
        return active.FinishAsync(Start + .9, path, completed: true).GetAwaiter().GetResult();
    }

    private static GroundTruthLineDocument Labels() => new()
    {
        Lines = [
            new("Tutorial_01", -40, 40, 0, GroundTruthLineKind.Positive, false),
            new("Tutorial_01", -30, 30, 1, GroundTruthLineKind.Positive, true),
            new("Other", -30, 30, 1, GroundTruthLineKind.Positive, false)
        ],
        Walls = [
            new GroundTruthWorldLine("Tutorial_01", -8, -8, -3, GroundTruthLineKind.Wall, false) { EndWorldY = 8 }
        ],
        Doors = [new("Tutorial_01", 10, 12, 0, GroundTruthLineKind.Door, false)],
        Spikes = [new("Tutorial_01", 5, 6, 0, GroundTruthLineKind.Spike, false)]
    };

    private sealed record Fixture(BgraFrame Frame, BotCaptureTiming Time, ReceiverGroundTruthSample Mod,
        LiveObjectDetectionBatch Detections);

    private static Fixture Input(int index)
    {
        const int w = 80;
        const int h = 48;
        var now = Start + .02 + index * .1;
        var frame = new BgraFrame(w, h, w * 4, new byte[w * h * 4], index, DateTimeOffset.UnixEpoch.AddSeconds(now));
        var clock = new BotCaptureTiming(now, now + .001, Generation, 1, 0);
        var mod = new ReceiverGroundTruthSample(2, "groundTruth", Guid.NewGuid(), 7, index, now, "Tutorial_01",
            true, 0, 0, 0, 0, 0, true, true, true, 0, 0, 0, 0, 0, 0, 24, 2, 2, 80, 36, 160, 96, 160, 96)
        {
            AtlasGameplayActive = true,
            Actors = new(1, new(1, "Knight", 80, 36, 10, 12),
                [new(2, "Crawlid", 128, 32, 12, 8)], [new(3, "Geo", 100, 32, 12, 18)],
                [new(4, "Cocoon", 110, 42, 10, 10)], new(EnemyEpoch, index >= 4 ? 1 : 0, 0), HeroHealth: 5),
            BlueHealthOutcomes = new(BlueEpoch, index >= 2 ? 2 : 0)
        };
        var detections = new LiveObjectDetectionBatch(
        [
            new ObjectDetection("game.playable-knight", new(.4, .4, .1, .2), .9f) { SourceFrameIdentifier = index },
            new ObjectDetection("enemies.crawlid", new(.1, .5, .08, .1), .8f) { SourceFrameIdentifier = index },
        ], index, (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds, Generation, 0);
        return new(frame, clock, mod, detections);
    }

    private static BotCaptureObservation? Hacker(Fixture input, ReceiverGroundTruthSample source) =>
        BotObservationBuilder.Hacker(input.Frame, (int)input.Frame.CaptureId, source, Labels(), input.Time, InputButtons.Right);

    private static BotCaptureObservation Visual(Fixture input) =>
        BotObservationBuilder.Gameplay(input.Frame, input.Time, input.Detections, [new(36, new(5, 62))],
            [new(70, 10, 42, true)], terrainAnalyzed: true, new(0, 0), null, new(30, 36), InputButtons.Right, input.Mod.BlueHealthOutcomes);

    private static void WithLibrary(Action<string> run)
    {
        var directory = Directory.CreateTempSubdirectory("hkv-bot-capture-test-");
        try { run(directory.FullName); }
        finally { directory.Delete(recursive: true); }
    }

    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
