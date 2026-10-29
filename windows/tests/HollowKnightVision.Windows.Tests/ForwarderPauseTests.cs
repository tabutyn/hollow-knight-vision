using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class ForwarderPauseTests
{
    private static void Check(bool condition, string message = "Receiver pause integration failed.")
    { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    public static void TestLeaseTransport() => LeaseTransport().GetAwaiter().GetResult();
    private static async Task LeaseTransport()
    {
        await using var fixture = new Fixture(pauseSupported: true);
        var forwarder = fixture.Forwarder;
        await Until(() => forwarder.PauseState == GamePauseState.Ready);
        await fixture.Next("state");
        forwarder.Press(InputButtons.ActionZ);
        var pressed = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(await fixture.Next("state"));
        Check(pressed.Enabled && pressed.HeldButtons == InputButtons.ActionZ);
        forwarder.ReleaseAll(); await fixture.Next("state");
        Check(await forwarder.StartRecordingAsync() && !forwarder.SetLabelPauseActive(true), "Label pause interrupted an input recording.");
        Check(forwarder.StopRecording() is not null);
        Check(forwarder.SetLabelPauseActive(true));
        var neutral = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(await fixture.Next("state"));
        var first = ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("pause"));
        Check(!neutral.Enabled && neutral.HeldButtons == InputButtons.None && first.LeaseMilliseconds == 2000);
        Check(forwarder.PauseState == GamePauseState.Pausing, "Bridge claimed a pause before acknowledgement.");
        await fixture.Acknowledge(first with { SessionId = Guid.NewGuid() }, true);
        var renewed = ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("pause"));
        Check(forwarder.PauseState == GamePauseState.Pausing && renewed.CommandId != first.CommandId,
            "Wrong-session acknowledgement was accepted or unconfirmed pause was not renewed.");
        await fixture.Acknowledge(renewed, true);
        await Until(() => forwarder.PauseState == GamePauseState.Paused);
        Check(!(await forwarder.StartRecordingAsync()) && !forwarder.ForwardPointer("leftDown", .5, .5));
        var boundary = fixture.Seen.Count;
        forwarder.Press(InputButtons.ActionX); forwarder.Heartbeat(); forwarder.Release(InputButtons.ActionX);
        await fixture.Next("pause"); // Real renewal timer is the observation barrier; no game or synthetic keys.
        Check(!fixture.Seen.Skip(boundary).Where(line => ReceiverWireCodec.MessageType(line) == "state")
            .Select(ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>).Any(snapshot => snapshot.Enabled || snapshot.HeldButtons != InputButtons.None),
            "Paused gameplay input reached the receiver.");
        Check(forwarder.SetLabelPauseActive(false));
        var resume = ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("resume"));
        Check(resume.LeaseMilliseconds is null && forwarder.PauseState == GamePauseState.Resuming);
        await fixture.Acknowledge(resume, true);
        resume = ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("resume"));
        await fixture.Acknowledge(resume, false);
        await Until(() => forwarder.PauseState == GamePauseState.Ready);
        forwarder.Heartbeat(); forwarder.Press(InputButtons.Left);
        var fresh = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(await fixture.Next("state"));
        Check(fresh.Enabled && fresh.HeldButtons == InputButtons.Left, "Resume restored previously held keys instead of requiring a new press.");
        forwarder.ReleaseAll(); await fixture.Next("state");
        Check(forwarder.SetLabelPauseActive(true)); await fixture.Next("state");
        var closing = ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("pause"));
        await fixture.Acknowledge(closing, true); await Until(() => forwarder.PauseState == GamePauseState.Paused);
        await fixture.CloseForwarder();
        Check(ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("resume")).SessionId == closing.SessionId,
            "Closing the bridge did not explicitly resume its paused game.");
    }

    public static void TestUnsupportedAndDisconnectedReceiver() => UnsupportedAndDisconnected().GetAwaiter().GetResult();
    public static void TestBlockedShutdown() => BlockedShutdown().GetAwaiter().GetResult();
    private static async Task BlockedShutdown()
    {
        await using var fixture = new Fixture(true); var forwarder = fixture.Forwarder;
        await Until(() => forwarder.PauseState == GamePauseState.Ready); await fixture.Next("state");
        forwarder.SetLabelPauseActive(true); await fixture.Next("state");
        var command = ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("pause"));
        await fixture.Acknowledge(command, true); await Until(() => forwarder.PauseState == GamePauseState.Paused);
        // Simulate an occupied send lane. Shutdown must abandon the bounded
        // resume attempt, close the connection and let the receiver lease expire.
        var gate = (SemaphoreSlim)typeof(WindowsGameControlForwarder).GetField("outboundGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(forwarder)!;
        await gate.WaitAsync();
        await fixture.CloseForwarder().WaitAsync(TimeSpan.FromSeconds(3));
        Check(forwarder.PauseState == GamePauseState.Unavailable, "Shutdown waited indefinitely for a blocked pause send.");
        // DisposeAsync owns/disposes the gate; no release after disposal.
    }

    private static async Task UnsupportedAndDisconnected()
    {
        foreach (var supported in new[] { false, true })
        {
            await using var fixture = new Fixture(supported); var forwarder = fixture.Forwarder;
            await Until(() => forwarder.MenuShortcutsAvailable); await fixture.Next("state");
            if (!supported)
            {
                Check(forwarder.PauseState == GamePauseState.Unavailable && !forwarder.SetLabelPauseActive(true));
                forwarder.Press(InputButtons.Up);
                Check(ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(await fixture.Next("state")).HeldButtons == InputButtons.Up,
                    "Missing optional pause capability disabled ordinary controls.");
            }
            else
            {
                forwarder.SetLabelPauseActive(true); await fixture.Next("state");
                var command = ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(await fixture.Next("pause"));
                await fixture.Acknowledge(command, true); await Until(() => forwarder.PauseState == GamePauseState.Paused);
            }
            fixture.Disconnect();
            await Until(() => !forwarder.MenuShortcutsAvailable);
            Check(forwarder.PauseState == GamePauseState.Unavailable && !forwarder.SetLabelPauseActive(true));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private readonly Task serving;
        private TcpClient? peer;
        private StreamWriter? writer;
        private Task? closeTask;
        internal readonly ConcurrentQueue<string> Seen = new();
        internal readonly WindowsGameControlForwarder Forwarder;
        internal Fixture(bool pauseSupported)
        {
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Never use the real receiver port; this fixture owns an ephemeral loopback listener.
            Forwarder = new WindowsGameControlForwarder(() => new ReceiverClient(port: port));
            serving = Serve(pauseSupported); Forwarder.Start();
        }
        private async Task Serve(bool pauseSupported)
        {
            try
            {
                peer = await listener.AcceptTcpClientAsync(lifetime.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                await using var output = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                writer = output;
                var hello = ReceiverWireCodec.DecodeLine<ReceiverCapabilityRequest>((await reader.ReadLineAsync(lifetime.Token))!);
                var capabilities = new List<string> { "input-state-v1", ReceiverProtocol.MenuShortcutsCapability, ReceiverProtocol.PointerCapability, ReceiverProtocol.PlayerCheckpointCapability };
                if (pauseSupported) capabilities.Add(ReceiverProtocol.PauseCapability);
                await output.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCapabilitiesAcknowledgement(2, "capabilitiesAck",
                    hello.SessionId, capabilities, pauseSupported ? 2000 : 0)));
                while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                {
                    Seen.Enqueue(line); await messages.Writer.WriteAsync(line, lifetime.Token);
                    if (ReceiverWireCodec.MessageType(line) == "captureCheckpoint")
                    {
                        var command = ReceiverWireCodec.DecodeLine<ReceiverCheckpointCommand>(line);
                        var checkpoint = System.Text.Json.JsonSerializer.Deserialize<RecordedGameCheckpoint>("""{"sceneName":"Tutorial_01","grounded":true} """, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        await output.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCheckpointAcknowledgement(
                            2, "captureCheckpointAck", hello.SessionId, command.CommandId, true, checkpoint, null)));
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            finally { messages.Writer.TryComplete(); }
        }
        internal async Task<string> Next(string type)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (true)
            {
                var line = await messages.Reader.ReadAsync(deadline.Token);
                if (ReceiverWireCodec.MessageType(line) == type) return line;
            }
        }
        internal async Task Acknowledge(ReceiverPauseCommand command, bool paused) =>
            await writer!.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverPauseAcknowledgement(2,
                command.Type == "pause" ? "pauseAck" : "resumeAck", command.SessionId, command.CommandId, paused)));
        internal Task CloseForwarder() => closeTask ??= Forwarder.DisposeAsync().AsTask();
        internal void Disconnect() { peer?.Close(); listener.Stop(); }
        public async ValueTask DisposeAsync()
        {
            await CloseForwarder(); lifetime.Cancel(); listener.Stop(); peer?.Dispose();
            await serving; lifetime.Dispose();
        }
    }
}
