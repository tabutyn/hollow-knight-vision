using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.Core;

internal static class RecordedTerrainDetectionAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private sealed record Source(Guid Id, RecordedVisualFrameArchive? VisualFrameArchive,
        PoseSample[]? TrackingSamples, RecordedCoarseMotionSample[]? CoarseMotionSamples);
    private sealed record SourceIsland(int Id, bool Measured);
    private sealed record PoseSample(double CaptureTimestamp, int? RoomId,
        double? PublishedCameraX, double? PublishedCameraY, SourceIsland? Island,
        bool PoseVerified, bool HasConfirmedGround);
    private sealed record Line(int Row, int X0, int X1, int[][] Evidence);
    private sealed record Detection(
        int Index, double CaptureTimestamp, int? RenderedGameFrame,
        [property: JsonPropertyName("imageSHA256")] string? ImageSha256, int Width, int Height,
        double InputTimestamp, bool ExactMaskInput, int[][] Exclusions,
        GroundTheoryTuning TrackingTuning, GroundTheoryTuning SemanticTuning,
        int[][] RawWalls, int[][] Walls, int[][] Corners, double[] Foot, int[] ContactFloor, int[] ContactWall,
        double DarkFraction, double WallMilliseconds, Line[] TrackingLines, Line[] SemanticLines, double DetectorMilliseconds);
    private sealed record Report(int SchemaVersion,
        [property: JsonPropertyName("sourcePathID")] Guid SourcePathId,
        [property: JsonPropertyName("recordingSHA256")] string RecordingSha256, int FrameStride,
        int ArchiveFrames, int[]? RequestedIndices, int SelectedFrames, int ProcessedFrames, int MissingFreshMaskInputs,
        int ExactMaskInputs, double ElapsedSeconds, string BuildConfiguration, string Method, IReadOnlyList<Detection> Frames);

    internal static void TestRecordedGroundDetectionsWhenRequested()
    {
        var input = Environment.GetEnvironmentVariable("HKV_GROUND_DETECTION_RECORDING");
        var output = Environment.GetEnvironmentVariable("HKV_GROUND_DETECTION_OUTPUT");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Set the ground detection recording and output paths.");
        if (!int.TryParse(Environment.GetEnvironmentVariable("HKV_GROUND_DETECTION_STRIDE") ?? "6",
                NumberStyles.None, CultureInfo.InvariantCulture, out var stride) || stride is < 1 or > 60)
            throw new InvalidDataException("The ground detection stride must be from 1 through 60.");
        var selection = Environment.GetEnvironmentVariable("HKV_GROUND_DETECTION_INDICES");
        var indices = selection?.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToHashSet();
        var report = Analyze(input, output, stride, indices);
        Console.WriteLine($"Ground detection audit processed {report.ProcessedFrames} images with {report.MissingFreshMaskInputs} missing fresh masks.");
    }

    internal static void TestGroundAuditKeepsSelectedFramesAndRejectsStaleMasks()
    {
        var root = Directory.CreateTempSubdirectory("hkv-terrain-evidence-");
        try
        {
            var recording = new InputPathRecorder();
            recording.Start(1, DateTimeOffset.UnixEpoch, visualArchiveRoot: root.FullName);
            var pixels = new byte[64 * 36 * 4];
            for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = byte.MaxValue;
            var frame = new BgraFrame(64, 36, 64 * 4, pixels, 0, DateTimeOffset.UnixEpoch);
            for (var index = 0; index < 4; index++)
            {
                var time = index switch { 0 => 1, 1 => 1.05, 2 => 1.08, _ => 1.3 };
                recording.RecordVisualFrame(frame, time, time, index == 2 ? 2UL : 1UL, null);
            }
            var regions = SceneRegions.Startup();
            recording.RecordVisualGroundInput(1, 1, frame.Height, new(regions, regions, [], [], null), null);
            var path = new RecordedInputPathStore(root.FullName).Save(recording.Stop(1.4)!);
            var reportPath = Path.Combine(root.FullName, "ground-report.json");
            var report = Analyze(path, reportPath, 1, new HashSet<int> { 0, 1, 2, 3 });
            if (report is not { ArchiveFrames: 4, SelectedFrames: 4, ProcessedFrames: 2, ExactMaskInputs: 1, MissingFreshMaskInputs: 2 }
                || !report.Frames.Select(row => (row.Index, row.ExactMaskInput)).SequenceEqual([(0, true), (1, false)]))
                throw new InvalidOperationException("Exact and briefly carried masks should pass. Foreign or expired masks should fail.");
            var saved = File.ReadAllBytes(reportPath);
            try { _ = Analyze(path, reportPath, 1, null); }
            catch (IOException) when (saved.SequenceEqual(File.ReadAllBytes(reportPath))) { return; }
            throw new InvalidOperationException("A completed ground report must never be overwritten.");
        }
        finally { root.Delete(recursive: true); }
    }

    private static Report Analyze(string input, string output, int stride, IReadOnlySet<int>? indices)
    {
        if (Path.Exists(output)) throw new IOException("Choose a new path for the ground detection report.");
        var path = Path.GetFullPath(input);
        var sourceBytes = File.ReadAllBytes(path);
        var source = JsonSerializer.Deserialize<Source>(sourceBytes, Json);
        if (source?.VisualFrameArchive is not { } archive)
            throw new InvalidDataException("The recording does not contain a visual archive.");
        var reader = new VisualReplayArchiveReader(Path.GetDirectoryName(path)!, archive, source.Id);
        var selected = reader.Manifest.Frames.Where(frame => indices?.Contains(frame.Index) ?? frame.Index % stride == 0)
            .OrderBy(frame => frame.CaptureTimestamp).ThenBy(frame => frame.Index).ToArray();
        if (selected.Length is < 1 or > 5000 || indices is not null && indices.Count != selected.Length)
            throw new InvalidDataException("Select 1 through 5,000 existing archived frame indices.");
        var masks = reader.Manifest.GroundInputs.OrderBy(sample => sample.CaptureTimestamp).ToArray();
        var recent = new Dictionary<ulong, VisualReplayGroundInput>();
        var poses = (source.TrackingSamples ?? []).GroupBy(sample => sample.CaptureTimestamp)
            .ToDictionary(group => group.Key, group => group.Last());
        var intent = (source.CoarseMotionSamples ?? []).GroupBy(sample => sample.CaptureTimestamp)
            .ToDictionary(group => group.Key, group => group.Last());
        var began = Stopwatch.GetTimestamp();
        var rows = new List<Detection>();
        var tracker = new WalkedGroundTracker();
        var next = 0;
        var exactCount = 0;
        foreach (var capture in selected)
        {
            while (next < masks.Length && masks[next].CaptureTimestamp <= capture.CaptureTimestamp)
            {
                var saved = masks[next++];
                recent[saved.CaptureGeneration] = saved;
            }
            if (!recent.TryGetValue(capture.CaptureGeneration, out var mask)
                || capture.CaptureTimestamp - mask.CaptureTimestamp > .1) continue;
            var exact = capture.CaptureTimestamp == mask.CaptureTimestamp;
            if (exact) exactCount++;
            var image = reader.ReadImage(capture);
            var regions = VisualReplayGroundInput.RestoreMasks(mask, image, exact);
            var started = Stopwatch.GetTimestamp();
            var analysis = GameplayTerrainAnalysis.Analyze(image, regions, mask.Tuning, mask.SemanticTuning);
            var wallStarted = Stopwatch.GetTimestamp();
            var raw = TerrainWallDetector.Detect(analysis.Edges, analysis.Floors);
            var clock = capture.CaptureTimestamp;
            var pose = poses.GetValueOrDefault(clock);
            var coarse = intent.GetValueOrDefault(clock);
            if (coarse?.IsTransitioning == true) tracker.BreakContinuity();
            var camera = MeasuredCamera(pose, image.Width / capture.SolveWidth);
            var direction = coarse?.Input?.HorizontalInput ?? mask.HorizontalDrive;
            var buttons = mask.WindowsMovementButtons ?? (direction switch
            {
                -1 => InputButtons.Left,
                1 => InputButtons.Right,
                _ => InputButtons.None
            });
            var room = (coarse?.RoomId ?? pose?.RoomId ?? -1).ToString(CultureInfo.InvariantCulture);
            var contact = tracker.Observe(room, analysis.Edges, exact ? regions.ContactKnight(image.Width, image.Height) : null,
                camera, buttons, clock, analysis.Floors, raw.Walls, checked((long)capture.CaptureGeneration),
                measuredLineage: pose?.Island?.Id, recentJump: mask.RecentJump == true);
            var terrain = TerrainWallDetector.ApplyContact(raw, contact, analysis.Floors, analysis.Edges);
            var wallTime = Stopwatch.GetElapsedTime(wallStarted).TotalMilliseconds;
            var luma = analysis.Edges.SourceLuma;
            rows.Add(new(capture.Index, clock, capture.RenderedGameFrame, capture.Sha256, image.Width, image.Height,
                mask.CaptureTimestamp, exact, regions.GroundExclusions.Select(rect => new[] { rect.X, rect.Y, rect.Width, rect.Height }).ToArray(),
                analysis.Theory.Tuning, analysis.SemanticTheory.Tuning,
                raw.Walls.Select(DescribeWall).ToArray(), terrain.Walls.Select(DescribeWall).ToArray(),
                terrain.Corners.Select(corner => new[] { corner.X, corner.Row }).ToArray(),
                contact.MeasuredFoot is { } foot ? [foot.X, foot.Y] : [],
                contact.Floor is { } floor ? [floor.Row, floor.XSpan.Start, floor.XSpan.EndInclusive] : [],
                contact.Wall is { } wall ? DescribeWall(wall) : [],
                luma is { Length: > 0 } ? luma.Count(value => value <= 10) / (double)luma.Length : 0,
                wallTime, analysis.CameraReferences.Select(Describe).ToArray(), analysis.Floors.Select(Describe).ToArray(),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
#if DEBUG
        const string configuration = "debug";
#else
        const string configuration = "release";
#endif
        var report = new Report(1, source.Id, Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(), stride,
            reader.Manifest.Frames.Count, indices?.Order().ToArray(), selected.Length, rows.Count, selected.Length - rows.Count,
            exactCount, Stopwatch.GetElapsedTime(began).TotalSeconds, configuration,
            "Pixel detections use archived images, masks, and tuning. Contact uses the recorded visual camera and input. Receiver truth and authored labels enter only during scoring.", rows);
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(report, Json), overwrite: false);
        return report;
    }

    private static AtlasPoint? MeasuredCamera(PoseSample? sample, double scale)
    {
        if (sample is not { PublishedCameraX: { } x, PublishedCameraY: { } y }
            || !(sample.Island?.Measured ?? (sample.PoseVerified && sample.HasConfirmedGround))
            || !double.IsFinite(x) || !double.IsFinite(y)) return null;
        return new(x * scale, y * scale);
    }

    private static Line Describe(DetectedFloorLine line) => new(line.Row, line.XSpan.Start, line.XSpan.EndInclusive,
        line.EvidenceSpans.Select(span => new[] { span.Start, span.EndInclusive }).ToArray());

    private static int[] DescribeWall(DetectedWall wall) => [wall.X, wall.Top, wall.Bottom];
}
