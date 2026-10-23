namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Tracks only state-changing snapshots that the receiver must acknowledge.
/// Heartbeats are deliberately excluded: they keep the receiver watchdog fed,
/// but cannot make the UI appear responsive when no input was applied.
/// </summary>
public sealed class InputAcknowledgementTracker
{
    public enum ResultKind
    {
        Matched,
        IgnoredIncompatible,
        IgnoredOldSession,
        IgnoredUnknownSequence,
        RejectedStateMismatch
    }

    public readonly record struct Result(ResultKind Kind, double? Latency = null)
    {
        public static Result Matched(double latency) => new(ResultKind.Matched, latency);
        public static Result IgnoredIncompatible => new(ResultKind.IgnoredIncompatible);
        public static Result IgnoredOldSession => new(ResultKind.IgnoredOldSession);
        public static Result IgnoredUnknownSequence => new(ResultKind.IgnoredUnknownSequence);
        public static Result RejectedStateMismatch => new(ResultKind.RejectedStateMismatch);
    }

    private readonly record struct EffectiveState(bool Enabled, InputButtons Buttons)
    {
        internal static EffectiveState From(bool enabled, InputButtons buttons) =>
            new(enabled, enabled ? Normalize(buttons) : InputButtons.None);

        private static InputButtons Normalize(InputButtons buttons)
        {
            if ((buttons & (InputButtons.Left | InputButtons.Right))
                == (InputButtons.Left | InputButtons.Right))
            {
                buttons &= ~(InputButtons.Left | InputButtons.Right);
            }
            if ((buttons & (InputButtons.Up | InputButtons.Down))
                == (InputButtons.Up | InputButtons.Down))
            {
                buttons &= ~(InputButtons.Up | InputButtons.Down);
            }
            return buttons;
        }
    }

    private readonly record struct Pending(EffectiveState State, double SentAt);

    private readonly int maximumOutstanding;
    private readonly int maximumLatencySamples;
    private readonly Dictionary<ulong, Pending> pending = [];
    private readonly List<ulong> pendingOrder = [];
    private readonly List<double> latencies = [];
    private EffectiveState? lastSentState;

    public InputAcknowledgementTracker(
        int maximumOutstanding = 64,
        int maximumLatencySamples = 120)
    {
        this.maximumOutstanding = Math.Max(1, maximumOutstanding);
        this.maximumLatencySamples = Math.Max(1, maximumLatencySamples);
    }

    public Guid? SessionId { get; private set; }
    public int OutstandingCount => pending.Count;

    public double? P95Latency
    {
        get
        {
            if (latencies.Count == 0) return null;
            var ordered = latencies.Order().ToArray();
            var index = (int)Math.Ceiling(ordered.Length * 0.95) - 1;
            return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
        }
    }

    public void BeginSession(Guid sessionId)
    {
        SessionId = sessionId;
        lastSentState = null;
        pending.Clear();
        pendingOrder.Clear();
        latencies.Clear();
    }

    /// <summary>
    /// Returns true only when this snapshot represents an acknowledgement
    /// worth measuring. The initial neutral state and every enabled/held-state
    /// transition are included; repeated heartbeats are not.
    /// </summary>
    public bool RecordSent(ReceiverInputSnapshot snapshot, double sentAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (SessionId != snapshot.SessionId) BeginSession(snapshot.SessionId);
        var state = EffectiveState.From(snapshot.Enabled, snapshot.HeldButtons);
        if (state == lastSentState) return false;
        lastSentState = state;
        if (pending.ContainsKey(snapshot.Sequence))
        {
            pendingOrder.RemoveAll(value => value == snapshot.Sequence);
        }
        pending[snapshot.Sequence] = new Pending(state, sentAt);
        pendingOrder.Add(snapshot.Sequence);
        while (pendingOrder.Count > maximumOutstanding)
        {
            var sequence = pendingOrder[0];
            pendingOrder.RemoveAt(0);
            pending.Remove(sequence);
        }
        return true;
    }

    public Result Receive(ReceiverInputAcknowledgement acknowledgement, double receivedAt)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.Type != "ack"
            || acknowledgement.Version != ReceiverProtocol.InputVersion)
        {
            return Result.IgnoredIncompatible;
        }
        if (acknowledgement.SessionId != SessionId) return Result.IgnoredOldSession;
        if (!pending.Remove(acknowledgement.Sequence, out var expected))
        {
            return Result.IgnoredUnknownSequence;
        }
        pendingOrder.RemoveAll(value => value == acknowledgement.Sequence);
        var applied = EffectiveState.From(
            acknowledgement.Enabled,
            acknowledgement.AppliedButtons);
        if (expected.State != applied) return Result.RejectedStateMismatch;
        var latency = Math.Max(0, receivedAt - expected.SentAt);
        latencies.Add(latency);
        if (latencies.Count > maximumLatencySamples)
        {
            latencies.RemoveRange(0, latencies.Count - maximumLatencySamples);
        }
        return Result.Matched(latency);
    }
}
