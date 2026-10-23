namespace HollowKnightVision.Windows.Core;

public enum GamePauseState { Unavailable, Ready, Pausing, Paused, Resuming }

/// <summary>Mac pause-lease state machine. Owner serializes access and sends returned commands.</summary>
public sealed class ReceiverPauseController
{
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromMilliseconds(750);
    public GamePauseState State { get; private set; } = GamePauseState.Unavailable;
    public bool Desired { get; private set; }
    public bool Confirmed { get; private set; }
    public bool BlocksInput => Desired || Confirmed || State == GamePauseState.Resuming;
    private Guid? session;
    private int leaseMilliseconds;

    public void Negotiate(Guid expectedSession, ReceiverCapabilitiesAcknowledgement acknowledgement)
    {
        Disconnect();
        if (acknowledgement.Version != ReceiverProtocol.ControlVersion || acknowledgement.Type != "capabilitiesAck"
            || acknowledgement.SessionId != expectedSession || acknowledgement.PauseLeaseMilliseconds <= 0
            || !acknowledgement.Capabilities.Contains(ReceiverProtocol.PauseCapability)) return;
        session = expectedSession; leaseMilliseconds = acknowledgement.PauseLeaseMilliseconds;
        State = GamePauseState.Ready;
    }
    public ReceiverPauseCommand? Request(bool active)
    {
        if (session is null || Desired == active && Confirmed == active) return null;
        Desired = active; State = active ? GamePauseState.Pausing : GamePauseState.Resuming;
        return Command(active);
    }
    public ReceiverPauseCommand? Renew() => Desired ? Command(true) : null;
    public ReceiverPauseCommand? Consume(ReceiverPauseAcknowledgement acknowledgement)
    {
        if (session is null || acknowledgement.Version != ReceiverProtocol.ControlVersion
            || acknowledgement.SessionId != session || acknowledgement.Type is not ("pauseAck" or "resumeAck")) return null;
        Confirmed = acknowledgement.Paused;
        if (acknowledgement.Type == "pauseAck")
        {
            if (Desired && Confirmed) State = GamePauseState.Paused;
            else if (!Desired) { State = GamePauseState.Resuming; return Command(false); }
            else { Desired = false; State = GamePauseState.Ready; }
        }
        else if (Desired) { State = GamePauseState.Pausing; return Command(true); }
        else if (Confirmed) { State = GamePauseState.Resuming; return Command(false); }
        else State = GamePauseState.Ready;
        return null;
    }
    private ReceiverPauseCommand? Command(bool pause) => session is { } id
        ? pause ? ReceiverPauseCommand.Pause(id, leaseMilliseconds) : ReceiverPauseCommand.Resume(id) : null;
    public void Disconnect()
    { session = null; leaseMilliseconds = 0; Desired = Confirmed = false; State = GamePauseState.Unavailable; }
}
