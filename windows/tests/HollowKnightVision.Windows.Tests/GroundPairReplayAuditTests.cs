using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.Core;

internal static class GroundPairReplayAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private sealed record Source(Guid Id, RecordedVisualFrameArchive? VisualFrameArchive);
    private sealed record Pair(int Id, double Timestamp, double ReferenceTimestamp, int[]? Rows,
        int[]? AdmittedRows, double[]? ProposedPose);
    private sealed record SequenceRange(double Start, double End);
    private sealed record Pixels(BgraFrame Image, byte[] Luma, DetectedFloorLine[] Lines, StencilRect[] Exclusions,
        double? TuningTimestamp, bool ArchivedMasksAvailable);
    private sealed record Measurement(bool Accepted, double[]? Translation, double[]? Displacement, int? Support,
        double? Error, string? Source, bool? ReferenceTrusted, string? ActualBackend);
    private sealed record FloorVerification(bool Accepted, double[]? Pose, int? Patches, double? Correlation);
    private sealed record SequenceResult(int Range, bool SubpixelEnabled, double Timestamp, int Index, int[] Rows,
        double[] Position, double? TuningTimestamp, bool ArchivedMasksAvailable, Measurement Measurement);
    private sealed record PairResult(int Id, string Backend, bool SubpixelEnabled, double Timestamp, double ReferenceTimestamp,
        int Index, int ReferenceIndex, int[] CurrentRows, int[] ReferenceRows, double?[] TuningTimestamps,
        bool[] ArchivedMasksAvailable, int[]? AdmittedReferenceRows, Measurement Measurement, FloorVerification? Floor);

    internal static void TestArchivedGroundPairsWhenRequested()
    {
        var recording = RequiredPath("HKV_GROUND_PAIR_RECORDING");
        var output = RequiredPath("HKV_GROUND_PAIR_OUTPUT");
        var pairsPath = Environment.GetEnvironmentVariable("HKV_GROUND_PAIR_PAIRS");
        var sequencesPath = Environment.GetEnvironmentVariable("HKV_GROUND_SEQUENCE_RANGES");
        if ((pairsPath is null) == (sequencesPath is null))
            throw new InvalidOperationException("Select one pair list or sequence range list.");
        if (Path.Exists(output)) throw new IOException("Choose a new path for the ground pair report.");

        var bytes = File.ReadAllBytes(recording);
        var source = JsonSerializer.Deserialize<Source>(bytes, Json);
        if (source?.VisualFrameArchive is not { } summary) throw new InvalidDataException("The recording has no visual archive.");
        var reader = new VisualReplayArchiveReader(Path.GetDirectoryName(recording)!, summary, source.Id,
            requireComplete: Environment.GetEnvironmentVariable("HKV_GROUND_PAIR_ALLOW_INCOMPLETE") != "1");
        object rows = sequencesPath is not null
            ? ReplaySequences(reader, Read<SequenceRange[]>(sequencesPath))
            : ReplayPairs(reader, Read<Pair[]>(pairsPath!), Environment.GetEnvironmentVariable("HKV_GROUND_PAIR_SUBPIXEL") == "1");
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sourcePathID = source.Id,
            recordingSHA256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            method = "Camera estimates and optional floor verification use archived pixels and masks. Floor verification uses detected rows without measured ground ownership.",
            rows
        }, Json), overwrite: false);
    }

    internal static void TestGroundPairReplayKeepsExactFramesAndMaskOwnership()
    {
        var root = Directory.CreateTempSubdirectory("hkv-ground-pairs-");
        try
        {
            var recorder = new InputPathRecorder();
            recorder.Start(1, DateTimeOffset.UnixEpoch, visualArchiveRoot: root.FullName);
            var image = FloorTexture();
            var region = SceneRegions.Startup();
            for (var index = 0; index < 4; index++)
            {
                var time = index switch { 0 => 1, 1 => 1.06, 2 => 1.08, _ => 1.3 };
                recorder.RecordVisualFrame(image, time, time, index == 2 ? 2UL : 1UL, null);
            }
            recorder.RecordVisualGroundInput(1, 1, image.Height, new(region, region, [], [], null), null);
            var path = new RecordedInputPathStore(root.FullName).Save(recorder.Stop(1.4)!);
            var source = Read<Source>(path);
            var reader = new VisualReplayArchiveReader(root.FullName, source.VisualFrameArchive!, source.Id);
            var pairs = ReplayPairs(reader, [new(7, 1.06, 1, null, [99], [0, 0]),
                new(9, 1.08, 1, [], null, null), new(10, 1.3, 1, null, null, null)], subpixel: true);
            Check(pairs.Count == 6 && pairs.Select(result => result.Backend).Distinct().SequenceEqual(["cpu", "cuda"])
                && pairs.All(result => result.ReferenceIndex == 0 && result.SubpixelEnabled),
                "Both backends must replay the original reference image for each exact pair.");
            Check(pairs.Where(result => result.Id == 7).All(result => result.Index == 1
                    && result.ArchivedMasksAvailable.SequenceEqual([true, true])
                    && result.AdmittedReferenceRows!.SequenceEqual([99])),
                "Briefly carried masks and admitted rows must stay attached to the correct target frame.");
            foreach (var measured in pairs.Where(result => result.Id == 7))
            {
                Check(measured.Floor is { Accepted: true },
                    $"The floor verifier rejected the identical captures at rows {string.Join(", ", measured.ReferenceRows)}.");
                Check(measured.Measurement is { Accepted: true, Support: > 0 },
                    $"The {measured.Backend} solver rejected the identical captures at rows {string.Join(", ", measured.CurrentRows)}.");
            }
            Check(pairs.Where(result => result.Id is 9 or 10).All(result => result.ArchivedMasksAvailable.SequenceEqual([true, false]))
                && pairs.Where(result => result.Id == 9).All(result => result.CurrentRows.Length == 0),
                "Filtering must apply to the requested rows, and foreign or stale masks cannot supply ground exclusions.");
            var sequence = ReplaySequences(reader, [new(1, 1.3)]);
            Check(sequence.Count == 8 && sequence.GroupBy(result => result.SubpixelEnabled)
                .All(group => group.Select(result => result.Index).SequenceEqual([0, 1, 2, 3])),
                "Both sequence variants must visit each original readable frame in order.");
        }
        finally { root.Delete(recursive: true); }
    }

    private static List<PairResult> ReplayPairs(VisualReplayArchiveReader reader, Pair[] pairs, bool subpixel)
    {
        if (pairs.Length is < 1 or > 100 || pairs.Select(pair => pair.Id).Distinct().Count() != pairs.Length)
            throw new InvalidDataException("Specify 1 through 100 ground pairs with distinct IDs.");
        var frames = reader.Manifest.Frames.ToDictionary(frame => frame.CaptureTimestamp);
        var inputs = InputsByGeneration(reader);
        var results = new List<PairResult>(pairs.Length * 2);
        foreach (var pair in pairs)
        {
            if (!frames.TryGetValue(pair.ReferenceTimestamp, out var old) || !frames.TryGetValue(pair.Timestamp, out var current))
                throw new InvalidDataException($"Ground pair {pair.Id} needs exact archived capture timestamps.");
            var reference = ReadPixels(reader, old, inputs, pair.Rows);
            var target = ReadPixels(reader, current, inputs, pair.Rows);
            var floor = pair.ProposedPose is { } pose ? VerifyFloor(reference, target, pose) : null;
            foreach (var backend in new[] { GroundComputeBackend.Cpu, GroundComputeBackend.Cuda })
            {
                var solver = MakeSolver(subpixel, backend);
                _ = Solve(solver, reference, old);
                if (pair.AdmittedRows is { } admitted)
                    solver.PrioritizeAdmittedReferenceRows(old.CaptureTimestamp,
                        reference.Lines.Where(line => admitted.Contains(line.Row)).ToArray());
                var result = Solve(solver, target, current);
                results.Add(new(pair.Id, BackendName(backend), subpixel, pair.Timestamp, pair.ReferenceTimestamp,
                    current.Index, old.Index, Rows(target), Rows(reference), [reference.TuningTimestamp, target.TuningTimestamp],
                    [reference.ArchivedMasksAvailable, target.ArchivedMasksAvailable], pair.AdmittedRows, result, floor));
            }
        }
        return results;
    }

    private static List<SequenceResult> ReplaySequences(VisualReplayArchiveReader reader, SequenceRange[] ranges)
    {
        if (ranges.Length is < 1 or > 8 || ranges.Any(range =>
                !double.IsFinite(range.Start) || !double.IsFinite(range.End) || range.End - range.Start is < 0 or > 10))
            throw new InvalidDataException("Specify 1 through 8 bounded sequence ranges of at most ten seconds.");
        var inputs = InputsByGeneration(reader);
        var results = new List<SequenceResult>();
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            var frames = reader.Manifest.Frames.Where(frame => frame.FileName is not null
                && frame.CaptureTimestamp >= range.Start && frame.CaptureTimestamp <= range.End).ToArray();
            if (frames.Length == 0) throw new InvalidDataException($"Ground sequence {index} has no readable frames.");
            var integer = MakeSolver(false, GroundComputeBackend.Automatic);
            var fractional = MakeSolver(true, GroundComputeBackend.Automatic);
            var integerPosition = CameraVector.Zero;
            var fractionalPosition = CameraVector.Zero;
            var wholeResults = new List<SequenceResult>(frames.Length);
            var subpixelResults = new List<SequenceResult>(frames.Length);
            foreach (var frame in frames)
            {
                var value = ReadPixels(reader, frame, inputs, null);
                wholeResults.Add(Observe(integer, subpixel: false, ref integerPosition));
                subpixelResults.Add(Observe(fractional, subpixel: true, ref fractionalPosition));

                SequenceResult Observe(GroundCameraSolver solver, bool subpixel, ref CameraVector position)
                {
                    var measurement = Solve(solver, value, frame);
                    if (measurement.Translation is [var x, var y]) position = new(position.X + x, position.Y + y);
                    return new(index, subpixel, frame.CaptureTimestamp, frame.Index, Rows(value), Vector(position),
                        value.TuningTimestamp, value.ArchivedMasksAvailable, measurement);
                }
            }
            results.AddRange(wholeResults);
            results.AddRange(subpixelResults);
        }
        return results;
    }

    private static Dictionary<ulong, VisualReplayGroundInput[]> InputsByGeneration(VisualReplayArchiveReader reader) =>
        reader.Manifest.GroundInputs.GroupBy(input => input.CaptureGeneration)
            .ToDictionary(group => group.Key, group => group.OrderBy(input => input.CaptureTimestamp).ToArray());

    private static Pixels ReadPixels(VisualReplayArchiveReader reader, VisualReplayFrame frame,
        IReadOnlyDictionary<ulong, VisualReplayGroundInput[]> inputs, int[]? selectedRows)
    {
        var image = reader.ReadImage(frame);
        var input = inputs.GetValueOrDefault(frame.CaptureGeneration)?
            .LastOrDefault(value => value.CaptureTimestamp <= frame.CaptureTimestamp);
        var fresh = input is not null && frame.CaptureTimestamp - input.CaptureTimestamp <= .1;
        var extent = new StencilRect(0, 0, image.Width, image.Height);
        var masks = VisualReplayGroundInput.RestoreMasks(fresh ? input : null, image, exactInput: false);
        var exclusions = !fresh
            ? SceneRegions.PixelRectangles(image.Width, image.Height, SceneRegions.AtlasGameplay([], extent).OmittedRects)
            : input!.WindowsAtlasExcludingRects is null ? masks.AtlasExclusions : masks.GroundExclusions;
        var edges = GroundLineDetector.Analyze(image, exclusions);
        var theory = GroundLineDetector.Theory(GroundLineDetector.Compare(edges), input?.Tuning ?? GroundTheoryTuning.Default);
        var lines = GroundLineDetector.RejectKnownForegroundLines(GroundLineDetector.CleanFloorLines(theory),
            image.Height, exclusions, masks.GroundKnight);
        return new(image, edges.SourceLuma ?? throw new InvalidDataException("Missing luma pixels."),
            lines.Where(line => selectedRows is null || selectedRows.Contains(line.Row)).ToArray(),
            exclusions.Select(rect => new StencilRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray(), input?.CaptureTimestamp, fresh);
    }

    private static FloorVerification VerifyFloor(Pixels reference, Pixels target, double[] pose)
    {
        if (pose is not [var x, var y] || !double.IsFinite(x) || !double.IsFinite(y))
            throw new InvalidDataException("Provide the proposed floor pose as two finite coordinates.");
        var result = GroundPlaneClosureVerifier.Verify(reference.Image, Geometry(reference), default,
            target.Image, Geometry(target), new(x, y), reference.Image.Width);
        return new(result is not null, result is null ? null : [result.CameraPose.X, result.CameraPose.Y],
            result?.SupportedPatches, result?.MeanCorrelation);

        static LiveGroundLineEvidence[] Geometry(Pixels pixels) => pixels.Lines.SelectMany((line, index) =>
            line.EvidenceSpans.Select(span => new LiveGroundLineEvidence(index,
                new(span.Start, pixels.Image.Height - line.Row), new(span.EndInclusive + 1, pixels.Image.Height - line.Row)))).ToArray();
    }

    private static GroundCameraSolver MakeSolver(bool subpixel, GroundComputeBackend backend) =>
        new(subpixelEnabled: subpixel, keyframeSubpixelEnabled: true, calibratedMotionEnabled: true,
            matcherBackend: backend, maximumLocalSearchRadius: 192, independentPatchConsensusEnabled: true);

    private static Measurement Solve(GroundCameraSolver solver, Pixels value, VisualReplayFrame frame)
    {
        var previousSearch = solver.LastPatchSearchExecution;
        var result = solver.Solve(value.Luma, value.Image.Width, value.Image.Height,
            value.Lines.Select(GroundTextureLine.FromDetected).ToArray(), frame.CaptureTimestamp, value.Exclusions);
        var actualBackend = solver.LastPatchSearchExecution is { } search && !ReferenceEquals(search, previousSearch)
            ? BackendName(search.Backend) : null;
        return new(result is not null, result is null ? null : Vector(result.ImageTranslation),
            result?.MatchedImageDisplacement is { } displacement ? Vector(displacement) : null,
            result?.Support, result?.Error, result?.MatchSource, result?.ReferenceTrusted, actualBackend);
    }

    private static string BackendName(GroundComputeBackend backend) => backend.ToString().ToLowerInvariant();
    private static double[] Vector(CameraVector point) => [point.X, point.Y];
    private static int[] Rows(Pixels value) => value.Lines.Select(line => line.Row).ToArray();
    private static T Read<T>(string path) where T : class => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException("Ground replay input is empty.");
    private static string RequiredPath(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? Path.GetFullPath(value) : throw new InvalidOperationException($"Set {key}.");

    private static BgraFrame FloorTexture()
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        var noise = GroundCameraSolverTests.Pixels(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var texture = noise[y * width + x] % 96;
            var value = (byte)(y < 100 ? 160 + texture : texture);
            var offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return new(width, height, width * 4, pixels, 0, DateTimeOffset.UnixEpoch);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
