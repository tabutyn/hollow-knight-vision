namespace HollowKnightVision.Windows.Core;

/// <summary>Owns the clean frozen frame and invalidates work crossing a pause/resume boundary.</summary>
public sealed class LabelCaptureSession
{
    private readonly object gate = new();
    private GamePauseState state = GamePauseState.Unavailable;
    private BgraFrame? latest;
    private BgraFrame? frozen;
    private long revision;
    public GamePauseState State { get { lock (gate) return state; } }
    public BgraFrame? FrozenFrame { get { lock (gate) return frozen; } }
    public long Revision { get { lock (gate) return revision; } }
    public bool IsFrozen { get { lock (gate) return state == GamePauseState.Paused && frozen is not null; } }
    public bool CanToggle { get { lock (gate) return state == GamePauseState.Paused || state == GamePauseState.Ready && latest is not null; } }
    public bool SuspendsInference { get { lock (gate) return state is GamePauseState.Pausing or GamePauseState.Paused or GamePauseState.Resuming; } }

    public bool Apply(GamePauseState next)
    {
        lock (gate)
        {
            if (state == next) return false;
            var completed = next == GamePauseState.Ready && state is GamePauseState.Paused or GamePauseState.Resuming;
            state = next; revision++;
            if (next == GamePauseState.Paused) frozen ??= latest;
            else if (next is GamePauseState.Ready or GamePauseState.Unavailable) frozen = null;
            return completed;
        }
    }
    public bool Accept(BgraFrame frame, long expectedRevision)
    {
        lock (gate)
        {
            if (revision != expectedRevision || state == GamePauseState.Paused && frozen is not null) return false;
            latest = frame;
            if (state == GamePauseState.Paused) { frozen = frame; revision++; }
            return true;
        }
    }
    public bool CanProcess(long expectedRevision)
    { lock (gate) return revision == expectedRevision && !(state == GamePauseState.Paused && frozen is not null); }
    public void Reset(GamePauseState next)
    { lock (gate) { latest = frozen = null; state = next; revision++; } }
}
