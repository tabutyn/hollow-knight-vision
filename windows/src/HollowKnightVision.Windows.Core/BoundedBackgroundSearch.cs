namespace HollowKnightVision.Windows.Core;

/// <summary>
/// One immutable search at a time. Submission never waits for the worker and
/// obsolete generations cannot publish. The caller owns cadence and snapshots.
/// </summary>
public sealed class BoundedBackgroundSearch<T>
{
    private readonly object sync = new();
    private ulong epoch;
    private bool busy;
    private bool hasCompleted;
    private T? completed;

    public bool Submit(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return SubmitPrepared(() => operation);
    }

    /// <summary>
    /// Reserve the sole worker slot before copying caller-owned state. Preparation
    /// runs synchronously on the submitting thread, only for accepted work; its
    /// returned operation runs on the worker against the immutable snapshot.
    /// </summary>
    public bool SubmitPrepared(Func<Func<T>> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ulong submittedEpoch;
        lock (sync)
        {
            if (busy)
            {
                return false;
            }
            busy = true;
            submittedEpoch = epoch;
        }

        Func<T> operation;
        try
        {
            operation = prepare() ?? throw new InvalidOperationException("Search preparation returned no operation.");
        }
        catch
        {
            lock (sync) busy = false;
            throw;
        }

        _ = Task.Run(() =>
        {
            T value;
            try
            {
                value = operation();
            }
            catch
            {
                lock (sync)
                {
                    busy = false;
                }
                throw;
            }

            lock (sync)
            {
                if (epoch == submittedEpoch)
                {
                    completed = value;
                    hasCompleted = true;
                }
                busy = false;
            }
        });
        return true;
    }

    public bool TryTake(out T? value)
    {
        lock (sync)
        {
            if (!hasCompleted)
            {
                value = default;
                return false;
            }
            value = completed;
            completed = default;
            hasCompleted = false;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (sync)
        {
            epoch = unchecked(epoch + 1);
            completed = default;
            hasCompleted = false;
        }
    }
}

/// <summary>
/// Compare corrections, rather than absolute poses, while the camera moves.
/// Exact-frame Hacker audits showed that sub-two-pixel proposals are mostly
/// descriptor noise, while unconfirmed two-to-eight-pixel proposals often
/// create more drift than they remove. Ignore the noise floor and require two
/// independently timed observations for every material correction.
/// </summary>
public struct GroundCorrectionConfirmation
{
    public const double Deadband = 2;
    private (CameraVector Correction, double Timestamp)? pending;

    public readonly bool NeedsConfirmation => pending is not null;

    public void Reset() => pending = null;

    public bool Accepts(CameraVector correction, double timestamp)
    {
        if (!double.IsFinite(correction.X)
            || !double.IsFinite(correction.Y)
            || !double.IsFinite(timestamp))
        {
            pending = null;
            return false;
        }

        var magnitude = Math.Sqrt(
            correction.X * correction.X + correction.Y * correction.Y);
        if (magnitude <= Deadband)
        {
            pending = null;
            return false;
        }

        var prior = pending;
        pending = (correction, timestamp);
        if (prior is null)
        {
            return false;
        }

        var elapsed = timestamp - prior.Value.Timestamp;
        var priorMagnitude = Math.Sqrt(
            prior.Value.Correction.X * prior.Value.Correction.X
                + prior.Value.Correction.Y * prior.Value.Correction.Y);
        var agreement = Math.Min(
            4,
            Math.Max(0.75, Math.Max(magnitude, priorMagnitude) * 0.25));
        var deltaX = correction.X - prior.Value.Correction.X;
        var deltaY = correction.Y - prior.Value.Correction.Y;
        return elapsed is >= 0.15 and <= 1.5
            && Math.Sqrt(deltaX * deltaX + deltaY * deltaY) <= agreement;
    }
}
