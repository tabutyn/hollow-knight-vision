using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class ModelVersionTests
{
    private const string Shared = LabelingModelVersionStore.SharedObjectModel;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly LabelingReviewMetrics Metrics = new(.5, .5, 1, 1, 0, .5, 1, .75);

    internal static void TestDurableVersions()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var first = fixture.Candidate("game.mana", 100, "first");
        var second = fixture.Candidate("game.mana", 200, "second");
        store.SynchronizeCandidates([second, first]);
        Equal("v0.1", store.CandidateVersion(first).DisplayName);
        Equal("v0.2", store.CandidateVersion(second).DisplayName);
        Check(store.ActiveSelection("game.mana") is null);
        var point = store.Promote(second, DateTimeOffset.FromUnixTimeSeconds(300));
        Equal("v1.0", point.Version.DisplayName);
        Equal(1, store.ActiveSelection("game.mana"));
        Equal("second", File.ReadAllText(store.ModelPath("game.mana", 1)!));
        Equal(point, store.Promote(second)); // repeated activation is idempotent
        Equal(1, store.RestorePoints("game.mana").Count);
        var restored = fixture.Reviews.LoadCandidate(Path.GetDirectoryName(store.ModelPath("game.mana", 1))!)!;
        Equal(second.Id, restored.Id);
        Equal("onnx", restored.Summary.ModelFormat);
        Equal(100, restored.Summary.AdditionalFields!["maximumIterations"].GetInt32());
        var third = fixture.Candidate("game.mana", 400, "third");
        store.SynchronizeCandidates([first, second, third]);
        Equal("v1.1", store.CandidateVersion(third).DisplayName);
        Equal("v2.0", store.Promote(third).Version.DisplayName);
        Equal(2, store.RestorePoints("game.mana").Count);
        store.Activate("game.mana", 1);
        var reloaded = new LabelingModelVersionStore(store.RootDirectory);
        Equal(1, reloaded.ActiveSelection("game.mana"));
        reloaded.Activate("game.mana", null);
        Check(reloaded.ActiveSelection("game.mana") is null && reloaded.ModelPath("game.mana", null) is null);
        Equal(2, reloaded.RestorePoints("game.mana").Count);
    }

    internal static void TestIndependentObjects()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var mana = fixture.Candidate("game.mana", 100, "mana");
        var health = fixture.Candidate("game.health", 200, "health");
        store.SynchronizeCandidates([mana, health]); store.Promote(mana);
        Equal(1, store.ActiveSelection("game.mana"));
        Check(store.ActiveSelection("game.health") is null);
        Equal(0, store.RestorePoints("game.health").Count);
        Equal("v0.1", store.CandidateVersion(health).DisplayName);
        Throws<InvalidOperationException>(() => store.Activate("game.health", 1));
        Equal(1, store.ActiveSelection("game.mana"));
    }

    internal static void TestSharedModel()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var mana = fixture.Candidate("game.mana", 100, "mana");
        var knight = fixture.Candidate("game.playable-knight", 200, "knight");
        var shared = fixture.Candidate(Shared, 300, "shared");
        store.SynchronizeCandidates([mana, knight, shared]); store.Promote(knight); store.Promote(mana);
        Equal(2, store.ActiveModelArtifacts().Count);
        Equal("game.mana", store.ActiveModelArtifacts()[0].ClassIdentifier);
        store.Promote(shared);
        Equal(Shared, store.ActiveModelArtifacts().Single().ClassIdentifier);
        store.Activate(Shared, null);
        Equal(2, store.ActiveModelArtifacts().Count);
    }

    internal static void TestCheckpointRestore()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var candidate = fixture.Candidate(Shared, 100, "model", checkpoint: true);
        store.SynchronizeCandidates([candidate]);
        Check(store.TrainingCheckpointPath(Shared, [candidate]) is null);
        store.RecordDevelopmentCandidate(candidate);
        Equal("checkpoint-model", File.ReadAllText(store.TrainingCheckpointPath(Shared, [candidate])!));
        store.Promote(candidate);
        Directory.Delete(candidate.RunDirectory, recursive: true); // isolated fixture only
        Equal("checkpoint-model", File.ReadAllText(store.TrainingCheckpointPath(Shared, [])!));
        store.Activate(Shared, null);
        Check(store.TrainingCheckpointPath(Shared, []) is null);
        store.Activate(Shared, 1);
        Equal("checkpoint-model", File.ReadAllText(store.TrainingCheckpointPath(Shared, [])!));
    }

    internal static void TestRegressionLineage()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var baseline = fixture.Candidate(Shared, 100, "base", true, ap: new() { ["title"] = 1, ["decoration"] = 1 });
        var failed = fixture.Candidate(Shared, 200, "regressed", true, baseline.Id,
            new() { ["title"] = 0, ["decoration"] = .5, ["mana"] = 1 });
        store.SynchronizeCandidates([baseline, failed]); store.RecordDevelopmentCandidate(failed);
        store.ReconcileDevelopmentCandidates([failed, baseline]);
        Equal("checkpoint-base", File.ReadAllText(store.TrainingCheckpointPath(Shared, [baseline, failed])!));
        Equal("v0.2", store.CandidateVersion(failed).DisplayName);
        var sibling = fixture.Candidate(Shared, 300, "sibling", true, baseline.Id,
            new() { ["title"] = .99, ["decoration"] = .99 });
        store.SynchronizeCandidates([baseline, failed, sibling]);
        store.ReconcileDevelopmentCandidates([baseline, failed, sibling]);
        Equal("checkpoint-sibling", File.ReadAllText(store.TrainingCheckpointPath(Shared, [baseline, failed, sibling])!));
        store.Promote(baseline); // explicitly selected restore point controls lineage
        store.ReconcileDevelopmentCandidates([baseline, failed, sibling]);
        Equal("checkpoint-base", File.ReadAllText(store.TrainingCheckpointPath(Shared, [baseline, failed, sibling])!));
        var meanFailure = sibling with { Summary = sibling.Summary with
            { ValidationMetrics = new() { IsValid = true, AveragePrecisionAt50PercentIOUByClass = new() { ["title"] = .97, ["decoration"] = .97 } } } };
        Check(!LabelingDevelopmentCandidatePolicy.PreservesBase(meanFailure, baseline));
        Check(LabelingDevelopmentCandidatePolicy.PreservesBase(meanFailure with { Summary = meanFailure.Summary with { ValidationMetrics = null } }, baseline));
    }

    internal static void TestReadiness()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var id = Guid.NewGuid();
        var candidate = fixture.Candidate(Shared, 100, "model", exampleId: id);
        store.SynchronizeCandidates([candidate]);
        Check(!store.HasTrainedGameplayModel(new HashSet<Guid> { id }, [candidate]));
        store.Promote(candidate);
        Check(store.HasTrainedGameplayModel(new HashSet<Guid> { id }, [candidate]));
        Check(!store.HasTrainedGameplayModel(new HashSet<Guid>(), [candidate]));
        Check(!store.HasTrainedGameplayModel(new HashSet<Guid> { Guid.NewGuid() }, [candidate]));
        Check(!store.HasTrainedGameplayModel(new HashSet<Guid> { id }, [candidate with { PredictionReview = null }]));
        File.Delete(store.ActiveModelArtifacts().Single().ModelPath);
        Check(!store.HasTrainedGameplayModel(new HashSet<Guid> { id }, [candidate]));
    }

    internal static void TestReviewStore()
    {
        using var fixture = new Fixture(); var reviews = fixture.Reviews;
        Check(reviews.LoadLatestCandidate() is null);
        var older = fixture.Candidate("game.playable-knight", 100, "older");
        var id = Guid.NewGuid();
        var newer = fixture.Candidate("game.playable-knight", 200, "newer", exampleId: id);
        var other = fixture.Candidate("game.mana", 300, "other");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "runs", ".staging-hidden"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "runs", "interrupted"));
        Equal(3, reviews.LoadCandidates().Count);
        var loaded = reviews.LoadLatestCandidate("game.playable-knight")!;
        Equal(newer.Id, loaded.Id); Equal(newer.DatasetDirectory, loaded.DatasetDirectory);
        Equal(id, loaded.PredictionReview!.Examples.Single().ExampleIdentifier);
        Equal(.8, loaded.PredictionReview.Examples.Single().Predictions.Single().Confidence);
        Equal(.5, loaded.PredictionReview.Metrics.Precision);
        Equal(id, loaded.PredictionReview.BaselineExamples!.Single().ExampleIdentifier);
        Equal(1.0, loaded.PredictionReview.BaselineMetrics!.Recall);
        Check(reviews.LoadLatestCandidate("game.health") is null);
        Equal(other.Id, reviews.LoadLatestCandidate()!.Id);
        Check(reviews.LoadCandidate(Path.Combine(older.RunDirectory, "training.json")) is null);
        File.WriteAllText(Path.Combine(other.RunDirectory, "training.json"), "{");
        Throws<JsonException>(() => reviews.LoadCandidates()); // corruption is not silently hidden
    }

    internal static void TestPromotionSafety()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var candidate = fixture.Candidate(Shared, 100, "original", true);
        Throws<InvalidOperationException>(() => store.Promote(candidate));
        store.SynchronizeCandidates([candidate]);
        foreach (var invalid in new[] { "..", "../outside", "C:\\outside", "file:stream", "CON", "foo." })
        {
            Throws<InvalidDataException>(() => store.Promote(candidate with { Summary = candidate.Summary with { ModelFilename = invalid } }));
            Throws<InvalidDataException>(() => store.Activate(invalid, null));
        }
        Throws<InvalidDataException>(() => store.Promote(candidate with { Summary = candidate.Summary with { CheckpointFilename = "training.json" } }));
        Equal(0, store.RestorePoints(Shared).Count);
        File.Delete(Path.Combine(candidate.RunDirectory, "Detector.onnx"));
        Throws<FileNotFoundException>(() => store.Promote(candidate));
        Check(store.ActiveSelection(Shared) is null);
        File.WriteAllText(Path.Combine(candidate.RunDirectory, "Detector.onnx"), "original");
        var destination = Path.Combine(store.RootDirectory, Shared, "v1.0");
        Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");
        Throws<IOException>(() => store.Promote(candidate));
        Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Equal(0, store.RestorePoints(Shared).Count);
        Directory.Delete(destination, recursive: true); // isolated pre-existing fixture
        store.Promote(candidate);
        File.WriteAllText(Path.Combine(store.RootDirectory, "registry.json"), "{\"schemaVersion\":99,\"objects\":{}}");
        Throws<InvalidDataException>(() => store.ActiveModelArtifacts());
    }

    internal static void TestLifecycle()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        var lifecycle = new LabelingModelLifecycle(fixture.Reviews, store);
        lifecycle.Refresh(); lifecycle.PromoteLatest();
        Equal("Default", lifecycle.ActiveVersionName()); Equal(0, lifecycle.ActiveRunDirectories().Length);
        var first = fixture.Candidate(Shared, 100, "first", true, exampleId: Guid.NewGuid());
        lifecycle.Refresh(); lifecycle.PromoteLatest();
        Equal("v1.0", lifecycle.ActiveVersionName());
        Equal("first", File.ReadAllText(Path.Combine(lifecycle.ActiveRunDirectories().Single(), "Detector.onnx")));
        var second = fixture.Candidate(Shared, 200, "second", true, first.Id);
        lifecycle.Refresh(); lifecycle.Promote(second);
        Equal("v2.0", lifecycle.ActiveVersionName());
        Equal("checkpoint-second", File.ReadAllText(lifecycle.TrainingCheckpointPath()!));
        var reloaded = new LabelingModelLifecycle(fixture.Reviews, new(store.RootDirectory));
        reloaded.Refresh(); reloaded.PromoteLatest();
        Equal("v2.0", reloaded.ActiveVersionName()); Equal(2, store.RestorePoints(Shared).Count);
        var incompatible = second with { Summary = second.Summary with { ModelFormat = "coreml" } };
        Throws<InvalidDataException>(() => lifecycle.Promote(incompatible));
        Equal("v2.0", lifecycle.ActiveVersionName());
    }

    internal static void TestRegistryFailureRollback()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows file-sharing semantics are the failure injection.
        using var fixture = new Fixture(); var store = fixture.Store;
        var first = fixture.Candidate(Shared, 100, "first");
        var second = fixture.Candidate(Shared, 200, "second");
        store.SynchronizeCandidates([first, second]); store.Promote(first);
        using (var locked = new FileStream(Path.Combine(store.RootDirectory, "registry.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? failure = null;
            try { store.Promote(second); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failure = error; }
            Check(failure is not null);
        }
        Equal(1, store.RestorePoints(Shared).Count);
        Equal(1, store.ActiveSelection(Shared));
        Equal("first", File.ReadAllText(store.ModelPath(Shared, 1)!));
        Check(!Directory.Exists(Path.Combine(store.RootDirectory, Shared, "v2.0")));
        Check(!Directory.EnumerateDirectories(Path.Combine(store.RootDirectory, Shared), ".staging-*").Any());
        Equal("v2.0", store.Promote(second).Version.DisplayName);
    }

    internal static void TestDurableOnnxInference()
    {
        using var fixture = new Fixture();
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        string? bundled = null;
        while (cursor is not null)
        {
            var path = Path.Combine(cursor.FullName, "models", Shared);
            if (File.Exists(Path.Combine(path, "Detector.onnx"))) { bundled = path; break; }
            cursor = cursor.Parent;
        }
        if (bundled is null) throw new FileNotFoundException("Bundled ONNX inference fixture missing.");
        var run = Path.Combine(fixture.Root, "runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        File.Copy(Path.Combine(bundled, "Detector.onnx"), Path.Combine(run, "Detector.onnx"));
        File.Copy(Path.Combine(bundled, "training.json"), Path.Combine(run, "training.json"));
        // Keep fixture self-contained even when the bundled manifest references an unbundled review/checkpoint.
        var summary = JsonSerializer.Deserialize<LabelingTrainingRunSummary>(File.ReadAllBytes(Path.Combine(run, "training.json")), Json)!;
        summary = summary with { Id = Guid.NewGuid(), PredictionsFilename = null, CheckpointFilename = null };
        File.WriteAllText(Path.Combine(run, "training.json"), JsonSerializer.Serialize(summary, Json));
        var lifecycle = new LabelingModelLifecycle(fixture.Reviews, fixture.Store);
        lifecycle.Refresh(); lifecycle.PromoteLatest();
        Equal("v1.0", lifecycle.ActiveVersionName());
        var pixels = new byte[640 * 360 * 4];
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var frame = new BgraFrame(640, 360, 640 * 4, pixels, 1, DateTimeOffset.UnixEpoch);
        IReadOnlyList<ObjectDetection> expected;
        using (var original = new OnnxObjectDetector(run)) expected = original.Detect(frame, raw: true);
        // A blank frame correctly has no object detections. Compare the non-empty raw
        // network tensors too, so two empty decoded lists cannot hide a broken model.
        var originalTensors = RunTensors(Path.Combine(run, "Detector.onnx"));
        Directory.Delete(run, recursive: true); // only the disposable run: promotion must be self-contained
        var durableTensors = RunTensors(Path.Combine(lifecycle.ActiveRunDirectories().Single(), "Detector.onnx"));
        Check(originalTensors.Length > 0 && originalTensors.Any(value => value != 0));
        Equal(originalTensors.Length, durableTensors.Length);
        Check(originalTensors.Zip(durableTensors).All(pair => Math.Abs(pair.First - pair.Second) < .000001));
        using (var active = new OnnxObjectDetectorSet(lifecycle.ActiveRunDirectories()))
            Check(expected.SequenceEqual(active.Detect(frame, raw: true)));
        using (var duplicate = new OnnxObjectDetectorSet(lifecycle.ActiveRunDirectories().Concat(lifecycle.ActiveRunDirectories())))
            Equal(expected.Count, duplicate.Detect(frame, raw: true).Count);
        using (var live = new OnnxObjectDetectorSet(lifecycle.ActiveRunDirectories(), lifecycle.ActiveArtifacts()))
        {
            Check(live.HasModels);
            var worker = new LiveObjectInferenceWorker();
            try
            {
                worker.Observe(frame, 1, () => live.Detect(frame));
                Check(worker.WhenIdleAsync().Wait(TimeSpan.FromSeconds(10)));
                Check(worker.Diagnostics.Issue is null && worker.Diagnostics.SourceFrameIdentifier == frame.CaptureId);
                Check(worker.Diagnostics.ModelSeconds > 0 && worker.Counters.Completed == 1);
            }
            finally { worker.SetSuspended(true); worker.WhenIdleAsync().GetAwaiter().GetResult(); }
        }
        using (var empty = new OnnxObjectDetectorSet([])) { Check(!empty.HasModels); Equal(0, empty.Detect(frame).Count); }
    }

    private static float[] RunTensors(string modelPath)
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath, options);
        using var input = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(new float[3 * 360 * 640], [1, 3, 360, 640]);
        using var runOptions = new Microsoft.ML.OnnxRuntime.RunOptions();
        using var outputs = session.Run(runOptions, new Dictionary<string, Microsoft.ML.OnnxRuntime.OrtValue> { ["image"] = input }, ["scores", "boxes"]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray().Concat(outputs[1].GetTensorDataAsSpan<float>().ToArray()).ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "hkv-model-parity-" + Guid.NewGuid().ToString("N"));
        internal LabelingModelVersionStore Store { get; }
        internal LabelingModelReviewStore Reviews { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "models")); Reviews = new(Path.Combine(Root, "runs"), Path.Combine(Root, "datasets"));
        }
        internal LabelingModelCandidate Candidate(string classId, int time, string contents, bool checkpoint = false,
            Guid? baseline = null, Dictionary<string, double>? ap = null, Guid? exampleId = null)
        {
            var run = Path.Combine(Root, "runs", Guid.NewGuid().ToString("D")); Directory.CreateDirectory(run);
            var summary = new LabelingTrainingRunSummary
            {
                Id = Guid.NewGuid(), DatasetIdentifier = Guid.NewGuid(), ClassIdentifier = classId,
                CompletedAt = DateTimeOffset.FromUnixTimeSeconds(time), ModelFilename = "Detector.onnx", ModelFormat = "onnx",
                CheckpointFilename = checkpoint ? "Detector.pt" : null, BaseRunIdentifier = baseline?.ToString("D").ToUpperInvariant(),
                ClassIdentifiers = ap?.Keys.ToArray() ?? [classId], PredictionsFilename = exampleId is null ? null : "predictions.json",
                ValidationMetrics = new() { IsValid = true, AveragePrecisionAt50PercentIOUByClass = ap ?? new() { [classId] = .5 } },
                AdditionalFields = new() { ["maximumIterations"] = JsonSerializer.SerializeToElement(100) }
            };
            File.WriteAllText(Path.Combine(run, "Detector.onnx"), contents);
            if (checkpoint) File.WriteAllText(Path.Combine(run, "Detector.pt"), "checkpoint-" + contents);
            LabelingPredictionReview? review = null;
            if (exampleId is { } id)
            {
                review = new([new(id, "held-out.png", "validation", [new(classId, .1, .2, .3, .4, 1)],
                    [new(classId, .12, .2, .3, .4, .8)])], Metrics, [new(id, "held-out.png", "validation", [], [])], Metrics);
                File.WriteAllText(Path.Combine(run, "predictions.json"), JsonSerializer.Serialize(review, Json));
            }
            File.WriteAllText(Path.Combine(run, "training.json"), JsonSerializer.Serialize(summary, Json));
            return new(run, Path.Combine(Root, "datasets", summary.DatasetIdentifier.ToString("D")), summary, review);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Model parity assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
