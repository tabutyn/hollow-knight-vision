namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Uses global key state only after Windows has positively reported a key held.
/// Local key events can still arrive when global polling is unavailable;
/// treating that unavailable state as released would turn every press into a tap.
/// </summary>
public sealed class PhysicalKeyReconciler
{
    private readonly HashSet<int> globallyObservedKeyCodes = [];

    public IReadOnlySet<int> GloballyObservedKeyCodes => globallyObservedKeyCodes;

    public IReadOnlySet<int> ReleasedKeyCodes(
        IReadOnlySet<int> heldKeyCodes,
        Func<int, bool> isPhysicallyHeld)
    {
        ArgumentNullException.ThrowIfNull(heldKeyCodes);
        ArgumentNullException.ThrowIfNull(isPhysicallyHeld);

        globallyObservedKeyCodes.IntersectWith(heldKeyCodes);
        HashSet<int> released = [];
        foreach (var keyCode in heldKeyCodes)
        {
            if (isPhysicallyHeld(keyCode))
            {
                globallyObservedKeyCodes.Add(keyCode);
            }
            else if (globallyObservedKeyCodes.Remove(keyCode))
            {
                released.Add(keyCode);
            }
        }
        return released;
    }

    public void NoteLocalRelease(int keyCode) => globallyObservedKeyCodes.Remove(keyCode);

    public void Reset() => globallyObservedKeyCodes.Clear();
}

/// <summary>
/// Owns the sender's held state. Every mutation produces an ordered complete
/// receiver snapshot; callers can additionally call <see cref="Heartbeat"/>.
/// </summary>
public sealed class InputBridgeSenderState
{
    private ulong nextSequence;

    public InputBridgeSenderState(Guid? sessionId = null)
    {
        SessionId = sessionId ?? Guid.NewGuid();
    }

    public Guid SessionId { get; private set; }
    public InputButtons HeldButtons { get; private set; } = InputButtons.None;
    public bool IsEnabled { get; private set; }

    public ReceiverInputSnapshot BeginNewSession(Guid? sessionId = null)
    {
        SessionId = sessionId ?? Guid.NewGuid();
        HeldButtons = InputButtons.None;
        IsEnabled = false;
        nextSequence = 0;
        return MakeSnapshot();
    }

    public ReceiverInputSnapshot SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        if (!enabled)
        {
            HeldButtons = InputButtons.None;
        }
        return MakeSnapshot();
    }

    /// <summary>
    /// Starts a newly focused control session with the first real press already
    /// held, avoiding an enabled-but-neutral tick before input reaches the game.
    /// </summary>
    public ReceiverInputSnapshot BeginFreshPress(InputButtons button)
    {
        IsEnabled = true;
        HeldButtons = button;
        return MakeSnapshot();
    }

    public ReceiverInputSnapshot? SetHeld(InputButtons button, bool isHeld)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var updated = isHeld ? HeldButtons | button : HeldButtons & ~button;
        if (updated == HeldButtons)
        {
            return null;
        }
        HeldButtons = updated;
        return MakeSnapshot();
    }

    public ReceiverInputSnapshot Heartbeat() => MakeSnapshot();

    public ReceiverInputSnapshot ReleaseAll()
    {
        HeldButtons = InputButtons.None;
        IsEnabled = false;
        return MakeSnapshot();
    }

    private ReceiverInputSnapshot MakeSnapshot()
    {
        var snapshot = ReceiverInputSnapshot.Create(
            SessionId,
            nextSequence,
            IsEnabled,
            IsEnabled ? HeldButtons : InputButtons.None);
        nextSequence = unchecked(nextSequence + 1);
        return snapshot;
    }
}
