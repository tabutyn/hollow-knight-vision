namespace HollowKnightVision.Windows.Core;

/// <summary>Identity of a pose published by the registration owner.</summary>
public readonly record struct FeatureRefinementRequest(ulong Generation, double Timestamp);

public readonly record struct FeatureCorrectionSnapshot(
    ulong Generation,
    CameraVector CumulativeOffset);

/// <summary>
/// Tracks global landmark corrections independently from registration motion.
/// The vision queue owns camera mutation; the landmark worker only reads this
/// locked ledger before it starts a queued job.
/// </summary>
public sealed class FeatureCorrectionPolicy
{
    private readonly object sync = new();
    private ulong generation;
    private CameraVector cumulativeOffset = CameraVector.Zero;
    private double? lastAppliedTimestamp;

    public FeatureCorrectionPolicy(ulong generation = 0)
    {
        this.generation = generation;
    }

    /// <summary>
    /// Begins a new capture generation. Its first registration already
    /// contains every correction that preceded the reset, so the per-generation
    /// offset starts from zero.
    /// </summary>
    public void BeginGeneration(ulong newGeneration)
    {
        lock (sync)
        {
            if (newGeneration < generation)
            {
                return;
            }
            generation = newGeneration;
            cumulativeOffset = CameraVector.Zero;
            lastAppliedTimestamp = null;
        }
    }

    public FeatureCorrectionSnapshot? CaptureSnapshot(FeatureRefinementRequest request)
    {
        lock (sync)
        {
            if (request.Generation != generation)
            {
                return null;
            }
            return new FeatureCorrectionSnapshot(generation, cumulativeOffset);
        }
    }

    /// <summary>
    /// Rebase a pose captured before an earlier slow refinement completed.
    /// This keeps the persistent tracker in the same world coordinates as the
    /// camera accumulator without reapplying corrections already in the pose.
    /// </summary>
    public AtlasPoint RebasedPosition(
        AtlasPoint capturedPosition,
        FeatureCorrectionSnapshot snapshot)
    {
        lock (sync)
        {
            if (snapshot.Generation != generation)
            {
                return capturedPosition;
            }
            return new AtlasPoint(
                capturedPosition.X + cumulativeOffset.X - snapshot.CumulativeOffset.X,
                capturedPosition.Y + cumulativeOffset.Y - snapshot.CumulativeOffset.Y);
        }
    }

    /// <summary>
    /// Returns true exactly once for a finite correction from the active
    /// generation whose observation is newer than the last applied correction.
    /// </summary>
    public bool Apply(CameraVector correction, FeatureRefinementRequest request) =>
        Apply(correction, request, static () => { });

    /// <summary>
    /// Runs <paramref name="onAcceptance"/> while the generation lock is held.
    /// The vision queue uses this to move the accumulator and tracking state as
    /// one atomic operation with <see cref="BeginGeneration"/>, so an old worker
    /// cannot leak a correction into a newly started capture generation.
    /// </summary>
    public bool Apply(
        CameraVector correction,
        FeatureRefinementRequest request,
        Action onAcceptance)
    {
        ArgumentNullException.ThrowIfNull(onAcceptance);
        if (!double.IsFinite(correction.X) || !double.IsFinite(correction.Y))
        {
            return false;
        }

        lock (sync)
        {
            if (request.Generation != generation
                || lastAppliedTimestamp is { } appliedAt && request.Timestamp <= appliedAt)
            {
                return false;
            }
            cumulativeOffset = new CameraVector(
                cumulativeOffset.X + correction.X,
                cumulativeOffset.Y + correction.Y);
            lastAppliedTimestamp = request.Timestamp;
            onAcceptance();
            return true;
        }
    }
}
