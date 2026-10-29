namespace HollowKnightVision.Windows.App;

/// <summary>Bounded latest-frame work plus ordered, lossless persistence commands.</summary>
internal sealed class SerialWorldWorker
{
    private readonly object gate = new();
    private readonly Queue<Action> commands = [];
    private Action? latest;
    private bool running;
    internal Exception? LastError { get; private set; }

    internal void Submit(Action work)
    {
        lock (gate) { latest = work; StartLocked(); }
    }

    internal Task<T> Exclusive<T>(Func<T> operation, bool discardPending = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (discardPending) latest = null;
            commands.Enqueue(() => {
                try { completion.TrySetResult(operation()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            StartLocked();
        }
        return completion.Task;
    }

    internal Task DrainAsync() => Exclusive(() => true, discardPending: true);

    private void StartLocked()
    {
        if (running) return;
        running = true;
        _ = Task.Run(() => {
            while (true)
            {
                Action? work;
                lock (gate)
                {
                    if (commands.Count > 0) work = commands.Dequeue();
                    else { work = latest; latest = null; }
                    if (work is null) { running = false; return; }
                }
                try { work(); }
                catch (Exception error) { LastError = error; }
            }
        });
    }
}
