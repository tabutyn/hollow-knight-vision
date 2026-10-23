using System.Windows.Threading;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

/// <summary>One queued UI update, no blocking dispatcher calls, and no output from a previous run after Stop/Begin.</summary>
public sealed class TrainingUiOutputPump(Dispatcher dispatcher, Action<TrainingOutputSnapshot> apply)
{
    private readonly object gate = new();
    private long generation;
    private long lastSequence;
    private bool queued;
    private TrainingOutputSnapshot? pending;
    public long Begin() { lock (gate) { pending = null; lastSequence = 0; return ++generation; } }
    public void Stop() { lock (gate) { generation++; pending = null; } }
    public void Post(long token, TrainingOutputSnapshot snapshot)
    {
        lock (gate)
        {
            if (token != generation || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            if (snapshot.Sequence <= lastSequence) return;
            lastSequence = snapshot.Sequence; pending = snapshot;
            if (queued) return; queued = true;
            try { _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain)); }
            catch (InvalidOperationException) { queued = false; pending = null; }
        }
    }
    private void Drain()
    {
        TrainingOutputSnapshot? snapshot;
        lock (gate) { snapshot = pending; pending = null; queued = false; }
        if (snapshot is not null) apply(snapshot);
    }
}
