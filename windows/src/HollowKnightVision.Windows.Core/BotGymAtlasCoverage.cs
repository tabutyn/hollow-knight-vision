namespace HollowKnightVision.Windows.Core;

/// <summary>Owns one Gym episode's visual pose graph and its committed coverage.</summary>
internal sealed class BotGymAtlasCoverage
{
    private const double ObservationInterval = 1 / 7.5;
    private const double RecoverySearchInterval = .25;
    private const int MaximumReferenceImages = 32;
    private readonly LiveWorldTracker world = new();
    private readonly BotCommittedAtlasCoverage coverage = new();
    private readonly Dictionary<int, BotVisualAtlasCoverage> localCharts = [];
    private readonly Dictionary<long, IReadOnlyList<GroundHypothesisAtlasLine>> recordedLocalWalk = [];
    private readonly Dictionary<int, IReadOnlyList<GroundHypothesisAtlasLine>> placedWalk = [];
    private readonly HashSet<int> placedRooms = [];
    private readonly Dictionary<int, (BgraFrame Image, IReadOnlyList<WorldRect> Exclusions)> referenceImages = [];
    private readonly Queue<int> referenceOrder = [];
    private LiveDoorwayTransition? incomingDoorway;
    private LiveWorldPlacementProjection doorPlacement = LiveWorldPlacementProjection.Empty;
    private int? room;
    private ulong epoch;
    private BotVisualAtlasCoverage? localCoverage;
    private AtlasPoint basis;
    private AtlasPoint? lastPose;
    private AtlasPoint? lastTrustedPose;
    private IReadOnlyList<StencilRect> lastMasks = [];
    private double lastScanAt = double.NegativeInfinity;
    private double lastRecoverySearch = double.NegativeInfinity;
    private bool awaitingPlacement;
    private bool requiresMatch;
    private bool open;

    internal bool HasPlacedRoom { get; private set; }
    internal bool CanContinueMeasuredScenery => HasPlacedRoom && open;
    internal bool PlacementPending => awaitingPlacement;
    internal LiveWorldSnapshot Snapshot => world.Snapshot;

    internal void Interrupt(bool requireMatch = false)
    {
        requiresMatch |= requireMatch && HasPlacedRoom;
        if (!open) return;
        BeginVisit();
        awaitingPlacement = HasPlacedRoom;
    }

    internal void EnterRoom(int roomId, LiveDoorwayTransition? transition = null)
    {
        if (room == roomId) return;
        BeginVisit();
        HasPlacedRoom = placedRooms.Contains(roomId);
        incomingDoorway = !HasPlacedRoom && transition is { Direction: VisualRoomDirection.Left or VisualRoomDirection.Right } cut
            && cut.TargetRoomId == roomId
            && cut.SourceRoomId == room && placedRooms.Contains(cut.SourceRoomId) ? cut : null;
        room = roomId;
        awaitingPlacement = HasPlacedRoom;
        requiresMatch = HasPlacedRoom;
        lastTrustedPose = null;
        basis = default;
    }

    internal void Observe(BgraFrame frame, AtlasPoint? pose, double time, IReadOnlyList<PixelRect> omissions,
        VisualRoomCompositionBounds bounds, GroundHypothesisTrackingResult tracking, bool admitsPlacement,
        bool continuesMeasuredScenery = false, bool retainBasis = false, LiveWorldCaptureEvidence? captureEvidence = null,
        IReadOnlyList<GroundHypothesisAtlasLine>? walkedGround = null)
    {
        var correction = tracking.GlobalCorrection;
        if (correction is { } shift && shift.X * shift.X + shift.Y * shift.Y > 9)
        {
            Interrupt();
            requiresMatch = HasPlacedRoom;
        }
        else if (pose is null && !retainBasis)
            Interrupt();
        if (room is not { } roomId || pose is not { } local || !admitsPlacement
            || !(GroundMotionEvidence.IsMeasured(tracking) || continuesMeasuredScenery && CanContinueMeasuredScenery)
            || time - lastScanAt < ObservationInterval)
            return;

        var size = new WorldSize(frame.Width, frame.Height);
        if (incomingDoorway is { } incoming && time - incoming.ArrivalCaptureTimestamp > 3)
            incomingDoorway = null;
        var masks = omissions.Select(rect => new StencilRect(rect.X, rect.Y, rect.Width, rect.Height))
            .Concat(VisualRoomCompositionMask.OmittedRects(bounds, local, size.Width, size)).ToArray();
        // A stationary Knight near a doorway still supplies fresh, separate
        // floor contacts. Preserve them at the ordinary graph cadence.
        var samplesDoorway = captureEvidence is { GroundGeometry: not null, PlayerBounds: { } player }
            && (incomingDoorway is not null || LiveDoorwayFloorEvidence.AtDepartureEdge(player.X + player.Width / 2,
                size.Width, rightSide: player.X + player.Width / 2 >= size.Width / 2));
        // The graph uses image centers. Walked positions use the same local
        // coordinate system and must retain a per-owner persistence baseline.
        var center = Center(local, size);
        var lineage = captureEvidence?.MeasuredLineage;
        var currentWalk = walkedGround ?? [];
        var previousWalk = lineage is { } owner ? recordedLocalWalk.GetValueOrDefault(owner) ?? [] : [];
        var freshWalk = lineage is not null && currentWalk.Count > 0
            ? LiveWorldWalkedGround.NewEvidence(currentWalk, previousWalk, center) : [];
        if (!awaitingPlacement && !samplesDoorway && freshWalk.Count == 0 && lastPose == local && masks.SequenceEqual(lastMasks)) return;

        var recovering = awaitingPlacement;
        // The same visually confirmed room can return at its last measured
        // pose after a fade, even when its texture cannot form a keyframe.
        if (awaitingPlacement && !requiresMatch && local == lastTrustedPose)
            awaitingPlacement = false;

        if (!HasPlacedRoom && incomingDoorway is { } arrival)
        {
            // The cut determines which side to draw the new room on. The
            // gap is estimated until additional world evidence relates them.
            var unplaced = world.Snapshot.UnplacedObservationIds();
            var previous = world.Snapshot.Observations.Where(row => !unplaced.Contains(row.Id));
            var centerX = arrival.Direction == VisualRoomDirection.Right
                ? previous.Max(row => row.OptimizedPose.X + row.SolveWidth / 2) + size.Width * .6
                : previous.Min(row => row.OptimizedPose.X - row.SolveWidth / 2) - size.Width * .6;
            basis = new(centerX - center.X, 0);
        }
        var placed = new AtlasPoint(center.X + basis.X, center.Y + basis.Y);
        var evidence = captureEvidence ?? new(time) { HasMeasuredSceneryContinuity = continuesMeasuredScenery };
        var id = world.Snapshot.Observations.Count;
        world.Ingest(id, time, frame, center, size.Width,
            exclusions: masks, searchForClosure: false, localPose: center, captureGeneration: 1,
            localEpoch: epoch, roomId: roomId, placedCameraPose: placed,
            walkedGround: freshWalk, captureEvidence: evidence, placementPending: awaitingPlacement);
        if (lineage is { } recordedOwner && freshWalk.Count > 0)
            recordedLocalWalk[recordedOwner] = GroundStrokeGeometry.Coalesce(previousWalk.Concat(currentWalk));
        BgraFrame? masked = null;
        var searchDue = time - lastRecoverySearch >= RecoverySearchInterval;
        if (awaitingPlacement && referenceImages.Count != 0
            && (evidence.FloorCandidates is not null || evidence.GroundGeometry is not null)
            && (searchDue || world.CanConfirmGroundRecovery(time)))
        {
            if (searchDue) lastRecoverySearch = time;
            masked = FrameRegionRenderer.MapFrame(frame, SceneRegions.Startup(), masks);
            world.LinkGroundRecovery(masked, masks, source => referenceImages[source].Image,
                source => referenceImages[source].Exclusions, isConfirmationProbe: !searchDue,
                hasSource: referenceImages.ContainsKey);
        }
        lastPose = local;
        lastMasks = masks;
        lastScanAt = time;
        open = true;

        var alignedDoorway = incomingDoorway is { } doorway && world.AlignNewRoomThroughDoorway(doorway);
        if (alignedDoorway)
        {
            incomingDoorway = null;
            doorPlacement = LiveWorldGroundOverlay.ProjectPlacement(world.Snapshot);
        }
        // The confirmation can shift the complete recovery island. Every
        // observation in it, including this frame, is recovery evidence.
        var credit = !recovering && !alignedDoorway && correction is null or { X: 0, Y: 0 };
        var snapshot = world.Snapshot;
        coverage.Update(snapshot, creditNewCells: credit);
        if (world.LatestIslandAwaitsPlacement)
        {
            var island = snapshot.TrackingIslands[^1].Id;
            if (!localCharts.TryGetValue(island, out var chart))
                localCharts.Add(island, chart = new BotVisualAtlasCoverage());
            localCoverage = chart;
            chart.Register(local, size, omissions, bounds, creditExploration: false);
            return;
        }

        var observation = snapshot.Observations[^1];
        if (recovering || alignedDoorway)
        {
            var pending = snapshot.UnplacedObservationIds();
            placedWalk[roomId] = GroundStrokeGeometry.Coalesce(snapshot.Observations
                .Where(row => row.RoomId == roomId && !pending.Contains(row.Id))
                .SelectMany(LiveWorldWalkedGround.ProjectObservation));
        }
        else if (freshWalk.Count > 0)
            placedWalk[roomId] = GroundStrokeGeometry.Coalesce((placedWalk.GetValueOrDefault(roomId) ?? [])
                .Concat(LiveWorldWalkedGround.ProjectObservation(observation)));
        if (observation.GroundGeometry is not null && snapshot.Keyframes.LastOrDefault()?.ObservationId == id)
        {
            // Only placed ground keyframes can verify a later recovery. Keep
            // a fixed number of images regardless of episode length.
            referenceImages[id] = (masked ?? FrameRegionRenderer.MapFrame(frame, SceneRegions.Startup(), masks),
                observation.ExcludedRects);
            referenceOrder.Enqueue(id);
            if (referenceOrder.Count > MaximumReferenceImages) referenceImages.Remove(referenceOrder.Dequeue());
        }
        var resolved = observation.OptimizedPose;
        basis = new(resolved.X - center.X, resolved.Y - center.Y);
        HasPlacedRoom = true;
        placedRooms.Add(roomId);
        awaitingPlacement = false;
        requiresMatch = false;
        lastTrustedPose = local;
        if (localCoverage is not null)
        {
            localCharts.Clear();
            localCoverage = null;
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
        if (!open || localCoverage?.View(pose, size, world.Snapshot.MapRevision, health) is not { } local)
            return null;
        var (covered, earned) = coverage.Totals;
        return local with { CoveredCells = covered, EarnedCells = earned };
    }

    internal IReadOnlyList<GroundHypothesisAtlasLine> WalkedForPolicy(
        IReadOnlyList<GroundHypothesisAtlasLine> current, long? lineage)
    {
        if (!HasPlacedRoom || awaitingPlacement || room is not { } roomId
            || !placedWalk.TryGetValue(roomId, out var projected)) return current;
        var recorded = lineage is { } owner ? recordedLocalWalk.GetValueOrDefault(owner) ?? [] : [];
        return LiveWorldWalkedGround.MergeForDisplay(current, recorded, projected, basis);
    }

    internal IReadOnlyList<BotLine>? ProjectDoors(AtlasPoint? pose, WorldSize size)
    {
        if (!HasPlacedRoom || awaitingPlacement || room is not { } roomId || pose is not { } local)
            return null;
        return BotObservationBuilder.ProjectVisualDoors(doorPlacement, roomId,
            new(local.X + basis.X, local.Y + basis.Y), size.Width, size);
    }

    private void BeginVisit()
    {
        world.BeginVisit();
        epoch++;
        lastPose = null;
        lastMasks = [];
        lastScanAt = double.NegativeInfinity;
        lastRecoverySearch = double.NegativeInfinity;
        open = false;
        localCharts.Clear();
        localCoverage = null;
    }

    private static AtlasPoint Center(AtlasPoint origin, WorldSize size) =>
        new(origin.X + size.Width / 2, origin.Y + size.Height / 2);
}
