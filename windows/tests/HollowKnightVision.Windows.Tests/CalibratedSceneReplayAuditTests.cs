using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.Core;
using Scene = HollowKnightVision.Windows.Core.CalibratedSceneMotionTracker;

internal static class CalibratedSceneReplayAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private sealed record Source(Guid Id, RecordedVisualFrameArchive? VisualFrameArchive);
    private sealed record VisualSample(double CaptureTimestamp, string? PoseSource, double? PublishedCameraX,
        double? PublishedCameraY, double? LocalReferenceCaptureTimestamp, double? LocalMatchImageDX, double? LocalMatchImageDY);
    private sealed record Samples(VisualSample[] TrackingSamples);
    private sealed record Row(int Index, double Timestamp, double Offset, bool EnteredFloorless,
        bool GroundCalibrationSupplied, bool ExactMaskAvailable, CalibratedSceneMotionEvidence? Measurement);

    internal static void TestArchivedFallWhenRequested()
    {
        var input = RequiredPath("HKV_CALIBRATED_SCENE_AUDIT_INPUT");
        var trackingPath = RequiredPath("HKV_CALIBRATED_SCENE_AUDIT_TRACKING");
        var output = RequiredPath("HKV_CALIBRATED_SCENE_AUDIT_OUTPUT");
        if (Path.Exists(output)) throw new IOException("Choose a new path for the calibrated scenery report.");
        var recording = File.ReadAllBytes(input);
        var tracking = File.ReadAllBytes(trackingPath);
        var reader = Open(input, recording);
        var samples = Parse<Samples>(tracking);
        var withMasks = Environment.GetEnvironmentVariable("HKV_CALIBRATED_SCENE_AUDIT_MASKS") == "1";
        var rows = Replay(reader, SelectFrames(reader, Environment.GetEnvironmentVariable("HKV_CALIBRATED_SCENE_AUDIT_INDICES")),
            samples, withMasks);
        var floorlessMeasurements = rows.Count(row => row.EnteredFloorless && row.Measurement is not null);
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sourcePathID = reader.Manifest.PathId,
            recordingSHA256 = Hash(recording),
            trackingSHA256 = Hash(tracking),
            withMasks,
            floorlessMeasurements,
            rows
        }, Json), overwrite: false);
        Check(floorlessMeasurements > 0, $"The selected replay yielded no floorless measurements. See {output}.");
        Console.WriteLine($"Calibrated scene replay checked {rows.Count} exact frames with {floorlessMeasurements} floorless measurements.");
    }

    internal static void TestSceneReplayStopsReadingGroundAfterFloorLoss()
    {
        var root = Directory.CreateTempSubdirectory("hkv-calibrated-scene-replay-");
        try
        {
            var recorder = new InputPathRecorder();
            recorder.Start(1, DateTimeOffset.UnixEpoch, visualArchiveRoot: root.FullName);
            Append(1, 0, 0);
            Append(1.05, 2, -3);
            Append(1.10, 3, -5);
            Append(1.15, 4, -7);
            var path = new RecordedInputPathStore(root.FullName).Save(recorder.Stop(1.2)!);
            var reader = Open(path, File.ReadAllBytes(path));
            var frozen = new Samples(
            [
                new(1, "ground", 0, 0, null, null, null),
                new(1.05, "ground", -8, -12, 1, 8, -12),
                new(1.10, "motionBridge", 1000, 1000, null, null, null),
                new(1.15, "ground", 3000, 3000, 1.10, 4, -8)
            ]);
            var rows = Replay(reader, SelectFrames(reader, "0,3"), frozen, withMasks: false);
            Check(rows.Select(row => row.Index).SequenceEqual([0, 1, 2, 3])
                && rows.Where(row => row.GroundCalibrationSupplied).Select(row => row.Index).SequenceEqual([1]),
                "The audit must join exact frames and use only frozen pre-loss ground calibration.");
            var measured = rows.Where(row => row.EnteredFloorless).Select(row => row.Measurement).ToArray();
            Check(measured is [{ HorizontalMeasured: true, VerticalMeasured: true, SupportedPatches: >= 3 } first,
                { HorizontalMeasured: true, VerticalMeasured: true, SupportedPatches: >= 3 } last]
                && Math.Abs(first.Position.X + 12) < .5 && Math.Abs(first.Position.Y + 20) < .5
                && Math.Abs(last.Position.X + 16) < .5 && Math.Abs(last.Position.Y + 28) < .5,
                "Calibrated scenery must follow the measured fall and ignore later published camera poses.");

            void Append(double timestamp, int dx, int dy)
            {
                const int width = 640, height = 360;
                var grid = CalibratedSceneMotionTests.Grid(dx, dy);
                var pixels = new byte[width * height * 4];
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var index = (y * width + x) * 4;
                    var value = (byte)grid.Pixels[y / 4 * grid.Width + x / 4];
                    pixels[index] = pixels[index + 1] = pixels[index + 2] = value;
                    pixels[index + 3] = byte.MaxValue;
                }
                recorder.RecordVisualFrame(new(width, height, width * 4, pixels, 0, DateTimeOffset.UnixEpoch),
                    timestamp, timestamp, 1, null);
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private static IReadOnlyList<VisualReplayFrame> SelectFrames(VisualReplayArchiveReader reader, string? indices)
    {
        var frames = reader.Manifest.Frames;
        var start = frames[0].CaptureTimestamp;
        if (indices is null)
            return frames.Where(frame => frame.CaptureTimestamp - start is >= 156.5 and <= 158.61).ToArray();
        var ends = indices.Split(',');
        if (ends is not [var first, var last]
            || !int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var lower)
            || !int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var upper)
            || lower < 0 || upper < lower || upper >= frames.Count || upper - lower >= 600)
            throw new InvalidDataException("Specify two inclusive archive indices covering at most 600 frames.");
        return frames.Skip(lower).Take(upper - lower + 1).ToArray();
    }

    private static List<Row> Replay(VisualReplayArchiveReader reader, IReadOnlyList<VisualReplayFrame> frames,
        Samples samples, bool withMasks)
    {
        var byTime = samples.TrackingSamples.ToLookup(sample => sample.CaptureTimestamp);
        var masks = reader.Manifest.GroundInputs.ToLookup(input => (input.CaptureGeneration, input.CaptureTimestamp));
        var scene = new Scene();
        var rows = new List<Row>();
        var floorless = false;
        ulong? generation = null;
        var started = reader.Manifest.Frames[0].CaptureTimestamp;
        foreach (var frame in frames)
        {
            if (byTime[frame.CaptureTimestamp].LastOrDefault() is not { } trace) continue;
            if (generation != frame.CaptureGeneration)
            {
                scene.Reset();
                generation = frame.CaptureGeneration;
            }
            var image = reader.ReadImage(frame);
            var mask = masks[(frame.CaptureGeneration, frame.CaptureTimestamp)].LastOrDefault();
            var restored = VisualReplayGroundInput.RestoreMasks(withMasks ? mask : null, image);
            var exclusions = mask?.WindowsAtlasExcludingRects is not null ? restored.GroundExclusions : restored.AtlasExclusions;
            var pixels = GroundPixels.Luma(image) ?? throw new InvalidDataException("The scene replay image has no luma.");
            var grid = Scene.FromLuma(pixels, image.Width, image.Height, exclusions)
                ?? throw new InvalidDataException("The scene replay image cannot form a motion grid.");
            floorless |= trace.PoseSource != "ground";
            var ground = !floorless && trace is { PublishedCameraX: { } x, PublishedCameraY: { } y,
                LocalReferenceCaptureTimestamp: { } reference, LocalMatchImageDX: { } dx, LocalMatchImageDY: { } dy }
                ? new Scene.GroundCalibration(new(x, y), reference, new(dx, dy)) : null;
            var measurement = scene.Observe(grid, frame.CaptureTimestamp, frame.SolveWidth, ground);
            rows.Add(new(frame.Index, frame.CaptureTimestamp, frame.CaptureTimestamp - started,
                floorless, ground is not null, mask is not null, measurement));
        }
        return rows;
    }

    private static VisualReplayArchiveReader Open(string input, byte[] recording)
    {
        var source = Parse<Source>(recording);
        return new VisualReplayArchiveReader(Path.GetDirectoryName(input)!,
            source.VisualFrameArchive ?? throw new InvalidDataException("The recording has no visual archive."), source.Id);
    }

    private static T Parse<T>(byte[] bytes) where T : class => JsonSerializer.Deserialize<T>(bytes, Json)
        ?? throw new InvalidDataException("The scenery replay input is empty.");

    private static string RequiredPath(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } path
        ? Path.GetFullPath(path) : throw new InvalidOperationException($"Set {name}.");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
