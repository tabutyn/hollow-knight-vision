using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class InputPathReplayIntegrationTests
{
    internal static void Run() => Task.Run(async () =>
    {
        await Exercise(cancel: false, reject: false);
        await Exercise(cancel: true, reject: false);
        await Exercise(cancel: false, reject: true);
        await Exercise(cancel: false, reject: false, missingPose: true);
        await Exercise(cancel: false, reject: false, step: true);
        await Exercise(cancel: true, reject: false, step: true);
        Console.WriteLine("PASS replay sends actual poses, full checkpoints, ordered preparation, shared telemetry, lease renewal and cancellation/rejection");
    }).GetAwaiter().GetResult();

    private static async Task Exercise(bool cancel, bool reject, bool missingPose = false, bool step = false)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var states = new ConcurrentQueue<ReceiverInputSnapshot>();
        var controlled = new ConcurrentQueue<ReceiverControlledFrameCommand>();
        var restores = 0;
        var captures = 0;
        var poses = new ConcurrentQueue<ReceiverPlayerPoseCommand>();
        var checkpoint = JsonSerializer.Deserialize<RecordedGameCheckpoint>("""{ "sceneName":"Tutorial_01", "entryGateName":"left1", "heroX":35,"heroY":11,"cameraX":36,"cameraY":14,"grounded":true,"enemies":[{"name":"Crawler","x":42,"y":11,"z":0,"hp":5,"active":true}],"isFirstGame":false,"enteredTutorialFirstTime":true,"visitedDirtmouth":false,"visitedCrossroads":true,"openedTown":true,"openedCrossroads":true,"scenesVisited":["Tutorial_01"] }""", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })! with { SceneMasks = Enumerable.Range(0, 12).Select(i => new RecordedSceneMask(
            $"Room[0]/Secret%20Mask%20Group[{i}]", "unmasker", "Fade", true, true,
            new Dictionary<string, bool> { ["Activated"] = true }, new Dictionary<string, double> { ["Timer"] = .25 },
            Enumerable.Range(0, 3).Select(j => new RecordedMaskSprite($"Room[0]/Secret%20Mask%20Group[{i}]/Sprite[{j}]", .25, true, true)).ToArray())).ToArray() };
        Check(ReceiverWireCodec.EncodeLine(checkpoint).Length > 4096, "Large checkpoint fixture no longer exercises transport growth.");
        var creations = 0;
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(timeout.Token);
            using var reader = new StreamReader(client.GetStream());
            using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
            var hello = ReceiverWireCodec.DecodeLine<ReceiverCapabilityRequest>((await reader.ReadLineAsync(timeout.Token))!);
            Check(hello.RenderFrameMarker == true, "Replay lost rendered-frame correlation.");
            await writer.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCapabilitiesAcknowledgement(
                2, "capabilitiesAck", hello.SessionId,
                ["input-state-v1", ReceiverProtocol.PlayerCheckpointCapability, ReceiverProtocol.PointerCapability, missingPose ? "old-receiver" : ReceiverProtocol.PlayerPosePlaybackCapability, ReceiverProtocol.PlayerOpsCapability, ReceiverProtocol.PauseCapability, ControlledFrameReplay.Capability], 2000)));
            long frame = 0;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (ReceiverWireCodec.MessageType(line) is "pause" or "resume")
                {
                    var pausing = ReceiverWireCodec.MessageType(line) == "pause";
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 2,
                        type = pausing ? "pauseAck" : "resumeAck", sessionID = hello.SessionId, paused = pausing }));
                }
                if (ReceiverWireCodec.MessageType(line) == "controlledFrame")
                {
                    var command = ReceiverWireCodec.DecodeLine<ReceiverControlledFrameCommand>(line);
                    controlled.Enqueue(command);
                    if (command.Pose is { } pose) poses.Enqueue(pose);
                    await writer.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverControlledFrameAcknowledgement(
                        2, "controlledFrameAck", hello.SessionId, command.CommandId, true, (int)++frame, command.DeltaSeconds, null)));
                }
                if (ReceiverWireCodec.MessageType(line) == "captureCheckpoint")
                {
                    var command = ReceiverWireCodec.DecodeLine<ReceiverCheckpointCommand>(line);
                    Interlocked.Increment(ref captures);
                    await writer.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCheckpointAcknowledgement(
                        2, "captureCheckpointAck", hello.SessionId, command.CommandId, true, checkpoint, null)));
                }
                if (ReceiverWireCodec.MessageType(line) == "playerPose")
                {
                    var pose = ReceiverWireCodec.DecodeLine<ReceiverPlayerPoseCommand>(line);
                    Check(pose.SessionId == hello.SessionId, "Pose crossed receiver sessions.");
                    poses.Enqueue(pose);
                }
                if (ReceiverWireCodec.MessageType(line) == "restoreCheckpoint")
                {
                    var command = ReceiverWireCodec.DecodeLine<ReceiverCheckpointCommand>(line);
                    Check(command.SessionId == hello.SessionId, "Checkpoint opened another control session.");
                    Interlocked.Increment(ref restores);
                    await writer.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCheckpointAcknowledgement(
                        2, "restoreCheckpointAck", hello.SessionId, command.CommandId, !reject, command.Checkpoint,
                        reject ? "test rejection" : null)));
                }
                if (ReceiverWireCodec.MessageType(line) != "state") continue;
                var state = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(line);
                states.Enqueue(state);
                await writer.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverInputAcknowledgement(
                    1, "ack", hello.SessionId, state.Sequence, state.HeldButtons, state.Enabled)));
                await writer.WriteLineAsync(JsonSerializer.Serialize(new {
                    version = 2, type = "groundTruth", sessionID = hello.SessionId,
                    sequence = ++frame, unityFrame = frame, unityRealtime = frame / 60.0,
                    sceneName = "Tutorial_01", heroAvailable = true, heroX = 35, heroY = 11,
                    cameraAvailable = true, cameraX = 36, cameraY = 14,
                    pixelsPerWorldUnitX = 22, pixelsPerWorldUnitY = 22,
                    screenWidth = 640, screenHeight = 360
                }));
            }
        });
        var bridge = new WindowsGameControlForwarder(() =>
        {
            Interlocked.Increment(ref creations);
            return new ReceiverClient(port: port);
        });
        try
        {
        bridge.Start();
        await Until(() => bridge.PlayerOpsAvailable && bridge.HasFreshGroundTruth, timeout.Token);
        Check(bridge.ForwardPointer("moved", .5, .5), "Pointer test capability was not negotiated.");
        Check(await bridge.StartRecordingAsync(timeout.Token), "Full checkpoint capture did not start recording.");
        await Task.Delay(60, timeout.Token);
        var captured = bridge.StopRecording();
        Check(captures == 1 && captured?.StartCheckpoint?.EntryGateName == "left1"
            && captured.StartCheckpoint.Enemies?.Single().Hp == 5 && captured.StartCheckpoint.ScenesVisited?.Single() == "Tutorial_01"
            && captured.StartCheckpoint.EnteredTutorialFirstTime == true && captured.StartCheckpoint.SceneMasks?.Count == 12, "Receiver checkpoint fields were discarded.");
        var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>("""{"version":2,"type":"groundTruth","sceneName":"Tutorial_01","heroAvailable":true,"heroX":35,"heroY":11,"grounded":true,"cameraAvailable":true} """);
        var path = new RecordedInputPath(1, Guid.NewGuid(), DateTimeOffset.UtcNow, .65,
            null, null, checkpoint,
            [new(.02, RecordedGameButton.Right, RecordedInputTransition.Pressed),
             new(.62, RecordedGameButton.Right, RecordedInputTransition.Released)], [],
            [new(.01, 1, sample), new(.3, 1.3, sample with { HeroX = 36, SceneName = "Tutorial_02" }), new(.6, 1.6, sample with { HeroX = 37 })]);
        if (step)
        {
            using var stop = new CancellationTokenSource();
            RecordedGameCheckpoint? endpoint = null;
            var began = false;
            var scored = 0;
            var frames = Enumerable.Range(0, 20).Select(i => new ControlledReplayFrame(i,
                Math.Min(.65, (i + 1) / 30.0), Math.Min(.65, (i + 1) / 30.0) - i / 30.0, InputButtons.Right, sample)).ToArray();
            var run = bridge.ReplayStepAsync(path, ct => {
                Check(restores == 1 && bridge.PauseState == GamePauseState.Paused, "Step prepared before paused checkpoint restoration.");
                Check(!bridge.SetLabelPauseActive(false), "Starting capture released the replay preparation pause.");
                return Task.CompletedTask;
            }, timestamp => {
                Check(bridge.PauseState == GamePauseState.Paused, "Controlled step released its pause ownership."); began = true;
            }, frames, async (tick, ack, ct) => {
                Check(ack.Accepted && ack.DeltaSeconds == tick.DeltaSeconds, "Frame acknowledgement changed simulation delta.");
                var before = controlled.Count;
                await Task.Delay(60, ct); // Capture/tracker work may take arbitrary wall time.
                Check(controlled.Count == before, "Game advanced before the previous frame was scored.");
                scored++;
                if (cancel) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            }, value => endpoint = value, value => Check(value.SceneMasks is not null, "Restored start omitted room state."), stop.Token);
            try { await run; Check(!cancel, "Cancelled step completed normally."); }
            catch (OperationCanceledException) when (cancel) { }
            Check(scored == (cancel ? 1 : frames.Length) && controlled.Count(c => c.Kind == "advance") == scored,
                "Controlled replay skipped frames or continued after rejection.");
            Check(controlled.Last().Kind == "end", "Controlled clock was not released at completion/cancellation.");
            Check(began && endpoint is not null && captures == 3, "Step did not capture its ending conditions.");
            Check(bridge.PauseState == GamePauseState.Paused && !bridge.IsReplaying, "Step did not finish paused and release its input lease.");
            Check(!bridge.SetLabelPauseActive(false), "Capture shutdown resumed a stashed replay.");
            await Task.Delay(100, timeout.Token);
            Check(bridge.PauseState == GamePauseState.Paused, "Boundary pause did not survive capture shutdown.");
            Check(creations == 1 && restores == 1 && poses.Count > 0, "Step lost its single connection or pose trajectory.");
            await bridge.LeaveStepReplayAsync(timeout.Token);
            Check(bridge.PauseState == GamePauseState.Ready, "Leaving step replay did not resume the game.");
        }
        else if (missingPose)
        {
            try { await bridge.ReplayPathAsync(path, 1, true, _ => { }, timeout.Token); throw new Exception("Missing pose capability was silently downgraded."); }
            catch (InvalidOperationException error) when (error.Message.Contains("player-pose-playback-v1")) { }
            Check(!bridge.IsReplaying && restores == 0 && poses.IsEmpty, "Unsupported replay partially started.");
        }
        else
        {
        RecordedInputPath? recording = null;
        using var stop = new CancellationTokenSource();
        var phases = new List<bool>();
        var replay = bridge.ReplayPathAsync(path, 1, true, value => recording = value, stop.Token,
            prepare: (restored, ct) => {
                Check(restores == (restored ? 1 : 0), "Preparation/restoration order changed.");
                phases.Add(restored); return Task.CompletedTask;
            });
        if (!reject)
        {
            await Until(() => states.Any(state => state.Enabled && state.HeldButtons == InputButtons.Right), timeout.Token);
            Check(!bridge.ForwardPointer("leftDown", .5, .5), "Live mouse click contaminated replay.");
            bridge.Press(InputButtons.Left);
            bridge.Heartbeat();
            bridge.Release(InputButtons.Right);
            Check(!await bridge.StartRecordingAsync(timeout.Token), "User recording can replace replay recording.");
            Check(!bridge.SetLabelPauseActive(true), "Labeling can pause an active replay.");
            if (cancel) { await Task.Delay(40); stop.Cancel(); }
        }
        try { await replay; Check(!cancel && !reject, "Replay unexpectedly succeeded."); }
        catch (OperationCanceledException) when (cancel) { }
        catch (InvalidOperationException error) when (reject && error.Message.Contains("test rejection")) { }
        await Until(() => states.Last() is { Enabled: false, HeldButtons: InputButtons.None }, timeout.Token);
        Check(creations == 1 && restores == 1, "Replay made another connection or restored more than once.");
        var posesAtEnd = poses.Count;
        await Task.Delay(80, timeout.Token);
        Check(poses.Count == posesAtEnd, "Pose traffic continued after replay ended.");
        Check(!bridge.IsReplaying && !bridge.IsRecording, "Replay left its input/recording lease active.");
        Check(!states.Any(state => (state.HeldButtons & InputButtons.Left) != 0), "Live key press contaminated replay.");
        if (reject) Check(recording is null, "Rejected checkpoint still played the path.");
        else
        {
            Check(recording?.SourcePathId == path.Id && recording.ReplayIteration == 1, "Replay evidence lost source identity.");
            Check(recording!.GroundTruthTrace.Count > 0, "Replay lost live ground truth.");
            recording.Validate();
            Check(poses.Count > 0, "No actual playerPose messages were sent.");
            Check(poses.Select(p => p.Sequence).SequenceEqual(poses.Select(p => p.Sequence).Order().Distinct()), "Pose sequences did not increase.");
            Check(recording.RuntimeMetadata?["playbackMode"] == "recorded-player-poses-and-input", "Playback metadata is inaccurate.");
            Check(phases.SequenceEqual(new[] { false, true }), "Replay skipped preparation phase.");
            if (!cancel) Check(poses.Last().HeroX == 37 && poses.Any(p => p.HeroX == 36 && p.SceneName == "Tutorial_02"), "Player trajectory was not sent.");
            if (!cancel) Check(states.Count(state => state.HeldButtons == InputButtons.Right) >= 5,
                "Long hold did not renew the receiver's 250ms lease.");
        }
        }
        }
        finally { await bridge.DisposeAsync(); }
        try { await serverTask.WaitAsync(timeout.Token); }
        catch (IOException) { /* Closing the client can reset the peer with telemetry in flight. */ }
    }

    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
