using System.Diagnostics;
using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed partial class WindowsGameControlForwarder
{
    private bool controlledFramesAvailable;
    private readonly Dictionary<Guid, TaskCompletionSource<ReceiverControlledFrameAcknowledgement>> pendingControlledFrames = [];

    private async Task<ReceiverControlledFrameAcknowledgement> AdvanceControlledFrameAsync(ReceiverClient active,
        ControlledReplayFrame? frame, CancellationToken token)
    {
        var wait = Stopwatch.StartNew();
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<ReceiverControlledFrameAcknowledgement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (stateLock) pendingControlledFrames.Add(id, completion);
        try
        {
            await outboundGate.WaitAsync(token).ConfigureAwait(false);
            try { await active.SendControlledFrameAsync(id, frame, token).ConfigureAwait(false); }
            finally { outboundGate.Release(); }
            var ack = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            if (!ack.Accepted || ack.Version != 2 || ack.UnityFrame < 0
                || !double.IsFinite(ack.DeltaSeconds) || Math.Abs(ack.DeltaSeconds - (frame?.DeltaSeconds ?? 0)) > .00001)
                throw new InvalidDataException("Controlled game frame rejected: " + (ack.Failure ?? "invalid simulation delta"));
            return ack with { GameWaitMilliseconds = wait.Elapsed.TotalMilliseconds };
        }
        finally { lock (stateLock) pendingControlledFrames.Remove(id); }
    }

    private async Task StepPauseAsync(ReceiverClient active, TaskCompletionSource fault, bool paused, CancellationToken token)
    {
        ReceiverPauseCommand? command;
        lock (stateLock)
        {
            if (!ReferenceEquals(receiver, active) || pause.State == GamePauseState.Unavailable)
                throw new IOException("Step replay requires the mod's pause capability.");
            command = pause.Request(paused);
        }
        if (command is not null) await SendPauseOrderedAsync(active, fault, command, token).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (stateLock)
            {
                if (!ReferenceEquals(receiver, active) || fault.Task.IsCompleted) throw new IOException("Step replay receiver disconnected.");
                if (pause.State == (paused ? GamePauseState.Paused : GamePauseState.Ready)) return;
            }
            if (timer.Elapsed.TotalSeconds > 3) throw new TimeoutException("Mod did not acknowledge step replay pause/resume.");
            await Task.Delay(5, token).ConfigureAwait(false);
        }
    }

    internal async Task ReplayStepAsync(RecordedInputPath path, Func<CancellationToken, Task> prepare,
        Action<double> begin, IReadOnlyList<ControlledReplayFrame> frames,
        Func<ControlledReplayFrame, ReceiverControlledFrameAcknowledgement, CancellationToken, Task> score,
        Action<RecordedGameCheckpoint> stashEndpoint, Action<RecordedGameCheckpoint> restoredStart, CancellationToken cancellationToken)
    {
        _ = InputPathPlayback.Poses(path);
        if (path.Duration > 1.000001 || path.StartCheckpoint is null) throw new InvalidDataException("A replay step must be at most one second with a checkpoint.");
        ReceiverClient active; TaskCompletionSource fault;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (stateLock)
        {
            if (receiver is null || connectionFault is null || replayCancellation is not null || capturingCheckpoint || inputPathRecorder.IsRecording
                || !controlledFramesAvailable || !checkpointAvailable || !posePlaybackAvailable || pause.State == GamePauseState.Unavailable)
                throw new InvalidOperationException("Step replay requires receiver 0.9.4 or newer with controlled-frame, checkpoint and pause support.");
            active = receiver; fault = connectionFault; replayCancellation = cancellation; replayFinished = finished;
            stepPauseOwned = true;
            heldButtons = InputButtons.None; isEnabled = false; awaitingFreshPress = true;
        }
        try
        {
            await SendStateOrderedAsync(active, fault, false, InputButtons.None, cancellation.Token).ConfigureAwait(false);
            await StepPauseAsync(active, fault, true, cancellation.Token).ConfigureAwait(false);
            await CheckpointAsync(active, ReceiverCheckpointCommand.Restore(active.SessionId, path.StartCheckpoint), cancellation.Token).ConfigureAwait(false);
            var restoredCheckpoint = await CheckpointAsync(active, ReceiverCheckpointCommand.Capture(active.SessionId), cancellation.Token).ConfigureAwait(false);
            if (restoredCheckpoint.SceneMasks is null) throw new InvalidDataException("Step replay requires receiver 0.9.5 with scene-mask checkpoints.");
            restoredStart(restoredCheckpoint);
            await prepare(cancellation.Token).ConfigureAwait(false);
            begin(MonotonicSeconds());
            foreach (var frame in frames)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var ack = await AdvanceControlledFrameAsync(active, frame, cancellation.Token).ConfigureAwait(false);
                // Do not advance the next simulation tick until this frame passes.
                await score(frame, ack, cancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AdvanceControlledFrameAsync(active, null, cleanup.Token).ConfigureAwait(false);
                await StepPauseAsync(active, fault, true, cleanup.Token).ConfigureAwait(false);
                await SendStateOrderedAsync(active, fault, false, InputButtons.None, cleanup.Token).ConfigureAwait(false);
                stashEndpoint(await CheckpointAsync(active, ReceiverCheckpointCommand.Capture(active.SessionId), cleanup.Token).ConfigureAwait(false));
            }
            finally
            {
                lock (stateLock)
                {
                    if (ReferenceEquals(replayCancellation, cancellation)) replayCancellation = null;
                    heldButtons = InputButtons.None; isEnabled = false; awaitingFreshPress = true;
                }
                finished.TrySetResult();
            }
        }
    }

    internal async Task LeaveStepReplayAsync(CancellationToken token = default)
    {
        ReceiverClient? active; TaskCompletionSource? fault;
        lock (stateLock) { active = receiver; fault = connectionFault; }
        if (active is not null && fault is not null) await StepPauseAsync(active, fault, false, token).ConfigureAwait(false);
        lock (stateLock) stepPauseOwned = false;
    }
}
