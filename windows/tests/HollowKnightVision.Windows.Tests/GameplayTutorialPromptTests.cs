using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.App;

internal static class GameplayTutorialPromptTests
{
    public static void TestPromptIsExcludedFromAtlasAndGround()
    {
        foreach (var scale in new[] { 1, 2 })
        {
            var frame = BuildFrame(scale, glyph: true, letters: true);
            var masks = GameplaySceneMasks.Create(frame, [], null);
            var mapped = FrameRegionRenderer.MapFrame(frame, masks.AtlasRegions);
            var terrain = GameplayTerrainAnalysis.Analyze(frame, masks);
            foreach (var x in new[] { 289, 335 })
            {
                Check(PixelAlpha(frame, x * scale, 323 * scale) == 255, "Source capture was modified.");
                Check(PixelAlpha(mapped, x * scale, 323 * scale) == 0, "Prompt pixel reached the atlas.");
                Check(terrain.Edges.ValidSourcePixels![323 * scale * frame.Width + x * scale] == 0,
                    "Prompt pixel reached ground analysis.");
            }
            Check(PixelAlpha(mapped, 400 * scale, 323 * scale) == 255, "Nearby scenery was masked.");
        }
    }

    public static void TestPromptBandRetainsSceneryWithoutCompletePrompt()
    {
        foreach (var (glyph, letters, square) in new[]
            { (false, false, false), (true, false, false), (false, true, false), (true, true, true) })
        {
            var frame = BuildFrame(1, glyph, letters, square);
            var masks = GameplaySceneMasks.Create(frame, [], null);
            Check(masks.AtlasRegions.TutorialPrompt is null, "An incomplete prompt was detected.");
            var mapped = FrameRegionRenderer.MapFrame(frame, masks.AtlasRegions);
            Check(PixelAlpha(mapped, 289, 323) == 255 && PixelAlpha(mapped, 335, 323) == 255,
                "Tutorial-band scenery was erased.");
        }
        var tiny = new BgraFrame(1, 1, 4, [255, 255, 255, 255], 0, DateTimeOffset.UnixEpoch);
        Check(GameplaySceneMasks.Create(tiny, [], null).GroundRegions.TutorialPrompt is null,
            "A one-pixel frame produced a tutorial prompt.");
    }

    internal static void TestRebuildExcludesSavedTutorialPrompt()
    {
        var root = Directory.CreateTempSubdirectory("hkv-rebuild-tutorial-prompt-");
        try
        {
            var store = new LiveWorldSessionStore(root.FullName);
            var tracker = new LiveWorldTracker();
            AddFrame(1, BuildFrame(1, glyph: false, letters: false));
            var oldCapture = AddFrame(2, BuildFrame(1, glyph: true, letters: true));
            var snapshot = tracker.Snapshot;
            Check(store.Commit(snapshot, 0, snapshot.MapRevision), "Could not commit the old Atlas fixture.");
            var evidencePath = Path.Combine(root.FullName, oldCapture.ImagePath);
            var archivedPixels = File.ReadAllBytes(evidencePath);

            var rebuilt = WindowsLiveWorldPipeline.RebuildAtlas(store.Load().Snapshot, store.Source, 1, 128);
            foreach (var x in new[] { 289, 335 })
                Check(rebuilt.TryReadPixel(x - 320, 323 - 180, out var blue, out var green, out var red, out _)
                    && (blue, green, red) == (76, 51, 25),
                    "A tutorial from saved evidence replaced the earlier room texture.");
            Check(rebuilt.TryReadPixel(400 - 320, 323 - 180, out var surroundingBlue, out _, out _, out _)
                && surroundingBlue == 76 && rebuilt.AcceptedFrameCount == 2,
                "Rebuilding skipped the unmasked parts of the old capture.");
            Check(File.ReadAllBytes(evidencePath).SequenceEqual(archivedPixels)
                && PixelAlpha(store.Source(oldCapture.Id), 289, 323) == 255,
                "Rebuilding modified the archived frame.");

            StoredFrameObservation AddFrame(int time, BgraFrame frame)
            {
                var source = store.Append(frame, time, default, frame.Width);
                tracker.Ingest(source.Id, time, frame, default, frame.Width, searchForClosure: false);
                return source;
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private static BgraFrame BuildFrame(int scale, bool glyph, bool letters, bool square = false)
    {
        var width = 640 * scale;
        var height = 360 * scale;
        var stride = width * BgraFrame.BytesPerPixel + 8;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = x / scale - 289;
            var dy = y / scale - 323;
            var letterColumn = x / scale - 309;
            var lit = glyph && (square ? Math.Abs(dx) <= 8 && Math.Abs(dy) <= 8 : dx * dx + dy * dy <= 49)
                || letters && letterColumn is >= 0 and < 33 && letterColumn % 6 < 3 && Math.Abs(dy) <= 3;
            var i = y * stride + x * BgraFrame.BytesPerPixel;
            pixels[i] = lit ? (byte)255 : (byte)76;
            pixels[i + 1] = lit ? (byte)255 : (byte)51;
            pixels[i + 2] = lit ? (byte)255 : (byte)25;
            pixels[i + 3] = 255;
        }
        return new(width, height, stride, pixels, 0, DateTimeOffset.UnixEpoch);
    }

    private static byte PixelAlpha(BgraFrame frame, int x, int y) =>
        frame.Pixels[y * frame.Stride + x * BgraFrame.BytesPerPixel + 3];

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
