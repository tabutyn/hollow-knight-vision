using HollowKnightVision.Windows.Core;

internal static class SceneRegionTests
{
    private static readonly StencilRect Extent = new(0, 0, 640, 360);
    private static void Check(bool value, string message = "Scene region parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static void Rect(StencilRect? actual, StencilRect expected)
    {
        Check(actual is { } rect && Math.Abs(rect.X - expected.X) < .000001
            && Math.Abs(rect.Y - expected.Y) < .000001 && Math.Abs(rect.Width - expected.Width) < .000001
            && Math.Abs(rect.Height - expected.Height) < .000001, $"Expected {expected}, got {actual}.");
    }
    private static ObjectDetection Knight(float confidence = .9f) => new("game.playable-knight", new(.5, .4, .05, .1), confidence);
    private static BgraFrame Solid(int width = 640, int height = 360, byte value = 100, int padding = 0,
        long captureId = 42, DateTimeOffset? observedAt = null)
    {
        var stride = width * 4 + padding;
        var pixels = Enumerable.Repeat((byte)177, stride * height).ToArray();
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var index = y * stride + x * 4; pixels[index] = pixels[index + 1] = pixels[index + 2] = value; pixels[index + 3] = 255; }
        return new(width, height, stride, pixels, captureId, observedAt ?? DateTimeOffset.UnixEpoch.AddSeconds(3));
    }
    private static byte Alpha(BgraFrame frame, int x, int y) => frame.Pixels[y * frame.Stride + x * 4 + 3];
    private static ReceiverGroundTruthSample Sample() => new(2, "groundTruth", Guid.Empty, 1, 1, 1,
        "Tutorial_01", true, 0, 0, 0, 0, 0, true, true, true, 0, 0, -38, 0, 0, 0, 180,
        1, 1, 320, 180, 640, 360, 640, 360);

    public static void TestMovingKnightOnly()
    {
        var detections = new[] { Knight(), new("game.mana", new(.03, .04, .05, .1), .9f),
            new("game.health", new(.12, .04, .1, .04), .9f), new("game.geo", new(.1, .16, .07, .04), .9f) };
        var regions = SceneRegions.FromObjectDetections(detections, Extent);
        Rect(regions.Knight, new(320, 144, 32, 36));
        Check(regions.Health.IsEmpty && regions.Geo.IsEmpty && regions.Mana.IsEmpty);
        Check(regions.OmittedRects.SequenceEqual(new[] { regions.Knight!.Value }));
        Check(SceneRegions.FromObjectDetections([], Extent).OmittedRects.Count == 0);
        var weak = Knight(.6f) with { Box = new(.1, .8, .05, .1) };
        var enemy = Knight(1) with { ClassIdentifier = "enemies.hollow-knight", Box = new(.7, .4, .1, .1) };
        Rect(SceneRegions.FromObjectDetections([enemy, weak, Knight()], Extent).Knight, regions.Knight.Value);
        Check(SceneRegions.FromObjectDetections([enemy], Extent).Knight is null);
    }

    public static void TestWeakKnightPolicy()
    {
        var weak = Knight(.08f) with { Box = new(.45, .82, .05, .04) };
        Check(SceneRegions.FromObjectDetections([weak], Extent).Knight is null);
        Rect(SceneRegions.FromObjectDetections([weak], Extent, knightMinimumConfidence: .05).Knight,
            new(288, 295.2, 32, 14.4));
        Check(SceneRegions.FromObjectDetections([Knight(.049f)], Extent, knightMinimumConfidence: .05).Knight is null);
        Check(SceneRegions.FromObjectDetections([Knight(float.NaN)], Extent).Knight is null);
        Check(SceneRegions.FromObjectDetections([Knight(float.PositiveInfinity)], Extent).Knight is null);
    }

    public static void TestHudGeometry()
    {
        var knight = new StencilRect(280, 192, 34, 48);
        var regions = SceneRegions.Hud(Extent, knight);
        Check(regions.OmittedRects.Count == 4 && regions.FocusPrompt is null);
        Rect(regions.Health, new(78.08, 25.2, 83.2, 16.92));
        Rect(regions.Geo, new(76.8, 51.12, 37.12, 16.2));
        Rect(regions.Mana, new(39.68, 23.4, 38.4, 43.2));
        Check(SceneRegions.Hud(Extent, null, -1).Geo == SceneRegions.Hud(Extent, null, 1).Geo);
        Check(SceneRegions.Hud(Extent, null, 9).Geo == SceneRegions.Hud(Extent, null, 6).Geo);
        Rect(SceneRegions.FixedFocusPrompt(Extent), new(259, 313, 151, 38));
        var prompt = new StencilRect(200, 300, 100, 20); var particle = new StencilRect(400, 120, 3, 3);
        var extra = SceneRegions.Hud(Extent, knight, focusPrompt: prompt, particles: [particle]);
        Check(extra.OmittedRects.SequenceEqual(new[] { knight, prompt, regions.Health, regions.Geo, regions.Mana, particle }));
        Check((extra with { HudStencilRects = [] }).OmittedRects.SequenceEqual(new[] { knight, prompt, particle }));
        Check(SceneRegions.Startup().OmittedRects.Count == 0);
        var shifted = SceneRegions.Hud(new(10, 20, 640, 360), knight);
        Rect(shifted.Health, regions.Health.Offset(10, 20));
    }

    public static void TestPersistentAtlasHud()
    {
        var regions = SceneRegions.AtlasGameplay([], Extent);
        Check(regions.OmittedRects.SequenceEqual(new[] { new StencilRect(0, 0, 256, 90) }));
        static bool Contains(StencilRect rect, int x, int y) => x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;
        Check(Contains(regions.Health, 80, 35) && Contains(regions.Mana, 45, 50) && Contains(regions.Geo, 80, 60));
        var retired = new[] { new ObjectDetection("game.health", new(.08, .03, .20, .08), .9f),
            new("game.mana", new(.02, .03, .10, .16), .9f), new("game.geo", new(.08, .13, .13, .08), .9f) };
        Check(SceneRegions.AtlasGameplay(retired, Extent).OmittedRects.SequenceEqual(regions.OmittedRects));
        var hud = new HudStencilResult([], null, [], [new(300, 20, 20, 20), new(325, 20, 20, 20)],
            [new(40, 30, 20, 30)], new(80, 70, 30, 10), false, 1);
        var matched = SceneRegions.AtlasGameplay([], Extent, hudStencil: hud);
        Rect(matched.Health, new(300, 20, 45, 20)); Rect(matched.Mana, new(40, 30, 20, 30)); Rect(matched.Geo, hud.Geo);
        Check(matched.OmittedRects.Skip(1).SequenceEqual(hud.OmittedRects));
        var empty = SceneRegions.AtlasGameplay([], Extent, hudStencil: hud with { HealthMasks = [], ManaMasks = [], Geo = default });
        Rect(empty.Health, regions.Health);
        Check(empty.Mana.IsEmpty && empty.Geo.IsEmpty, "Present-but-empty stencil must not revive legacy mana/Geo masks.");
    }

    public static void TestMapFrameHudPixels()
    {
        var source = Solid(padding: 12);
        var regions = SceneRegions.AtlasGameplay([], Extent);
        var output = FrameRegionRenderer.MapFrame(source, regions, [new(400, 200, 10, 10)]);
        foreach (var (x, y) in new[] { (80, 35), (45, 50), (80, 60), (405, 205) }) Check(Alpha(output, x, y) == 0);
        Check(Alpha(output, 320, 180) == 255 && Alpha(source, 80, 35) == 255);
        Check(output.CaptureId == source.CaptureId && output.ObservedAt == source.ObservedAt && output.Stride == source.Stride);
        Check(output.Pixels[640 * 4] == 177 && output.Pixels[359 * output.Stride + 640 * 4] == 177);
    }

    public static void TestMapFrameDetectedRectangles()
    {
        // Exact Mac CameraSolverTests fixture, flipped into top-origin pixels.
        var source = Solid(80, 50);
        var regions = new SceneRegions(new(5, 37, 8, 8), new(20, 7, 10, 8),
            new(35, 7, 10, 8), new(50, 7, 10, 8), Particles: [new(64, 35, 8, 8)]);
        var output = FrameRegionRenderer.MapFrame(source, regions);
        foreach (var (x, y) in new[] { (8, 42), (24, 12), (39, 12), (54, 12), (68, 39) })
            Check(Alpha(output, x, y) == 0);
        foreach (var x in new[] { 40, 15, 65 }) Check(Alpha(output, x, 30) == 255);
    }

    public static void TestOpaqueBlackScenery()
    {
        var frame = Solid(80, 50, 0);
        for (var y = 0; y < 50; y++) for (var x = 40; x < 80; x++)
        { var i = y * frame.Stride + x * 4; frame.Pixels[i] = 102; frame.Pixels[i + 1] = 178; frame.Pixels[i + 2] = 51; }
        var output = FrameRegionRenderer.MapFrame(frame, SceneRegions.Startup());
        foreach (var x in new[] { 10, 38, 60 }) Check(Alpha(output, x, 25) == 255);
        Check(output.Pixels.SequenceEqual(frame.Pixels));
        var masked = FrameRegionRenderer.MapFrame(frame, SceneRegions.Startup(), [new(8, 20, 4, 8)]);
        Check(Alpha(masked, 10, 25) == 0 && Alpha(masked, 38, 25) == 255);
    }

    public static void TestGeometryClipping()
    {
        Rect(SceneRegions.ImageRect(new("game.playable-knight", new(.6, .5, -.2, -.3), .9f), 640, 360), new(256, 72, 128, 108));
        Rect(SceneRegions.ImageRect(new("game.playable-knight", new(-.1, .9, .3, .3), .9f), 640, 360), new(0, 324, 128, 36));
        Check(SceneRegions.ImageRect(Knight() with { Box = new(2, 2, 1, 1) }, 640, 360) is null);
        Check(SceneRegions.ImageRect(Knight() with { Box = new(double.NaN, 0, 1, 1) }, 640, 360) is null);
        var result = SceneRegions.PixelRectangles(640, 360, [new(-3.4, -1.2, 5.8, 6), new(638.5, 358.5, 20, 20),
            new(double.NaN, 0, 1, 1), new(1000, 0, 10, 10), new(0, 0, -2, 5), new(0, 0, double.PositiveInfinity, 1)]);
        Check(result.SequenceEqual(new[] { new PixelRect(0, 0, 3, 5), new PixelRect(638, 358, 2, 2) }));
        // Raster dimensions follow the actual capture, never the editor's fixed 640x360 canvas.
        Rect(SceneRegions.FromObjectDetections([Knight()], new(0, 0, 1280, 720)).Knight, new(640, 288, 64, 72));
    }

    public static void TestLiveMaskPersistence()
    {
        var unrelated = new ObjectDetection("world.sign", new(.7, .4, .05, .1), .99f);
        var masks = GameplaySceneMasks.Create(640, 360, [unrelated, Knight()], null);
        Rect(masks.AtlasRegions.Knight, new(320, 144, 32, 36));
        Check(masks.AtlasExclusions.Count == 2 && masks.GroundExclusions.Count == 2);
        var frame = Solid();
        var mosaic = new GroundTruthAtlasMosaic(1, 64, 0);
        Check(mosaic.AddFrame(frame, Sample(), masks.AtlasExclusions));
        Check(!mosaic.TryReadPixel(15, -20, out _, out _, out _, out _), "Knight entered the persisted atlas.");
        Check(mosaic.TryReadPixel(135, -20, out _, out _, out _, out _), "Unrelated model object erased room texture.");
        Check(mosaic.TryReadPixel(80, -160, out _, out _, out _, out _), "HUD mask erased the entire top edge.");
        var weak = GameplaySceneMasks.Create(640, 360, [Knight(.08f)], null);
        Check(weak.AtlasRegions.Knight is null && weak.GroundKnight is not null);
        var grid = LowResolutionMotionGrid.FromFrame(frame, excludedRectangles: weak.GroundExclusions);
        Check(grid.Luma[16 * grid.Width + 33] == 0 && grid.Luma[16 * grid.Width + 45] == 100);
        // Later unoccluded observations can fill the omitted region. No stale mask is cached.
        var laterFrame = Solid(captureId: 43, observedAt: DateTimeOffset.UnixEpoch.AddSeconds(4));
        var laterSample = Sample() with { Sequence = 2, UnityFrame = 2, UnityRealtime = 2 };
        Check(mosaic.AddFrame(laterFrame, laterSample, weak.AtlasExclusions));
        Check(mosaic.TryReadPixel(15, -20, out _, out _, out _, out var observations) && observations == 1);
    }

    public static void TestReceiverHeroMask()
    {
        var sample = Sample() with { HeroScreenX = 400, HeroScreenY = 200, ProjectionPixelWidth = 1280, ProjectionPixelHeight = 720 };
        var regions = SceneRegions.HackerAtlas(sample, 640, 360);
        Rect(regions.Knight, new(152, 185.76, 96, 128));
        Rect(SceneRegions.HackerAtlas(sample, 1280, 720).Knight, new(304, 371.52, 192, 256));
        var masked = FrameRegionRenderer.MapFrame(Solid(), regions);
        Check(Alpha(masked, 200, 260) == 0 && Alpha(masked, 200, 120) == 255, "Receiver Y was not converted from bottom-origin.");
        var mosaic = new GroundTruthAtlasMosaic(1, 64, 0);
        var current = SceneRegions.HackerAtlas(Sample(), 640, 360);
        Check(mosaic.AddFrame(Solid(), Sample(), current.PixelExclusions(640, 360)));
        Check(!mosaic.TryReadPixel(0, 0, out _, out _, out _, out _));
        foreach (var unavailable in new[] { sample with { HeroAvailable = false }, sample with { HeroScreenX = null },
            sample with { HeroScreenY = double.NaN }, sample with { ProjectionPixelWidth = 0 }, sample with { ProjectionPixelHeight = null } })
            Check(SceneRegions.HackerAtlas(unavailable, 640, 360).Knight is null);
    }
}
