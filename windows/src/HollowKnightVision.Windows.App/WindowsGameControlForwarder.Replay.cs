using System.Diagnostics;
using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed partial class WindowsGameControlForwarder
{
    private bool checkpointAvailable;
    private CancellationTokenSource? replayCancellation;
    private readonly Dictionary<Guid, TaskCompletionSource<ReceiverCheckpointAcknowledgement>> pendingCheckpoints = [];
    internal bool IsReplaying { get { lock (stateLock) return replayCancellation is not null; } }
    internal void CancelReplay() { lock (stateLock) replayCancellation?.Cancel(); }

    // The receiver serves one connection. Keep controls, checkpoint acknowledgements,
    // render markers and recording telemetry on the already negotiated live connection.
    internal async Task ReplayPathAsync(RecordedInputPath path, int iteration, bool restoreCheckpoint,
        Action<RecordedInputPath> saveRecording, CancellationToken cancellationToken = default)
    {
        path.Validate();
        ArgumentNullException.ThrowIfNull(saveRecording);
        ReceiverClient active;
        TaskCompletionSource fault;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(path.Duration + 30));
        lock (stateLock)
        {
            if (receiver is null || connectionFault is null || replayCancellation is not null
                || inputPathRecorder.IsRecording || pause.BlocksInput)
                throw new InvalidOperationException("Enter modded Gameplay and stop recording before replaying.");
            if (restoreCheckpoint && (path.StartCheckpoint is null || !checkpointAvailable))
                throw new InvalidOperationException("Replay needs a saved start checkpoint and a compatible receiver.");
            active = receiver;
            fault = connectionFault;
            replayCancellation = cancellation;
            heldButtons = InputButtons.None;
            isEnabled = false;
            awaitingFreshPress = true;
        }
        var token = cancellation.Token;
        var recordingStarted = false;
        try
        {
            await SendReplayStateAsync(active, fault, InputButtons.None, token).ConfigureAwait(false);
            if (restoreCheckpoint)
            {
                var command = ReceiverCheckpointCommand.Restore(active.SessionId, path.StartCheckpoint!);
                var completion = new TaskCompletionSource<ReceiverCheckpointAcknowledgement>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (stateLock) pendingCheckpoints.Add(command.CommandId, completion);
                try
                {
                    await outboundGate.WaitAsync(token).ConfigureAwait(false);
                    try { await active.SendCheckpointCommandAsync(command, token).ConfigureAwait(false); }
                    finally { outboundGate.Release(); }
                    var ack = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    if (!ack.Accepted) throw new InvalidOperationException($"Checkpoint restore rejected: {ack.Failure}");
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                finally { lock (stateLock) pendingCheckpoints.Remove(command.CommandId); }
            }
            token.ThrowIfCancellationRequested();
            var start = MonotonicSeconds();
            recordingStarted = inputPathRecorder.Start(start, DateTimeOffset.UtcNow,
                sourcePathId: path.Id, replayIteration: iteration, startCheckpoint: path.StartCheckpoint);
            if (!recordingStarted) throw new InvalidOperationException("Replay recorder is already active.");
            var previous = InputButtons.None;
            foreach (var change in path.PlaybackStates())
            {
                var remaining = start + change.Offset - MonotonicSeconds();
                if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), token).ConfigureAwait(false);
                await SendReplayStateAsync(active, fault, change.HeldButtons, token).ConfigureAwait(false);
                var now = MonotonicSeconds();
                foreach (var button in Enum.GetValues<RecordedGameButton>())
                {
                    var bit = button.ToInputButton();
                    if ((previous & bit) != (change.HeldButtons & bit))
                        inputPathRecorder.RecordInput(button, (change.HeldButtons & bit) != 0, now);
                }
                previous = change.HeldButtons;
            }
        }
        finally
        {
            RecordedInputPath? recording = null;
            try
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendStateOrderedAsync(active, fault, false, InputButtons.None, releaseTimeout.Token).ConfigureAwait(false);
            }
            finally
            {
                if (recordingStarted) recording = inputPathRecorder.Stop(MonotonicSeconds());
                lock (stateLock)
                {
                    if (ReferenceEquals(replayCancellation, cancellation)) replayCancellation = null;
                    heldButtons = InputButtons.None;
                    isEnabled = false;
                    awaitingFreshPress = true;
                }
                // Keep partial evidence on cancellation/failure as well as completed runs.
                if (recording is not null) saveRecording(recording);
            }
        }
    }

    private async Task SendReplayStateAsync(ReceiverClient active, TaskCompletionSource fault,
        InputButtons buttons, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (stateLock)
            if (!ReferenceEquals(receiver, active) || fault.Task.IsCompleted || pause.BlocksInput)
                throw new IOException("Receiver connection changed during replay.");
        await SendStateOrderedAsync(active, fault, true, buttons, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (fault.Task.IsCompleted) throw new IOException("Receiver send failed during replay.");
    }
}
