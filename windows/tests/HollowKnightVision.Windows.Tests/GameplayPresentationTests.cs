using HollowKnightVision.Windows.Core;

internal static class GameplayPresentationTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static BgraFrame Frame(long id, byte value, byte alpha = 255)
    {
        var pixels = new byte[40 * 30 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = alpha;
        }
        return new(40, 30, 160, pixels, id, DateTimeOffset.UnixEpoch.AddSeconds(id / 30d));
    }

    private static ReceiverGroundTruthSample Sample(BgraFrame frame) =>
        new VisualGameplayTracker().Observe(frame, null);

    public static void TestDelayedAnalysis()
    {
        var state = new GameplayPresentationState();
        // Analysis always finishes three captures behind. None of these results
        // would pass the old equality-to-latest-capture gate.
        for (var id = 1L; id <= 60; id++)
        {
            var latest = Frame(id + 3, (byte)(id + 3));
            state.Capture(latest);
            Check(state.TryAcceptAnalysis(Frame(id, (byte)id)),
                $"Live position stalled at analysis frame {id} while capture advanced.");
            Check(ReferenceEquals(state.LatestFrame, latest), "Old analysis replaced live pixels.");
            Check(!state.TryAcceptAnalysis(Frame(id - 1, 0)), "Out-of-order position rewound the view.");
            Check(!state.TryAcceptAnalysis(Frame(id, 0)), "Repeated position was applied twice.");
        }
        Check(!state.TryAcceptAnalysis(Frame(99, 0)), "Future analysis was accepted.");
        state.Reset();
        Check(state.LatestFrame is null && !state.TryAcceptAnalysis(Frame(1, 0)),
            "Reset retained an old capture or pose.");
        state.Capture(Frame(1, 8));
        Check(state.TryAcceptAnalysis(Frame(1, 8)), "New capture epoch cannot restart IDs.");
    }

    public static void TestNewestPixels()
    {
        var atlas = new GroundTruthAtlasMosaic(pixelsPerWorldUnit: 1, tileSize: 64, sourceTopInsetFraction: 0);
        var old = Frame(1, 230);
        Check(atlas.AddFrame(old, Sample(old)), "Initial observation failed.");
        var black = Frame(2, 0);
        // World (0,0) maps to source (20,15); keep it masked while its neighbor changes.
        Check(atlas.AddFrame(black, Sample(black), [new(20, 15, 1, 1)]), "Masked observation failed.");
        Check(Pixel(atlas, 0, 0) == 230 && Pixel(atlas, 1, 0) == 0,
            "Black was treated as missing evidence, or a masked pixel overwrote old scenery.");
        var transparent = Frame(3, 40, 0);
        Check(atlas.AddFrame(transparent, Sample(transparent)), "Transparent observation failed.");
        Check(Pixel(atlas, 0, 0) == 230 && Pixel(atlas, 1, 0) == 0,
            "Transparent holes erased existing pixels.");
        var bright = Frame(4, 255);
        Check(atlas.AddFrame(bright, Sample(bright)), "Newest bright observation failed.");
        Check(Pixel(atlas, 0, 0) == 255 && Pixel(atlas, 1, 0) == 255,
            "A newer bright pixel waited for consensus.");
        var revision = atlas.Revision;
        atlas.AddFrame(black, Sample(black));
        Check(atlas.Revision == revision && Pixel(atlas, 0, 0) == 255,
            "Replayed capture overwrote a newer pixel.");
        var restored = new GroundTruthAtlasMosaic(pixelsPerWorldUnit: 1, tileSize: 64, sourceTopInsetFraction: 0);
        foreach (var tile in atlas.Snapshot()) restored.ImportTile(tile.Coordinate, tile.Frame);
        restored.AddFrame(black, Sample(black));
        Check(Pixel(restored, 0, 0) == 0, "Restored atlas resisted newest black pixel.");
        Check(atlas.RetainedObservationCount == 0 && atlas.OutstandingObservationBufferCount == 0,
            "Newest policy retained temporal image buffers.");
    }

    public static void TestNewestRollback()
    {
        var fail = false;
        var atlas = new GroundTruthAtlasMosaic(pixelsPerWorldUnit: 1, tileSize: 64,
            sourceTopInsetFraction: 0, temporalCommitCheckpoint: () =>
            {
                if (fail) throw new ApplicationException("test checkpoint");
            });
        var old = Frame(1, 200);
        var next = Frame(2, 20);
        atlas.AddFrame(old, Sample(old));
        var revision = atlas.Revision;
        fail = true;
        try
        {
            atlas.AddFrame(next, Sample(next));
            throw new InvalidOperationException("Checkpoint did not fail.");
        }
        catch (ApplicationException) { }
        Check(atlas.Revision == revision && Pixel(atlas, 0, 0) == 200
            && atlas.OutstandingObservationBufferCount == 0, "Failed write leaked pixels or memory.");
        fail = false;
        atlas.AddFrame(next, Sample(next));
        Check(Pixel(atlas, 0, 0) == 20, "Failed capture could not be retried.");
    }

    public static void TestLiveAtlasDefault()
    {
        var pipeline = new LiveAtlasPresentationPipeline();
        pipeline.SubmitAtlasObservation(Frame(1, 240), new(0, 0), 40, 1, true);
        pipeline.SubmitAtlasObservation(Frame(2, 26), new(0, 0), 40, 1.1, true);
        var result = pipeline.Process(Frame(3, 100), new(5, 0), 40);
        Check(result.Atlas is { Tiles.Count: > 0 }, "Atlas is absent.");
        var tile = result.Atlas!.Tiles[0].Image;
        var found = false;
        for (var i = 0; i < tile.Pixels.Length; i += 4)
        {
            if (tile.Pixels[i + 3] == 0) continue;
            found = true;
            Check(tile.Pixels[i] == 26, "Default pipeline kept old color after newest dark observation.");
        }
        Check(found, "No opaque dark pixels were written.");
    }

    private static byte Pixel(GroundTruthAtlasMosaic atlas, int x, int y)
    {
        Check(atlas.TryReadPixel(x, y, out var blue, out _, out _, out _), "Missing world pixel.");
        return blue;
    }
}
