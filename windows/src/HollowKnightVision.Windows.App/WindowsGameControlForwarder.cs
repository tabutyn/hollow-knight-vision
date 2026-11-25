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
    private bool skillControlsAvailable;
    private bool pointerAvailable;
    private bool playerOpsAvailable;
    private bool roomResetAvailable;
    private bool roomResetPending;
    private ReceivedGroundTruth? latestGroundTruth;
    private readonly Dictionary<Guid, TaskCompletionSource<ReceiverPlayerOpsAcknowledgement>>
        pendingPlayerOps = [];
    private readonly InputPathRecorder inputPathRecorder;

    internal event Action<ReceiverGroundTruthSample, double>? GroundTruthReceived;

    internal InputButtons? CaptureMovement()
    {
        lock (stateLock)
        {
            if (receiver is null || replayCancellation is not null || pause.BlocksInput || roomResetPending)
                return null;
            return botOwner.HasValue ? (MonotonicSeconds() < botDeadline ? botApplied : InputButtons.None) : heldButtons;
        }
    }

    internal bool MenuShortcutsAvailable
    {
        get { lock (stateLock) return menuShortcutsAvailable; }
    }

    internal bool SkillControlsAvailable
    {
        get { lock (stateLock) return skillControlsAvailable; }
    }

    internal bool PlayerOpsAvailable
    {
        get { lock (stateLock) return playerOpsAvailable; }
    }

    internal bool RoomResetAvailable
    {
        get { lock (stateLock) return CanResetRoomLocked(); }
    }

    private bool CanResetRoomLocked() => receiver is not null && connectionFault is not null
        && playerOpsAvailable && roomResetAvailable && !lifetime.IsCancellationRequested
        && !roomResetPending && pendingPlayerOps.Count == 0
        && replayCancellation is null && !capturingCheckpoint
        && !pause.BlocksInput && !inputPathRecorder.IsRecording && !botOwner.HasValue;

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

    internal (Guid Id, double StartedAt)? Recording => inputPathRecorder.Recording;

    internal async Task<bool> StartRecordingAsync(CancellationToken cancellationToken = default, string? visualArchiveRoot = null)
    {
        ReceiverClient active;
        lock (stateLock)
        {
            if (receiver is null || !checkpointAvailable || replayCancellation is not null || capturingCheckpoint
                || roomResetPending || inputPathRecorder.IsRecording || pause.BlocksInput || botOwner.HasValue
                || heldButtons != InputButtons.None) return false;
            active = receiver;
            capturingCheckpoint = true;
        }
        try
        {
            var checkpoint = await CheckpointAsync(active, ReceiverCheckpointCommand.Capture(active.SessionId), cancellationToken).ConfigureAwait(false);
            lock (stateLock)
            {
                if (!ReferenceEquals(active, receiver) || lifetime.IsCancellationRequested) return false;
                return inputPathRecorder.Start(MonotonicSeconds(), DateTimeOffset.UtcNow, startCheckpoint: checkpoint,
                    metadata: new Dictionary<string, string>
                    {
                        ["checkpointCapture"] = "receiver-acknowledged-v1", ["playbackMode"] = "recording",
                        [RecordedInputPath.SkillControlsMetadata] = skillControlsAvailable && menuShortcutsAvailable ? "true" : "false"
                    },
                    visualArchiveRoot: visualArchiveRoot);
            }
        }
        finally { lock (stateLock) capturingCheckpoint = false; }
    }

    internal async Task<RecordedInputPath?> StopRecordingAsync(bool cancelled = false)
    {
        Task<RecordedInputPath?> pending;
        lock (stateLock)
            pending = inputPathRecorder.StopAsync(MonotonicSeconds(), cancelled);
        var result = await pending.ConfigureAwait(false);
        result?.Validate();
        return result;
    }

    internal void RecordVisualFrame(BgraFrame frame, double timestamp, double capturedAt, ulong generation, int? marker) =>
        inputPathRecorder.RecordVisualFrame(frame, timestamp, capturedAt, generation, marker);

    internal void RecordVisualStage(string stage, double timestamp, ulong generation) =>
        inputPathRecorder.RecordVisualStage(stage, timestamp, generation);

    internal void RecordVisualGroundInput(double timestamp, ulong generation, int imageHeight,
        GameplaySceneMasks masks, InputButtons? buttons) =>
        inputPathRecorder.RecordVisualGroundInput(timestamp, generation, imageHeight, masks, buttons);

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
        double signalVisibleFraction, int roomId = 0, ulong roomRevision = 0)
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
                roomId: roomId,
                roomRevision: roomRevision,
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
        bool atlasWriteAllowed, double groundMilliseconds, int roomId, ulong roomRevision,
        string recoveryState, FloorlessCameraEvidence? floorless, double? sceneMotionMilliseconds,
        double? recoveredAtlasReferenceTimestamp)
    {
        lock (stateLock)
        {
            inputPathRecorder.RecordTracking(
                observation,
                captureTimestamp,
                groundTrackingMilliseconds: groundMilliseconds,
                roomId: roomId, roomRevision: roomRevision, placementRecoveryState: recoveryState,
                floorlessCoarsePosition: floorless?.CoarsePosition,
                floorlessMaskedPosition: floorless?.MaskedPosition, floorlessExpectedDirection: floorless?.ExpectedDirection,
                floorlessSelectionSource: floorless?.SelectionName,
                sceneMotion: floorless?.Scene, sceneMotionMilliseconds: sceneMotionMilliseconds,
                recoveredAtlasReferenceTimestamp: recoveredAtlasReferenceTimestamp,
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

    internal void RecordLoopClosure(
        double captureTimestamp,
        int worldRevision,
        AtlasPoint correction,
        LiveWorldLoopClosureEdge closure)
    {
        lock (stateLock)
        {
            inputPathRecorder.RecordLoopClosure(
                captureTimestamp,
                worldRevision,
                new CameraVector(correction.X, correction.Y),
                closure,
                MonotonicSeconds());
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
        long epoch;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            if (active is null || fault is null || (replayCancellation is not null || capturingCheckpoint) || roomResetPending
                || pause.BlocksInput || !skillControlsAvailable && (button & InputButtons.SkillAbilities) != 0) return;
            DropBotControlLocked();
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
            epoch = inputEpoch;
        }
        QueueState(active, fault, true, snapshot, epoch);
    }

    internal void Release(InputButtons button)
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        InputButtons snapshot;
        long epoch;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            if (active is null || fault is null || (replayCancellation is not null || capturingCheckpoint) || awaitingFreshPress || !isEnabled || roomResetPending || pause.BlocksInput) return;
            snapshot = heldButtons & ~button;
            if (snapshot == heldButtons) return;
            heldButtons = snapshot;
            RecordTransition(button, RecordedInputTransition.Released);
            epoch = inputEpoch;
        }
        QueueState(active, fault, true, snapshot, epoch);
    }

    internal void Heartbeat()
    {
        ReceiverClient? active;
        TaskCompletionSource? fault;
        InputButtons snapshot;
        long epoch;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            if (botOwner is { } owner)
            {
                if (CanDriveBotLocked) QueueBotStateLocked(owner);
                return;
            }
            if (active is null || fault is null || replayCancellation is not null || capturingCheckpoint
                || awaitingFreshPress || !isEnabled || roomResetPending || pause.BlocksInput) return;
            snapshot = heldButtons;
            epoch = inputEpoch;
        }
        QueueState(active, fault, true, snapshot, epoch);
    }

    internal void ReleaseAll()
    {
        CancelReplay();
        lock (stateLock)
        {
            DropBotControlLocked();
            heldButtons = InputButtons.None;
            isEnabled = false;
            awaitingFreshPress = true;
            if (receiver is { } active && connectionFault is { } fault)
                QueueState(active, fault, false, InputButtons.None, inputEpoch);
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
            if (active is null || fault is null || !pointerAvailable || replayCancellation is not null || capturingCheckpoint
                || pause.BlocksInput && kind != "exited") return false;
        }
        _ = SendPointerOrderedAsync(
            active, fault, kind, normalizedX, normalizedY, clickCount, lifetime.Token);
        return true;
    }

    internal async Task<ReceiverPlayerOpsAcknowledgement?> PerformPlayerOpsAsync(
        Func<Guid, ReceiverPlayerOpsCommand> createCommand,
        CancellationToken cancellationToken = default,
        Guid? requiredSession = null)
    {
        ArgumentNullException.ThrowIfNull(createCommand);
        ReceiverClient active;
        TaskCompletionSource? fault;
        ReceiverPlayerOpsCommand command;
        bool resetsRoom;
        TaskCompletionSource<ReceiverPlayerOpsAcknowledgement> pending;
        lock (stateLock)
        {
            if (receiver is null || connectionFault is null || !playerOpsAvailable
                || replayCancellation is not null || capturingCheckpoint || roomResetPending
                || requiredSession is { } expected && receiver.SessionId != expected) return null;
            active = receiver;
            fault = connectionFault;
            command = createCommand(active.SessionId);
            resetsRoom = command.Operation == ReceiverPlayerOpsCommand.ResetRoomOperation;
            if (resetsRoom && !CanResetRoomLocked()) return null;
            roomResetPending = resetsRoom;
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
            timeout.CancelAfter(TimeSpan.FromSeconds(resetsRoom ? 15 : 3));
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
            lock (stateLock)
            {
                pendingPlayerOps.Remove(command.CommandId);
                if (resetsRoom) roomResetPending = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? replayDrain;
        lock (stateLock) { replayCancellation?.Cancel(); replayDrain = replayFinished?.Task; }
        if (replayDrain is not null) await replayDrain.ConfigureAwait(false);
        ReceiverClient? active;
        TaskCompletionSource? fault;
        Task? loop;
        ReceiverPauseCommand? resume;
        lock (stateLock)
        {
            active = receiver;
            fault = connectionFault;
            DropBotControlLocked();
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
        try { await StopRecordingAsync(cancelled: true).ConfigureAwait(false); }
        finally
        {
            outboundGate.Dispose();
            lifetime.Dispose();
        }
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
                    skillControlsAvailable = capabilities.Capabilities.Contains(ReceiverProtocol.SkillControlsCapability);
                    pointerAvailable = capabilities.Capabilities.Contains(
                        ReceiverProtocol.PointerCapability);
                    checkpointAvailable = capabilities.Capabilities.Contains(ReceiverProtocol.PlayerCheckpointCapability);
                    posePlaybackAvailable = capabilities.Capabilities.Contains(ReceiverProtocol.PlayerPosePlaybackCapability);
                    controlledFramesAvailable = capabilities.Capabilities.Contains(ControlledFrameReplay.Capability);
                    playerOpsAvailable = capabilities.Capabilities.Contains(
                        ReceiverProtocol.PlayerOpsCapability);
                    roomResetAvailable = capabilities.Capabilities.Contains(
                        ReceiverProtocol.RoomResetCapability);
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
                            DropBotControlLocked();
                            heldButtons = InputButtons.None;
                            isEnabled = false;
                            awaitingFreshPress = true;
                            menuShortcutsAvailable = false;
                            skillControlsAvailable = false;
                            inputPathRecorder.InvalidateSkillControls();
                            pointerAvailable = false;
                            playerOpsAvailable = false;
                            roomResetAvailable = false;
                            checkpointAvailable = false;
                            posePlaybackAvailable = false;
                            replayCancellation?.Cancel();
                            foreach (var command in pendingCheckpoints.Values) command.TrySetCanceled();
                            pendingCheckpoints.Clear();
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
            if (type == "controlledFrameAck")
            {
                var acknowledgement = ReceiverWireCodec.DecodeLine<ReceiverControlledFrameAcknowledgement>(line);
                lock (stateLock)
                    if (ReferenceEquals(receiver, client) && acknowledgement.SessionId == client.SessionId
                        && pendingControlledFrames.TryGetValue(acknowledgement.CommandId, out var pending))
                        pending.TrySetResult(acknowledgement);
                continue;
            }
            if (type is "captureCheckpointAck" or "restoreCheckpointAck")
            {
                var acknowledgement = ReceiverWireCodec.DecodeLine<ReceiverCheckpointAcknowledgement>(line);
                lock (stateLock)
                    if (ReferenceEquals(receiver, client) && acknowledgement.SessionId == client.SessionId
                        && pendingCheckpoints.TryGetValue(acknowledgement.CommandId, out var pending))
                        pending.TrySetResult(acknowledgement);
                continue;
            }
            if (type == "playerOpsAck")
            {
                var acknowledgement = ReceiverWireCodec
                    .DecodeLine<ReceiverPlayerOpsAcknowledgement>(line);
                lock (stateLock)
                {
                    if (ReferenceEquals(receiver, client)
                        && acknowledgement.Version == ReceiverProtocol.ControlVersion
                        && acknowledgement.SessionId == client.SessionId
                        && pendingPlayerOps.TryGetValue(acknowledgement.CommandId, out var pending))
                    {
                        pending.TrySetResult(acknowledgement);
                    }
                }
                continue;
            }
            if (type != "groundTruth") continue;
            var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(line);
            if (!sample.HasFiniteCoordinates()) continue;
            var observedAt = MonotonicSeconds();
            lock (stateLock)
            {
                if (!ReferenceEquals(receiver, client)) continue;
                if (sample.CameraAvailable)
                    Volatile.Write(ref latestGroundTruth, new ReceivedGroundTruth(sample, observedAt));
            }
            inputPathRecorder.RecordGroundTruth(sample, observedAt);
            PublishGroundTruth(sample, observedAt);
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

    private void QueueState(
        ReceiverClient active,
        TaskCompletionSource fault,
        bool enabled,
        InputButtons buttons,
        long epoch) =>
        _ = SendStateOrderedAsync(active, fault, enabled, buttons, lifetime.Token, epoch);

    private async Task SendStateOrderedAsync(
        ReceiverClient active,
        TaskCompletionSource fault,
        bool enabled,
        InputButtons buttons,
        CancellationToken cancellationToken,
        long? expectedEpoch = null)
    {
        try
        {
            await outboundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Task pending;
                lock (stateLock)
                {
                    if (!ReferenceEquals(receiver, active) || expectedEpoch is { } epoch && epoch != inputEpoch) return;
                    if (pause.BlocksInput || roomResetPending)
                    {
                        enabled = false;
                        buttons = InputButtons.None;
                    }
                    pending = active.SendStateAsync(
                        enabled,
                        buttons,
                        cancellationToken,
                        snapshot => inputAcknowledgements.RecordSent(snapshot, MonotonicSeconds()));
                }
                await pending.ConfigureAwait(false);
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
