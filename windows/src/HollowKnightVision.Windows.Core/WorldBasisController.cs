namespace HollowKnightVision.Windows.Core;

public enum WorldBasisTrackingState
{
    Tracking,
    Recovering,
}

public readonly record struct WorldBasisObservationTicket(
    ulong CaptureGeneration,
    ulong LocalEpoch,
    ulong BasisRevision,
    double CaptureTimestamp,
    AtlasPoint LocalPose);

public readonly record struct WorldBasisSnapshot(
    WorldBasisTrackingState TrackingState,
    ulong CaptureGeneration,
    ulong LocalEpoch,
    ulong BasisRevision,
    AtlasPoint WorldFromLocal,
    double? LatestCaptureTimestamp,
    AtlasPoint? LatestLocalPose,
    AtlasPoint? LatestWorldPose);

public readonly record struct WorldBasisResolvedObservation(
    WorldBasisObservationTicket Ticket,
    AtlasPoint WorldPose);

/// <summary>
/// Keeps a translation-only mapping from a short-lived local tracker into the
/// persistent world. All reads and transitions are serialized by one lock so
/// delayed matcher results cannot partially apply an obsolete basis.
/// </summary>
public sealed class WorldBasisController
{
    private readonly object sync = new();
    private WorldBasisTrackingState state = WorldBasisTrackingState.Recovering;
    private ulong generation;
    private ulong epoch;
    private ulong revision;
    private AtlasPoint basis;
    private (AtlasPoint Pose, double Timestamp)? latestLocal;
    private double? lastPlacementTimestamp;

    /// <summary>
    /// Begins a fresh capture generation. A reset intentionally drops the old
    /// basis; only a local-tracking loss retains it for recovery.
    /// </summary>
    public ulong BeginCapture(AtlasPoint worldFromLocal = default)
    {
        lock (sync)
        {
            generation = unchecked(generation + 1);
            epoch = 0;
            revision = unchecked(revision + 1);
            basis = worldFromLocal;
            state = WorldBasisTrackingState.Tracking;
            latestLocal = null;
            lastPlacementTimestamp = null;
            return generation;
        }
    }

    public ulong ResetCapture(AtlasPoint worldFromLocal = default) =>
        BeginCapture(worldFromLocal);

    /// <summary>
    /// Records a local observation without changing recovery state. Recovery
    /// ends only after a confirmed placement against the committed world.
    /// </summary>
    public WorldBasisObservationTicket? Observe(
        AtlasPoint localPose,
        double captureTimestamp)
    {
        if (!IsFinite(localPose) || !double.IsFinite(captureTimestamp))
        {
            return null;
        }

        lock (sync)
        {
            var ticket = new WorldBasisObservationTicket(
                generation,
                epoch,
                revision,
                captureTimestamp,
                localPose);
            if (latestLocal is null || captureTimestamp >= latestLocal.Value.Timestamp)
            {
                latestLocal = (localPose, captureTimestamp);
            }
            return ticket;
        }
    }

    /// <summary>
    /// Invalidates local coordinates while retaining the last known world basis.
    /// </summary>
    public void LoseLocalTracking()
    {
        lock (sync)
        {
            if (state != WorldBasisTrackingState.Tracking)
            {
                return;
            }
            state = WorldBasisTrackingState.Recovering;
            epoch = unchecked(epoch + 1);
            latestLocal = null;
        }
    }

    /// <summary>
    /// Starts a new room-local coordinate epoch without invalidating the
    /// capture generation. Outstanding tickets from the prior room become
    /// unusable, while the caller can continue displaying the persistent atlas.
    /// </summary>
    public void BeginLocalEpoch(AtlasPoint worldFromLocal = default)
    {
        if (!IsFinite(worldFromLocal))
        {
            return;
        }

        lock (sync)
        {
            epoch = unchecked(epoch + 1);
            revision = unchecked(revision + 1);
            basis = worldFromLocal;
            state = WorldBasisTrackingState.Tracking;
            latestLocal = null;
            lastPlacementTimestamp = null;
        }
    }

    /// <summary>
    /// Applies a placement only when its complete capture identity is current.
    /// The ticket's local pose is the historical pose used to derive the new
    /// basis; the newest local pose is then remapped through that basis.
    /// </summary>
    public bool ConfirmPlacement(
        WorldBasisObservationTicket ticket,
        AtlasPoint matchedWorldPose)
    {
        if (!IsFinite(matchedWorldPose))
        {
            return false;
        }

        lock (sync)
        {
            if (ticket.CaptureGeneration != generation
                || ticket.LocalEpoch != epoch
                || ticket.BasisRevision != revision
                || !double.IsFinite(ticket.CaptureTimestamp)
                || lastPlacementTimestamp is { } placedAt
                    && ticket.CaptureTimestamp < placedAt)
            {
                return false;
            }

            basis = new AtlasPoint(
                matchedWorldPose.X - ticket.LocalPose.X,
                matchedWorldPose.Y - ticket.LocalPose.Y);
            revision = unchecked(revision + 1);
            lastPlacementTimestamp = ticket.CaptureTimestamp;
            state = WorldBasisTrackingState.Tracking;
            return true;
        }
    }

    /// <summary>
    /// Maps queued work through the latest basis when it remains in this local
    /// coordinate epoch. A basis revision mismatch is deliberately rebaseable.
    /// </summary>
    public AtlasPoint? RebasedWorldPose(WorldBasisObservationTicket ticket) =>
        RebasedObservation(ticket)?.WorldPose;

    /// <summary>
    /// Refreshes a queued ticket to the authoritative basis revision in the
    /// same atomic read that maps its local pose.
    /// </summary>
    public WorldBasisResolvedObservation? RebasedObservation(
        WorldBasisObservationTicket ticket)
    {
        lock (sync)
        {
            if (ticket.CaptureGeneration != generation || ticket.LocalEpoch != epoch)
            {
                return null;
            }
            var refreshed = new WorldBasisObservationTicket(
                generation,
                epoch,
                revision,
                ticket.CaptureTimestamp,
                ticket.LocalPose);
            return new WorldBasisResolvedObservation(
                refreshed,
                Add(ticket.LocalPose, basis));
        }
    }

    public WorldBasisSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return new WorldBasisSnapshot(
                    state,
                    generation,
                    epoch,
                    revision,
                    basis,
                    latestLocal?.Timestamp,
                    latestLocal?.Pose,
                    latestLocal is { } latest ? Add(latest.Pose, basis) : null);
            }
        }
    }

    public WorldBasisTrackingState TrackingState => Snapshot.TrackingState;

    public AtlasPoint? LatestWorldPose => Snapshot.LatestWorldPose;

    private static AtlasPoint Add(AtlasPoint left, AtlasPoint right) =>
        new(left.X + right.X, left.Y + right.Y);

    private static bool IsFinite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}
