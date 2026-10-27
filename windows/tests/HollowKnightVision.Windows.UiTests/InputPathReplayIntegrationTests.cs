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
        Console.WriteLine("PASS input replay shares live connection, restores checkpoint, records telemetry, renews holds, and releases on cancellation/rejection");
    }).GetAwaiter().GetResult();

    private static async Task Exercise(bool cancel, bool reject)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var states = new ConcurrentQueue<ReceiverInputSnapshot>();
        var restores = 0;
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
                ["input-state-v1", ReceiverProtocol.PlayerCheckpointCapability, ReceiverProtocol.PlayerOpsCapability], 0)));
            long frame = 0;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
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
        var checkpoint = JsonSerializer.Deserialize<RecordedGameCheckpoint>(
            "{\"sceneName\":\"Tutorial_01\",\"heroX\":35,\"heroY\":11,\"cameraX\":36,\"cameraY\":14,\"grounded\":true}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var path = new RecordedInputPath(1, Guid.NewGuid(), DateTimeOffset.UtcNow, .65,
            null, null, checkpoint,
            [new(.02, RecordedGameButton.Right, RecordedInputTransition.Pressed),
             new(.62, RecordedGameButton.Right, RecordedInputTransition.Released)], null);
        RecordedInputPath? recording = null;
        using var stop = new CancellationTokenSource();
        var replay = bridge.ReplayPathAsync(path, 1, true, value => recording = value, stop.Token);
        if (!reject)
        {
            await Until(() => states.Any(state => state.Enabled && state.HeldButtons == InputButtons.Right), timeout.Token);
            bridge.Press(InputButtons.Left);
            bridge.Heartbeat();
            bridge.Release(InputButtons.Right);
            Check(!bridge.StartRecording(), "User recording can replace replay recording.");
            Check(!bridge.SetLabelPauseActive(true), "Labeling can pause an active replay.");
            if (cancel) { await Task.Delay(40); stop.Cancel(); }
        }
        try { await replay; Check(!cancel && !reject, "Replay unexpectedly succeeded."); }
        catch (OperationCanceledException) when (cancel) { }
        catch (InvalidOperationException error) when (reject && error.Message.Contains("test rejection")) { }
        await Until(() => states.Last() is { Enabled: false, HeldButtons: InputButtons.None }, timeout.Token);
        Check(creations == 1 && restores == 1, "Replay made another connection or restored more than once.");
        Check(!bridge.IsReplaying && !bridge.IsRecording, "Replay left its input/recording lease active.");
        Check(!states.Any(state => (state.HeldButtons & InputButtons.Left) != 0), "Live key press contaminated replay.");
        if (reject) Check(recording is null, "Rejected checkpoint still played the path.");
        else
        {
            Check(recording?.SourcePathId == path.Id && recording.ReplayIteration == 1, "Replay evidence lost source identity.");
            Check(recording!.GroundTruthTrace.Count > 0, "Replay lost live ground truth.");
            recording.Validate();
            if (!cancel) Check(states.Count(state => state.HeldButtons == InputButtons.Right) >= 5,
                "Long hold did not renew the receiver's 250ms lease.");
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
