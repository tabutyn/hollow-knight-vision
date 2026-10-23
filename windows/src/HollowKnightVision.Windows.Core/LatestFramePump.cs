namespace HollowKnightVision.Windows.Core;

/// <summary>One active ticket plus one replaceable pending frame, with epoch-safe publication.</summary>
public sealed class LatestFramePump<T>(ulong epoch = 0, Action<T>? discard = null)
{
    public sealed record Ticket(ulong Id, ulong Epoch, T Item);
    public sealed record Completion(bool ShouldAcceptResult, Ticket? Next);
    public readonly record struct Statistics(ulong Submitted = 0, ulong Started = 0, ulong Completed = 0,
        ulong Superseded = 0, ulong Cancelled = 0, ulong StaleSubmissions = 0, ulong StaleCompletions = 0);
    private readonly object gate = new();
    private ulong currentEpoch = epoch, nextId;
    private Ticket? active, pending;
    private Statistics counters;
    public Statistics Counters { get { lock (gate) return counters; } }
    public bool IsProcessing { get { lock (gate) return active is not null; } }
    public int PendingCount { get { lock (gate) return pending is null ? 0 : 1; } }
    public Ticket? Submit(T item, ulong epoch)
    {
        lock (gate)
        {
            if (epoch != currentEpoch) { counters = counters with { StaleSubmissions = counters.StaleSubmissions + 1 }; return null; }
            counters = counters with { Submitted = counters.Submitted + 1 };
            var ticket = new Ticket(nextId++, epoch, item);
            if (active is null) { active = ticket; counters = counters with { Started = counters.Started + 1 }; return ticket; }
            if (pending is not null) { counters = counters with { Superseded = counters.Superseded + 1 }; discard?.Invoke(pending.Item); }
            pending = ticket; return null;
        }
    }
    public Completion Complete(Ticket ticket)
    {
        lock (gate)
        {
            if (active?.Id != ticket.Id) return new(false, null);
            var accepted = ticket.Epoch == currentEpoch;
            counters = counters with { Completed = counters.Completed + 1, StaleCompletions = counters.StaleCompletions + (accepted ? 0UL : 1UL) };
            active = null;
            if (pending is null || pending.Epoch != currentEpoch) { ClearPendingLocked(); return new(accepted, null); }
            active = pending; pending = null; counters = counters with { Started = counters.Started + 1 }; return new(accepted, active);
        }
    }
    public void Invalidate(ulong epoch)
    { lock (gate) { if (epoch < currentEpoch) return; currentEpoch = epoch; ClearPendingLocked(); } }
    public void ClearPending() { lock (gate) ClearPendingLocked(); }
    private void ClearPendingLocked()
    {
        if (pending is not null) { counters = counters with { Cancelled = counters.Cancelled + 1 }; discard?.Invoke(pending.Item); }
        pending = null;
    }
}
