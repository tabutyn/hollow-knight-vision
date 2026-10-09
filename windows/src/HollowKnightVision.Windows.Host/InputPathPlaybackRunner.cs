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
        bool restoreCheckpoint)
    {
        if (repeatCount is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(repeatCount));
        var path = new RecordedInputPathStore().Load(pathFile);
        if (path.Duration * repeatCount > 86_400)
        {
            throw new ArgumentException("Total input-path replay duration exceeds 24 hours.");
        }
        if (HollowKnightWindowLocator.FindBest() is null)
        {
            Console.Error.WriteLine(
                "Hollow Knight window not found; input-path replay does not launch it.");
            return 3;
        }

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(path.Duration * repeatCount + 30));
        var negotiated = await ReceiverConnection
            .ConnectNegotiatedWithRetryAsync(cancellation.Token).ConfigureAwait(false);
        await using var receiver = negotiated.Client;
        if (restoreCheckpoint && path.StartCheckpoint is not null
            && !negotiated.Capabilities.Capabilities.Contains(
                ReceiverProtocol.PlayerCheckpointCapability,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Receiver does not advertise player-checkpoint-v1.");
        }

        var acknowledgements = Channel.CreateUnbounded<ReceiverCheckpointAcknowledgement>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var drainTask = DrainAsync(receiver, acknowledgements.Writer, cancellation.Token);
        var changes = path.StateChanges();
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
                    await Task.Delay(500, cancellation.Token).ConfigureAwait(false);
                }

                var iterationStart = stopwatch.Elapsed;
                foreach (var change in changes)
                {
                    await DelayUntilAsync(
                        stopwatch,
                        iterationStart + TimeSpan.FromSeconds(change.Offset),
                        cancellation.Token).ConfigureAwait(false);
                    await receiver.SendStateAsync(
                        true,
                        change.HeldButtons,
                        cancellation.Token).ConfigureAwait(false);
                }
                await DelayUntilAsync(
                    stopwatch,
                    iterationStart + TimeSpan.FromSeconds(path.Duration),
                    cancellation.Token).ConfigureAwait(false);
                await receiver.SendStateAsync(true, InputButtons.None, cancellation.Token)
                    .ConfigureAwait(false);
                completedIterations++;
            }
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
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            mode = "replay-input-path",
            path = Path.GetFullPath(pathFile),
            path.Id,
            path.Duration,
            eventCount = path.Events.Count,
            repeatCount,
            completedIterations,
            checkpointRestored = restoreCheckpoint && path.StartCheckpoint is not null,
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task DrainAsync(
        ReceiverClient receiver,
        ChannelWriter<ReceiverCheckpointAcknowledgement> acknowledgements,
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
}
