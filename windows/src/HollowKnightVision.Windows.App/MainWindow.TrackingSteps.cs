using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private StepCapture? stepCapture;
    private bool stepMode, restartStepSession;
    private string? stepSessionDirectory;

    private sealed class StepCapture(int segment, int count, CancellationTokenSource stop)
    {
        private readonly object gate = new();
        private readonly Queue<TrackingStepEvidence> seed = new();
        private readonly Queue<TrackingStepPipelineSample> pipeline = new();
        private readonly object traceGate = new();
        private readonly Queue<(Guid Command, TrackingStepEvidence Evidence)> completed = new();
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource captureRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid? requestedCommand;
        public async Task WaitForCaptureRequestAsync(CancellationToken token)
        {
            while (true)
            {
                Task pending;
                lock (gate)
                {
                    if (started is null && seed.Count < 20 || requestedCommand is { } id && !completed.Any(v => v.Command == id)) return;
                    pending = captureRequested.Task;
                }
                await pending.WaitAsync(token).ConfigureAwait(false);
            }
        }
        private TrackingStepProcessor? processor;
        private AtlasPoint alignment;
        private readonly TrackingStepGate scoring = new(segment, count);
        private readonly List<TrackingControlledFrameTiming> controlledFrames = [];
        private double? started;
        public int SeedCount { get { lock (gate) return seed.Count; } }
        public string? Failure { get { lock (gate) return scoring.Failure; } }
        public bool Started { get { lock (gate) return started is not null; } }
        public void Trace(string stage, long captureId, long? gameFrame, double capturedAt, double? milliseconds = null)
        {
            lock (traceGate)
            {
                pipeline.Enqueue(new(stage, captureId, gameFrame, capturedAt,
                    Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, milliseconds, GC.CollectionCount(2)));
                while (pipeline.Count > 600) pipeline.Dequeue();
            }
        }
        public void Ready(TrackingStepProcessor value, AtlasPoint offset) { lock (gate) { processor = value; alignment = offset; } }
        public void Begin(double timestamp) { lock (gate) started = timestamp; }
        public TrackingStepEvidence[] Seeds()
        {
            lock (gate)
            {
                var values = seed.ToArray();
                var end = values[^1].Input.Timestamp + 1.0 / 30;
                return values.Select(v => v with { Input = v.Input with { Timestamp = v.Input.Timestamp - end } }).ToArray();
            }
        }
        public void Observe(TrackingStepEvidence evidence, Guid? command)
        {
            lock (gate)
            {
                if (started is null)
                {
                    seed.Enqueue(evidence);
                    while (seed.Count > 20) seed.Dequeue();
                }
                else if (command is { } id && requestedCommand == id && !completed.Any(v => v.Command == id))
                {
                    completed.Enqueue((id, evidence));
                    while (completed.Count > 64) completed.Dequeue();
                    changed.TrySetResult();
                    changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
        public async Task ScoreAsync(ControlledReplayFrame frame, ReceiverControlledFrameAcknowledgement ack, CancellationToken token)
        {
            var wait = Stopwatch.StartNew();
            lock (gate)
            {
                completed.Clear(); requestedCommand = ack.CommandId;
                captureRequested.TrySetResult();
                captureRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            while (true)
            {
                Task pending;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    var found = completed.FirstOrDefault(v => v.Command == ack.CommandId && v.Evidence.GameFrame >= ack.UnityFrame);
                    if (found.Evidence is { } evidence)
                    {
                        requestedCommand = null;
                        controlledFrames.Add(new(frame.Index, ack.CommandId, ack.UnityFrame, evidence.GameFrame,
                            ack.DeltaSeconds, ack.GameWaitMilliseconds, wait.Elapsed.TotalMilliseconds));
                        Trace("controlled-capture-wait", evidence.Input.Frame.CaptureId, evidence.GameFrame,
                            evidence.Input.Timestamp, wait.Elapsed.TotalMilliseconds);
                        evidence = evidence with { Input = evidence.Input with { Timestamp = segment + frame.Offset } };
                        var before = Stopwatch.GetTimestamp();
                        var result = processor!.Process(evidence.Input);
                        var elapsed = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                        if (!scoring.Observe(frame.Index, evidence, new(result.Telemetry.CameraX, result.Telemetry.CameraY), alignment,
                            elapsed, result.Source, processor.LastProfile)) stop.Cancel();
                        token.ThrowIfCancellationRequested();
                        return;
                    }
                    pending = changed.Task;
                }
                var remaining = TimeSpan.FromSeconds(8) - wait.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("No exact captured frame arrived for the controlled simulation tick.");
                await pending.WaitAsync(remaining, token).ConfigureAwait(false);
            }
        }
        public TrackingStepAttempt Finish()
        {
            lock (gate) lock (traceGate) return scoring.Finish() with { ControlledFrames = controlledFrames.ToArray(), Pipeline = pipeline
                .Where(p => started is null || p.CapturedAt >= started.Value - .1)
                .Select(p => p with { CapturedAt = p.CapturedAt - (started ?? 0), ObservedAt = p.ObservedAt - (started ?? 0) })
                .ToArray() };
        }
    }

    private void ObserveTrackingStep(BgraFrame frame, GameplaySceneMasks masks, bool admitted,
        double timestamp, ReceiverGroundTruthSample truth, int gameFrame)
    {
        if (Volatile.Read(ref stepCapture) is not { } active) return;
        var transform = GroundTruthCameraTransform.FromSample(truth, timestamp, new(frame.Width, frame.Height));
        if (transform is null) return;
        // WGC normalization already returns owned pixels; keep that immutable
        // frame instead of making another large allocation for every observation.
        var owned = frame;
        active.Trace("tracker", frame.CaptureId, gameFrame, timestamp);
        active.Observe(new(new(owned, timestamp, truth.SceneName, masks.GroundExclusions.ToArray(),
            masks.GroundRegions.OmittedRects.ToArray(), masks.GroundKnight, admitted), gameFrame,
            HackerAtlasCameraPlacement.CameraPosition(transform)), truth.ControlledFrameId);
    }

    private sealed record StepPresentation(BgraFrame Frame, string Backend, string? Fallback, double ReadbackMilliseconds);

    // Keep WGC receipt and exact-frame joining off the WPF dispatcher. UI image
    // uploads may coalesce, but must not delay the pixels used by the replay gate.
    private async Task RunStepCaptureAsync(WindowsGameCapture capture, long generation,
        Task analysisTask, Task hackerTask, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        StepPresentation? pending = null;
        var worker = Task.Run(async () => {
            long id = 0;
            var window = HollowKnightVision.Windows.Host.HollowKnightWindowLocator.FindBest()
                ?? throw new GameWindowLostException();
            ValidateGameWindowMode(window);
            while (!stop.IsCancellationRequested)
            {
                if (analysisTask.IsCompleted) await analysisTask.ConfigureAwait(false);
                if (hackerTask.IsCompleted) await hackerTask.ConfigureAwait(false);
                if (capture.Backend == "GDI")
                {
                    window = HollowKnightVision.Windows.Host.HollowKnightWindowLocator.FindBest()
                        ?? throw new GameWindowLostException();
                    ValidateGameWindowMode(window);
                }
                if (Volatile.Read(ref stepCapture) is { } requested)
                    await requested.WaitForCaptureRequestAsync(stop.Token).ConfigureAwait(false);
                var revision = labelCapture.Revision;
                var requestAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                Volatile.Read(ref stepCapture)?.Trace("capture-request", id + 1, null, requestAt);
                var frame = await capture.CaptureAsync(window, ++id, stop.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    window = HollowKnightVision.Windows.Host.HollowKnightWindowLocator.FindBest()
                        ?? throw new GameWindowLostException();
                    ValidateGameWindowMode(window);
                    continue;
                }
                if (!labelCapture.Accept(frame, revision)) continue;
                var timestamp = capture.CaptureTimestamp;
                var gameFrame = RenderedFrameMarker.Decode(frame);
                Volatile.Read(ref stepCapture)?.Trace("capture", frame.CaptureId, gameFrame, timestamp, capture.ReadbackMilliseconds);
                BufferReceiverCapture(generation, new PendingVisionFrame(frame, revision, timestamp), gameFrame);
                Interlocked.Exchange(ref pending, new StepPresentation(frame, capture.Backend,
                    capture.FallbackReason, capture.ReadbackMilliseconds));
                if (capture.Backend == "GDI") await Task.Delay(16, stop.Token).ConfigureAwait(false);
            }
        }, stop.Token);
        try
        {
            while (!worker.IsCompleted)
            {
                await Task.Delay(16, stop.Token);
                if (Interlocked.Exchange(ref pending, null) is not { } latest) continue;
                var frame = latest.Frame;
                captureBackend = latest.Backend; captureFallbackReason = latest.Fallback;
                captureReadbackMilliseconds = latest.ReadbackMilliseconds;
                var first = !hasFreshGameFrame; hasFreshGameFrame = true;
                GameplayImage.Source = FrameBitmap(frame);
                Interlocked.Exchange(ref latestPresentedCaptureId, frame.CaptureId);
                if (first)
                {
                    PositionUnregisteredGameplayFrame(frame);
                    ApplyViewport(GameplayAtlasCanvas, GameplayPanel, "Current", GameplayImage);
                }
                GameplayPlaceholder.Visibility = Visibility.Collapsed;
                UpdateModeAvailability();
            }
            await worker;
        }
        finally
        {
            stop.Cancel();
            try { await worker; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private string StepConditionsHash()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HollowKnightVision", "labeling-v1");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(TrackingStepProcessor.StateContract + ";controlled-frame-v2-scene-masks;30fps;5px;33.333ms;"));
        if (Directory.Exists(root))
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "selection.json").Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file)));
                hash.AppendData(File.ReadAllBytes(file));
            }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async void TrackingStep_Click(object sender, RoutedEventArgs e)
    {
        if (inputPathReplayCancellation is not null) { inputPathReplayCancellation.Cancel(); return; }
        if (gameMode != GameRuntimeMode.Modded) { ShowError(new InvalidOperationException("Step replay requires Modded Hollow Knight.")); return; }
        if (string.IsNullOrWhiteSpace(InputPathText.Text)) BrowseInputPath_Click(sender, e);
        var path = InputPathText.Text.Trim(); if (string.IsNullOrEmpty(path)) return;
        using var cancellation = new CancellationTokenSource();
        inputPathReplayCancellation = cancellation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); replayUiFinished = finished;
        PathRecordingButton.IsEnabled = false; ReplayFullTraceItem.IsEnabled = ReplayFailFastItem.IsEnabled = false;
        StepReplayButton.Content = "Stop step"; ReplayInputPathButton.IsEnabled = false;
        stepMode = true;
        try
        {
            GameplayWorkspace_Click(GameplayWorkspaceButton, new RoutedEventArgs());
            var source = new RecordedInputPathStore().Load(path);
            if (source.StartCheckpoint is null) throw new InvalidDataException("Step replay requires a full start checkpoint.");
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var conditions = StepConditionsHash();
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HollowKnightVision", "tracking-steps", source.Id.ToString("N"));
            Directory.CreateDirectory(root);
            var pointer = Path.Combine(root, "current-controlled-v2.txt");
            if (restartStepSession || !File.Exists(pointer))
            {
                var id = Guid.NewGuid().ToString("N"); var temporary = pointer + "." + id;
                File.WriteAllText(temporary, id); File.Move(temporary, pointer, overwrite: true); restartStepSession = false;
            }
            var session = File.ReadAllText(pointer).Trim();
            if (!Guid.TryParseExact(session, "N", out _)) throw new InvalidDataException("Invalid step replay session pointer.");
            stepSessionDirectory = Path.Combine(root, session);
            var store = new TrackingStepStore(stepSessionDirectory);
            TrackingStepManifest? manifest = store.Exists ? store.Load(sourceHash, conditions) : null;
            var second = manifest?.Accepted.Length ?? 0;
            if (second >= source.Duration - .05) { SetStatus("All recorded seconds passed the strict tracking gate."); return; }
            await StopReplayCaptureAsync();
            var checkpoint = manifest is null ? source.StartCheckpoint : second == 0 ? manifest.StartCheckpoint : manifest.Accepted[^1].EndCheckpoint;
            var slice = TrackingStepPath.Slice(source, second, checkpoint);
            var frames = ControlledFrameReplay.Frames(source, second);
            var count = frames.Length;
            var capture = new StepCapture(second, count, cancellation); Volatile.Write(ref stepCapture, capture);
            RecordedGameCheckpoint? endpoint = null;
            RecordedGameCheckpoint? restoredStart = null;
            Exception? replayFailure = null;
            try
            {
                await gameControls.ReplayStepAsync(slice, token => Dispatcher.InvokeAsync(async () => {
                    StartVision_Click(this, new RoutedEventArgs());
                    SetStatus($"Preparing second {second}–{second + slice.Duration:F2}; game paused…");
                    var timer = Stopwatch.StartNew();
                    while (capture.SeedCount < 20)
                    {
                        token.ThrowIfCancellationRequested();
                        if (timer.Elapsed.TotalSeconds > 8) throw new TimeoutException("No exact game frames arrived while preparing the step.");
                        await Task.Delay(20, token);
                    }
                    manifest ??= await Task.Run(() => store.Initialize(sourceHash, conditions, restoredStart ?? source.StartCheckpoint, capture.Seeds()), token);
                    var restored = await Task.Run(() => store.Rehydrate(manifest), token);
                    manifest = restored.Manifest; capture.Ready(restored.Processor, restored.Alignment);
                    SetStatus($"Checking second {second}–{second + slice.Duration:F2}: every frame ≤5 px at 30 fps.");
                }).Task.Unwrap(), capture.Begin, frames, capture.ScoreAsync, value => endpoint = value, value => restoredStart = value, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { replayFailure = error; }
            await Task.Delay(80); // Drain captures already taken before the mod acknowledged pause.
            await StopReplayCaptureAsync();
            if (replayFailure is not null && !capture.Started) throw replayFailure;
            var attempt = capture.Finish();
            if (replayFailure is not null)
                attempt = attempt with { Failure = (attempt.Failure is null ? "" : attempt.Failure + " ")
                    + "Replay boundary failed: " + replayFailure.Message };
            if (attempt.Passed && endpoint is not null && manifest is not null && !cancellation.IsCancellationRequested)
            {
                manifest = await Task.Run(() => store.Accept(manifest, attempt, endpoint));
                StepReplayButton.Content = $"Next: {manifest.Accepted.Length}s";
                SetStatus($"Second {second} passed all {count} frames. Game paused; next boundary saved.");
            }
            else
            {
                await Task.Run(() => store.SaveAttempt(attempt with { Failure = attempt.Failure ?? "Step cancelled before committing its boundary." }));
                StepReplayButton.Content = $"Retry: {second}s";
                SetStatus($"Stopped at second {second}: {attempt.Failure ?? "cancelled"} Game paused; accepted prefix preserved.");
            }
            StepReplayButton.ToolTip = $"{attempt.Failure ?? "All frames passed"}\nEvidence: {stepSessionDirectory}";
        }
        catch (Exception error) { StepReplayButton.Content = "Retry step"; ShowError(error); }
        finally
        {
            Volatile.Write(ref stepCapture, null);
            try { await StopReplayCaptureAsync(); }
            finally
            {
                if (GameplayImage.Source is not null) GameplayPlaceholder.Visibility = Visibility.Collapsed;
                inputPathReplayCancellation = null; ReplayInputPathButton.IsEnabled = true;
                PathRecordingButton.IsEnabled = false; // Leave step replay resumes the game.
                ReplayFullTraceItem.IsEnabled = ReplayFailFastItem.IsEnabled = true;
                finished.TrySetResult();
            }
        }
    }

    private async void LeaveTrackingSteps_Click(object sender, RoutedEventArgs e)
    {
        inputPathReplayCancellation?.Cancel();
        if (replayUiFinished is { Task.IsCompleted: false } pending) await pending.Task;
        await gameControls.LeaveStepReplayAsync();
        stepMode = false; PathRecordingButton.IsEnabled = true; StepReplayButton.Content = "Step 1s";
        StartVision_Click(this, new RoutedEventArgs());
    }
    private void RestartTrackingSteps_Click(object sender, RoutedEventArgs e)
    {
        if (inputPathReplayCancellation is not null) return;
        restartStepSession = true; TrackingStep_Click(sender, e);
    }
}
