using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

// Truth is deliberately outside the input accepted by the tracking algorithm.
public sealed record TrackingStepInput(BgraFrame Frame, double Timestamp, string Scene,
    PixelRect[] GroundExclusions, StencilRect[] MotionExclusions, PixelRect? Knight, bool Admitted);
public sealed record TrackingStepEvidence(TrackingStepInput Input, int GameFrame, AtlasPoint Truth);
public sealed record TrackingStepScore(int Frame, int GameFrame, double ErrorPixels,
    double ProcessingMilliseconds, string PoseSource, AtlasPoint Estimated, TrackingStepProfile? Profile = null);
public sealed record TrackingStepProfile(double GroundDetectionMilliseconds, double MotionMilliseconds,
    double VisualTrackingMilliseconds, double PatchSearchMilliseconds, int PatchCandidates, string Backend, long AllocatedBytes, double GlobalSearchMilliseconds = 0,
    double CameraSolveMilliseconds = 0, double FeatureMatchingMilliseconds = 0,
    double MaintenanceMilliseconds = 0, double OutputMilliseconds = 0,
    int Generation0Collections = 0, int Generation1Collections = 0, int Generation2Collections = 0, GroundCameraSolveProfile? Camera = null);
public sealed record TrackingStepPipelineSample(string Stage, long CaptureId, long? GameFrame,
    double CapturedAt, double ObservedAt, double? Milliseconds = null, int Generation2 = 0);
public sealed record TrackingControlledFrameTiming(int Frame, Guid CommandId, int RenderedGameFrame,
    int CapturedGameFrame, double DeltaSeconds, double GameWaitMilliseconds, double CaptureWaitMilliseconds);
public sealed record TrackingStepAttempt(int Segment, int ExpectedFrames, TrackingStepEvidence[] Frames,
    TrackingStepScore[] Scores, string? Failure, TrackingStepPipelineSample[]? Pipeline = null,
    TrackingControlledFrameTiming[]? ControlledFrames = null)
{
    public bool Passed => Failure is null && Frames.Length == ExpectedFrames && Scores.Length == ExpectedFrames;
}

/// <summary>Runs the production visual camera algorithm without receiver positions or async search races.</summary>
public sealed class TrackingStepProcessor
{
    public const string StateContract = "ground-visual-journal-v1";
    private readonly GroundHypothesisTracker ground = new(.2, backgroundGlobalSearch: true,
        confirmedLossGraceSeconds: 3, productionCameraSolver: true, broadSearchEveryFrame: true,
        deterministicGlobalSearch: true);
    private readonly VisualGameplayTracker visual = new();
    private readonly MaskedFrameMotionTracker motion = new();
    private string? scene;
    private int width, height;
    private bool committed;
    public TrackingStepProfile? LastProfile { get; private set; }
    internal bool WarmsFeatureRecovery { get; init; }
    public VisualGameplayObservation Process(TrackingStepInput input)
    {
        LastProfile = null;
        var frame = input.Frame;
        if (scene is not null && (scene != input.Scene || width != frame.Width || height != frame.Height))
            throw new InvalidDataException("Segment replay requires a new state contract for scene or resolution changes.");
        scene = input.Scene; width = frame.Width; height = frame.Height;
        if (!input.Admitted)
        {
            ground.Reset(keepingGlobalFeatures: true); motion.Reset();
            visual.PauseGroundVerification();
            return visual.ObserveUnverified(frame, null);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var timing = Stopwatch.GetTimestamp();
        var analysis = GroundLineDetector.Analyze(frame, input.GroundExclusions);
        var comparison = GroundLineDetector.Compare(analysis);
        var theory = GroundLineDetector.Theory(comparison, GroundTheoryTuning.Default);
        var lines = GroundLineDetector.RejectKnownForegroundLines(GroundLineDetector.CleanFloorLines(theory),
            frame.Height, input.GroundExclusions, input.Knight);
        var exclusions = input.GroundExclusions.ToList();
        if (input.Knight is { } knight) exclusions.Add(knight);
        var detectionMs = Stopwatch.GetElapsedTime(timing).TotalMilliseconds;
        timing = Stopwatch.GetTimestamp();
        var movement = motion.Observe(frame, input.MotionExclusions);
        var motionMs = Stopwatch.GetElapsedTime(timing).TotalMilliseconds;
        timing = Stopwatch.GetTimestamp();
        var result = visual.ObserveGroundAnchored(frame, movement,
            analysis.SourceLuma!, lines, exclusions, committed, input.Knight, ground, input.Timestamp);
        var visualMs = Stopwatch.GetElapsedTime(timing).TotalMilliseconds;
        if (WarmsFeatureRecovery)
            ground.WarmFeatureRecovery(analysis.SourceLuma!, frame.Width, frame.Height, lines, exclusions);
        var search = visual.LastPatchSearchExecution;
        LastProfile = new(detectionMs, motionMs, visualMs, search?.Elapsed.TotalMilliseconds ?? 0,
            search?.CandidateCount ?? 0, search?.Backend.ToString() ?? "none", GC.GetAllocatedBytesForCurrentThread() - allocated, ground.LastGlobalSearchMilliseconds,
            ground.LastCameraSolveMilliseconds, ground.LastFeatureMatchingMilliseconds,
            ground.LastMaintenanceMilliseconds, ground.LastOutputMilliseconds,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, ground.LastCameraProfile);
        committed |= result.AllowsAtlasWrite;
        return result;
    }
}

/// <summary>Fail on the first bad, missing, repeated or over-budget sample. Never re-zero at a segment boundary.</summary>
public sealed class TrackingStepGate(int segment, int expectedFrames = 30, bool enforceFrameBudget = true)
{
    public const double MaximumError = 5, FrameBudgetMilliseconds = 1000.0 / 30;
    private readonly List<TrackingStepEvidence> frames = [];
    private readonly List<TrackingStepScore> scores = [];
    private readonly HashSet<int> gameFrames = [];
    public string? Failure { get; private set; }
    public int Count => frames.Count;
    public bool Observe(int index, TrackingStepEvidence evidence, AtlasPoint estimate,
        AtlasPoint alignment, double milliseconds, string source, TrackingStepProfile? profile = null)
    {
        if (Failure is not null) return false;
        if (index != frames.Count) { Failure = $"Missing 30 fps frame {frames.Count}; next sample was {index}."; return false; }
        if (!gameFrames.Add(evidence.GameFrame)) { Failure = $"Repeated game frame at sample {index}."; return false; }
        var dx = estimate.X - alignment.X - evidence.Truth.X;
        var dy = estimate.Y - alignment.Y - evidence.Truth.Y;
        var error = Math.Sqrt(dx * dx + dy * dy);
        frames.Add(evidence); scores.Add(new(index, evidence.GameFrame, error, milliseconds, source, estimate, profile));
        if (!double.IsFinite(error) || error > MaximumError)
            Failure = $"Frame {index}: camera error {error:F3} px exceeds 5 px.";
        else if (!double.IsFinite(milliseconds) || enforceFrameBudget && milliseconds > FrameBudgetMilliseconds)
            Failure = $"Frame {index}: processing {milliseconds:F2} ms exceeds 33.33 ms.";
        return Failure is null;
    }
    public TrackingStepAttempt Finish()
    {
        if (Failure is null && frames.Count != expectedFrames)
            Failure = $"Missing 30 fps frames: received {frames.Count}/{expectedFrames}.";
        return new(segment, expectedFrames, frames.ToArray(), scores.ToArray(), Failure);
    }
}

public sealed record TrackingStepBoundary(string File, string Sha256, RecordedGameCheckpoint EndCheckpoint);
public sealed record TrackingStepManifest(int Version, string Contract, string SourceSha256,
    string ConditionsSha256, string SeedFile, string SeedSha256, RecordedGameCheckpoint StartCheckpoint,
    TrackingStepBoundary[] Accepted, string ValidatedAlgorithm);

/// <summary>
/// A portable checkpoint is an immutable input journal, not a dump of GPU pointers.
/// Reconstruct all history and revalidate every accepted frame after changing code.
/// Bad prefixes invalidate later boundaries. Failed attempts never advance progress.
/// </summary>
public static class TrackingStepWarmup
{
    private static readonly object Gate = new();
    private static readonly HashSet<(int Width, int Height)> Ready = [];
    public static void Prepare(TrackingStepInput seed)
    {
        var frame = seed.Frame;
        if (frame.Width < 160 || frame.Height < 90) return;
        lock (Gate)
        {
            if (Ready.Contains((frame.Width, frame.Height))) return;
            GroundPatchMatcher.Shared.PrepareBroadSearch(frame.Width, frame.Height);
            // A separate tracker exercises moving frames, reversals, vertical shake,
            // CPU JIT paths and GPU launches. No warm-up history enters the replay.
            var throwaway = new TrackingStepProcessor { WarmsFeatureRecovery = true };
            var warmup = Stopwatch.StartNew();
            // Sustained small motion matures global features before the large
            // reversals. Include enough wall time for tiered CPU compilation.
            for (var i = 0; i < 180 || warmup.Elapsed.TotalSeconds < 2; i++)
            {
                var dx = i < 120 ? i % 60 / 3 : (i % 6 - 3) * 32;
                var dy = i < 120 ? 0 : (i % 5 - 2) * 24;
                var pixels = new byte[frame.Pixels.Length];
                // Integer shifts leave subpixel fitting at its zero-error early
                // return. Fractional motion exercises fitting, held-out validation,
                // and fine consensus before the first measured frame.
                var fx = i % 3 * .3; var fy = i % 5 * .15;
                for (var y = 0; y < frame.Height; y++)
                {
                    var sourceY = y - dy;
                    if (sourceY < 0 || sourceY + 1 >= frame.Height) continue;
                    for (var x = 0; x < frame.Width; x++)
                    {
                        var sourceX = x - dx;
                        if (sourceX < 0 || sourceX + 1 >= frame.Width) continue;
                        var source = sourceY * frame.Stride + sourceX * 4;
                        var target = y * frame.Stride + x * 4;
                        for (var channel = 0; channel < 4; channel++)
                        {
                            var top = frame.Pixels[source + channel] * (1 - fx) + frame.Pixels[source + 4 + channel] * fx;
                            var bottom = frame.Pixels[source + frame.Stride + channel] * (1 - fx) + frame.Pixels[source + frame.Stride + 4 + channel] * fx;
                            pixels[target + channel] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                        }
                    }
                }
                var moved = new BgraFrame(frame.Width, frame.Height, frame.Stride, pixels, i + 1,
                    DateTimeOffset.UnixEpoch.AddSeconds(i / 30.0));
                _ = throwaway.Process(seed with { Frame = moved, Timestamp = i / 30.0, Admitted = true });
            }
            var textureWarmup = new GroundCameraSolver(matcherBackend: GroundComputeBackend.Cuda,
                maximumLocalSearchRadius: 192, broadSearchEveryFrame: true);
            textureWarmup.WarmTextureFallback(GroundLineDetector.Analyze(frame, []).SourceLuma!, frame.Width, frame.Height);
            Ready.Add((frame.Width, frame.Height));
        }
    }
}

public sealed class TrackingStepStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private static readonly JsonSerializerOptions Json = new() {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private string ManifestPath => Path.Combine(DirectoryPath, "progress.json");
    public bool Exists => File.Exists(ManifestPath);
    public static string AlgorithmFingerprint => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TrackingStepProcessor).Assembly.Location)));
    public TrackingStepManifest Load(string source, string conditions)
    {
        var m = JsonSerializer.Deserialize<TrackingStepManifest>(File.ReadAllBytes(ManifestPath), Json)
            ?? throw new InvalidDataException("Empty step replay manifest.");
        if (m.Version != 1 || m.Contract != TrackingStepProcessor.StateContract || m.SourceSha256 != source || m.ConditionsSha256 != conditions)
            throw new InvalidDataException("Stashed conditions are incompatible. Restart step replay from the beginning.");
        return m;
    }
    public TrackingStepManifest Initialize(string source, string conditions, RecordedGameCheckpoint checkpoint,
        TrackingStepEvidence[] seed)
    {
        if (Exists) throw new InvalidOperationException("Never overwrite an existing step replay.");
        if (seed.Length == 0) throw new InvalidDataException("Missing seed images.");
        var name = "seed.json.br"; var hash = WriteEvidence(name, seed);
        var m = new TrackingStepManifest(1, TrackingStepProcessor.StateContract, source, conditions,
            name, hash, checkpoint, [], AlgorithmFingerprint); Save(m); return m;
    }
    public (TrackingStepProcessor Processor, AtlasPoint Alignment, TrackingStepManifest Manifest) Rehydrate(TrackingStepManifest m)
    {
        var seeds = ReadEvidence(m.SeedFile, m.SeedSha256);
        if (seeds.Length == 0) throw new InvalidDataException("Missing warm-up seed.");
        TrackingStepWarmup.Prepare(seeds[0].Input);
        var processor = new TrackingStepProcessor(); AtlasPoint? alignment = null;
        foreach (var evidence in seeds)
        {
            var result = processor.Process(evidence.Input);
            alignment ??= new(result.Telemetry.CameraX - evidence.Truth.X, result.Telemetry.CameraY - evidence.Truth.Y);
        }
        if (alignment is null) throw new InvalidDataException("Empty seed state.");
        for (var segment = 0; segment < m.Accepted.Length; segment++)
        {
            var boundary = m.Accepted[segment]; var frames = ReadEvidence(boundary.File, boundary.Sha256);
            // Restoring a journal is preparation, not another live deadline.
            // Keep the exact same accuracy/identity gate; only timing is untimed.
            var gate = new TrackingStepGate(segment, frames.Length, enforceFrameBudget: false);
            for (var i = 0; i < frames.Length; i++)
            {
                var start = Stopwatch.GetTimestamp(); var result = processor.Process(frames[i].Input);
                if (!gate.Observe(i, frames[i], new(result.Telemetry.CameraX, result.Telemetry.CameraY), alignment.Value,
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds, result.Source, processor.LastProfile)) break;
            }
            var attempt = gate.Finish();
            if (!attempt.Passed)
            {
                SaveAttempt(attempt);
                var invalidated = m with { Accepted = m.Accepted.Take(segment).ToArray(), ValidatedAlgorithm = AlgorithmFingerprint };
                Save(invalidated);
                throw new InvalidDataException($"Saved prefix failed at second {segment}: {attempt.Failure} Later boundaries invalidated; retry from this boundary.");
            }
        }
        m = m with { ValidatedAlgorithm = AlgorithmFingerprint }; Save(m);
        // Warm-up, decompression and reconstruction allocate temporary images.
        // Collect that preparation debt while the game is still paused.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        return (processor, alignment.Value, m);
    }
    public TrackingStepManifest Accept(TrackingStepManifest m, TrackingStepAttempt attempt, RecordedGameCheckpoint endpoint)
    {
        if (!attempt.Passed || attempt.Segment != m.Accepted.Length) throw new InvalidOperationException("Only a complete passing next segment may advance the stash.");
        var name = $"accepted-{attempt.Segment:D4}-{Guid.NewGuid():N}.json.br";
        var boundary = new TrackingStepBoundary(name, WriteEvidence(name, attempt.Frames), endpoint);
        var next = m with { Accepted = [..m.Accepted, boundary], ValidatedAlgorithm = AlgorithmFingerprint };
        SaveAttempt(attempt); Save(next); return next;
    }
    public void SaveAttempt(TrackingStepAttempt attempt)
    {
        Directory.CreateDirectory(DirectoryPath);
        var stem = $"attempt-{attempt.Segment:D4}-{Guid.NewGuid():N}";
        var hash = WriteEvidence(stem + ".json.br", attempt.Frames);
        var report = JsonSerializer.SerializeToUtf8Bytes(new {
            attempt.Segment, attempt.ExpectedFrames, attempt.Passed, attempt.Failure, attempt.Scores, attempt.Pipeline, attempt.ControlledFrames,
            evidenceFile = stem + ".json.br", sha256 = hash, algorithm = AlgorithmFingerprint }, Json);
        AtomicFile.Write(Path.Combine(DirectoryPath, stem + ".json"), report);
        AtomicFile.Write(Path.Combine(DirectoryPath, "last-attempt.json"), report);
    }

    // Verify all accepted raw observations with the current installed runtime.
    // Historical reports remain immutable; this separate report cannot advance
    // or rewrite accepted boundaries. Loading/decompression is outside timing.
    public bool VerifyAccepted(string sourcePath, Action<string>? progress = null)
    {
        var prior = JsonSerializer.Deserialize<TrackingStepManifest>(File.ReadAllBytes(ManifestPath), Json)!;
        var m = Load(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))), prior.ConditionsSha256);
        var seeds = ReadEvidence(m.SeedFile, m.SeedSha256);
        if (seeds.Length == 0) throw new InvalidDataException("Missing warm-up seed.");
        TrackingStepWarmup.Prepare(seeds[0].Input);
        var processor = new TrackingStepProcessor(); AtlasPoint? alignment = null;
        foreach (var evidence in seeds)
        {
            var result = processor.Process(evidence.Input);
            alignment ??= new(result.Telemetry.CameraX - evidence.Truth.X, result.Telemetry.CameraY - evidence.Truth.Y);
        }
        var reports = new List<object>(); var passed = true;
        for (var segment = 0; segment < m.Accepted.Length; segment++)
        {
            var boundary = m.Accepted[segment];
            var frames = ReadEvidence(boundary.File, boundary.Sha256);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            var gate = new TrackingStepGate(segment, frames.Length);
            for (var i = 0; i < frames.Length; i++)
            {
                var started = Stopwatch.GetTimestamp(); var result = processor.Process(frames[i].Input);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!gate.Observe(i, frames[i], new(result.Telemetry.CameraX, result.Telemetry.CameraY),
                    alignment!.Value, elapsed, result.Source, processor.LastProfile)) break;
            }
            var attempt = gate.Finish();
            reports.Add(new { attempt.Segment, attempt.Passed, attempt.Failure, attempt.Scores,
                evidenceFile = boundary.File, evidenceSha256 = boundary.Sha256 });
            progress?.Invoke(JsonSerializer.Serialize(new { segment, passed = attempt.Passed, failure = attempt.Failure,
                frames = attempt.Scores.Length, maxError = attempt.Scores.Select(s => s.ErrorPixels).DefaultIfEmpty().Max(),
                maxMs = attempt.Scores.Select(s => s.ProcessingMilliseconds).DefaultIfEmpty().Max() }, Json));
            if (!attempt.Passed) { passed = false; break; }
        }
        AtomicFile.Write(Path.Combine(DirectoryPath, "current-code-prefix-validation.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { algorithm = AlgorithmFingerprint, acceptedSegments = m.Accepted.Length,
                checkedSegments = reports.Count, passedSegments = reports.Count - (passed ? 0 : 1), passed, reports }, Json));
        return passed;
    }

    public object RetryFailed(string sourcePath)
    {
        var prior = JsonSerializer.Deserialize<TrackingStepManifest>(File.ReadAllBytes(ManifestPath), Json)!;
        var m = Load(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))), prior.ConditionsSha256);
        var restored = Rehydrate(m);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DirectoryPath, "last-attempt.json")));
        var item = report.RootElement;
        var segment = item.GetProperty("Segment").GetInt32();
        if (segment != restored.Manifest.Accepted.Length) throw new InvalidDataException("Failed attempt is not at the accepted boundary.");
        var expected = item.GetProperty("ExpectedFrames").GetInt32();
        var frames = ReadEvidence(item.GetProperty("evidenceFile").GetString()!, item.GetProperty("sha256").GetString()!);
        var gate = new TrackingStepGate(segment, expected);
        for (var i = 0; i < frames.Length; i++)
        {
            var start = Stopwatch.GetTimestamp(); var result = restored.Processor.Process(frames[i].Input);
            if (!gate.Observe(i, frames[i], new(result.Telemetry.CameraX, result.Telemetry.CameraY), restored.Alignment,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, result.Source, restored.Processor.LastProfile)) break;
        }
        var attempt = gate.Finish();
        var resultReport = new { attempt.Segment, attempt.ExpectedFrames, attempt.Passed, attempt.Failure,
            attempt.Scores, state = "Rechecked saved pixels; progress advances only after a complete live second passes.", algorithm = AlgorithmFingerprint };
        AtomicFile.Write(Path.Combine(DirectoryPath, "retry-report.json"), JsonSerializer.SerializeToUtf8Bytes(resultReport, Json));
        return resultReport;
    }

    private void Save(TrackingStepManifest m)
    {
        Directory.CreateDirectory(DirectoryPath);
        AtomicFile.Write(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(m, Json));
    }
    private string WriteEvidence(string name, TrackingStepEvidence[] evidence)
    {
        Directory.CreateDirectory(DirectoryPath);
        using var buffer = new MemoryStream();
        using (var stream = new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) JsonSerializer.Serialize(stream, evidence, Json);
        var bytes = buffer.ToArray(); AtomicFile.Write(Path.Combine(DirectoryPath, name), bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    public TrackingStepEvidence[] ReadEvidence(string name, string hash)
    {
        if (Path.GetFileName(name) != name) throw new InvalidDataException("Invalid evidence filename.");
        var bytes = File.ReadAllBytes(Path.Combine(DirectoryPath, name));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("Stashed evidence failed its hash check.");
        using var input = new MemoryStream(bytes); using var stream = new BrotliStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<TrackingStepEvidence[]>(stream, Json) ?? throw new InvalidDataException("Empty evidence.");
    }
}

public static class TrackingStepPath
{
    public static RecordedInputPath Slice(RecordedInputPath source, int second, RecordedGameCheckpoint checkpoint)
    {
        _ = InputPathPlayback.Poses(source);
        var duration = Math.Min(1, source.Duration - second);
        if (second < 0 || duration < .05) throw new ArgumentOutOfRangeException(nameof(second));
        var held = new HashSet<RecordedGameButton>();
        foreach (var e in source.Events.Where(e => e.Offset < second))
            if (e.Transition == RecordedInputTransition.Pressed) held.Add(e.Button); else held.Remove(e.Button);
        var events = held.Select(b => new RecordedInputPathEvent(0, b, RecordedInputTransition.Pressed))
            .Concat(source.Events.Where(e => e.Offset >= second && e.Offset < second + duration).Select(e => e with { Offset = e.Offset - second })).ToArray();
        var atEnd = new HashSet<RecordedGameButton>();
        foreach (var e in events)
            if (e.Transition == RecordedInputTransition.Pressed) atEnd.Add(e.Button); else atEnd.Remove(e.Button);
        events = events.Concat(atEnd.Select(b => new RecordedInputPathEvent(duration, b, RecordedInputTransition.Released))).ToArray();
        var poses = source.GroundTruthTrace.Where(p => p.Offset >= second && p.Offset < second + duration)
            .Select(p => p with { Offset = p.Offset - second }).ToArray();
        return new(RecordedInputPath.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow, duration, source.Id, second + 1,
            checkpoint, events, [], poses, runtimeMetadata: new Dictionary<string,string> { ["replay"] = "one-second-strict" });
    }
}
