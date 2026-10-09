using System.Diagnostics;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

public static class VisualRouteRunner
{
    public static async Task<int> RecordAsync(
        string outputDirectory,
        int durationSeconds,
        int intervalMilliseconds)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new IOException($"Visual route output directory is not empty: {output}");
        }
        var window = HollowKnightWindowLocator.FindBest();
        if (window is null)
        {
            Console.Error.WriteLine("Hollow Knight window not found; route recording does not launch it.");
            return 3;
        }
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(durationSeconds + 15));
        await using var receiver = await ReceiverConnection
            .ConnectWithRetryAsync(cancellation.Token).ConfigureAwait(false);
        var telemetry = new RouteTelemetryMonitor();
        var monitorTask = telemetry.RunAsync(receiver, cancellation.Token);
        var capture = new GdiWindowCapture();
        var timer = Stopwatch.StartNew();
        var samples = new List<VisualRouteSample>();
        long captureId = 0;
        long? lastUnityFrame = null;
        try
        {
            while (timer.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var latestWindow = HollowKnightWindowLocator.FindBest()
                    ?? throw new InvalidOperationException(
                        "Hollow Knight window closed while recording visual route.");
                var frame = CpuBgraNormalizer.Normalize(
                    capture.Capture(latestWindow, ++captureId));
                var received = telemetry.Latest;
                if (received is not null
                    && DateTimeOffset.UtcNow - received.ReceivedAt <= TimeSpan.FromMilliseconds(500)
                    && received.Value.UnityFrame != lastUnityFrame)
                {
                    var truth = received.Value;
                    var pixelsPerWorldUnitY = truth.PixelsPerWorldUnit(frame.Height);
                    var pixelsPerWorldUnitX = truth.PixelsPerWorldUnitX is > 0
                        && truth.ProjectionPixelWidth is > 0
                            ? truth.PixelsPerWorldUnitX.Value * frame.Width
                                / truth.ProjectionPixelWidth.Value
                            : pixelsPerWorldUnitY;
                    if (pixelsPerWorldUnitX is > 0 && pixelsPerWorldUnitY is > 0
                        && !string.IsNullOrWhiteSpace(truth.SceneName))
                    {
                        samples.Add(new VisualRouteSample(
                            samples.Count,
                            timer.Elapsed.TotalSeconds,
                            truth.SceneName,
                            truth.CameraX,
                            truth.CameraY,
                            pixelsPerWorldUnitX.Value,
                            pixelsPerWorldUnitY.Value,
                            LowResolutionMotionGrid.FromFrame(frame)));
                        lastUnityFrame = truth.UnityFrame;
                    }
                }
                await Task.Delay(intervalMilliseconds, cancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        if (samples.Count < 2)
        {
            throw new InvalidOperationException(
                "Fewer than two fresh receiver-camera samples were recorded.");
        }
        var session = new VisualRouteSession(
            VisualRouteSession.CurrentSchemaVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FrameGeometry.ReferenceWidth,
            FrameGeometry.ReferenceHeight,
            samples);
        new VisualRouteStore(output).Save(session);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            mode = "record-visual-route",
            output,
            session.Id,
            sampleCount = samples.Count,
            scenes = samples.Select(sample => sample.SceneName).Distinct().ToArray(),
            elapsedSeconds = timer.Elapsed.TotalSeconds
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static int Replay(
        string routeDirectory,
        int repeatCount,
        double maximumMeanErrorPixels,
        double maximumStepErrorPixels,
        string? reportPath)
    {
        var store = new VisualRouteStore(routeDirectory);
        var output = string.IsNullOrWhiteSpace(reportPath)
            ? Path.Combine(store.RootDirectory, "replay-report.json")
            : Path.GetFullPath(reportPath);
        var report = new VisualRouteReplayEvaluator(store).Evaluate(
            new VisualRouteReplayOptions(
                repeatCount,
                maximumMeanErrorPixels,
                maximumStepErrorPixels,
                ReopenSessionBetweenPasses: true),
            output);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = report.Passed,
            mode = "replay-visual-route",
            report.SessionId,
            report.RepeatCount,
            report.Passed,
            report.FailureReasons,
            reportPath = output,
            passes = report.Passes.Select(pass => new
            {
                pass.PassIndex,
                pass.ComparableSteps,
                pass.AcceptedSteps,
                pass.StationarySteps,
                pass.SceneTransitionSteps,
                pass.RejectedSteps,
                pass.MeanErrorPixels,
                pass.MaximumErrorPixels,
                pass.Passed
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        return report.Passed ? 0 : 7;
    }

    public static int BuildWorld(
        string routeDirectory,
        string? worldDirectory,
        int keyframeInterval)
    {
        var route = new VisualRouteStore(routeDirectory);
        var output = string.IsNullOrWhiteSpace(worldDirectory)
            ? Path.Combine(route.RootDirectory, "world")
            : Path.GetFullPath(worldDirectory);
        var report = VisualRouteWorldBuilder.BuildAndSave(
            route,
            new LiveWorldSnapshotStore(output),
            keyframeInterval);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            mode = "build-route-world",
            report.SessionId,
            report.SourceSamples,
            report.Rooms,
            report.Observations,
            report.Keyframes,
            report.RelativeMotionEdges,
            report.RejectedSteps,
            report.Rejections,
            worldPath = Path.Combine(output, "world.json")
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static int OptimizeWorld(string worldDirectory)
    {
        var store = new LiveWorldSnapshotStore(worldDirectory);
        var current = store.Load();
        if (current.Observations.Count == 0)
        {
            throw new InvalidDataException("World snapshot contains no observations.");
        }
        var optimized = LiveWorldPoseOptimizer.Optimize(current);
        var changed = !ReferenceEquals(current, optimized);
        if (changed && !store.Commit(optimized, current.MapRevision))
        {
            throw new IOException("World snapshot changed while optimization was running.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            mode = "optimize-world",
            changed,
            previousRevision = current.MapRevision,
            mapRevision = optimized.MapRevision,
            optimized.Observations.Count,
            rooms = optimized.Observations.Select(value => value.RoomId).Distinct().Count(),
            motionEdges = optimized.RelativeMotionEdges.Count,
            loopEdges = optimized.LoopClosureEdges.Count,
            worldPath = store.SnapshotPath
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private sealed record ReceivedRouteTelemetry(
        ReceiverGroundTruthSample Value,
        DateTimeOffset ReceivedAt);

    private sealed class RouteTelemetryMonitor
    {
        private ReceivedRouteTelemetry? latest;
        internal ReceivedRouteTelemetry? Latest => Volatile.Read(ref latest);

        internal async Task RunAsync(
            ReceiverClient receiver,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await receiver.ReceiveLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("Receiver closed while recording visual route.");
                if (ReceiverWireCodec.MessageType(line) != "groundTruth") continue;
                var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
                if (sample.CameraAvailable && sample.HasFiniteCoordinates())
                {
                    Volatile.Write(
                        ref latest,
                        new ReceivedRouteTelemetry(sample, DateTimeOffset.UtcNow));
                }
            }
        }
    }
}
