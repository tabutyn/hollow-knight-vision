using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>
/// Mirrors the Mac app's focused-window input bridge. The game receives
/// receiver-protocol state; this class never synthesizes Windows input.
/// </summary>
internal sealed class WindowsGameControlForwarder : IAsyncDisposable
{
    private sealed record ReceivedGroundTruth(
        ReceiverGroundTruthSample Value,
        DateTimeOffset ReceivedAt);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(500);
    private readonly object stateLock = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim outboundGate = new(1, 1);
    private Task? connectionLoop;
    private ReceiverClient? receiver;
    private TaskCompletionSource? connectionFault;
    private InputButtons heldButtons;
    private bool isEnabled;
    private bool awaitingFreshPress = true;
    private bool menuShortcutsAvailable;
    private bool pointerAvailable;
    private bool playerOpsAvailable;
    private ReceivedGroundTruth? latestGroundTruth;
    private readonly Dictionary<Guid, TaskCompletionSource<ReceiverPlayerOpsAcknowledgement>>
        pendingPlayerOps = [];
    private long recordingStartedTimestamp;
    private DateTimeOffset recordingCreatedAt;
    private RecordedGameCheckpoint? recordingCheckpoint;
    private List<RecordedInputPathEvent>? recordingEvents;

    internal bool MenuShortcutsAvailable
    {
        get { lock (stateLock) return menuShortcutsAvailable; }
    }

    internal bool PlayerOpsAvailable
    {
        get { lock (stateLock) return playerOpsAvailable; }
    }

    internal bool IsRecording
    {
        get { lock (stateLock) return recordingEvents is not null; }
    }

    internal bool StartRecording()
    {
        lock (stateLock)
        {
            if (receiver is null || recordingEvents is not null) return false;
            recordingCreatedAt = DateTimeOffset.UtcNow;
            recordingStartedTimestamp = Stopwatch.GetTimestamp();
            recordingEvents = [];
            recordingCheckpoint = latestGroundTruth is { } truth
                ? ToCheckpoint(truth.Value)
                : null;
            return true;
        }
    }

    internal RecordedInputPath? StopRecording()
    {
        lock (stateLock)
        {
            if (recordingEvents is null) return null;
            var duration = RecordingOffset();
            var recordedHeld = InputButtons.None;
            foreach (var item in recordingEvents)
            {
                var flag = item.Button.ToInputButton();
                recordedHeld = item.Transition == RecordedInputTransition.Pressed
                    ? recordedHeld | flag : recordedHeld & ~flag;
            }
            foreach (var flag in SingleButtons(recordedHeld))
            {
                recordingEvents.Add(new RecordedInputPathEvent(
                    duration, ToRecordedButton(flag), RecordedInputTransition.Released));
            }
            var result = new RecordedInputPath(
                RecordedInputPath.CurrentSchemaVersion,
                Guid.NewGuid(),
                recordingCreatedAt,
                duration,
                null,
                null,
                recordingCheckpoint,
                recordingEvents.ToArray(),
                new Dictionary<string, string>
                {
                    ["platform"] = "windows",
                    ["recorder"] = "HollowKnightVision"
                });
            recordingEvents = null;
            recordingCheckpoint = null;
            result.Validate();
            return result;
        }
    }

    internal bool TryGetFreshGroundTruth(
        TimeSpan maximumAge,
        out ReceiverGroundTruthSample sample)
    {
        var latest = Volatile.Read(ref latestGroundTruth);
        if (latest is not null
            && DateTimeOffset.UtcNow - latest.ReceivedAt <= maximumAge)
        {
            sample = latest.Value;
            return true;
        }

        sample = null!;
        return false;
    }

    internal void Start()
    {
        lock (stateLock)
        {
            connectionLoop ??= Task.Run(() => RunConnectionLoopAsync(lifetime.Token));
        }
    }

    internal void Press(InputButtons button)
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        InputButtons snapshot;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            if (active is null || fault is null) return;
            if (awaitingFreshPress)
            {
                awaitingFreshPress = false;
                isEnabled = true;
                heldButtons = button;
            }
            else
            {
                snapshot = heldButtons | button;
                if (snapshot == heldButtons) return;
                heldButtons = snapshot;
            }
            snapshot = heldButtons;
            RecordTransition(button, RecordedInputTransition.Pressed);
        }
        QueueState(active, fault, true, snapshot);
    }

    internal void Release(InputButtons button)
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        InputButtons snapshot;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            if (active is null || fault is null || awaitingFreshPress || !isEnabled) return;
            snapshot = heldButtons & ~button;
            if (snapshot == heldButtons) return;
            heldButtons = snapshot;
            RecordTransition(button, RecordedInputTransition.Released);
        }
        QueueState(active, fault, true, snapshot);
    }

    internal void Heartbeat()
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        InputButtons snapshot;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            if (active is null || fault is null || awaitingFreshPress || !isEnabled) return;
            snapshot = heldButtons;
        }
        QueueState(active, fault, true, snapshot);
    }

    internal void ReleaseAll()
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            heldButtons = InputButtons.None;
            isEnabled = false;
            awaitingFreshPress = true;
        }
        if (active is not null && fault is not null)
        {
            QueueState(active, fault, false, InputButtons.None);
        }
    }

    internal bool ForwardPointer(
        string kind,
        double normalizedX,
        double normalizedY,
        int clickCount = 1)
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            if (active is null || fault is null || !pointerAvailable) return false;
        }
        _ = SendPointerOrderedAsync(
            active, fault, kind, normalizedX, normalizedY, clickCount, lifetime.Token);
        return true;
    }

    internal async Task<ReceiverPlayerOpsAcknowledgement?> PerformPlayerOpsAsync(
        Func<Guid, ReceiverPlayerOpsCommand> createCommand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createCommand);
        ReceiverClient active;
        TaskCompletionSource? fault;
        ReceiverPlayerOpsCommand command;
        TaskCompletionSource<ReceiverPlayerOpsAcknowledgement> pending;
        lock (stateLock)
        {
            if (receiver is null || connectionFault is null || !playerOpsAvailable) return null;
            active = receiver;
            fault = connectionFault;
            command = createCommand(active.SessionId);
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingPlayerOps[command.CommandId] = pending;
        }
        try
        {
            await outboundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await active.SendPlayerOpsCommandAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                outboundGate.Release();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await pending.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            fault.TrySetResult();
            return null;
        }
        finally
        {
            lock (stateLock) pendingPlayerOps.Remove(command.CommandId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        Task? loop;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            heldButtons = InputButtons.None;
            isEnabled = false;
            awaitingFreshPress = true;
            loop = connectionLoop;
        }
        if (active is not null && fault is not null)
        {
            await SendStateOrderedAsync(
                active, fault, false, InputButtons.None, CancellationToken.None)
                .ConfigureAwait(false);
        }
        lifetime.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        outboundGate.Dispose();
        lifetime.Dispose();
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReceiverClient? pending = null;
            try
            {
                pending = new ReceiverClient();
                await pending.ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var capabilities = await NegotiateAsync(pending, cancellationToken).ConfigureAwait(false);
                await pending.SendStateAsync(false, InputButtons.None, cancellationToken)
                    .ConfigureAwait(false);
                var fault = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (stateLock)
                {
                    receiver = pending;
                    connectionFault = fault;
                    heldButtons = InputButtons.None;
                    isEnabled = false;
                    awaitingFreshPress = true;
                    menuShortcutsAvailable = capabilities.Capabilities.Contains(
                        ReceiverProtocol.MenuShortcutsCapability);
                    pointerAvailable = capabilities.Capabilities.Contains(
                        ReceiverProtocol.PointerCapability);
                    playerOpsAvailable = capabilities.Capabilities.Contains(
                        ReceiverProtocol.PlayerOpsCapability);
                }

                var receive = ReceiveUntilClosedAsync(pending, cancellationToken);
                var completed = await Task.WhenAny(receive, fault.Task).ConfigureAwait(false);
                if (completed == fault.Task)
                {
                    throw new IOException("Receiver send failed.");
                }
                await receive.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is SocketException
                or IOException or InvalidDataException or ObjectDisposedException)
            {
                try { await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (pending is not null)
                {
                    lock (stateLock)
                    {
                        if (ReferenceEquals(receiver, pending))
                        {
                            receiver = null;
                            connectionFault = null;
                            heldButtons = InputButtons.None;
                            isEnabled = false;
                            awaitingFreshPress = true;
                            menuShortcutsAvailable = false;
                            pointerAvailable = false;
                            playerOpsAvailable = false;
                            foreach (var pendingCommand in pendingPlayerOps.Values)
                            {
                                pendingCommand.TrySetCanceled();
                            }
                            pendingPlayerOps.Clear();
                            Volatile.Write(ref latestGroundTruth, null);
                        }
                    }
                    await pending.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<ReceiverCapabilitiesAcknowledgement> NegotiateAsync(
        ReceiverClient client,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await client.ReceiveLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Receiver closed during negotiation.");
            if (ReceiverWireCodec.MessageType(line) != "capabilitiesAck") continue;
            var acknowledgement = ReceiverWireCodec
                .DecodeLine<ReceiverCapabilitiesAcknowledgement>(line);
            if (acknowledgement.SessionId != client.SessionId
                || !acknowledgement.Capabilities.Contains("input-state-v1"))
            {
                throw new InvalidDataException("Receiver capabilities are incompatible.");
            }
            return acknowledgement;
        }
    }

    private async Task ReceiveUntilClosedAsync(
        ReceiverClient client,
        CancellationToken cancellationToken)
    {
        while (await client.ReceiveLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var type = ReceiverWireCodec.MessageType(line);
            if (type == "playerOpsAck")
            {
                var acknowledgement = ReceiverWireCodec
                    .DecodeLine<ReceiverPlayerOpsAcknowledgement>(line);
                TaskCompletionSource<ReceiverPlayerOpsAcknowledgement>? pending;
                lock (stateLock)
                {
                    pendingPlayerOps.TryGetValue(acknowledgement.CommandId, out pending);
                }
                pending?.TrySetResult(acknowledgement);
                continue;
            }
            if (type != "groundTruth") continue;
            var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
            if (sample.CameraAvailable && sample.HasFiniteCoordinates())
            {
                Volatile.Write(
                    ref latestGroundTruth,
                    new ReceivedGroundTruth(sample, DateTimeOffset.UtcNow));
            }
        }
        throw new IOException("Receiver closed the connection.");
    }

    private double RecordingOffset() =>
        Math.Max(0, Stopwatch.GetElapsedTime(recordingStartedTimestamp).TotalSeconds);

    private void RecordTransition(InputButtons button, RecordedInputTransition transition)
    {
        if (recordingEvents is null) return;
        recordingEvents.Add(new RecordedInputPathEvent(
            RecordingOffset(), ToRecordedButton(button), transition));
    }

    private static IEnumerable<InputButtons> SingleButtons(InputButtons buttons)
    {
        foreach (var button in new[]
        {
            InputButtons.Left, InputButtons.Right, InputButtons.Down, InputButtons.Up,
            InputButtons.ActionA, InputButtons.ActionZ, InputButtons.ActionX,
            InputButtons.Inventory, InputButtons.PauseMenu
        })
        {
            if ((buttons & button) != 0) yield return button;
        }
    }

    private static RecordedGameButton ToRecordedButton(InputButtons button) => button switch
    {
        InputButtons.Left => RecordedGameButton.Left,
        InputButtons.Right => RecordedGameButton.Right,
        InputButtons.Down => RecordedGameButton.Down,
        InputButtons.Up => RecordedGameButton.Up,
        InputButtons.ActionA => RecordedGameButton.A,
        InputButtons.ActionZ => RecordedGameButton.Z,
        InputButtons.ActionX => RecordedGameButton.X,
        InputButtons.Inventory => RecordedGameButton.Inventory,
        InputButtons.PauseMenu => RecordedGameButton.Pause,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private static RecordedGameCheckpoint ToCheckpoint(ReceiverGroundTruthSample sample) => new(
        sample.SceneName,
        null,
        sample.HeroX,
        sample.HeroY,
        sample.HeroZ,
        sample.VelocityX,
        sample.VelocityY,
        sample.FacingRight,
        sample.Grounded,
        sample.CameraX,
        sample.CameraY,
        sample.CameraZ,
        sample.CameraTargetX,
        sample.CameraTargetY,
        sample.CameraTargetZ,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    private void QueueState(
        ReceiverClient active,
        TaskCompletionSource fault,
        bool enabled,
        InputButtons buttons) =>
        _ = SendStateOrderedAsync(active, fault, enabled, buttons, lifetime.Token);

    private async Task SendStateOrderedAsync(
        ReceiverClient active,
        TaskCompletionSource fault,
        bool enabled,
        InputButtons buttons,
        CancellationToken cancellationToken)
    {
        try
        {
            await outboundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await active.SendStateAsync(enabled, buttons, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                outboundGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException
            or SocketException or ObjectDisposedException)
        {
            fault.TrySetResult();
        }
    }

    private async Task SendPointerOrderedAsync(
        ReceiverClient active,
        TaskCompletionSource fault,
        string kind,
        double normalizedX,
        double normalizedY,
        int clickCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await outboundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await active.SendPointerAsync(
                    kind, normalizedX, normalizedY, clickCount, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                outboundGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException
            or SocketException or ObjectDisposedException)
        {
            fault.TrySetResult();
        }
    }
}
