namespace HollowKnightVision.Windows.Core;

/// <summary>Owns one Gym episode's visual pose graph and its committed coverage.</summary>
internal sealed class BotGymAtlasCoverage
{
    private const double ObservationInterval = 1 / 7.5;
    private readonly LiveWorldTracker world = new();
    private readonly BotCommittedAtlasCoverage coverage = new();
    private readonly BotVisualAtlasCoverage localCoverage = new();
    private int? room;
    private ulong epoch;
    private int? localIslandId;
    private AtlasPoint basis;
    private AtlasPoint? lastPose;
    private AtlasPoint? lastTrustedPose;
    private IReadOnlyList<StencilRect> lastMasks = [];
    private double lastScanAt = double.NegativeInfinity;
    private bool awaitingPlacement;
    private bool requiresMatch;
    private bool open;

    internal bool HasPlacedRoom { get; private set; }
    internal bool CanContinueMeasuredScenery => HasPlacedRoom && open;
    internal bool PlacementPending => awaitingPlacement;
    internal LiveWorldSnapshot Snapshot => world.Snapshot;

    internal void Interrupt()
    {
        if (!open) return;
        BeginVisit();
        awaitingPlacement = HasPlacedRoom;
    }

    internal void EnterRoom(int roomId)
    {
        if (room == roomId) return;
        BeginVisit();
        room = roomId;
        HasPlacedRoom = world.HasRecoveryReference(roomId);
        awaitingPlacement = HasPlacedRoom;
        requiresMatch = HasPlacedRoom;
        lastTrustedPose = null;
        basis = default;
    }

    internal void Observe(BgraFrame frame, AtlasPoint? pose, double time, IReadOnlyList<PixelRect> omissions,
        VisualRoomCompositionBounds bounds, GroundHypothesisTrackingResult tracking, bool admitsPlacement,
        bool continuesMeasuredScenery = false)
    {
        var correction = tracking.GlobalCorrection;
        if (correction is { } shift && shift.X * shift.X + shift.Y * shift.Y > 9)
        {
            Interrupt();
            requiresMatch = HasPlacedRoom;
        }
        else if (pose is null)
            Interrupt();
        if (room is not { } roomId || pose is not { } local || !admitsPlacement
            || !(GroundMotionEvidence.IsMeasured(tracking) || continuesMeasuredScenery && CanContinueMeasuredScenery)
            || time - lastScanAt < ObservationInterval)
            return;

        var size = new WorldSize(frame.Width, frame.Height);
        var masks = omissions.Select(rect => new StencilRect(rect.X, rect.Y, rect.Width, rect.Height))
            .Concat(VisualRoomCompositionMask.OmittedRects(bounds, local, size.Width, size)).ToArray();
        if (!awaitingPlacement && lastPose == local && masks.SequenceEqual(lastMasks)) return;

        var recovering = awaitingPlacement;
        // The same visually confirmed room can return at its last measured
        // pose after a fade, even when its texture cannot form a keyframe.
        if (awaitingPlacement && !requiresMatch && local == lastTrustedPose)
            awaitingPlacement = false;

        // The camera matcher and committed grid use frame centers. The
        // local ground tracker supplies the image's lower-left origin.
        var center = Center(local, size);
        var placed = new AtlasPoint(center.X + basis.X, center.Y + basis.Y);
        var update = world.Ingest(world.Snapshot.Observations.Count, time, frame, center, size.Width,
            exclusions: masks, searchForClosure: awaitingPlacement, localPose: center, captureGeneration: 1,
            localEpoch: epoch, roomId: roomId, placedCameraPose: placed,
            captureEvidence: new(time) { HasMeasuredSceneryContinuity = continuesMeasuredScenery },
            placementPending: awaitingPlacement);
        lastPose = local;
        lastMasks = masks;
        lastScanAt = time;
        open = true;

        // The confirmation can shift the complete recovery island. Every
        // observation in it, including this frame, is recovery evidence.
        var credit = !recovering && correction is null or { X: 0, Y: 0 };
        coverage.Update(update.Snapshot, creditNewCells: credit);
        if (world.LatestIslandAwaitsPlacement)
        {
            var island = update.Snapshot.TrackingIslands[^1].Id;
            if (localIslandId != island)
            {
                localCoverage.Reset();
                localIslandId = island;
            }
            localCoverage.Register(local, size, omissions, bounds, creditExploration: false);
            return;
        }

        var resolved = update.Snapshot.Observations[^1].OptimizedPose;
        basis = new(resolved.X - center.X, resolved.Y - center.Y);
        HasPlacedRoom = true;
        awaitingPlacement = false;
        requiresMatch = false;
        lastTrustedPose = local;
        if (localIslandId.HasValue)
        {
            localCoverage.Reset();
            localIslandId = null;
        }
    }

    internal BotAtlasView? View(AtlasPoint? pose, WorldSize size, int? health)
    {
        if (!HasPlacedRoom || awaitingPlacement || room is not { } roomId || pose is not { } local) return null;
        var center = Center(local, size);
        return coverage.View(new(center.X + basis.X, center.Y + basis.Y), size, roomId, health);
    }

    internal BotAtlasView? PolicyView(AtlasPoint? pose, WorldSize size, int? health)
    {
        if (!awaitingPlacement) return View(pose, size, health);
        // The local chart can supply directional context during recovery. Its
        // cells have no global position or exploration credit before placement.
        if (!open || localCoverage.View(pose, size, world.Snapshot.MapRevision, health) is not { } local)
            return null;
        var (covered, earned) = coverage.Totals;
        return local with { CoveredCells = covered, EarnedCells = earned };
    }

    private void BeginVisit()
    {
        world.BeginVisit();
        epoch++;
        lastPose = null;
        lastMasks = [];
        lastScanAt = double.NegativeInfinity;
        open = false;
        localCoverage.Reset();
        localIslandId = null;
    }

    private static AtlasPoint Center(AtlasPoint origin, WorldSize size) =>
        new(origin.X + size.Width / 2, origin.Y + size.Height / 2);
}
