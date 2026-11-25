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

    internal static void TestTrialCheckpointUsesTheOwnedReceiver() => TrialCheckpoint().GetAwaiter().GetResult();

    private static async Task TrialCheckpoint()
    {
        await using var fixture = new Fixture(pauseSupported: true);
        var forwarder = fixture.Forwarder;
        await Until(() => forwarder.PauseState == GamePauseState.Ready);
        var initial = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(await fixture.Next("state"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        var pose = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(
            """{"version":2,"type":"groundTruth","sequence":1,"unityFrame":1,"sceneName":"Tutorial_01","cameraAvailable":true,"atlasGameplayActive":true,"heroAvailable":true}""")
            with { SessionId = initial.SessionId };
        Check(!forwarder.CanUseTrialCheckpoint, "Trial checkpoint requires measured gameplay.");
        await fixture.SendGroundTruth(pose);
        await Until(() => forwarder.CanUseTrialCheckpoint);
        var owner = forwarder.BeginBotControl();
        Check(owner is not null && !forwarder.CanUseTrialCheckpoint, "Trial checkpoints must not interrupt an active bot.");
        forwarder.EndBotControl(owner!.Value);
        var point = await forwarder.UseTrialCheckpointAsync(null, null, token);
        Check(point.Session == initial.SessionId && point.Checkpoint.SceneName == "Tutorial_01",
            "Capture the return point through the live receiver connection.");
        _ = await fixture.Next("captureCheckpoint");
        await fixture.SendGroundTruth(pose with { Sequence = 2, UnityFrame = 2 });
        await Until(() => forwarder.CanUseTrialCheckpoint);
        try
        {
            _ = await forwarder.UseTrialCheckpointAsync(point.Checkpoint, Guid.NewGuid(), token);
            Check(false, "An earlier receiver session should not authorize a restore.");
        }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("previous receiver session", StringComparison.Ordinal));
        }
        var restore = forwarder.UseTrialCheckpointAsync(point.Checkpoint, point.Session, token);
        Check(!forwarder.CanUseTrialCheckpoint && forwarder.BeginBotControl() is null, "Hold the control lease through the restore receipt.");
        var command = ReceiverWireCodec.DecodeLine<ReceiverCheckpointCommand>(await fixture.Next("restoreCheckpoint"));
        Check(command.Checkpoint == point.Checkpoint && command.SessionId == point.Session,
            "Restore the original checkpoint in the same session.");
        await fixture.AcknowledgeTrialCheckpoint(command, accepted: true);
        Check((await restore).Checkpoint == point.Checkpoint, "Acknowledge the exact original checkpoint.");
        await fixture.SendGroundTruth(pose with { Sequence = 3, UnityFrame = 3 });
        await Until(() => forwarder.CanUseTrialCheckpoint);
        var refused = forwarder.UseTrialCheckpointAsync(point.Checkpoint, point.Session, token);
        var rejected = ReceiverWireCodec.DecodeLine<ReceiverCheckpointCommand>(await fixture.Next("restoreCheckpoint"));
        await fixture.AcknowledgeTrialCheckpoint(rejected, accepted: false);
        try { _ = await refused; }
        catch (InvalidOperationException error) when (error.Message.Contains("test rejection", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A rejected restore must report the receiver failure.");
    }

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
        Check(await forwarder.StopRecordingAsync() is not null);
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

    public static void TestBotOwnershipAndExpiration() => BotOwnershipAndExpiration().GetAwaiter().GetResult();

    public static void TestBotPlaybackWaitsAndIgnoresDelayedEvidence() => BotPlaybackWaitsAndIgnoresDelayedEvidence().GetAwaiter().GetResult();

    public static void TestAtlasPathsUseReceiverMenuWithoutCamera() => AtlasPathsUseReceiverMenuWithoutCamera().GetAwaiter().GetResult();

    private static async Task BotPlaybackWaitsAndIgnoresDelayedEvidence()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-bot-evidence-lease-");
        try
        {
            BotPolicyTests.WriteRuntime(temporary.FullName, alwaysAttack: true);
            var playback = new BotPolicyPlayback(BotNativePolicy.Load(temporary.FullName), BotCaptureDomain.Hacker);
            await using var fixture = new Fixture(pauseSupported: true);
            var forwarder = fixture.Forwarder;
            var recordedControls = Channel.CreateUnbounded<(Guid Owner, InputButtons Buttons, double Time)>();
            forwarder.BotInputSent += (owner, buttons, time) => recordedControls.Writer.TryWrite((owner, buttons, time));
            await Until(() => forwarder.PauseState == GamePauseState.Ready);
            await fixture.Next("state");
            var owner = forwarder.BeginBotControl() ?? throw new Exception("Bot control did not start.");
            await Expect(InputButtons.None);
            Check(!recordedControls.Reader.TryRead(out _), "An initial neutral heartbeat is not a control transition.");
            Check(new BotPlaybackUpdate(BotPlaybackUpdate.WaitingForFrame).Decision is null);

            var first = Authored(Clock() - .01);
            var fresh = Apply(first);
            Check(fresh?.Decision is { } decision && decision.Action.Buttons.HasFlag(InputButtons.ActionX)
                && decision.Deadline <= first.CapturedAt + .2);
            await Expect(fresh!.Decision!.Value.Action.Buttons);
            var sent = await ReadControl(fresh.Decision.Value.Action.Buttons);
            Check(Apply(Authored(first.CaptureTimestamp - 1) with { AvailableAt = Clock(), Issues = ["stale-capture"] }) is null);
            Check(Apply(first with { Issues = ["missing-terrain-analysis"] }) is null);
            Check(forwarder.CaptureMovement() == fresh.Decision.Value.Action.Buttons,
                "Delayed or duplicate evidence released the current controls.");

            var missing = Apply(Authored(Clock()) with { Issues = ["missing-knight"] });
            Check(missing is { Decision: null, Status: "Bot waiting for Knight detection" });
            await Expect(InputButtons.None);
            Check(await ReadControl(InputButtons.None) >= sent, "Evaluation timestamps must follow the receiver send order.");
            Check(forwarder.OwnsBotControl(owner) && Apply(first) is null, "Waiting lost ownership or enabled a replayed observation.");

            var recovered = Apply(Authored(Clock()));
            Check(recovered?.Decision is { Action.Buttons: not InputButtons.None }, "Fresh terrain and Knight evidence should resume playback.");
            await Expect(recovered!.Decision!.Value.Action.Buttons);
            _ = await ReadControl(recovered.Decision.Value.Action.Buttons);
            forwarder.EndBotControl(owner);
            await Expect(InputButtons.None);
            Check(!forwarder.OwnsBotControl(owner) && !forwarder.ApplyBotPlayback(owner, recovered),
                "A stopped session accepted another policy action.");

            BotPlaybackUpdate? Apply(BotCaptureObservation row)
            {
                var update = playback.Evaluate(row, forwarder.CaptureMovement(), Clock());
                Check(forwarder.ApplyBotPlayback(owner, update), "The receiver input lease was not renewed.");
                return update;
            }

            async Task Expect(InputButtons buttons)
            {
                var wire = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(await fixture.Next("state"));
                Check(wire.HeldButtons == buttons && wire.Enabled == (buttons != InputButtons.None),
                    $"Expected wire input {buttons} but received {wire.HeldButtons}.");
            }

            async Task<double> ReadControl(InputButtons buttons)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var recorded = await recordedControls.Reader.ReadAsync(timeout.Token);
                Check(recorded.Owner == owner && recorded.Buttons == buttons && double.IsFinite(recorded.Time),
                    "An evaluation must observe the successfully sent state for the current bot owner.");
                return recorded.Time;
            }
        }
        finally { temporary.Delete(recursive: true); }

        static BotCaptureObservation Authored(double time) => BotVisualKnightMemoryTests.Row(time, .3) with
            { TerrainSource = "human-authored", HumanTerrainCount = 2, KnightSource = "hacker-frame" };
        static double Clock() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    private static async Task AtlasPathsUseReceiverMenuWithoutCamera()
    {
        await using var fixture = new Fixture(pauseSupported: true);
        var forwarder = fixture.Forwarder;
        var evidence = new AtlasPathFrameEvidence();
        var received = Channel.CreateUnbounded<double>();
        forwarder.GroundTruthReceived += (sample, observedAt) =>
        {
            evidence.ObserveReceiver(sample.SessionId, sample.AtlasGameplayActive, observedAt);
            received.Writer.TryWrite(observedAt);
        };
        await Until(() => forwarder.PauseState == GamePauseState.Ready);
        var active = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(
            """{"version":2,"type":"groundTruth","sessionID":"193f132f-ae84-423b-ac82-1bcd65dc1fe1","sequence":1,"unityFrame":40,"sceneName":"Tutorial_01","cameraAvailable":true,"atlasGameplayActive":true}""");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await fixture.SendGroundTruth(active);
        var first = await received.Reader.ReadAsync(deadline.Token);
        Check(evidence.AdmitsGameplay(40, first, modded: true));

        await fixture.SendGroundTruth(active with { Sequence = 2, CameraAvailable = false, AtlasGameplayActive = false });
        var paused = await received.Reader.ReadAsync(deadline.Token);
        Check(!evidence.AdmitsGameplay(40, first, modded: true)
            && !evidence.AdmitsHacker(1, 40, first, active.SessionId, true, true),
            "A menu without a camera sample must retire the current overlays.");
        Check(forwarder.TryGetFreshGroundTruth(TimeSpan.FromSeconds(4), out var camera) && camera.Sequence == 1,
            "A camera-less menu sample must not replace the last camera reference.");
        await fixture.SendGroundTruth(active with { Sequence = 3, UnityFrame = 41 });
        var resumed = await received.Reader.ReadAsync(deadline.Token);
        Check(resumed > paused && !evidence.AdmitsGameplay(40, first, modded: true)
            && evidence.AdmitsGameplay(41, resumed, modded: true)
            && evidence.AdmitsHacker(1, 41, resumed, active.SessionId, true, true),
            "A resume authorizes only fresh gameplay frames.");
    }

    private static async Task BotOwnershipAndExpiration()
    {
        await using var fixture = new Fixture(pauseSupported: true);
        var forwarder = fixture.Forwarder;
        await Until(() => forwarder.PauseState == GamePauseState.Ready);
        await fixture.Next("state");

        forwarder.Press(InputButtons.ActionZ);
        await Expect(InputButtons.ActionZ);
        forwarder.Release(InputButtons.ActionZ);
        await Expect(InputButtons.None, enabled: true);
        Check(forwarder.CaptureMovement(out var afterHumanTap) == InputButtons.None && afterHumanTap,
            "A tap between captures must keep jump evidence without reporting a held button.");

        var owner = forwarder.BeginBotControl() ?? throw new Exception("Bot control did not start.");
        await Expect(InputButtons.None);
        Check(forwarder.OwnsBotControl(owner) && forwarder.BeginBotControl() is null);
        Check(!await forwarder.StartRecordingAsync(), "Bot input could enter a demonstration.");
        Check(!forwarder.RenewBotControl(owner, InputButtons.Left, Clock() - .1), "Expired evidence was authorized.");
        Check(!forwarder.RenewBotControl(owner, InputButtons.Left, Clock() + 10), "An overlong decision was authorized.");
        var jumpingAttack = InputButtons.Right | InputButtons.ActionX | InputButtons.ActionZ;
        Check(forwarder.RenewBotControl(owner, jumpingAttack, Clock() + .26));
        await Expect(jumpingAttack);
        await Until(() => forwarder.CaptureMovement() == jumpingAttack);
        await Until(() => forwarder.CaptureMovement() == InputButtons.None);
        Check(forwarder.CaptureMovement(out var afterBotJump) == InputButtons.None && afterBotJump,
            "A bot jump must protect walked-ground evidence after the held state expires.");
        forwarder.Heartbeat();
        await Expect(InputButtons.None);
        Check(forwarder.OwnsBotControl(owner), "A short observation gap should retain session ownership.");

        Check(forwarder.RenewBotControl(owner, InputButtons.ActionX, Clock() + .23));
        await Expect(InputButtons.ActionX);
        forwarder.Press(InputButtons.Left);
        await Expect(InputButtons.Left);
        Check(!forwarder.OwnsBotControl(owner) && !forwarder.RenewBotControl(owner, InputButtons.Right, Clock() + .23),
            "A late policy decision overrode the player's input.");
        forwarder.Release(InputButtons.Left);
        await Expect(InputButtons.None, enabled: true);

        var next = forwarder.BeginBotControl() ?? throw new Exception("Bot control did not restart.");
        await Expect(InputButtons.None);
        forwarder.EndBotControl(owner);
        Check(forwarder.OwnsBotControl(next) && !forwarder.RenewBotControl(owner, InputButtons.Right, Clock() + .23),
            "An old controller cannot end or renew a newer session.");
        Check(forwarder.RenewBotControl(next, InputButtons.ActionX, Clock() + .32));
        await Expect(InputButtons.ActionX);
        forwarder.NeutralizeBotControl(owner);
        Check(forwarder.CaptureMovement() == InputButtons.ActionX,
            "An old controller cannot clear the new session's decision.");
        forwarder.NeutralizeBotControl(next);
        await Expect(InputButtons.None);
        var gate = (SemaphoreSlim)typeof(WindowsGameControlForwarder).GetField("outboundGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(forwarder)!;
        await gate.WaitAsync();
        try
        {
            var expires = Clock() + .09;
            Check(forwarder.RenewBotControl(next, InputButtons.ActionZ, expires));
            await Until(() => Clock() >= expires);
        }
        finally { gate.Release(); }
        await Expect(InputButtons.None);

        await gate.WaitAsync();
        try
        {
            Check(forwarder.RenewBotControl(next, InputButtons.ActionZ, Clock() + .30));
            forwarder.ReleaseAll(); // The dashboard uses this on focus loss.
        }
        finally { gate.Release(); }
        await Expect(InputButtons.None);
        Check(!forwarder.OwnsBotControl(next) && forwarder.CaptureMovement(out var afterReset) == InputButtons.None
            && !afterReset, "Focus loss left bot control or its jump evidence armed.");

        var waiting = forwarder.BeginBotControl() ?? throw new Exception("Bot control did not enter the waiting state.");
        await Expect(InputButtons.None);
        forwarder.ReleaseAll();
        await Expect(InputButtons.None);
        Check(!forwarder.OwnsBotControl(waiting), "Focus loss must release a bot that is still waiting for observations.");

        var final = forwarder.BeginBotControl() ?? throw new Exception("Bot control did not restart.");
        await Expect(InputButtons.None);
        Check(forwarder.RenewBotControl(final, InputButtons.ActionX, Clock() + .30));
        await Expect(InputButtons.ActionX);
        Check(forwarder.SetLabelPauseActive(true));
        await Expect(InputButtons.None);
        await fixture.Next("pause");
        Check(!forwarder.OwnsBotControl(final));

        async Task Expect(InputButtons buttons, bool? enabled = null)
        {
            var snapshot = ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(await fixture.Next("state"));
            Check(snapshot.Enabled == (enabled ?? buttons != InputButtons.None) && snapshot.HeldButtons == buttons,
                $"Expected {buttons}, got {snapshot.HeldButtons} (enabled {snapshot.Enabled}).");
        }

        static double Clock() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
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

    public static void TestRoomResetTransport() => RoomResetTransport().GetAwaiter().GetResult();

    private static async Task RoomResetTransport()
    {
        foreach (var supported in new[] { false, true })
        {
            await using var fixture = new Fixture(pauseSupported: true, roomResetSupported: supported);
            var forwarder = fixture.Forwarder;
            await Until(() => forwarder.PlayerOpsAvailable);
            await fixture.Next("state");
            Check(forwarder.RoomResetAvailable == supported);
            if (!supported)
            {
                Check(await forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.ResetRoom) is null);
                var query = forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.Query);
                var queryCommand = ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsCommand>(await fixture.Next("playerOps"));
                Check(queryCommand.Operation == "query");
                await fixture.AcknowledgePlayerOps(queryCommand);
                Check((await query)?.Accepted == true);
                continue;
            }

            Check(await forwarder.StartRecordingAsync());
            Check(!forwarder.RoomResetAvailable);
            Check(await forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.ResetRoom) is null);
            Check(await forwarder.StopRecordingAsync() is not null);
            var owner = forwarder.BeginBotControl() ?? throw new InvalidOperationException("Failed to acquire bot control.");
            Check(!forwarder.RoomResetAvailable);
            Check(await forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.ResetRoom) is null);
            Check(forwarder.OwnsBotControl(owner), "A refused room reset must leave the bot running.");
            forwarder.EndBotControl(owner);

            var queryInProgress = forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.Query);
            var activeQuery = ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsCommand>(await fixture.Next("playerOps"));
            Check(activeQuery.Operation == "query" && !forwarder.RoomResetAvailable);
            Check(await forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.ResetRoom) is null);
            await fixture.AcknowledgePlayerOps(activeQuery);
            Check((await queryInProgress)?.Accepted == true && forwarder.RoomResetAvailable);

            var recorded = new RecordedInputPath(RecordedInputPath.CurrentSchemaVersion,
                Guid.NewGuid(), DateTimeOffset.UtcNow, .1, null, null, null, [], runtimeMetadata: null);

            var reset = forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.ResetRoom);
            var command = ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsCommand>(await fixture.Next("playerOps"));
            Check(command.Operation == "resetRoom" && command.State is null);
            Check(await forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.Query) is null);
            Check(!await forwarder.StartRecordingAsync());
            try
            {
                await forwarder.ReplayPathAsync(recorded, 0, restoreCheckpoint: false, _ => { });
                throw new Exception("Replay started during room reset.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("stop recording before replaying"))
            {
            }
            await fixture.AcknowledgePlayerOps(command with { SessionId = Guid.NewGuid() }, "wrong");
            await fixture.AcknowledgePlayerOps(command, "Tutorial_01");
            var result = await reset.WaitAsync(TimeSpan.FromSeconds(4));
            Check(result?.ConfirmsRoomReset == true && result.ResetScene == "Tutorial_01");
            Check(!(result! with { Accepted = false }).ConfirmsRoomReset);
            Check(!(result! with { ResetScene = " " }).ConfirmsRoomReset);

            var nextReset = forwarder.PerformPlayerOpsAsync(ReceiverPlayerOpsCommand.ResetRoom);
            var next = ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsCommand>(await fixture.Next("playerOps"));
            await fixture.AcknowledgePlayerOps(next);
            Check((await nextReset) is { Accepted: true, ConfirmsRoomReset: false });
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
        internal Fixture(bool pauseSupported, bool? roomResetSupported = null)
        {
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Never use the real receiver port; this fixture owns an ephemeral loopback listener.
            Forwarder = new WindowsGameControlForwarder(() => new ReceiverClient(port: port));
            serving = Serve(pauseSupported, roomResetSupported); Forwarder.Start();
        }
        private async Task Serve(bool pauseSupported, bool? roomResetSupported)
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
                if (roomResetSupported.HasValue) capabilities.Add(ReceiverProtocol.PlayerOpsCapability);
                if (roomResetSupported == true) capabilities.Add(ReceiverProtocol.RoomResetCapability);
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
        internal async Task AcknowledgePlayerOps(ReceiverPlayerOpsCommand command, string? scene = null) =>
            await writer!.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverPlayerOpsAcknowledgement(
                2, "playerOpsAck", command.SessionId, command.CommandId, true, null, null, null, scene)));
        internal Task SendGroundTruth(ReceiverGroundTruthSample sample) =>
            writer!.WriteAsync(ReceiverWireCodec.EncodeLine(sample));
        internal Task AcknowledgeTrialCheckpoint(ReceiverCheckpointCommand command, bool accepted) =>
            writer!.WriteAsync(ReceiverWireCodec.EncodeLine(new ReceiverCheckpointAcknowledgement(2,
                "restoreCheckpointAck", command.SessionId, command.CommandId, accepted, command.Checkpoint,
                accepted ? null : "test rejection")));
        internal Task CloseForwarder() => closeTask ??= Forwarder.DisposeAsync().AsTask();
        internal void Disconnect() { peer?.Close(); listener.Stop(); }
        public async ValueTask DisposeAsync()
        {
            await CloseForwarder(); lifetime.Cancel(); listener.Stop(); peer?.Dispose();
            await serving; lifetime.Dispose();
        }
    }
}
