using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed partial class WindowsGameControlForwarder
{
    private bool checkpointAvailable, posePlaybackAvailable, capturingCheckpoint;
    private CancellationTokenSource? replayCancellation;
    private TaskCompletionSource? replayFinished;
    private readonly Dictionary<Guid, TaskCompletionSource<ReceiverCheckpointAcknowledgement>> pendingCheckpoints = [];
    internal bool IsReplaying { get { lock (stateLock) return replayCancellation is not null; } }
    internal void CancelReplay() { lock (stateLock) replayCancellation?.Cancel(); }

    private async Task<RecordedGameCheckpoint> CheckpointAsync(ReceiverClient active,
        ReceiverCheckpointCommand command, CancellationToken token)
    {
        var completion = new TaskCompletionSource<ReceiverCheckpointAcknowledgement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        lock (stateLock) pendingCheckpoints.Add(command.CommandId, completion);
        try
        {
            await outboundGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try { await active.SendCheckpointCommandAsync(command, cancellation.Token).ConfigureAwait(false); }
            finally { outboundGate.Release(); }
            var ack = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token).ConfigureAwait(false);
            if (!ack.Accepted || ack.Checkpoint is not { } checkpoint || !checkpoint.HasFiniteCoordinates())
                throw new InvalidOperationException($"Checkpoint rejected: {ack.Failure ?? "missing or invalid state"}");
            return checkpoint;
        }
        finally { lock (stateLock) pendingCheckpoints.Remove(command.CommandId); }
    }

    // All controls, poses and acknowledgements use the receiver's single live connection.
    internal async Task ReplayPathAsync(RecordedInputPath path, int iteration, bool restoreCheckpoint,
        Action<RecordedInputPath> saveRecording, CancellationToken cancellationToken = default,
        Func<bool, CancellationToken, Task>? prepare = null, Action<double>? beginEvaluation = null,
        string evaluationMode = "none")
    {
        var poses = InputPathPlayback.Poses(path);
        ArgumentNullException.ThrowIfNull(saveRecording);
        ReceiverClient active;
        TaskCompletionSource fault;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(path.Duration + 120));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (stateLock)
        {
            if (receiver is null || connectionFault is null || replayCancellation is not null || capturingCheckpoint
                || inputPathRecorder.IsRecording || pause.BlocksInput)
                throw new InvalidOperationException("Enter modded Gameplay and stop recording before replaying.");
            if (restoreCheckpoint && (path.StartCheckpoint is null || !checkpointAvailable))
                throw new InvalidOperationException("Replay needs a saved start checkpoint and a compatible receiver.");
            if (poses.Length > 0 && !posePlaybackAvailable)
                throw new InvalidOperationException("Update the mod receiver: player-pose-playback-v1 is required for this recording.");
            active = receiver;
            fault = connectionFault;
            replayCancellation = cancellation;
            replayFinished = finished;
            heldButtons = InputButtons.None;
            isEnabled = false;
            awaitingFreshPress = true;
        }
        var token = cancellation.Token;
        var recordingStarted = false;
        try
        {
            await SendReplayStateAsync(active, fault, InputButtons.None, token).ConfigureAwait(false);
            if (prepare is not null) await prepare(false, token).ConfigureAwait(false);
            if (restoreCheckpoint)
                await CheckpointAsync(active, ReceiverCheckpointCommand.Restore(active.SessionId, path.StartCheckpoint!), token).ConfigureAwait(false);
            if (prepare is not null) await prepare(true, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var start = MonotonicSeconds();
            recordingStarted = inputPathRecorder.Start(start, DateTimeOffset.UtcNow,
                sourcePathId: path.Id, replayIteration: iteration, startCheckpoint: path.StartCheckpoint,
                metadata: new Dictionary<string, string> {
                    ["playbackMode"] = poses.Length > 0 ? "recorded-player-poses-and-input" : "input-only",
                    ["pathPlaybackPreparation"] = prepare is not null ? "isolated-single-restore-settle-reset-v1" : restoreCheckpoint ? "restore-ack-only" : "none",
                    ["evaluationMode"] = evaluationMode });
            if (!recordingStarted) throw new InvalidOperationException("Replay recorder is already active.");
            beginEvaluation?.Invoke(start);
            var previous = InputButtons.None;
            await InputPathPlayback.RunAsync(path, async (buttons, ct) => {
                await SendReplayStateAsync(active, fault, buttons, ct).ConfigureAwait(false);
                foreach (var button in Enum.GetValues<RecordedGameButton>())
                {
                    var bit = button.ToInputButton();
                    if ((previous & bit) != (buttons & bit))
                        inputPathRecorder.RecordInput(button, (buttons & bit) != 0, MonotonicSeconds());
                }
                previous = buttons;
            }, async (sample, ct) => {
                ct.ThrowIfCancellationRequested();
                await outboundGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    lock (stateLock)
                        if (!ReferenceEquals(receiver, active) || fault.Task.IsCompleted)
                            throw new IOException("Receiver connection changed during pose replay.");
                    await active.SendPlayerPoseAsync(sample, ct).ConfigureAwait(false);
                }
                finally { outboundGate.Release(); }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendStateOrderedAsync(active, fault, false, InputButtons.None, releaseTimeout.Token).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    var recording = recordingStarted ? inputPathRecorder.Stop(MonotonicSeconds()) : null;
                    lock (stateLock)
                    {
                        if (ReferenceEquals(replayCancellation, cancellation)) replayCancellation = null;
                        heldButtons = InputButtons.None;
                        isEnabled = false;
                        awaitingFreshPress = true;
                    }
                    if (recording is not null) saveRecording(recording);
                }
                finally { finished.TrySetResult(); }
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
