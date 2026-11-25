using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class LiveIslandReplayAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record TileDigest(int X, int Y, int Width, int Height, int WrittenPixels, long Observations, string Sha256);
    private sealed record ReopenReport(int Observations, int Islands, int RawGroundSpans, int GroundStrokes,
        int Revision, int RegisteredFrames, int TileCount, string PixelDigest, TileDigest[] Tiles);

    internal static void TestRecordedOwnedWorldReopensWithIdenticalPixels()
    {
        var root = Path.GetFullPath(Required("HKV_WORLD_REOPEN_AUDIT_INPUT"));
        var result = VerifyReopen(root);
        if (Environment.GetEnvironmentVariable("HKV_WORLD_REOPEN_AUDIT_OUTPUT") is { Length: > 0 } output)
            AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(result, Json), overwrite: false);
        Console.WriteLine($"Atlas reopen verified {result.Observations} screenshots, {result.Islands} islands, "
            + $"{result.RawGroundSpans} ground spans, and {result.TileCount} identical tiles.");
    }

    internal static void TestWorldReopenPreservesMaskedPixelsAndSavedGround()
    {
        var root = Directory.CreateTempSubdirectory("hkv-world-reopen-");
        try
        {
            var worldRoot = Path.Combine(root.FullName, "world");
            var store = new LiveWorldSessionStore(worldRoot);
            var tracker = new LiveWorldTracker();
            AddFrame(1, blue: false, []);
            AddFrame(2, blue: true, [new(0, 0, 32, 36)]);
            var snapshot = tracker.Snapshot;
            Check(store.Commit(snapshot, 0, snapshot.MapRevision), "Publish the synthetic recorded world.");
            File.WriteAllBytes(Path.Combine(root.FullName, "world-snapshot.json"), LiveWorldJson.WriteSnapshot(snapshot));
            var report = VerifyReopen(root.FullName, atlas =>
            {
                Check(atlas.TryReadPixel(-8, 0, out var blue, out _, out var red, out _) && (blue, red) == (0, 255)
                    && atlas.TryReadPixel(8, 0, out blue, out _, out red, out _) && (blue, red) == (255, 0),
                    "Recomposition must retain older masked columns and place the newer exposed pixels.");
            });
            Check(report is { Observations: 2, Islands: 1, RawGroundSpans: 2, GroundStrokes: 1, TileCount: > 0 },
                "The reopened graph and overlapping owned ground must retain their saved identity.");

            void AddFrame(int timestamp, bool blue, StencilRect[] masks)
            {
                const int width = 64, height = 36;
                var pixels = new byte[width * height * 4];
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    pixels[offset + (blue ? 0 : 2)] = byte.MaxValue;
                    pixels[offset + 3] = byte.MaxValue;
                }
                var image = new BgraFrame(width, height, width * 4, pixels, timestamp, DateTimeOffset.UnixEpoch);
                var source = store.Append(image, timestamp, default, width, masks);
                var geometry = new LiveWorldGroundGeometry([new(17, new(0, 8), new(64, 8))]);
                var evidence = new LiveWorldCaptureEvidence(timestamp, geometry) { MeasuredLineage = 1 };
                _ = tracker.Ingest(source.Id, timestamp, image, default, width, exclusions: masks,
                    searchForClosure: false, captureGeneration: 1, captureEvidence: evidence);
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private static ReopenReport VerifyReopen(string root, Action<GroundTruthAtlasMosaic>? inspect = null)
    {
        var worldRoot = Path.Combine(root, "world");
        var snapshotPath = Path.Combine(root, "world-snapshot.json");
        var worldPath = Path.Combine(worldRoot, "scene.snapshot");
        var manifestPath = Path.Combine(worldRoot, "manifest.json");
        var snapshotBytes = File.ReadAllBytes(snapshotPath);
        var worldBytes = File.ReadAllBytes(worldPath);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var expected = LiveWorldJson.ReadSnapshot(snapshotBytes);
        var graphBytes = LiveWorldJson.WriteSnapshot(expected);
        var expectedGround = LiveWorldGroundOverlay.Project(expected);
        TileDigest[]? priorTiles = null;
        var registered = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reopened = LiveWorldSessionStore.ValidateExisting(worldRoot);
            Check(reopened.SceneRevision == expected.MapRevision
                && LiveWorldJson.WriteSnapshot(reopened.Snapshot).AsSpan().SequenceEqual(graphBytes),
                "Reopening must preserve the entire saved graph and its revision.");
            Check(LiveWorldGroundOverlay.Project(reopened.Snapshot).SequenceEqual(expectedGround),
                "Reopening must preserve projected owned ground strokes.");
            using var evidence = SceneSessionStore.OpenReadOnly(worldRoot);
            var sources = evidence.Manifest.Observations.ToDictionary(source => source.Id);
            var atlas = WindowsLiveWorldPipeline.RebuildAtlas(reopened.Snapshot, id => evidence.Source(sources[id]),
                pixelsPerWorldUnit: 1, tileSize: LiveTiledAtlas.TileSize);
            Check(atlas.TileCount > 0, "The recorded world must recompose visible Atlas tiles.");
            inspect?.Invoke(atlas);
            var tiles = atlas.Snapshot().OrderBy(tile => tile.Coordinate.X).ThenBy(tile => tile.Coordinate.Y)
                .Select(tile => new TileDigest(tile.Coordinate.X, tile.Coordinate.Y, tile.Frame.Width, tile.Frame.Height,
                    tile.WrittenPixelCount, tile.ObservationCount, Hash(tile.Frame.Pixels))).ToArray();
            Check(priorTiles is null || priorTiles.SequenceEqual(tiles), "Reopening changed the pixels or coordinates of a saved tile.");
            priorTiles = tiles;
            registered = checked((int)atlas.AcceptedFrameCount);
        }
        Check(File.ReadAllBytes(snapshotPath).AsSpan().SequenceEqual(snapshotBytes)
            && File.ReadAllBytes(worldPath).AsSpan().SequenceEqual(worldBytes)
            && File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifestBytes),
            "The recorded world or manifest changed during read-only recomposition.");
        return new(expected.Observations.Count, expected.TrackingIslands.Count,
            expected.Observations.Sum(observation => observation.GroundGeometry?.Lines.Count ?? 0),
            expectedGround.Count, expected.MapRevision, registered, priorTiles!.Length,
            Hash(JsonSerializer.SerializeToUtf8Bytes(priorTiles, Json)), priorTiles);
    }

    private static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {key}.");

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
