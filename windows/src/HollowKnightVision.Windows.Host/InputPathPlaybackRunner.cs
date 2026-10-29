using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

public static class InputPathPlaybackRunner
{
    public static async Task<int> RunAsync(
        string pathFile,
        int repeatCount,
        bool restoreCheckpoint,
        PlaybackDivergenceMonitor? divergenceMonitor = null,
        Action<PlaybackDivergenceDecision>? onDivergence = null,
        CancellationToken cancellationToken = default)
    {
        if (repeatCount is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(repeatCount));
        var path = new RecordedInputPathStore().Load(pathFile);
        if (path.Duration * repeatCount > 86_400)
        {
            throw new ArgumentException("Total input-path replay duration exceeds 24 hours.");
        }
        var poses = InputPathPlayback.Poses(path);
        var gameWindow = HollowKnightWindowLocator.FindBest();
        if (gameWindow is null)
        {
            Console.Error.WriteLine(
                "Hollow Knight window not found; input-path replay does not launch it.");
            return 3;
        }

        using var timeoutCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(path.Duration * repeatCount + 30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCancellation.Token,
            cancellationToken);
        divergenceMonitor?.Prepare();
        PlaybackDivergenceDecision? divergence = null;
        void Diverged(PlaybackDivergenceDecision decision)
        {
            if (Interlocked.CompareExchange(ref divergence, decision, null) is not null) return;
            onDivergence?.Invoke(decision);
            cancellation.Cancel();
        }
        var negotiated = await ReceiverConnection
            .ConnectNegotiatedWithRetryAsync(
                cancellation.Token,
                renderFrameMarker: divergenceMonitor is not null)
            .ConfigureAwait(false);
        await using var receiver = negotiated.Client;
        if (restoreCheckpoint && path.StartCheckpoint is not null
            && !negotiated.Capabilities.Capabilities.Contains(
                ReceiverProtocol.PlayerCheckpointCapability,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Receiver does not advertise player-checkpoint-v1.");
        }

        if (poses.Length > 0 && !negotiated.Capabilities.Capabilities.Contains(ReceiverProtocol.PlayerPosePlaybackCapability))
            throw new InvalidOperationException("Receiver does not advertise player-pose-playback-v1.");
        var acknowledgements = Channel.CreateUnbounded<ReceiverCheckpointAcknowledgement>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var drainTask = DrainAsync(
            receiver,
            acknowledgements.Writer,
            divergenceMonitor,
            new PlaybackFrameSize(gameWindow.ClientWidth, gameWindow.ClientHeight),
            Diverged,
            cancellation.Token);
        var changes = path.PlaybackStates();
        var completedIterations = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            for (var iteration = 0; iteration < repeatCount; iteration++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (restoreCheckpoint && path.StartCheckpoint is { } checkpoint)
                {
                    var command = ReceiverCheckpointCommand.Restore(
                        receiver.SessionId,
                        checkpoint);
                    await receiver.SendCheckpointCommandAsync(command, cancellation.Token)
                        .ConfigureAwait(false);
                    var acknowledgement = await WaitForCheckpointAsync(
                        acknowledgements.Reader,
                        command.CommandId,
                        cancellation.Token).ConfigureAwait(false);
                    if (!acknowledgement.Accepted)
                    {
                        throw new InvalidOperationException(
                            $"Checkpoint restore rejected: {acknowledgement.Failure ?? "unknown failure"}");
                    }

                }

                divergenceMonitor?.BeginEvaluation(MonotonicSeconds());

                await InputPathPlayback.RunAsync(path,
                    (buttons, ct) => receiver.SendStateAsync(true, buttons, ct),
                    (pose, ct) => receiver.SendPlayerPoseAsync(pose, ct), cancellation.Token).ConfigureAwait(false);
                completedIterations++;
            }
        }
        catch (OperationCanceledException) when (
            divergence is not null || divergenceMonitor?.LastDecision is not null)
        {
            divergence ??= divergenceMonitor?.LastDecision;
        }
        finally
        {
            try
            {
                await receiver.SendStateAsync(true, InputButtons.None, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Connection loss already guarantees the receiver's input lease expires.
            }
            cancellation.Cancel();
            try
            {
                await drainTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            divergence ??= divergenceMonitor?.LastDecision;
            divergenceMonitor?.Stop();
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = divergence is null,
            trackingEvaluated = divergenceMonitor is not null,
            playbackMode = poses.Length > 0 ? "recorded-player-poses-and-input" : "input-only",
            preparation = "restore-ack-only",
            mode = "replay-input-path",
            path = Path.GetFullPath(pathFile),
            path.Id,
            path.Duration,
            eventCount = path.Events.Count,
            repeatCount,
            completedIterations,
            checkpointRestored = restoreCheckpoint && path.StartCheckpoint is not null,
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            divergence
        }, new JsonSerializerOptions { WriteIndented = true }));
        return divergence is null ? 0 : 4;
    }

    private static async Task DrainAsync(
        ReceiverClient receiver,
        ChannelWriter<ReceiverCheckpointAcknowledgement> acknowledgements,
        PlaybackDivergenceMonitor? divergenceMonitor,
        PlaybackFrameSize frameSize,
        Action<PlaybackDivergenceDecision> onDivergence,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var line = await receiver.ReceiveLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("Receiver closed during input-path replay.");
                var type = ReceiverWireCodec.MessageType(line);
                if (type == "groundTruth" && divergenceMonitor is not null)
                {
                    var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
                    if (divergenceMonitor.AppendGroundTruth(
                            sample,
                            MonotonicSeconds(),
                            frameSize) is { } decision)
                    {
                        onDivergence(decision);
                    }
                    continue;
                }
                if (type is not ("captureCheckpointAck" or "restoreCheckpointAck")) continue;
                var acknowledgement = ReceiverWireCodec
                    .DecodeLine<ReceiverCheckpointAcknowledgement>(line);
                if (acknowledgement.SessionId == receiver.SessionId)
                {
                    await acknowledgements.WriteAsync(acknowledgement, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            acknowledgements.TryComplete(failure);
        }
    }

    private static async Task<ReceiverCheckpointAcknowledgement> WaitForCheckpointAsync(
        ChannelReader<ReceiverCheckpointAcknowledgement> acknowledgements,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (await acknowledgements.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
            {
                while (acknowledgements.TryRead(out var acknowledgement))
                {
                    if (acknowledgement.CommandId == commandId) return acknowledgement;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for checkpoint restore acknowledgement.");
        }
        throw new IOException("Receiver acknowledgement stream closed.");
    }

    private static async Task DelayUntilAsync(
        Stopwatch stopwatch,
        TimeSpan target,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = target - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private static double MonotonicSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
