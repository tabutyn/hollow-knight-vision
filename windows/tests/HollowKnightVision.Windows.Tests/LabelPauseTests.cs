using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class LabelPauseTests
{
    private static void Check(bool condition, string message = "Label pause assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static ReceiverCapabilitiesAcknowledgement Capabilities(Guid session) =>
        new(2, "capabilitiesAck", session, ["input-state-v1", ReceiverProtocol.PauseCapability], 2000);
    private static ReceiverPauseAcknowledgement Ack(Guid session, string type, bool paused) => new(2, type, session, Guid.NewGuid(), paused);
    private static BgraFrame Frame(long id) => new(2, 2, 8, new byte[16], id, DateTimeOffset.UnixEpoch.AddSeconds(id));

    public static void TestWireMessages()
    {
        var session = Guid.NewGuid(); var pause = ReceiverPauseCommand.Pause(session, 2000);
        var line = ReceiverWireCodec.EncodeLine(pause);
        Check(line.EndsWith('\n') && ReceiverWireCodec.MessageType(line) == "pause");
        Check(ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(line) == pause && pause.Version == 2);
        using var document = JsonDocument.Parse(line);
        Check(document.RootElement.GetProperty("sessionID").GetGuid() == session
            && document.RootElement.GetProperty("commandID").GetGuid() == pause.CommandId);
        line = ReceiverWireCodec.EncodeLine(ReceiverPauseCommand.Resume(session));
        Check(ReceiverWireCodec.MessageType(line) == "resume"
            && ReceiverWireCodec.DecodeLine<ReceiverPauseCommand>(line).LeaseMilliseconds is null);
        Check(!line.Contains("leaseMilliseconds"), "Resume did not omit its optional lease.");
        var capabilities = ReceiverWireCodec.DecodeLine<ReceiverCapabilitiesAcknowledgement>(
            $$"""{"version":2,"type":"capabilitiesAck","sessionID":"{{session}}","capabilities":["input-state-v1","pause-lease-v1","menu-shortcuts-v1","player-checkpoint-v1"],"pauseLeaseMilliseconds":2000}""");
        Check(capabilities.SessionId == session && capabilities.Capabilities.Contains(ReceiverProtocol.PauseCapability));
        var id = Guid.NewGuid(); var ack = ReceiverWireCodec.DecodeLine<ReceiverPauseAcknowledgement>(
            $$"""{"version":2,"type":"pauseAck","sessionID":"{{session}}","commandID":"{{id}}","paused":true}""");
        Check(ack.CommandId == id && ack.Paused && ack.SessionId == session);
    }

    public static void TestCapabilityAdmission()
    {
        var session = Guid.NewGuid(); var controller = new ReceiverPauseController();
        foreach (var invalid in new[] { Capabilities(session) with { Version = 1 }, Capabilities(Guid.NewGuid()),
            Capabilities(session) with { Type = "hello" }, Capabilities(session) with { PauseLeaseMilliseconds = 0 },
            Capabilities(session) with { Capabilities = ["input-state-v1"] } })
        {
            controller.Negotiate(session, invalid);
            Check(controller.State == GamePauseState.Unavailable && controller.Request(true) is null && !controller.BlocksInput);
        }
        controller.Negotiate(session, Capabilities(session));
        Check(controller.State == GamePauseState.Ready && controller.Renew() is null);
        var request = controller.Request(true)!;
        Check(controller.State == GamePauseState.Pausing && controller.BlocksInput && !controller.Confirmed
            && request.LeaseMilliseconds == 2000 && request.SessionId == session);
        foreach (var invalid in new[] { Ack(Guid.NewGuid(), "pauseAck", true), Ack(session, "pauseAck", true) with { Version = 1 },
            Ack(session, "unknown", true) })
        { Check(controller.Consume(invalid) is null && controller.State == GamePauseState.Pausing); }
        controller.Consume(Ack(session, "pauseAck", true));
        Check(controller.State == GamePauseState.Paused && controller.Request(true) is null);
        var renewal = controller.Renew()!;
        Check(renewal.Type == "pause" && renewal.CommandId != request.CommandId && renewal.LeaseMilliseconds == 2000);
        Check(ReceiverPauseController.RenewalInterval == TimeSpan.FromMilliseconds(750));
    }

    public static void TestAcknowledgementRaces()
    {
        var session = Guid.NewGuid(); var controller = new ReceiverPauseController();
        controller.Negotiate(session, Capabilities(session)); controller.Request(true);
        Check(controller.Request(false)?.Type == "resume" && controller.State == GamePauseState.Resuming && controller.BlocksInput);
        Check(controller.Consume(Ack(session, "pauseAck", true))?.Type == "resume" && controller.State == GamePauseState.Resuming);
        Check(controller.Consume(Ack(session, "resumeAck", true))?.Type == "resume" && controller.BlocksInput);
        controller.Consume(Ack(session, "resumeAck", false));
        Check(controller.State == GamePauseState.Ready && !controller.BlocksInput && controller.Renew() is null);
        controller.Request(true);
        Check(controller.Consume(Ack(session, "resumeAck", false))?.Type == "pause" && controller.State == GamePauseState.Pausing);
        controller.Consume(Ack(session, "pauseAck", false));
        Check(controller.State == GamePauseState.Ready && !controller.Desired && !controller.BlocksInput, "Rejected pause left controls latched.");
        controller.Request(true); controller.Consume(Ack(session, "pauseAck", true)); controller.Disconnect();
        Check(controller.State == GamePauseState.Unavailable && !controller.BlocksInput && controller.Renew() is null);
        controller.Negotiate(Guid.NewGuid(), Capabilities(session)); Check(controller.State == GamePauseState.Unavailable);
        var next = Guid.NewGuid(); controller.Negotiate(next, Capabilities(next)); controller.Consume(Ack(session, "pauseAck", true));
        Check(controller.State == GamePauseState.Ready && !controller.Confirmed, "Old connection re-paused a new session.");
    }

    public static void TestFrozenFrameOwnership()
    {
        var session = new LabelCaptureSession(); Check(!session.CanToggle);
        session.Apply(GamePauseState.Ready); Check(!session.CanToggle);
        var first = Frame(1); Check(session.Accept(first, session.Revision) && session.CanToggle);
        var beforePause = session.Revision; session.Apply(GamePauseState.Pausing);
        Check(!session.CanToggle && session.SuspendsInference && !session.CanProcess(beforePause));
        var last = Frame(2); Check(session.Accept(last, session.Revision));
        session.Apply(GamePauseState.Paused);
        Check(session.IsFrozen && session.CanToggle && ReferenceEquals(session.FrozenFrame, last));
        var pausedRevision = session.Revision; Check(!session.Accept(Frame(3), pausedRevision) && !session.CanProcess(pausedRevision));
        session.Apply(GamePauseState.Paused); Check(session.Revision == pausedRevision, "Lease renewal changed the editor identity.");
        session.Apply(GamePauseState.Resuming); Check(!session.CanToggle && session.SuspendsInference && !session.IsFrozen);
        Check(session.Apply(GamePauseState.Ready) && !session.SuspendsInference && session.FrozenFrame is null);
        Check(!session.CanProcess(beforePause) && !session.Accept(Frame(4), pausedRevision), "Pre-pause work reappeared after resume.");
        Check(session.Accept(Frame(5), session.Revision) && !session.Apply(GamePauseState.Ready));
    }

    public static void TestNormalAndMissingFirstFrame()
    {
        var session = new LabelCaptureSession(); session.Reset(GamePauseState.Ready);
        var clean = Frame(1); session.Accept(clean, session.Revision);
        session.Apply(GamePauseState.Paused); Check(ReferenceEquals(clean, session.FrozenFrame) && session.IsFrozen);
        Check(session.Apply(GamePauseState.Ready), "Normal label completion did not trigger resume behavior.");
        session.Reset(GamePauseState.Paused); Check(session.FrozenFrame is null && !session.IsFrozen);
        var revision = session.Revision; Check(session.Accept(Frame(2), revision) && session.IsFrozen);
        Check(!session.CanProcess(revision) && !session.Accept(Frame(3), session.Revision), "First paused capture was overwritten.");
        session.Reset(GamePauseState.Unavailable);
        Check(!session.CanToggle && session.FrozenFrame is null && !session.SuspendsInference && !session.CanProcess(revision));
    }
}
