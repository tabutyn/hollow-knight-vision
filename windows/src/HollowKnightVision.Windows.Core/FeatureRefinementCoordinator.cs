namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Runs expensive persistent-landmark work behind a one-item latest queue.
/// Results return on the owner scheduler in strictly increasing request order
/// within a capture generation. The caller may apply a valid global correction
/// to a newer camera pose, but must never use delayed work to stamp old pixels.
/// </summary>
public sealed class FeatureRefinementCoordinator<TOutput>
{
    private sealed record Job(
        FeatureRefinementRequest Request,
        Func<TOutput> Work,
        Action<TOutput> Receive);

    private readonly TaskScheduler workerScheduler;
    private readonly TaskScheduler ownerScheduler;
    private readonly object gate = new();
    private ulong invalidBeforeGeneration;
    private FeatureRefinementRequest? lastDelivered;
    private bool inFlight;
    private Job? pending;
    private Action? pendingReset;

    public FeatureRefinementCoordinator(
        TaskScheduler ownerScheduler,
        TaskScheduler? workerScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(ownerScheduler);
        this.ownerScheduler = ownerScheduler;
        this.workerScheduler = workerScheduler
            ?? new ConcurrentExclusiveSchedulerPair(
                TaskScheduler.Default,
                maxConcurrencyLevel: 1).ExclusiveScheduler;
    }

    /// <summary>
    /// Records that registration has advanced. Delivery deliberately does not
    /// require this exact request to remain current: a slow landmark solve may
    /// still provide a valid global correction for the current generation.
    /// </summary>
    public void DidPublishRegistration(FeatureRefinementRequest request)
    {
        // Kept as an explicit registration boundary for callers. The worker
        // owns ordering; the correction policy owns whether a result can move
        // the current camera.
    }

    /// <summary>
    /// Keeps at most one queued refinement. A busy worker finishes its current
    /// item, then skips directly to the latest observation.
    /// </summary>
    public void Submit(
        FeatureRefinementRequest request,
        Func<TOutput> work,
        Action<TOutput> receive)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(receive);
        var job = new Job(request, work, receive);
        lock (gate)
        {
            if (request.Generation < invalidBeforeGeneration)
            {
                return;
            }
            if (inFlight)
            {
                pending = job;
                return;
            }
            inFlight = true;
        }
        Run(job);
    }

    /// <summary>
    /// Invalidates both completed and queued corrections from an old capture
    /// generation. The caller owns tracker state and may reset it separately.
    /// </summary>
    public void Invalidate(ulong beforeGeneration)
    {
        lock (gate)
        {
            invalidBeforeGeneration = Math.Max(
                invalidBeforeGeneration,
                beforeGeneration);
            if ((lastDelivered?.Generation ?? 0) < beforeGeneration)
            {
                lastDelivered = null;
            }
            if ((pending?.Request.Generation ?? 0) < beforeGeneration)
            {
                pending = null;
            }
        }
    }

    /// <summary>
    /// Serializes mutable tracker resets behind any active refinement. A reset
    /// is a barrier: retained newest work starts only after it completes.
    /// </summary>
    public void SubmitReset(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Action reset;
        lock (gate)
        {
            pendingReset = work;
            if (inFlight)
            {
                return;
            }
            inFlight = true;
            reset = pendingReset;
            pendingReset = null;
        }
        RunReset(reset);
    }

    private void Run(Job job)
    {
        _ = Task.Factory.StartNew(
            () =>
            {
                var output = job.Work();
                _ = Task.Factory.StartNew(
                    () => Deliver(job, output),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    ownerScheduler);
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            workerScheduler);
    }

    private void RunReset(Action reset)
    {
        _ = Task.Factory.StartNew(
            () =>
            {
                reset();
                _ = Task.Factory.StartNew(
                    FinishReset,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    ownerScheduler);
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            workerScheduler);
    }

    private void Deliver(Job job, TOutput output)
    {
        bool accepts;
        lock (gate)
        {
            accepts = job.Request.Generation >= invalidBeforeGeneration
                && IsMonotonicallyNewer(job.Request, lastDelivered);
            if (accepts)
            {
                lastDelivered = job.Request;
            }
        }
        if (accepts)
        {
            job.Receive(output);
        }

        Action? reset;
        Job? next;
        lock (gate)
        {
            if (pendingReset is not null)
            {
                reset = pendingReset;
                pendingReset = null;
                next = null;
            }
            else
            {
                reset = null;
                next = pending;
                pending = null;
                if (next is null)
                {
                    inFlight = false;
                }
            }
        }
        if (reset is not null)
        {
            RunReset(reset);
        }
        else if (next is not null)
        {
            Run(next);
        }
    }

    private void FinishReset()
    {
        Action? reset;
        Job? next;
        lock (gate)
        {
            if (pendingReset is not null)
            {
                reset = pendingReset;
                pendingReset = null;
                next = null;
            }
            else
            {
                reset = null;
                next = pending;
                pending = null;
                if (next is null)
                {
                    inFlight = false;
                }
            }
        }
        if (reset is not null)
        {
            RunReset(reset);
        }
        else if (next is not null)
        {
            Run(next);
        }
    }

    private static bool IsMonotonicallyNewer(
        FeatureRefinementRequest request,
        FeatureRefinementRequest? previous)
    {
        if (previous is null)
        {
            return true;
        }
        if (request.Generation != previous.Value.Generation)
        {
            return request.Generation > previous.Value.Generation;
        }
        return request.Timestamp > previous.Value.Timestamp;
    }
}
