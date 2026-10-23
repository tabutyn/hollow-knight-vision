using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>
/// Mirrors the Mac app's focused-window input bridge. The game receives
/// receiver-protocol state; this class never synthesizes Windows input.
/// </summary>
internal sealed partial class WindowsGameControlForwarder : IAsyncDisposable
{
    private sealed record ReceivedGroundTruth(
        ReceiverGroundTruthSample Value,
        double ReceivedAt);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(500);
    private readonly object stateLock = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim outboundGate = new(1, 1);
    private readonly InputAcknowledgementTracker inputAcknowledgements = new();
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
    private readonly InputPathRecorder inputPathRecorder = new();

    internal event Action<ReceiverGroundTruthSample, double>? GroundTruthReceived;

    internal bool MenuShortcutsAvailable
    {
        get { lock (stateLock) return menuShortcutsAvailable; }
    }

    internal bool PlayerOpsAvailable
    {
        get { lock (stateLock) return playerOpsAvailable; }
    }

    internal double? InputAcknowledgementP95Seconds
    {
        get { lock (stateLock) return inputAcknowledgements.P95Latency; }
    }

    internal bool HasFreshGroundTruth
    {
        get
        {
            var latest = Volatile.Read(ref latestGroundTruth);
            return latest is not null && IsFresh(
                latest.ReceivedAt,
                TimeSpan.FromMilliseconds(500));
        }
    }

    internal bool IsRecording
    {
        get => inputPathRecorder.IsRecording;
    }

    internal bool StartRecording()
    {
        lock (stateLock)
        {
            if (receiver is null || inputPathRecorder.IsRecording || pause.BlocksInput) return false;
            var checkpoint = Volatile.Read(ref latestGroundTruth) is { } truth
                ? ToCheckpoint(truth.Value)
                : null;
            return inputPathRecorder.Start(
                timestamp: MonotonicSeconds(),
                createdAt: DateTimeOffset.UtcNow,
                startCheckpoint: checkpoint);
        }
    }

    internal RecordedInputPath? StopRecording()
    {
        lock (stateLock)
        {
            var result = inputPathRecorder.Stop(MonotonicSeconds());
            if (result is null) return null;
            result.Validate();
            return result;
        }
    }

    internal void RecordCoarseMotion(
        double captureTimestamp,
        int? renderedGameFrame,
        AtlasPoint presentedCameraPosition,
        AtlasPoint? coarseCameraPosition,
        LowResolutionRoomMotionEstimate? estimate,
        LowResolutionMotionGrid? motionGrid,
        bool isControlling,
        bool isTransitioning,
        double signalMeanPeak,
        double signalVisibleFraction)
    {
        lock (stateLock)
        {
            inputPathRecorder.RecordCoarseMotion(
                captureTimestamp,
                renderedGameFrame,
                presentedCameraPosition,
                coarseCameraPosition,
                estimate,
                isControlling,
                isTransitioning,
                roomId: 0,
                roomRevision: 0,
                signalMeanPeak,
                signalVisibleFraction,
                motionGrid: motionGrid,
                observedAt: MonotonicSeconds());
        }
    }

    internal void RecordVisualTracking(
        double captureTimestamp,
        InputPathTrackingObservation observation,
        AtlasPoint publishedCameraPosition,
        string poseSource,
        double? motionBridgeConfidence,
        string? motionBridgeSource,
        double signalMeanPeak,
        double signalVisibleFraction,
        int? renderedGameFrame,
        bool atlasWriteAllowed)
    {
        lock (stateLock)
        {
            inputPathRecorder.RecordTracking(
                observation,
                captureTimestamp,
                publishedCameraPosition: publishedCameraPosition,
                poseSource: poseSource,
                motionBridgeConfidence: motionBridgeConfidence,
                motionBridgeSource: motionBridgeSource,
                signalMeanPeak: signalMeanPeak,
                signalVisibleFraction: signalVisibleFraction,
                renderedGameFrame: renderedGameFrame,
                observedAt: MonotonicSeconds());
            inputPathRecorder.RecordAtlasAdmission(captureTimestamp, atlasWriteAllowed);
        }
    }

    internal bool TryGetFreshGroundTruth(
        TimeSpan maximumAge,
        out ReceiverGroundTruthSample sample)
    {
        var latest = Volatile.Read(ref latestGroundTruth);
        if (latest is not null && IsFresh(latest.ReceivedAt, maximumAge))
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
            if (active is null || fault is null || pause.BlocksInput) return;
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
            if (active is null || fault is null || awaitingFreshPress || !isEnabled || pause.BlocksInput) return;
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
            if (active is null || fault is null || awaitingFreshPress || !isEnabled || pause.BlocksInput) return;
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
            if (active is null || fault is null || !pointerAvailable || pause.BlocksInput && kind != "exited") return false;
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
        ReceiverPauseCommand? resume;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            heldButtons = InputButtons.None;
            isEnabled = false;
            awaitingFreshPress = true;
            loop = connectionLoop;
            resume = active is not null && pause.BlocksInput
                ? pause.Request(false) ?? ReceiverPauseCommand.Resume(active.SessionId) : null;
        }
        if (active is not null && fault is not null)
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            if (resume is not null)
                await SendPauseOrderedAsync(active, fault, resume, shutdown.Token).ConfigureAwait(false);
            await SendStateOrderedAsync(
                active, fault, false, InputButtons.None, shutdown.Token)
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
            using var connected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? renewal = null;
            Task? receive = null;
            try
            {
                pending = createReceiver();
                await pending.ConnectAsync(
                    renderFrameMarker: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var capabilities = await NegotiateAsync(pending, cancellationToken).ConfigureAwait(false);
                inputAcknowledgements.BeginSession(pending.SessionId);
                await pending.SendStateAsync(
                        false,
                        InputButtons.None,
                        cancellationToken,
                        snapshot => inputAcknowledgements.RecordSent(
                            snapshot,
                            MonotonicSeconds()))
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
                    pause.Negotiate(pending.SessionId, capabilities);
                }

                renewal = RenewPauseLeaseAsync(pending, fault, connected.Token);
                receive = ReceiveUntilClosedAsync(pending, connected.Token);
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
                or IOException or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException)
            {
                try { await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                connected.Cancel();
                if (renewal is not null) await renewal.ConfigureAwait(false);
                if (receive is not null)
                {
                    // The loop has already handled this result; observe the
                    // canceled/faulted reader before disposing its connection.
                    try { await receive.ConfigureAwait(false); }
                    catch (Exception error) when (error is OperationCanceledException or IOException or SocketException
                        or ObjectDisposedException or System.Text.Json.JsonException) { }
                }
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
                            pause.Disconnect();
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
            if (acknowledgement.Version != ReceiverProtocol.ControlVersion || acknowledgement.SessionId != client.SessionId
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
            if (type is "pauseAck" or "resumeAck")
            {
                var acknowledgement = ReceiverWireCodec.DecodeLine<ReceiverPauseAcknowledgement>(line);
                lock (stateLock)
                {
                    if (ReferenceEquals(receiver, client) && connectionFault is { } fault
                        && pause.Consume(acknowledgement) is { } response)
                        _ = SendPauseOrderedAsync(client, fault, response, cancellationToken);
                }
                continue;
            }
            if (type == "ack")
            {
                var acknowledgement = ReceiverWireCodec
                    .DecodeLine<ReceiverInputAcknowledgement>(line);
                InputAcknowledgementTracker.Result result;
                TaskCompletionSource? fault;
                lock (stateLock)
                {
                    if (!ReferenceEquals(receiver, client)) continue;
                    result = inputAcknowledgements.Receive(
                        acknowledgement,
                        MonotonicSeconds());
                    fault = connectionFault;
                }
                if (result.Kind == InputAcknowledgementTracker.ResultKind.RejectedStateMismatch)
                {
                    fault?.TrySetException(new InvalidDataException(
                        "Receiver acknowledged an input state different from the state sent."));
                }
                continue;
            }
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
            if (!sample.HasFiniteCoordinates()) continue;
            var observedAt = MonotonicSeconds();
            inputPathRecorder.RecordGroundTruth(sample, observedAt);
            if (sample.CameraAvailable)
            {
                lock (stateLock)
                {
                    if (!ReferenceEquals(receiver, client)) continue;
                    var received = new ReceivedGroundTruth(sample, observedAt);
                    Volatile.Write(ref latestGroundTruth, received);
                }
                PublishGroundTruth(sample, observedAt);
            }
        }
        throw new IOException("Receiver closed the connection.");
    }

    private void RecordTransition(InputButtons button, RecordedInputTransition transition)
    {
        if (button.ToRecordedGameButton() is not { } recorded) return;
        inputPathRecorder.RecordInput(
            recorded,
            transition == RecordedInputTransition.Pressed,
            MonotonicSeconds());
    }

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
                // A queued pre-label key state must not re-enable controls
                // after the neutral state and pause command were requested.
                lock (stateLock) if (pause.BlocksInput) { enabled = false; buttons = InputButtons.None; }
                await active.SendStateAsync(
                        enabled,
                        buttons,
                        cancellationToken,
                        snapshot =>
                        {
                            lock (stateLock)
                            {
                                if (ReferenceEquals(receiver, active))
                                {
                                    inputAcknowledgements.RecordSent(
                                        snapshot,
                                        MonotonicSeconds());
                                }
                            }
                        })
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

    private static double MonotonicSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static bool IsFresh(double observedAt, TimeSpan maximumAge)
    {
        if (!double.IsFinite(observedAt) || maximumAge < TimeSpan.Zero) return false;
        var age = MonotonicSeconds() - observedAt;
        return age >= 0 && age <= maximumAge.TotalSeconds;
    }

    private void PublishGroundTruth(
        ReceiverGroundTruthSample sample,
        double observedAt)
    {
        var handlers = GroundTruthReceived;
        if (handlers is null) return;
        foreach (Action<ReceiverGroundTruthSample, double> handler
                 in handlers.GetInvocationList())
        {
            try { handler(sample, observedAt); }
            catch
            {
                // A presentation consumer must never tear down the receiver's
                // input/control connection.
            }
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
                lock (stateLock) if (pause.BlocksInput && kind != "exited") return;
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
