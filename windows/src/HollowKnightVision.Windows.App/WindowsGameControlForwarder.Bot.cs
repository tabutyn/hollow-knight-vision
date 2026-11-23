using System.IO;
using System.Net.Sockets;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed partial class WindowsGameControlForwarder
{
    private Guid? botOwner;
    private Guid? queuedBotOwner;
    private bool botStateDirty;
    private double botDeadline;
    private InputButtons botRequested;
    private InputButtons botApplied;
    // A queued human snapshot from before an ownership change cannot re-enable input.
    private long inputEpoch;

    internal Guid? BeginBotControl()
    {
        lock (stateLock)
        {
            if (!CanDriveBotLocked || botOwner.HasValue || heldButtons != InputButtons.None) return null;
            botOwner = Guid.NewGuid();
            inputEpoch++;
            isEnabled = false;
            awaitingFreshPress = true;
            QueueBotStateLocked(botOwner.Value);
            return botOwner;
        }
    }

    internal bool OwnsBotControl(Guid owner)
    {
        lock (stateLock) return botOwner == owner && CanDriveBotLocked;
    }

    internal bool ApplyBotPlayback(Guid owner, BotPlaybackUpdate? update)
    {
        if (update is null) return true;
        if (update.Decision is { } decision) return RenewBotControl(owner, decision.Action.Buttons, decision.Deadline);
        NeutralizeBotControl(owner);
        return true;
    }

    internal bool RenewBotControl(Guid owner, InputButtons buttons, double deadline)
    {
        lock (stateLock)
        {
            var remaining = deadline - MonotonicSeconds();
            if (botOwner != owner || !CanDriveBotLocked || !(remaining > 0 && remaining <= .35)) return false;
            botRequested = buttons;
            botDeadline = deadline;
            QueueBotStateLocked(owner);
            return true;
        }
    }

    // Retain ownership through an observation gap, while releasing all buttons.
    internal void NeutralizeBotControl(Guid owner)
    {
        lock (stateLock)
        {
            if (botOwner != owner || botRequested == InputButtons.None && botApplied == InputButtons.None) return;
            botRequested = InputButtons.None;
            botDeadline = 0;
            QueueBotStateLocked(owner);
        }
    }

    internal void EndBotControl(Guid owner)
    {
        lock (stateLock)
        {
            if (botOwner != owner) return;
            DropBotControlLocked();
            if (receiver is { } active && connectionFault is { } fault)
                QueueState(active, fault, false, InputButtons.None, inputEpoch);
        }
    }

    private bool CanDriveBotLocked => receiver is not null && connectionFault is not null
        && !lifetime.IsCancellationRequested && replayCancellation is null && !capturingCheckpoint
        && !roomResetPending && !inputPathRecorder.IsRecording && !pause.BlocksInput;

    private void DropBotControlLocked()
    {
        if (!botOwner.HasValue) return;
        botOwner = null;
        botDeadline = 0;
        botRequested = botApplied = InputButtons.None;
        botStateDirty = false;
        inputEpoch++;
    }

    private void QueueBotStateLocked(Guid owner)
    {
        botStateDirty = true;
        if (queuedBotOwner == owner || receiver is not { } active || connectionFault is not { } fault) return;
        queuedBotOwner = owner;
        _ = SendBotStateOrderedAsync(active, fault, owner, lifetime.Token);
    }

    private async Task SendBotStateOrderedAsync(ReceiverClient active, TaskCompletionSource fault, Guid owner, CancellationToken token)
    {
        try
        {
            await outboundGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Task send;
                InputButtons buttons;
                lock (stateLock)
                {
                    if (botOwner != owner || !ReferenceEquals(active, receiver) || !CanDriveBotLocked) return;
                    botStateDirty = false;
                    buttons = MonotonicSeconds() < botDeadline ? botRequested : InputButtons.None;
                    send = active.SendStateAsync(buttons != InputButtons.None, buttons, token,
                        snapshot => inputAcknowledgements.RecordSent(snapshot, MonotonicSeconds()));
                }
                await send.ConfigureAwait(false);
                lock (stateLock)
                    if (botOwner == owner && ReferenceEquals(active, receiver)) botApplied = buttons;
            }
            finally { outboundGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            fault.TrySetResult();
        }
        finally
        {
            lock (stateLock)
            {
                if (queuedBotOwner == owner)
                {
                    queuedBotOwner = null;
                    if (botOwner == owner && botStateDirty && CanDriveBotLocked && !fault.Task.IsCompleted)
                        QueueBotStateLocked(owner);
                }
            }
        }
    }
}
