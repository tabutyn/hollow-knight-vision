using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class HudStencilTests
{
    private sealed record Fixture(string Id, int Width, int Height, byte[] HudPNG, int[]? Health, int? Mana);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static BgraFrame Frame(Fixture fixture, int scale = 1)
    {
        using var stream = new MemoryStream(fixture.HudPNG);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var crop = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(crop, source.PixelWidth * 4, 0);
        var width = fixture.Width * scale; var height = fixture.Height * scale;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < source.PixelHeight * scale; y++)
            for (var x = 0; x < source.PixelWidth * scale; x++)
                Array.Copy(crop, ((y / scale) * source.PixelWidth + x / scale) * 4, pixels, (y * width + x) * 4, 4);
        return new(width, height, width * 4, pixels, 1, DateTimeOffset.UnixEpoch);
    }
    private static int[] Counts(HudStencilResult result) => new[] { "full", "empty", "lifeblood" }
        .Select(kind => result.Health.Count(item => item.Kind == kind)).ToArray();

    internal static void Run()
    {
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hud-stencil-cases.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var template = HudStencilTemplate.Bundled;
        Check(template?.IsValid == true, "Bundled Mac HUD template missing or invalid.");
        foreach (var fixture in fixtures)
        {
            var result = new HudStencilTracker().Observe(Frame(fixture), true, 1, 1)!;
            Check(result.ManaMain is not null, $"{fixture.Id}: missing Soul container match.");
            if (fixture.Health is { } health)
            {
                Check(Counts(result).SequenceEqual(health), $"{fixture.Id}: expected health {string.Join(',', health)}, got {string.Join(',', Counts(result))}.");
                Check(result.HealthMasks.Count == health.Sum(), $"{fixture.Id}: missing health masks.");
                Check(result.EstablishesGameplay == (health.Sum() >= 2), $"{fixture.Id}: incorrect gameplay gate.");
            }
            if (fixture.Mana is { } mana) Check(result.ManaReserves.Count == mana, $"{fixture.Id}: expected {mana} mana reserves, got {result.ManaReserves.Count}.");
        }
        Console.WriteLine($"PASS HUD matcher reproduces all {fixtures.Length} Mac photographic fixture expectations");
        var first = Frame(fixtures[0]);
        var tracker = new HudStencilTracker();
        var one = tracker.Observe(first, true, 1, 1)!;
        var two = tracker.Observe(Frame(fixtures[0], 2), true, 2, 2)!;
        Check(Counts(two).SequenceEqual(Counts(one)), "Scaled HUD lost health matches.");
        Check(Math.Abs(two.Geo.Width - one.Geo.Width * 2) < .01 && Math.Abs(two.Geo.Y - one.Geo.Y * 2) < .01, "Scaled masks have incorrect coordinates.");
        Console.WriteLine("PASS HUD counts and mask bounds survive 2x capture scale");
        var blank = new BgraFrame(640, 360, 640 * 4, new byte[640 * 360 * 4], 0, DateTimeOffset.UnixEpoch);
        tracker.Reset();
        var initial = tracker.Observe(first, true, 1, 1)!;
        var held = tracker.Observe(blank, true, 1.2, 1)!;
        Check(held.Health.Count == 0 && !held.EstablishesGameplay && held.HoldsHealthMask
            && held.HealthMasks.SequenceEqual(initial.HealthMasks), "Animation must hold exclusion masks without inventing health matches.");
        Check(tracker.Observe(blank, true, 1.7, 1)!.HealthMasks.Count == 0, "Mask hold did not expire.");
        _ = tracker.Observe(first, true, 2, 1);
        Check(tracker.Observe(blank, true, 2.1, 2)!.HealthMasks.Count == 0, "Generation change retained old health masks.");
        _ = tracker.Observe(first, true, 3, 2);
        Check(tracker.Observe(first, false, 3.1, 2) is null, "Known menu retained HUD matches.");
        Check(tracker.Observe(blank, true, 3.2, 2)!.HealthMasks.Count == 0, "Menu exit retained old masks.");
        Console.WriteLine("PASS HUD mask hold expires and resets on menu/generation changes");
        var full = template!.Health.First(atom => atom.Kind == "full").Bounds;
        var annotations = Enumerable.Range(0, 2).Select(index => new LabelingExampleAnnotation(Guid.NewGuid(), "game.health",
            (full.X + index * template.HealthPitch) / 640, full.Y / 360, full.Width / 640, full.Height / 360)).ToArray();
        var captured = HudStencilTemplate.Captured(first, annotations, "fixture-capture");
        Check(captured?.SourceExampleID == "fixture-capture", "Two health labels did not build a captured template.");
        foreach (var fixture in fixtures.Where(item => item.Health?.Sum() >= 2))
            Check(new HudStencilTracker(captured).Observe(Frame(fixture), true, 1, 1)?.EstablishesGameplay == true,
                $"{fixture.Id}: captured template cannot establish gameplay after health changes.");
        var nineFull = fixtures.Single(item => item.Health?.SequenceEqual(new[] { 9, 0, 0 }) == true);
        Check(new HudStencilTracker(captured).Observe(Frame(nineFull), true, 1, 1)!.Health.Count == 9,
            "Captured two-label pitch did not extrapolate all nine masks.");
        Check(HudStencilTemplate.Captured(first, annotations.Take(1), "incomplete") is null, "One label incorrectly unlocks HUD detection.");
        Check(HudStencilTemplate.Captured(blank, annotations, "blank") is null, "Blank patches incorrectly unlock HUD detection.");
        Check(HudStencilTemplate.Captured(first, [annotations[0], annotations[0]], "overlap") is null, "Overlapping labels accepted.");
        Console.WriteLine("PASS Two captured health labels extrapolate nine icons and preserve damaged/lifeblood gameplay");
        TestCapturedHUDUnlocksFirstCampaignWithoutModelOrClearSave(fixtures, first, template);
        TestRecognizer(first, Frame(nineFull), annotations);
        TestAtlasMasks(first, one);
        var twoHealth = new HudStencilResult([new(0, "full", default, .9), new(1, "full", default, .9)], null, [], [], [], default, false, 1);
        Check(twoHealth.EstablishesGameplay && !(twoHealth with { Health = [twoHealth.Health[0]] }).EstablishesGameplay,
            "Gameplay requires two verified health icons, not a Soul match or one icon.");
        var many = new HudStencilTracker().Observe(Frame(fixtures[1]), true, 1, 1)!;
        Check(many.Geo == one.Geo && many.Health.Count > one.Health.Count, "Geo bounds changed with health count.");
        Console.WriteLine("PASS Health-only gameplay gate and fixed Geo bounds match Mac");
        TestPipelineConnectsRoomsAndRestoresOriginalSceneAnchor();
        TestNewRoomAlignsKnightsDoorwayPositionAcrossSceneCoordinates();
        TestPipelineReinsertsLastBrightFrameWhenRoomFadesToBlack();
        TestNewestHackerFrameOverwritesOverlapAndAddsDarkRoomPixels();
        TestHackerAtlasUsesPixelStencilToRemoveHUD(fixtures[0]);
        TestCutsceneWithoutHeroDoesNotBecomeARoom();
        TestLiveMarkerRepairPreservesPixelsAndCopiesCorrectRows();
        TestPipelineClearsRenderedMarkerAtCapturedTopEdge();
        TestEveryMatchedFramePreparesWhileOnlyCadencedFramesIntegrate();
        Console.WriteLine("Windows HUD parity regression groups passed: 9");
        Console.WriteLine("Windows Hacker atlas parity regressions passed: 9");
    }

    private static void TestCapturedHUDUnlocksFirstCampaignWithoutModelOrClearSave(
        Fixture[] fixtures,
        BgraFrame image,
        HudStencilTemplate bundled)
    {
        var full = bundled.Health.First(atom => atom.Kind == "full").Bounds;
        var root = Path.Combine(Path.GetTempPath(), "hkv-hud-campaign-" + Guid.NewGuid().ToString("N"));
        try
        {
            var examplesRoot = Path.Combine(root, "labeling-v1", "examples");
            var store = new LabelingExampleStore(examplesRoot);
            var coordinator = new StencilInitializationCoordinator(store, GameRuntimeMode.Normal);
            var recognizer = new WindowsMenuStencilRecognizer(examplesRoot, "en");

            void Refresh(string? language = null)
            {
                coordinator.Refresh();
                recognizer.Refresh(language);
                coordinator.HasUsableHUD = recognizer.HasGameplayReference;
            }

            Refresh();
            Check(!coordinator.IsFirstPhaseComplete,
                "An empty label store incorrectly completed the first stencil campaign.");
            foreach (var contextIdentifier in new[] { "main-title", "select-profile" })
            {
                var context = StencilInitializationPolicy.Contexts(GameRuntimeMode.Normal)
                    .Single(item => item.Identifier == contextIdentifier);
                var boxes = context.Requirements
                    .Where(requirement => !requirement.ModdedOnly)
                    .SelectMany(requirement => Enumerable.Range(0, requirement.Count).Select(_ =>
                        new LabelingExampleAnnotation(Guid.NewGuid(), requirement.Identifier, .1, .2, .1, .04)))
                    .ToArray();
                _ = store.Save(image, context.Identifier, boxes, languageIdentifier: "en");
            }

            Refresh();
            Check(!coordinator.IsFirstPhaseComplete,
                "Title and profile labels incorrectly bypassed captured HUD setup.");
            Check(coordinator.Steps.FirstOrDefault()?.ContextIdentifier == "gameplay-hud",
                "Gameplay HUD was not the next Phase 1 campaign step.");
            Check(coordinator.Guidance(null, null) == "Enter gameplay, make health icons visible, press L.",
                "Gameplay HUD navigation guidance differs from Mac.");
            Check(coordinator.Guidance("select-profile", null)?.Contains("Press L", StringComparison.Ordinal) == true,
                "Completed Select Profile guidance does not return the user with L.");

            var health = Enumerable.Range(0, 2).Select(offset =>
                new LabelingExampleAnnotation(Guid.NewGuid(), "game.health",
                    (full.X + offset * bundled.HealthPitch) / 640,
                    full.Y / 360,
                    full.Width / 640,
                    full.Height / 360)).ToArray();
            var saved = store.Save(image, "gameplay-hud", health, languageIdentifier: "en");
            var custom = HudStencilTemplate.Captured(image, saved.Manifest.Annotations, saved.Id.ToString());
            Check(custom?.SourceExampleID == saved.Id.ToString(),
                "Captured HUD did not retain its source example identity.");
            Check(new HudStencilTracker(custom).Observe(image, true, 1, 1)?.EstablishesGameplay == true,
                "Captured HUD did not recognize its source gameplay frame.");
            foreach (var fixture in fixtures.Where(item => (item.Health?.Sum() ?? 0) >= 2))
            {
                Check(new HudStencilTracker(custom).Observe(Frame(fixture), true, 2, 2)?.EstablishesGameplay == true,
                    $"{fixture.Id}: captured campaign HUD did not establish gameplay.");
            }

            Refresh();
            Check(coordinator.IsFirstPhaseComplete,
                "A valid captured HUD did not unlock the first stencil campaign.");
            Check(!coordinator.IsComplete,
                "Later menus must not block Phase 1.");

            coordinator.SetLanguage("fr");
            Refresh(coordinator.LanguageIdentifier);
            Check(!coordinator.IsFirstPhaseComplete && !coordinator.HasUsableHUD,
                "A language change must relock incomplete references.");
            Console.WriteLine("PASS Captured HUD unlocks Phase 1 without model training or Clear Save");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void TestPipelineConnectsRoomsAndRestoresOriginalSceneAnchor()
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var image = SolidImage(64, 36);

        var firstPrepared = Require(preparer.Process(Match(
            image,
            Sample(frame: 1, cameraX: 10, scene: "A"))));
        Check(pipeline.Process(firstPrepared) is not null, "First Hacker frame was not integrated.");
        Close(0, firstPrepared.LiveBounds.X);
        Close(0, firstPrepared.LiveBounds.Y);

        var movedPrepared = Require(preparer.Process(Match(
            image,
            Sample(frame: 2, cameraX: 18, scene: "A"))));
        var moved = Require(pipeline.Process(movedPrepared));
        Close(8, movedPrepared.LiveBounds.MinX);
        Close(0, movedPrepared.LiveBounds.MinY);
        Check(Require(moved.AtlasBounds).Width > 64, "Moving camera did not expand the Hacker atlas.");

        var newScenePrepared = Require(preparer.Process(Match(
            image,
            Sample(frame: 3, cameraX: 50, scene: "B"))));
        var newScene = Require(pipeline.Process(newScenePrepared));
        Close(66, newScenePrepared.LiveBounds.MinX);
        Close(0, newScenePrepared.LiveBounds.MinY);
        Check(Require(newScene.AtlasBounds) == new HackerAtlasRect(0, 0, 130, 36),
            "Connected room atlas bounds differ from Mac.");
        Check(newScene.RoomCount == 2, "Connected rooms were not retained.");
        Check(newScene.KnownScenes.SequenceEqual(new[] { "A", "B" }),
            "Known Hacker scenes are not stable and sorted.");
        Check(newScene.AtlasTiles.Select(tile => tile.Id).Distinct().Count() == newScene.AtlasTiles.Count,
            "Flattened Hacker tile IDs are not unique.");
        Check(newScene.Rooms.Select(room => room.SceneName).SequenceEqual(new[] { "A", "B" }),
            "Hacker rooms are not sorted by scene.");
        Check(newScene.Rooms[0].Position == new AtlasPoint(0, 0), "Original room anchor moved.");
        Check(newScene.Rooms[0].Bounds == new HackerAtlasRect(0, 0, 72, 36),
            "Original room bounds do not include camera travel.");
        Check(newScene.Rooms[1].Position == new AtlasPoint(66, 0), "New room anchor is incorrect.");
        Check(newScene.Rooms[1].Bounds == new HackerAtlasRect(0, 0, 64, 36),
            "New room local bounds are incorrect.");
        Check(newScene.Connections.Count == 1, "Scene transition did not create exactly one connection.");
        Check(newScene.Connections[0].FromSide == HackerRoomSide.Right
              && newScene.Connections[0].ToSide == HackerRoomSide.Left,
            "Scene transition chose the wrong doorway sides.");

        var revisitedPrepared = Require(preparer.Process(Match(
            image,
            Sample(frame: 4, cameraX: 18, scene: "A"))));
        var revisited = Require(pipeline.Process(revisitedPrepared));
        Close(8, revisitedPrepared.LiveBounds.MinX);
        Close(130, Require(revisited.AtlasBounds).Width);
        Check(revisited.RoomCount == 2 && revisited.Connections.Count == 1,
            "Revisiting a room duplicated or discarded topology.");
        Console.WriteLine("PASS Hacker pipeline connects rooms and restores the original scene anchor");
    }

    private static void TestNewRoomAlignsKnightsDoorwayPositionAcrossSceneCoordinates()
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var image = SolidImage(64, 36);

        var leaving = Require(preparer.Process(Match(
            image,
            Sample(frame: 1, cameraX: 10, scene: "A", heroScreenX: 60, heroScreenY: 18))));
        Check(leaving.RoomPlacementReady, "Playable source room was not placement-ready.");
        Check(pipeline.Process(leaving) is not null, "Playable source room was not integrated.");
        var cameraSnap = Require(preparer.Process(Match(
            image,
            Sample(frame: 2, cameraX: -150, scene: "B", heroScreenX: -100, heroScreenY: 18))));
        Check(!cameraSnap.RoomPlacementReady, "Out-of-bounds projected Knight initialized a room.");
        Check(pipeline.Process(cameraSnap) is null, "Camera snap became an atlas room.");

        var entering = Require(preparer.Process(Match(
            image,
            Sample(frame: 3, cameraX: -100, scene: "B", heroScreenX: 4, heroScreenY: 18))));
        Close(0, leaving.LiveBounds.X);
        Close(0, leaving.LiveBounds.Y);
        Check(entering.RoomPlacementReady, "Valid destination doorway did not initialize its room.");
        Close(56, entering.LiveBounds.MinX);
        Close(0, entering.LiveBounds.MinY);
        Check(Require(pipeline.Process(entering)).RoomCount == 2,
            "Doorway-aligned destination was not integrated.");
        Console.WriteLine("PASS Hacker new room aligns the Knight doorway across scene coordinates");
    }

    private static void TestPipelineReinsertsLastBrightFrameWhenRoomFadesToBlack()
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var bright = SolidImage(64, 36, red: .6, green: .7, blue: .8);
        var black = SolidImage(64, 36, red: 0, green: 0, blue: 0);
        var preFade = Require(preparer.Process(Match(
            bright,
            Sample(frame: 10, cameraX: 10),
            integratesAtlas: false)));
        Check(pipeline.Process(preFade) is null, "Noncadenced pre-fade frame integrated early.");

        var faded = Require(preparer.Process(Match(
            black,
            Sample(frame: 11, cameraX: 10),
            integratesAtlas: true)));
        var output = Require(pipeline.Process(faded));
        Check(output.UnityFrame == 10, "Fade recovery did not reinsert the retained bright frame.");
        Check(output.RoomCount == 1, "Fade recovery changed room ownership.");
        var pixel = AtlasPixel(new AtlasPoint(32, 18), output.AtlasTiles);
        Check(pixel.Red > 100 && pixel.Green > 100 && pixel.Blue > 100,
            "Fade recovery left black pixels over the retained bright room.");
        Console.WriteLine("PASS Hacker pipeline reinserts the last bright frame during a fade");
    }

    private static void TestNewestHackerFrameOverwritesOverlapAndAddsDarkRoomPixels()
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var bright = SolidImage(64, 36, red: .6, green: .7, blue: .8);
        var first = Require(preparer.Process(Match(
            bright,
            Sample(frame: 30, cameraX: 0))));
        Check(pipeline.Process(first) is not null, "Initial bright frame was not integrated.");

        var iris = IrisImage(64, 36, radius: 8);
        var narrowed = Require(preparer.Process(Match(
            iris,
            Sample(frame: 31, cameraX: 30))));
        var output = Require(pipeline.Process(narrowed));

        var overwritten = AtlasPixel(new AtlasPoint(35, 18), output.AtlasTiles);
        Check(overwritten is { Red: 0, Green: 0, Blue: 0, Alpha: 255 },
            "Newest opaque black did not overwrite the overlap.");
        var newlyObservedBlack = AtlasPixel(new AtlasPoint(80, 18), output.AtlasTiles);
        Check(newlyObservedBlack is { Red: 0, Green: 0, Blue: 0, Alpha: 255 },
            "Newly observed black room pixels were treated as transparency.");
        Console.WriteLine("PASS Hacker newest frame overwrites overlap and adds dark room pixels");
    }

    private static void TestHackerAtlasUsesPixelStencilToRemoveHUD(Fixture fixture)
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var image = GameplayFrameWithRealHud(fixture);
        var sample = Sample(
            frame: 12,
            cameraX: 0,
            heroScreenX: 32,
            heroScreenY: 18);
        var prepared = Require(preparer.Process(Match(image, sample)));
        var stencil = Require(new HudStencilTracker().Observe(
            prepared.LiveImage,
            gameplay: true,
            timestamp: prepared.CapturedAt,
            generation: 0));
        Check(stencil.OmittedRects.Count > 0, "Photographic HUD produced no pixel stencil.");
        var output = Require(pipeline.Process(prepared));

        Check(AtlasAlpha(new AtlasPoint(25, 320), output.AtlasTiles) == 0,
            "Soul-vessel ornament outside matched sprites survived the atlas shield.");
        Check(AtlasAlpha(new AtlasPoint(220, 330), output.AtlasTiles) == 0,
            "Maximum health-row footprint survived the atlas shield.");
        foreach (var rect in stencil.OmittedRects)
        {
            Check(AtlasAlpha(
                    new AtlasPoint(rect.MidX, image.Height - rect.MidY),
                    output.AtlasTiles) == 0,
                "A detected HUD stencil midpoint survived atlas masking.");
        }
        Check(AtlasAlpha(new AtlasPoint(500, 100), output.AtlasTiles) == 255,
            "HUD masking removed unrelated room pixels.");
        Console.WriteLine("PASS Hacker atlas uses the photographic pixel stencil to remove HUD");
    }

    private static void TestCutsceneWithoutHeroDoesNotBecomeARoom()
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var image = SolidImage(64, 36);
        var first = Require(preparer.Process(Match(
            image,
            Sample(frame: 20, cameraX: 0, scene: "Room A"))));
        Check(Require(pipeline.Process(first)).RoomCount == 1, "First playable room was not created.");

        var cutscene = Require(preparer.Process(Match(
            image,
            Sample(frame: 21, cameraX: 0, scene: "Opening Cutscene", heroAvailable: false))));
        Check(pipeline.Process(cutscene) is null, "Hero-less cutscene became an atlas room.");

        var second = Require(preparer.Process(Match(
            image,
            Sample(frame: 22, cameraX: 0, scene: "Room B"))));
        var output = Require(pipeline.Process(second));
        Check(output.RoomCount == 2, "Playable room after cutscene was not created.");
        Check(output.KnownScenes.SequenceEqual(new[] { "Room A", "Room B" }),
            "Hero-less cutscene polluted known scene ownership.");
        Console.WriteLine("PASS Hacker cutscene without a hero does not become a room");
    }

    private static void TestLiveMarkerRepairPreservesPixelsAndCopiesCorrectRows()
    {
        const int width = 640;
        const int height = 360;
        var image = GradientImage(width, height);
        var prepared = Require(new HackerFramePreparer().Process(Match(
            image,
            Sample(frame: 4, cameraX: 0),
            integratesAtlas: false)));
        var expected = image.Pixels.ToArray();
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 120; x++)
        {
            Array.Copy(
                image.Pixels,
                ((y + 6) * width + x) * 4,
                expected,
                (y * width + x) * 4,
                4);
        }
        Check(prepared.LiveImage.Pixels.SequenceEqual(expected),
            "Marker repair shifted or recolored game pixels.");
        Console.WriteLine("PASS Hacker marker repair preserves pixels and copies adjacent rows");
    }

    private static void TestPipelineClearsRenderedMarkerAtCapturedTopEdge()
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var image = MarkerBandImage();
        var prepared = Require(preparer.Process(Match(
            image,
            Sample(frame: 4, cameraX: 0))));
        var output = Require(pipeline.Process(prepared));
        var pixels = prepared.LiveImage.Pixels;
        Check(pixels[3] == 255, "Captured top-left marker pixel was not repaired opaquely.");
        Check(pixels[0] == 0 && pixels[1] == 255 && pixels[2] == 0,
            "Captured top-left marker was not replaced by the adjacent green row.");
        Check(pixels[((359 * 640) + 0) * 4 + 3] == 255,
            "Marker repair changed the captured bottom edge.");
        Check(AtlasAlpha(new AtlasPoint(0, 359), output.AtlasTiles) == 0,
            "Repaired marker strip escaped the persistent HUD atlas shield.");
        Check(AtlasAlpha(new AtlasPoint(200, 200), output.AtlasTiles) == 255,
            "Marker/HUD masking removed an unrelated atlas pixel.");
        Console.WriteLine("PASS Hacker pipeline clears the rendered marker at the captured top edge");
    }

    private static void TestEveryMatchedFramePreparesWhileOnlyCadencedFramesIntegrate()
    {
        var preparer = new HackerFramePreparer();
        var pipeline = new HackerAtlasPipeline();
        var image = SolidImage(64, 36);
        var liveOnly = Require(preparer.Process(Match(
            image,
            Sample(frame: 5, cameraX: 0),
            integratesAtlas: false)));
        Check(!liveOnly.IntegratesAtlas && liveOnly.UnityFrame == 5,
            "Every-match preparation lost cadence ownership or Unity frame.");
        Check(pipeline.Process(liveOnly) is null, "Live-only frame was rasterized into the atlas.");

        var atlasFrame = Require(preparer.Process(Match(
            image,
            Sample(frame: 6, cameraX: 1),
            integratesAtlas: true)));
        Check(pipeline.Process(atlasFrame) is not null,
            "Cadenced prepared frame was not integrated.");
        Console.WriteLine("PASS Every matched Hacker frame prepares while only cadence frames integrate");
    }

    private static HackerMatchedFrame Match(
        BgraFrame image,
        ReceiverGroundTruthSample sample,
        bool integratesAtlas = true) => new(
        new HackerCapturedFrame(
            HackerFrameSynchronizer.Key(sample.UnityFrame),
            image,
            sample.UnityRealtime,
            integratesAtlas),
        sample,
        sample.UnityRealtime);

    private static ReceiverGroundTruthSample Sample(
        long frame,
        double cameraX,
        string scene = "A",
        bool heroAvailable = true,
        double? heroScreenX = null,
        double? heroScreenY = null) => new(
        Version: 2,
        Type: "groundTruth",
        SessionId: Guid.NewGuid(),
        Sequence: checked((ulong)frame),
        UnityFrame: frame,
        UnityRealtime: frame,
        SceneName: scene,
        HeroAvailable: heroAvailable,
        HeroX: 0,
        HeroY: 0,
        HeroZ: 0,
        VelocityX: 0,
        VelocityY: 0,
        FacingRight: true,
        Grounded: true,
        CameraAvailable: true,
        CameraX: cameraX,
        CameraY: 4,
        CameraZ: -38,
        CameraTargetX: cameraX,
        CameraTargetY: 4,
        CameraTargetZ: 0,
        OrthographicSize: 18,
        PixelsPerWorldUnitX: 1,
        PixelsPerWorldUnitY: 1,
        HeroScreenX: heroScreenX,
        HeroScreenY: heroScreenY,
        ProjectionPixelWidth: 64,
        ProjectionPixelHeight: 36,
        ScreenWidth: 64,
        ScreenHeight: 36);

    private static BgraFrame SolidImage(
        int width,
        int height,
        double red = .2,
        double green = .4,
        double blue = .6)
    {
        static byte Channel(double value) => (byte)Math.Round(
            Math.Clamp(value, 0, 1) * 255,
            MidpointRounding.AwayFromZero);
        var pixels = new byte[checked(width * height * 4)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = Channel(blue);
            pixels[offset + 1] = Channel(green);
            pixels[offset + 2] = Channel(red);
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(
            width,
            height,
            width * 4,
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static BgraFrame GameplayFrameWithRealHud(Fixture fixture)
    {
        var hud = Frame(fixture);
        var pixels = new byte[checked(hud.Width * hud.Height * 4)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 64;
            pixels[offset + 1] = 51;
            pixels[offset + 2] = 38;
            pixels[offset + 3] = 255;
            var alpha = hud.Pixels[offset + 3];
            if (alpha == 0) continue;
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[offset + channel] = (byte)(
                    (hud.Pixels[offset + channel] * alpha
                     + pixels[offset + channel] * (255 - alpha)
                     + 127) / 255);
            }
        }
        return new BgraFrame(
            hud.Width,
            hud.Height,
            hud.Stride,
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static BgraFrame IrisImage(int width, int height, double radius)
    {
        var pixels = new byte[checked(width * height * 4)];
        var centerX = width * .5;
        var centerY = height * .5;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var inside = Math.Pow(x + .5 - centerX, 2)
                + Math.Pow(y + .5 - centerY, 2) <= radius * radius;
            var value = inside ? (byte)204 : (byte)0;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(
            width,
            height,
            width * 4,
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static BgraFrame GradientImage(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)((x + y) % 256);
            pixels[offset + 1] = (byte)(y % 256);
            pixels[offset + 2] = (byte)(x % 256);
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(
            width,
            height,
            width * 4,
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static BgraFrame MarkerBandImage()
    {
        const int width = 640;
        const int height = 360;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            if (y < 6 && x < 120) pixels[offset + 2] = 255;
            else pixels[offset + 1] = 255;
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(
            width,
            height,
            width * 4,
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private readonly record struct AtlasColor(byte Red, byte Green, byte Blue, byte Alpha);

    private static AtlasColor AtlasPixel(
        AtlasPoint point,
        IReadOnlyList<HackerAtlasLayer> tiles)
    {
        var tile = tiles.FirstOrDefault(candidate =>
            point.X >= candidate.Bounds.MinX
            && point.X < candidate.Bounds.MaxX
            && point.Y >= candidate.Bounds.MinY
            && point.Y < candidate.Bounds.MaxY)
            ?? throw new InvalidOperationException("Atlas point has no tile.");
        var localX = checked((int)Math.Floor(point.X - tile.Bounds.MinX));
        var localY = checked((int)Math.Floor(point.Y - tile.Bounds.MinY));
        var topY = tile.Image.Height - 1 - localY;
        var offset = checked(topY * tile.Image.Stride + localX * 4);
        return new AtlasColor(
            tile.Image.Pixels[offset + 2],
            tile.Image.Pixels[offset + 1],
            tile.Image.Pixels[offset],
            tile.Image.Pixels[offset + 3]);
    }

    private static byte AtlasAlpha(
        AtlasPoint point,
        IReadOnlyList<HackerAtlasLayer> tiles) => AtlasPixel(point, tiles).Alpha;

    private static T Require<T>(T? value) where T : class =>
        value ?? throw new InvalidOperationException($"Expected {typeof(T).Name}.");

    private static HackerAtlasRect Require(HackerAtlasRect? value) =>
        value ?? throw new InvalidOperationException("Expected Hacker atlas bounds.");

    private static void Close(double expected, double actual, double tolerance = .001) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected}, got {actual}.");

    private static void TestAtlasMasks(BgraFrame source, HudStencilResult hud)
    {
        var regions = SceneRegions.AtlasGameplay([], new(0, 0, source.Width, source.Height), hudStencil: hud);
        Check(regions.OmittedRects[0] == new StencilRect(0, 0, 256, 90), "Persistent HUD shield differs from Mac.");
        Check(regions.OmittedRects.Skip(1).SequenceEqual(hud.OmittedRects), "Exact photographic HUD masks were lost.");
        var output = FrameRegionRenderer.MapFrame(source, regions);
        foreach (var rect in hud.OmittedRects.Where(rect => !rect.IsEmpty))
            Check(output.Pixels[(int)rect.MidY * output.Stride + (int)rect.MidX * 4 + 3] == 0,
                "Photographic HUD pixels survived the atlas mask.");
        // The overlay's complementary checks verify these same centers are visible only in the debug layer.
        TestOverlay(hud);
        Console.WriteLine("PASS Mac photographic HUD masks clear atlas pixels and remain visible in the separate overlay");
    }

    private static void TestRecognizer(BgraFrame source, BgraFrame live, LabelingExampleAnnotation[] annotations)
    {
        var root = Path.Combine(Path.GetTempPath(), "hkv-hud-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var examplesRoot = Path.Combine(root, "labeling-v1", "examples");
            new LabelingExampleStore(examplesRoot).Save(source, "gameplay-hud", annotations, languageIdentifier: "en");
            var type = typeof(HollowKnightVision.Windows.App.MainWindow).Assembly.GetType("HollowKnightVision.Windows.App.WindowsMenuStencilRecognizer")!;
            var recognizer = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, [examplesRoot, "en"], null)!;
            var detect = type.GetMethod("Detect", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var refresh = type.GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var reference = type.GetProperty("HasGameplayReference", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = type.GetProperty("LatestHud", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Check((bool)reference.GetValue(recognizer)!, "Live recognizer rejected valid saved labels.");
            Check((string?)detect.Invoke(recognizer, [live]) == "game", "Live recognizer did not use the captured HUD matcher.");
            Check(((HudStencilResult)result.GetValue(recognizer)!).Health.Count == 9, "Live recognizer still detects only the two labeled icons.");
            var observe = type.GetMethod("Observe", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var blankPixels = new byte[live.Width * live.Height * 4];
            var blank = new BgraFrame(live.Width, live.Height, live.Width * 4, blankPixels, 2, live.ObservedAt.AddSeconds(1));
            var lostHud = observe.Invoke(recognizer, [blank, false])!;
            Check((string?)lostHud.GetType().GetProperty("Context")!.GetValue(lostHud) == "game", "HUD dropout cleared established Gameplay state.");
            Check(!(bool)lostHud.GetType().GetProperty("HasFreshGameplayEvidence")!.GetValue(lostHud)!, "Presentation latch incorrectly authorizes new atlas pixels.");
            type.GetMethod("ResetTracking", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(recognizer, null);
            var clockRestart = new BgraFrame(live.Width, live.Height, live.Width * 4, blankPixels, 3, DateTimeOffset.UnixEpoch);
            var restarted = observe.Invoke(recognizer, [clockRestart, false])!;
            Check((string?)restarted.GetType().GetProperty("Context")!.GetValue(restarted) == "game", "Capture transport reset erased established gameplay.");
            type.GetMethod("ResetGameSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(recognizer, null);
            var newSession = observe.Invoke(recognizer, [clockRestart, false])!;
            Check(newSession.GetType().GetProperty("Context")!.GetValue(newSession) is null, "New game process inherited old session's Gameplay latch.");
            refresh.Invoke(recognizer, ["fr"]);
            Check(!(bool)reference.GetValue(recognizer)!, "Language switch retained the wrong HUD references.");
            Check(detect.Invoke(recognizer, [live]) is null, "Unlabeled language bypassed setup via bundled templates.");
            refresh.Invoke(recognizer, ["en"]);
            Check((string?)detect.Invoke(recognizer, [live]) == "game", "Returning to labeled language did not restore HUD detection.");
            Console.WriteLine("PASS Live HUD recognition preserves Gameplay through dropout/transport reset, resets for a new game, and relocks language");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void TestOverlay(HudStencilResult hud)
    {
        var canvas = new System.Windows.Controls.Canvas { Width = 640, Height = 360 };
        typeof(HollowKnightVision.Windows.App.MainWindow).GetMethod("DrawHudStencil", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [canvas, hud]);
        var expected = hud.Health.Count + hud.HealthMasks.Count + hud.ManaMasks.Count + (hud.ManaMain is null ? 0 : 1) + (hud.Geo.IsEmpty ? 0 : 1);
        Check(canvas.Children.Count == expected, "HUD diagnostic overlay omitted matches or exclusion masks.");
        foreach (var match in hud.Health)
            Check(canvas.Children.OfType<System.Windows.Shapes.Rectangle>().Any(shape =>
                Math.Abs(shape.Width - match.Rect.Width) < .001 && Math.Abs(shape.Height - match.Rect.Height) < .001
                && Math.Abs(System.Windows.Controls.Canvas.GetLeft(shape) - match.Rect.X) < .001
                && Math.Abs(System.Windows.Controls.Canvas.GetTop(shape) - match.Rect.Y) < .001), "HUD overlay bounds differ from actual matches.");
        canvas.Measure(new System.Windows.Size(640, 360));
        canvas.Arrange(new System.Windows.Rect(0, 0, 640, 360));
        canvas.UpdateLayout();
        var image = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        image.Render(canvas);
        var pixels = new byte[640 * 360 * 4];
        image.CopyPixels(pixels, 640 * 4, 0);
        foreach (var rect in hud.OmittedRects.Where(rect => !rect.IsEmpty))
            Check(pixels[((int)rect.MidY * 640 + (int)rect.MidX) * 4 + 3] > 0, "HUD mask center not rendered.");
        Check(pixels[(155 * 640 + 405) * 4 + 3] == 0, "HUD overlay paints unrelated world pixels.");
        Console.WriteLine("PASS HUD overlay draws every matched icon and its exact mask bounds");
    }
}
