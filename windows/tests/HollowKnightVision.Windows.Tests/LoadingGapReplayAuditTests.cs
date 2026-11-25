using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class LoadingGapReplayAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record GridFrame(double CaptureTimestamp, int Width, int Height, byte[] Luma);
    private sealed record DirectionInput(int? HorizontalInput);
    private sealed record InputFrame(double CaptureTimestamp, DirectionInput? Input);
    private sealed record Archive(GridFrame[] LowResolutionFrames, InputFrame[] CoarseMotionSamples);
    private sealed record DoorwayArchive(Guid Id, RecordedVisualFrameArchive? VisualFrameArchive);
    private sealed record DoorwayFloor(int Row, int MinimumX, int MaximumX);
    private sealed record DoorwayEvidence(int ImageIndex, double CaptureTimestamp, double Offset, DoorwayFloor[] FloorLines,
        double? LeftBlackFraction, double? RightBlackFraction);

    internal sealed record MeasuredCut(double DepartureSeconds, double ArrivalSeconds, int Direction);

    internal static void TestArchivedDoorwayFloorCandidates()
    {
        var input = Path.GetFullPath(GetSetting("HKV_DOOR_FLOOR_AUDIT_INPUT"));
        var output = Path.GetFullPath(GetSetting("HKV_DOOR_FLOOR_AUDIT_OUTPUT"));
        if (Path.Exists(output)) throw new IOException("Choose a new path for the doorway floor report.");
        var encodedRange = Environment.GetEnvironmentVariable("HKV_DOOR_FLOOR_AUDIT_RANGE") ?? "[142,157]";
        if (JsonSerializer.Deserialize<double[]>(encodedRange, Json) is not [var start, var end]
            || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end - start is < 0 or > 60)
            throw new InvalidDataException("Select a finite doorway window of at most 60 seconds.");
        var bytes = File.ReadAllBytes(input);
        var reader = OpenDoorwayArchive(input, bytes);
        var rows = ReplayDoorwayFloors(reader, start, end);
        if (rows.Count == 0) throw new InvalidDataException("The doorway window has no exact archived ground inputs.");
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sourcePathID = reader.Manifest.PathId,
            recordingSHA256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            rows
        }, Json), overwrite: false);
        Console.WriteLine($"Doorway floor image audit checked {rows.Count} frames.");
    }

    internal static void TestDoorwayFloorReplayNeedsExactOwnedMasks()
    {
        var root = Directory.CreateTempSubdirectory("hkv-recorded-doorway-floors-");
        try
        {
            var recorder = new InputPathRecorder();
            recorder.Start(1, DateTimeOffset.UnixEpoch, visualArchiveRoot: root.FullName);
            const int width = 320, height = 180;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var value = x < 80 ? (byte)0 : y < 100 ? (byte)185 : (byte)25;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = byte.MaxValue;
            }
            var frame = new BgraFrame(width, height, width * 4, pixels, 0, DateTimeOffset.UnixEpoch);
            var regions = SceneRegions.Startup();
            var times = new[] { 1, 1.05, 1.12, 1.25, 1.4 };
            for (var index = 0; index < times.Length; index++)
                recorder.RecordVisualFrame(frame, times[index], times[index], index == 2 ? 2UL : 1UL, null);
            foreach (var time in times.Take(4))
                recorder.RecordVisualGroundInput(time, 1, height, new(regions, regions, [], [], null), null);
            var path = new RecordedInputPathStore(root.FullName).Save(recorder.Stop(1.5)!);
            var rows = ReplayDoorwayFloors(OpenDoorwayArchive(path, File.ReadAllBytes(path)), 0, 1);
            if (!rows.Select(row => row.ImageIndex).SequenceEqual([0, 3])
                || rows.Any(row => row.LeftBlackFraction != .25 || row.RightBlackFraction is not null
                    || !row.FloorLines.Any(line => line.Row == 99 && line.MinimumX <= 90 && line.MaximumX >= 300)))
                throw new InvalidOperationException("Archive replay must space samples, keep only exact owned masks, and show the observed floor and matte. "
                    + JsonSerializer.Serialize(rows));
        }
        finally { root.Delete(recursive: true); }
    }

    private static VisualReplayArchiveReader OpenDoorwayArchive(string path, byte[] bytes)
    {
        var source = JsonSerializer.Deserialize<DoorwayArchive>(bytes, Json);
        if (source?.VisualFrameArchive is not { } archive) throw new InvalidDataException("The recording has no visual archive.");
        return new(Path.GetDirectoryName(path)!, archive, source.Id);
    }

    private static List<DoorwayEvidence> ReplayDoorwayFloors(VisualReplayArchiveReader reader, double start, double end)
    {
        var frames = reader.Manifest.Frames;
        var origin = frames[0].CaptureTimestamp;
        var masks = reader.Manifest.GroundInputs.ToLookup(input => (input.CaptureGeneration, input.CaptureTimestamp));
        var rows = new List<DoorwayEvidence>();
        var previous = double.NegativeInfinity;
        foreach (var frame in frames)
        {
            var offset = frame.CaptureTimestamp - origin;
            if (offset < start || offset > end || offset - previous < .10
                || masks[(frame.CaptureGeneration, frame.CaptureTimestamp)].LastOrDefault() is not { } input) continue;
            var image = reader.ReadImage(frame);
            var regions = VisualReplayGroundInput.RestoreMasks(input, image);
            var omissions = input.WindowsAtlasExcludingRects is null ? regions.AtlasExclusions : regions.GroundExclusions;
            var analysis = GroundLineDetector.Compare(GroundLineDetector.Analyze(image, omissions));
            var lines = GroundLineDetector.RejectKnownForegroundLines(
                GroundLineDetector.CleanFloorLines(analysis, input.Tuning ?? GroundTheoryTuning.Default),
                image.Height, omissions, regions.GroundKnight);
            var bands = FrameRegionRenderer.SignalProfile(image).EdgeBlackBands;
            rows.Add(new(frame.Index, frame.CaptureTimestamp, offset, lines.Select(line =>
                new DoorwayFloor(line.Row, line.XSpan.Start, line.XSpan.EndInclusive)).ToArray(),
                bands?.Left?.WidthFraction, bands?.Right?.WidthFraction));
            previous = offset;
        }
        return rows;
    }

    internal static void TestArchivedVisualCuts()
    {
        var cuts = Replay(GetSetting("HKV_SCENE_CUT_AUDIT_INPUT"));
        Console.WriteLine($"Visual cut arrivals: {string.Join(", ", cuts.Select(cut => cut.ArrivalSeconds))}");
        if (cuts.Count != 1)
            throw new InvalidDataException($"The first-bench recording must contain one loading cut. Found {cuts.Count}.");
    }

    internal static void TestArchivedLoadingGapsWhenRequested()
    {
        var input = Path.GetFullPath(GetSetting("HKV_LOADING_GAP_AUDIT_INPUT"));
        var output = Path.GetFullPath(GetSetting("HKV_LOADING_GAP_AUDIT_OUTPUT"));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The loading-gap report must use a different path from the recording.");
        var windows = JsonSerializer.Deserialize<double[][]>(GetSetting("HKV_LOADING_GAP_AUDIT_WINDOWS"), Json);
        if (windows is not { Length: > 0 } || windows.Any(window => window is not { Length: 2 }
            || !double.IsFinite(window[0]) || !double.IsFinite(window[1]) || window[0] > window[1]))
            throw new InvalidDataException("Supply at least one valid loading-gap arrival window.");

        var cuts = Replay(input);
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(cuts, Json));
        foreach (var window in windows)
        {
            if (!cuts.Any(cut => cut.ArrivalSeconds >= window[0] && cut.ArrivalSeconds <= window[1]))
                throw new InvalidDataException($"No image-confirmed arrival between {window[0]} and {window[1]} seconds.");
        }
    }

    internal static IReadOnlyList<MeasuredCut> Replay(string path)
    {
        using var stream = File.OpenRead(path);
        var archive = JsonSerializer.Deserialize<Archive>(stream, Json);
        if (archive?.LowResolutionFrames is not { Length: > 0 } frames || archive.CoarseMotionSamples is not { } motion)
            throw new InvalidDataException("The loading-gap recording must contain image grids and input samples.");
        var inputs = new Dictionary<double, int?>();
        foreach (var sample in motion)
            inputs[sample.CaptureTimestamp] = sample.Input?.HorizontalInput;

        var start = frames[0].CaptureTimestamp;
        var tracker = new VisualSceneDiscontinuityTracker();
        var cuts = new List<MeasuredCut>();
        foreach (var frame in frames)
        {
            var direction = inputs.GetValueOrDefault(frame.CaptureTimestamp) switch
            {
                -1 => VisualRoomDirection.Left,
                1 => VisualRoomDirection.Right,
                _ => (VisualRoomDirection?)null
            };
            var grid = new LowResolutionMotionGrid(frame.Width, frame.Height, frame.Luma);
            // Keep this audit image-only. Neither scene identity nor a saved pose belongs in the detector.
            if (tracker.Observe(grid, frame.CaptureTimestamp, direction, default) is { } cut)
            {
                cuts.Add(new(cut.DepartureTimestamp - start, cut.ArrivalTimestamp - start, (int)cut.Direction));
            }
        }
        return cuts;
    }

    private static string GetSetting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Set {name} to run the recorded loading-gap audit.");
}
