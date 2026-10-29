using System.IO;
using System.Net.Sockets;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal sealed partial class WindowsGameControlForwarder
{
    // Remains owned between segments. Capture shutdown must not resume a
    // stashed game; only LeaveStepReplayAsync or disconnect releases this lease.
    private bool stepPauseOwned;

    private readonly ReceiverPauseController pause = new();
    private readonly Func<ReceiverClient> createReceiver;
    internal WindowsGameControlForwarder(Func<ReceiverClient>? createReceiver = null) =>
        this.createReceiver = createReceiver ?? (() => new ReceiverClient());
    internal GamePauseState PauseState { get { lock (stateLock) return pause.State; } }

    internal bool SetLabelPauseActive(bool active)
    {
        lock (stateLock)
        {
            if (stepPauseOwned || receiver is null || connectionFault is null || pause.State == GamePauseState.Unavailable
                || active && (inputPathRecorder.IsRecording || replayCancellation is not null || capturingCheckpoint)) return false;
            var command = pause.Request(active);
            if (command is null) return true;
            if (active)
            {
                heldButtons = InputButtons.None; isEnabled = false; awaitingFreshPress = true;
                QueueState(receiver, connectionFault, false, InputButtons.None);
            }
            _ = SendPauseOrderedAsync(receiver, connectionFault, command, lifetime.Token);
            return true;
        }
    }

    private async Task RenewPauseLeaseAsync(ReceiverClient active, TaskCompletionSource fault, CancellationToken token)
    {
        using var timer = new PeriodicTimer(ReceiverPauseController.RenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Task? send = null;
                lock (stateLock)
                    if (ReferenceEquals(receiver, active) && pause.Renew() is { } command)
                        send = SendPauseOrderedAsync(active, fault, command, token);
                if (send is not null) await send.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task SendPauseOrderedAsync(ReceiverClient active, TaskCompletionSource fault,
        ReceiverPauseCommand command, CancellationToken token)
    {
        try
        {
            await outboundGate.WaitAsync(token).ConfigureAwait(false);
            try { await active.SendPauseCommandAsync(command, token).ConfigureAwait(false); }
            finally { outboundGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        { fault.TrySetResult(); }
    }
}
