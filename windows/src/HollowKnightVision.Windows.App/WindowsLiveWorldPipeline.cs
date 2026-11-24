using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal enum WindowsLiveWorldPipelineState
{
    NotApplicable,
    Throttled,
    PendingRecovery,
    Committed,
    Failed
}

internal sealed record WindowsLiveWorldFrame(
    BgraFrame Frame,
    BgraFrame MaskedFrame,
    double Timestamp,
    double MonotonicTimestamp,
    AtlasPoint LocalPose,
    double SolveWidth,
    IReadOnlyList<StencilRect> Exclusions,
    IReadOnlyList<StencilRect> GroundAlignmentExclusions,
    ulong CaptureGeneration,
    string? RoomName,
    double AtlasPixelsPerWorldUnit,
    int AtlasTileSize,
    bool StableTrackingVerified = true)
{
    internal IReadOnlyList<GroundHypothesisAtlasLine> WalkedGround { get; init; } = [];
    internal long WalkedCoordinateEpoch { get; init; }
    internal bool RoomIdentityIsVisual { get; init; }
    internal VisualSceneDiscontinuityTracker.Cut? DoorwayCut { get; init; }
    internal LiveWorldCaptureEvidence? WorldEvidence { get; init; }
}

internal sealed record WindowsLiveWorldPipelineResult(
    WindowsLiveWorldPipelineState State,
    ulong CaptureGeneration,
    AtlasPoint CameraPose,
    AtlasPoint AppliedCorrection,
    AtlasPoint WorldBasisOffset,
    int Revision,
    LiveWorldLoopClosureEdge? AcceptedClosure,
    AtlasPoint? ConfirmedRecoveryPose,
    GroundTruthAtlasMosaic? RebuiltAtlas,
    string Diagnostic)
{
    internal bool AllowsAtlasWrite => State == WindowsLiveWorldPipelineState.Committed;
    internal bool AllowsAtlasComposition => AllowsAtlasWrite && !PlacementPending;
    internal bool Discarded { get; init; }
    internal bool PlacementPending { get; init; }
    public IReadOnlyList<GroundHypothesisAtlasLine> WalkedGround { get; init; } = [];
    internal LiveWorldGroundProjection? SavedWorld { get; init; }
    internal GameplayPathRegistration? PathRegistration { get; init; }
    internal long WalkedCoordinateEpoch { get; init; }
    internal IReadOnlyList<GroundHypothesisAtlasLine> RecordedLocalWalk { get; init; } = [];

    internal GameplayPathRegistration? RegisteredPath(long coordinateEpoch) =>
        coordinateEpoch == WalkedCoordinateEpoch ? PathRegistration : null;

    internal static WindowsLiveWorldPipelineResult NotApplicable(
        WindowsLiveWorldFrame frame,
        string diagnostic) => new(
            WindowsLiveWorldPipelineState.NotApplicable,
            frame.CaptureGeneration,
            frame.LocalPose,
            new AtlasPoint(0, 0),
            new AtlasPoint(0, 0),
            0,
            null,
            null,
            null,
            diagnostic);
}

internal sealed record WindowsLiveRoomBoundaryEvidenceResult(
    VisualRoomBoundaryEvidence? Evidence,
    string? PersistenceIssue);

/// <summary>
/// Owns the durable live-world transaction used by the Windows analysis
/// worker. MainWindow's bounded analysis channel is latest-only, so durable
/// work can never delay the separately published newest captured frame.
/// </summary>
internal sealed class WindowsLiveWorldPipeline
{
    private enum RecoveryDisposition
    {
        None,
        SearchReference,
        FindSavedRoom,
        AwaitSafeNewWorld,
        AwaitNewWorldBoundary
    }

    private const double GlobalSearchIntervalSeconds = 0.25;
    private const double PersistenceIntervalSeconds = 1.0 / 7.5;

    private readonly object gate = new();
    private readonly string rootDirectory;
    private readonly Action<string>? transactionObserver;
    private readonly Func<string, bool>? transactionFault;
    private readonly Dictionary<int, string> roomNames = [];
    private readonly Dictionary<int, int> placedRoomIds = [];
    private readonly Dictionary<int, IReadOnlyList<WorldRect>> compositionExclusions = [];
    private readonly Dictionary<int, IReadOnlyList<GroundHypothesisAtlasLine>> projectedWalkedGround = [];
    private (ulong Capture, int Room, long CoordinateEpoch)? walkedScope;
    private IReadOnlyList<GroundHypothesisAtlasLine> committedLocalWalk = [];
    private LiveWorldSessionStore? store;
    private LiveWorldTracker? tracker;
    private WorldBasisController worldBasis = new();
    private ulong? captureGeneration;
    private ulong persistentGenerationBase;
    private ulong persistentCaptureGeneration;
    private int? roomId;
    private int? receiverRoomId;
    private double? placedRoomStartedAt;
    private (int RoomId, double StartedAt)? doorwaySource;
    private VisualSceneDiscontinuityTracker.Cut? examinedDoorway;
    private int unmatchedArrivalSearches;
    private bool arrivalHasCompetingPlacement;
    private LiveDoorwayTransition? newRoomDoorway;
    private RecoveryDisposition recoveryDisposition;
    private bool placementPending;
    private bool canResumeLocalTracking;
    private (double First, double Last, int Count, long CoordinateEpoch)? recoverySamples;
    private long recoveryCoordinateEpoch;
    private double lastPlacedVerticalBasis;
    private (ulong Generation, long Id, double Timestamp)? latestAdmittedFrame;
    private double lastGlobalSearchAt = double.NegativeInfinity;
    private double lastPersistenceAt = double.NegativeInfinity;
    private static readonly DateTimeOffset SwiftDateEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long workEpoch;
    // A constructor-time reopen failure remains a processable persistence
    // failure so Process can report (and retry) the real store error. Only an
    // explicit InvalidatePendingWork call closes admission until Reopen succeeds.
    private bool acceptsWork = true;
    private bool hasDurableEvidence;
    private LiveWorldGroundProjection savedGround = LiveWorldGroundProjection.Empty;
    private long pathGeometryEpoch;

    internal bool HasDurableEvidence => Volatile.Read(ref hasDurableEvidence);
    internal LiveWorldGroundProjection SavedWorld => Volatile.Read(ref savedGround);

    internal WindowsLiveWorldPipeline(string rootDirectory)
        : this(rootDirectory, transactionObserver: null, transactionFault: null) { }

    internal WindowsLiveWorldPipeline(
        string rootDirectory,
        Action<string>? transactionObserver)
        : this(rootDirectory, transactionObserver, transactionFault: null) { }

    /// <summary>
    /// Internal deterministic fault seam used by the production-path harness.
    /// The normal application never supplies it.
    /// </summary>
    internal WindowsLiveWorldPipeline(
        string rootDirectory,
        Action<string>? transactionObserver,
        Func<string, bool>? transactionFault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = rootDirectory;
        this.transactionObserver = transactionObserver;
        this.transactionFault = transactionFault;
        Reopen();
    }

    internal string? LastError { get; private set; }

    internal WindowsLiveRoomBoundaryEvidenceResult BoundaryEvidence(
        int leftRoomID,
        int rightRoomID)
    {
        lock (gate)
        {
            if (LastError is { Length: > 0 } existingIssue)
            {
                return new(null, existingIssue);
            }
            if (store is null || tracker is null)
            {
                return new(null, "The live-world persistence pipeline is unavailable.");
            }

            var observations = tracker.Snapshot.Observations;
            var firstLeft = observations
                .Where(value => value.RoomId == leftRoomID)
                .OrderBy(value => value.Timestamp)
                .ThenBy(value => value.Id)
                .FirstOrDefault();
            if (firstLeft is null) return new(null, null);

            var precedingRight = observations
                .Where(value => value.RoomId == rightRoomID
                                && value.Timestamp < firstLeft.Timestamp)
                .OrderByDescending(value => value.Timestamp)
                .ThenByDescending(value => value.Id)
                .FirstOrDefault();
            var lastRight = precedingRight ?? observations
                .Where(value => value.RoomId == rightRoomID)
                .OrderByDescending(value => value.Timestamp)
                .ThenByDescending(value => value.Id)
                .FirstOrDefault();
            if (lastRight is null) return new(null, null);

            try
            {
                var leftImage = store.Source(firstLeft.SourceObservationId);
                var rightImage = store.Source(lastRight.SourceObservationId);
                var leftProfile = FrameRegionRenderer.SignalProfile(leftImage);
                var rightProfile = FrameRegionRenderer.SignalProfile(rightImage);
                var solveHeight = firstLeft.SolveWidth * firstLeft.SourceHeight
                                  / Math.Max(1d, firstLeft.SourceWidth);
                return new(new VisualRoomBoundaryEvidence(
                    leftRoomID,
                    rightRoomID,
                    firstLeft.OptimizedPose,
                    lastRight.OptimizedPose,
                    firstLeft.SolveWidth,
                    solveHeight,
                    rightProfile.EdgeBlackBands?.Left,
                    leftProfile.EdgeBlackBands?.Right), null);
            }
            catch (Exception error)
            {
                LastError = $"live-room boundary audit failed: {error.Message}";
                return new(null, LastError);
            }
        }
    }

    internal bool Reopen()
    {
        lock (gate)
        {
            if (!ReloadLocked()) return false;
            latestAdmittedFrame = null;
            Interlocked.Increment(ref workEpoch);
            Volatile.Write(ref acceptsWork, true);
            return true;
        }
    }

    /// <summary>
    /// Prevents queued and active analysis from publishing into an atlas
    /// generation that is about to be replaced. Reopen admits fresh work.
    /// </summary>
    internal void InvalidatePendingWork()
    {
        Interlocked.Increment(ref workEpoch);
        Volatile.Write(ref acceptsWork, false);
        lock (gate)
        {
            // Taking the transaction lock drains any operation that observed
            // the prior epoch. Active work detects the bump at its next stage.
            Interlocked.Increment(ref workEpoch);
            Volatile.Write(ref acceptsWork, false);
        }
    }

    internal WindowsLiveWorldPipelineResult Process(WindowsLiveWorldFrame request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Frame);
        ArgumentNullException.ThrowIfNull(request.MaskedFrame);
        ArgumentNullException.ThrowIfNull(request.Exclusions);
        ArgumentNullException.ThrowIfNull(request.GroundAlignmentExclusions);
        var processEpoch = Volatile.Read(ref workEpoch);

        lock (gate)
        {
            if (!IsWorkCurrent(processEpoch)) return Invalidated(request);
            if (string.IsNullOrWhiteSpace(request.RoomName))
            {
                return WindowsLiveWorldPipelineResult.NotApplicable(
                    request,
                    "live-world skipped: no receiver scene identity");
            }
            if ((store is null || tracker is null) && !ReloadLocked())
            {
                if (!IsWorkCurrent(processEpoch)) return Invalidated(request);
                return Failed(request, LastError ?? "live-world store is unavailable");
            }
            if (!IsWorkCurrent(processEpoch)) return Invalidated(request);

            var activeTracker = tracker!;
            var activeStore = store!;
            var transactionBaseline = activeTracker.Snapshot;
            WindowsLiveWorldPipelineResult Cancelled() =>
                Invalidated(request, transactionBaseline);
            WindowsLiveWorldPipelineResult Publish(
                WindowsLiveWorldPipelineResult result) =>
                IsWorkCurrent(processEpoch) ? result : Cancelled();
            WindowsLiveWorldPipelineResult Fail(string diagnostic)
            {
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                var result = Failed(request, diagnostic);
                return IsWorkCurrent(processEpoch) ? result : Cancelled();
            }
            WindowsLiveWorldPipelineResult Discard(string reason) =>
                Publish(PendingRecovery(request, activeTracker, $"live-world skipped: {reason}") with { Discarded = true });

            // Match votes and room ownership can advance between commits.
            // Keep the live frame order through a store rollback.
            if (!double.IsFinite(request.Timestamp) || !double.IsFinite(request.MonotonicTimestamp))
                return Discard("invalid frame timestamp");
            if (request.Frame.CaptureId < 0) return Discard("stale analysis frame");
            if (latestAdmittedFrame is { } latest)
            {
                if (request.CaptureGeneration < latest.Generation) return Discard("stale capture generation");
                if (request.CaptureGeneration == latest.Generation)
                {
                    if (request.Frame.CaptureId <= latest.Id) return Discard("stale analysis frame");
                    if (request.MonotonicTimestamp <= latest.Timestamp) return Discard("stale world timestamp");
                }
            }
            int requestedRoomId;
            try
            {
                requestedRoomId = RoomId(request.RoomName.Trim());
            }
            catch (Exception error)
            {
                RollBackTracker(activeTracker.Snapshot);
                return Fail($"live-world room identity failed: {error.Message}");
            }
            var beginsCaptureGeneration = captureGeneration != request.CaptureGeneration;
            if (beginsCaptureGeneration)
            {
                try
                {
                    var localGeneration = request.CaptureGeneration == 0
                        ? 1UL
                        : request.CaptureGeneration;
                    persistentCaptureGeneration = checked(
                        persistentGenerationBase + localGeneration);
                }
                catch (OverflowException error)
                {
                    return Fail(
                        $"live-world capture generation overflowed: {error.Message}");
                }
                captureGeneration = request.CaptureGeneration;
                if (request.RoomIdentityIsVisual) placedRoomIds.Clear();
                activeTracker.BeginVisit();
                worldBasis = new WorldBasisController();
                worldBasis.BeginCapture();
                worldBasis.LoseLocalTracking();
                lastPlacedVerticalBasis = 0;
                roomId = null;
                receiverRoomId = null;
                placedRoomStartedAt = null;
                doorwaySource = null;
                newRoomDoorway = null;
                ResetLocalRecovery();
                recoveryDisposition = RecoveryDisposition.AwaitSafeNewWorld;
                lastGlobalSearchAt = double.NegativeInfinity;
                lastPersistenceAt = double.NegativeInfinity;
            }
            latestAdmittedFrame = (request.CaptureGeneration, request.Frame.CaptureId, request.MonotonicTimestamp);
            var hasPlacedRoomId = placedRoomIds.TryGetValue(requestedRoomId, out var placedRoomId);
            var resolvedRoomId = hasPlacedRoomId ? placedRoomId : requestedRoomId;
            if (receiverRoomId != requestedRoomId)
            {
                var changesRoom = receiverRoomId is not null;
                doorwaySource = roomId is { } departingRoom && placedRoomStartedAt is { } started
                    ? (departingRoom, started) : null;
                placedRoomStartedAt = null;
                ResetLocalRecovery();
                examinedDoorway = null;
                unmatchedArrivalSearches = 0;
                arrivalHasCompetingPlacement = false;
                newRoomDoorway = null;
                receiverRoomId = requestedRoomId;
                roomId = resolvedRoomId;
                activeTracker.BeginRecoveryEpoch();
                if (changesRoom)
                {
                    worldBasis.LoseLocalTracking();
                }
                // Visual IDs are capture-local. Imported worlds can also use
                // a different room namespace from the receiver.
                if ((request.RoomIdentityIsVisual || activeTracker.Snapshot.UsesSwiftWireFormat)
                    && !hasPlacedRoomId && activeTracker.Snapshot.Observations.Count != 0)
                {
                    recoveryDisposition = RecoveryDisposition.FindSavedRoom;
                }
                else if (activeTracker.HasRecoveryReference(resolvedRoomId)
                    || activeTracker.HasPlacedGroundReference(resolvedRoomId, request.SolveWidth))
                {
                    recoveryDisposition = RecoveryDisposition.SearchReference;
                }
                else
                {
                    recoveryDisposition = beginsCaptureGeneration || changesRoom
                        ? RecoveryDisposition.AwaitSafeNewWorld
                        : RecoveryDisposition.AwaitNewWorldBoundary;
                }
                lastGlobalSearchAt = double.NegativeInfinity;
            }
            var locallyRecovered = canResumeLocalTracking && ObserveLocalRecovery(request);
            if (recoveryDisposition == RecoveryDisposition.AwaitNewWorldBoundary)
            {
                if (!locallyRecovered)
                    return Publish(PendingRecovery(request, activeTracker, "recovery pending: no searchable reference or new-world boundary"));
                BeginLocalRecovery(activeTracker, request.WalkedCoordinateEpoch);
            }
            if (placementPending)
            {
                if (!HasMeasuredLocalReference(request))
                    return Publish(PendingRecovery(request, activeTracker, "recovery pending: local motion is unmeasured"));
                if (recoveryCoordinateEpoch != request.WalkedCoordinateEpoch)
                {
                    BeginLocalRecovery(activeTracker, request.WalkedCoordinateEpoch);
                }
            }
            if (recoveryDisposition == RecoveryDisposition.AwaitSafeNewWorld)
            {
                if (!request.StableTrackingVerified)
                {
                    return Publish(PendingRecovery(
                        request,
                        activeTracker,
                        "recovery pending: new-world tracking is not verified"));
                }
                if (!request.RoomIdentityIsVisual
                    && TryPlaceNewDoorwayRoom(request, requestedRoomId, activeTracker.Snapshot))
                {
                    return Publish(PendingRecovery(request, activeTracker,
                        "new Atlas room provisionally placed; awaiting a normal frame"));
                }

                // The first room in a visual capture has no previous reference.
                if (request.RoomIdentityIsVisual) placedRoomIds[requestedRoomId] = resolvedRoomId;
                worldBasis.BeginLocalEpoch();
                recoveryDisposition = RecoveryDisposition.None;
            }

            var basisTicket = worldBasis.Observe(
                request.LocalPose,
                request.MonotonicTimestamp);
            if (basisTicket is null)
            {
                return Fail("live-world basis ticket was invalid");
            }

            var globalSearchDue = request.MonotonicTimestamp - lastGlobalSearchAt
                >= GlobalSearchIntervalSeconds;
            var findSavedRoom = recoveryDisposition == RecoveryDisposition.FindSavedRoom;
            if (findSavedRoom || recoveryDisposition == RecoveryDisposition.SearchReference)
            {
                if (!globalSearchDue)
                {
                    return Publish(PendingRecovery(
                        request,
                        activeTracker,
                        "recovery throttled"));
                }
                lastGlobalSearchAt = request.MonotonicTimestamp;
                LiveWorldRecoveryUpdate recovery;
                try
                {
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    recovery = activeTracker.Recover(
                        request.Frame,
                        request.LocalPose,
                        request.SolveWidth,
                        request.GroundAlignmentExclusions,
                        findSavedRoom ? null : resolvedRoomId);
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    if (!canResumeLocalTracking && recovery.MatchedReferenceKeyframeId is null
                        && !recovery.Review.Rejections.Contains(LiveWorldTrackerRejection.AmbiguousPlacement)
                        && request.StableTrackingVerified
                        && request.WorldEvidence is { } evidence && evidence.CaptureTimestamp == request.MonotonicTimestamp)
                    {
                        var floorRecovery = activeTracker.RecoverFromPlacedGround(request.MaskedFrame,
                            request.LocalPose, request.SolveWidth, evidence, request.Exclusions,
                            activeStore.Source, id => compositionExclusions[id], findSavedRoom ? null : resolvedRoomId);
                        if (floorRecovery.MatchedReferenceKeyframeId is not null
                            || floorRecovery.Review.Rejections.Contains(LiveWorldTrackerRejection.AmbiguousPlacement))
                            recovery = floorRecovery;
                    }
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                }
                catch (Exception error)
                {
                    RollBackTracker(activeTracker.Snapshot);
                    return Fail($"live-world recovery failed: {error.Message}");
                }
                if (recovery.ConfirmedPlacement is not { } placement)
                {
                    if (findSavedRoom && TryPlaceNewDoorwayRoom(request, requestedRoomId, activeTracker.Snapshot, recovery))
                    {
                        return Publish(PendingRecovery(request, activeTracker,
                            "new Atlas room provisionally placed; awaiting a normal frame"));
                    }
                    if (!locallyRecovered || recovery.MatchedReferenceKeyframeId is not null
                        || recovery.Review.Rejections.Contains(LiveWorldTrackerRejection.AmbiguousPlacement))
                    {
                        return Publish(PendingRecovery(request, activeTracker,
                            recovery.Review.Rejections.Count == 0
                                ? "recovery pending"
                                : $"recovery pending: {string.Join(',', recovery.Review.Rejections)}"));
                    }
                    BeginLocalRecovery(activeTracker, request.WalkedCoordinateEpoch);
                    basisTicket = worldBasis.Observe(request.LocalPose, request.MonotonicTimestamp);
                    if (basisTicket is null) return Fail("local recovery basis ticket was invalid");
                    globalSearchDue = false;
                }
                else
                {
                    if (captureGeneration != request.CaptureGeneration)
                        return Publish(PendingRecovery(request, activeTracker, "recovery generation expired"));
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    if (!worldBasis.ConfirmPlacement(basisTicket.Value, placement.CameraPose))
                        return Publish(PendingRecovery(request, activeTracker, "recovery basis expired"));
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    lastPlacedVerticalBasis = worldBasis.Snapshot.WorldFromLocal.Y;
                    if (findSavedRoom)
                    {
                        placedRoomIds[requestedRoomId] = placement.RoomId;
                        roomId = placement.RoomId;
                    }
                    ResetLocalRecovery();
                    recoveryDisposition = RecoveryDisposition.None;
                    placedRoomStartedAt ??= request.MonotonicTimestamp;
                    LastError = null;
                    return Publish(ConfirmedRecovery(request, activeTracker, placement));
                }
            }

            if (worldBasis.TrackingState == WorldBasisTrackingState.Recovering)
            {
                return Publish(PendingRecovery(
                    request,
                    activeTracker,
                    "recovery pending: world basis is not confirmed"));
            }

            var resolvedObservation = worldBasis.RebasedObservation(basisTicket.Value);
            if (resolvedObservation is null)
            {
                return Publish(PendingRecovery(
                    request,
                    activeTracker,
                    "live-world basis expired"));
            }
            if (request.MonotonicTimestamp - lastPersistenceAt
                < PersistenceIntervalSeconds)
            {
                return Publish(Throttled(request, activeTracker, resolvedRoomId));
            }
            var worldPose = resolvedObservation.Value.WorldPose;
            // Keep raw motion in the capture basis. The placed pose belongs to
            // the image and to any ground geometry recorded with that image.
            var capturePose = resolvedObservation.Value.Ticket.LocalPose;
            var priorSnapshot = activeTracker.Snapshot;
            var storedTimestamp = StoredCaptureTimestamp(request, priorSnapshot);
            var scope = (request.CaptureGeneration, resolvedRoomId, request.WalkedCoordinateEpoch);
            IReadOnlyList<GroundHypothesisAtlasLine> priorWalk = walkedScope == scope ? committedLocalWalk : [];
            IReadOnlyList<GroundHypothesisAtlasLine> walked = request.StableTrackingVerified ? request.WalkedGround : [];
            var groundEvidence = LiveWorldWalkedGround.NewEvidence(walked, priorWalk, capturePose);
            StoredFrameObservation? source = null;
            try
            {
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (transactionFault?.Invoke("append") == true)
                {
                    throw new IOException("Injected live-world append failure.");
                }
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                source = activeStore.Append(
                    request.MaskedFrame,
                    storedTimestamp,
                    capturePose,
                    request.SolveWidth,
                    request.Exclusions);
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                compositionExclusions[source.Id] = CompositionRects(source.ExcludedRects);
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                transactionObserver?.Invoke("append");
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                var searchForClosure = globalSearchDue;
                if (searchForClosure) lastGlobalSearchAt = request.MonotonicTimestamp;
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                LiveWorldRecoveryPlacement? restoredPlacement = null;
                if (searchForClosure && placementPending && priorSnapshot.UsesSwiftWireFormat)
                {
                    // The original Mac landmark IDs do not describe our regenerated
                    // features. Confirm placement without writing a fabricated edge.
                    restoredPlacement = activeTracker.Recover(request.Frame, request.LocalPose,
                        request.SolveWidth, request.GroundAlignmentExclusions, resolvedRoomId).ConfirmedPlacement;
                    searchForClosure = false;
                }
                var update = activeTracker.Ingest(
                    source.Id,
                    storedTimestamp,
                    request.Frame,
                    capturePose,
                    request.SolveWidth,
                    sourceObservationId: source.Id,
                    exclusions: request.GroundAlignmentExclusions,
                    searchForClosure: searchForClosure,
                    localPose: resolvedObservation.Value.Ticket.LocalPose,
                    captureGeneration: persistentCaptureGeneration,
                    localEpoch: resolvedObservation.Value.Ticket.LocalEpoch,
                    basisRevision: resolvedObservation.Value.Ticket.BasisRevision,
                    roomId: resolvedRoomId,
                    placedCameraPose: worldPose,
                    walkedGround: groundEvidence,
                    captureEvidence: request.WorldEvidence,
                    placementPending: placementPending);
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                transactionObserver?.Invoke("ingest");
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (newRoomDoorway is { } pendingDoor && request.MonotonicTimestamp - pendingDoor.ArrivalCaptureTimestamp > 3)
                    newRoomDoorway = null;

                var groundConfirmationProbe = !globalSearchDue
                    && activeTracker.CanConfirmGroundRecovery(request.MonotonicTimestamp);
                var groundClosure = (globalSearchDue || groundConfirmationProbe)
                    && placementPending && update.AcceptedClosure is null
                    && restoredPlacement is null
                    ? activeTracker.LinkGroundRecovery(request.MaskedFrame, request.Exclusions, activeStore.Source,
                        id => compositionExclusions[id], groundConfirmationProbe)
                    : null;
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                var acceptedClosure = groundClosure ?? update.AcceptedClosure;
                var restored = restoredPlacement is not null && activeTracker.PlaceRecoveredIsland(restoredPlacement);
                var localRecoveryPlaced = (acceptedClosure is not null || restored)
                    && !activeTracker.LatestIslandAwaitsPlacement;
                var doorwayAligned = !localRecoveryPlaced && newRoomDoorway is { } doorway
                    && activeTracker.AlignNewRoomThroughDoorway(doorway);
                var requiresRebuild = doorwayAligned || localRecoveryPlaced;
                var requiresBasisUpdate = requiresRebuild || groundClosure is not null;
                var durableSnapshot = WithCompositionExclusions(activeTracker.Snapshot);
                GroundTruthAtlasMosaic? rebuiltAtlas = null;
                if (requiresRebuild)
                {
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    rebuiltAtlas = RebuildAtlas(durableSnapshot, activeStore.Source,
                        request.AtlasPixelsPerWorldUnit, request.AtlasTileSize);
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    transactionObserver?.Invoke(doorwayAligned ? "doorway-rebuild" : "closure-rebuild");
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                }
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (transactionFault?.Invoke("commit") == true || !activeStore.Commit(
                        durableSnapshot,
                        priorSnapshot.MapRevision,
                        durableSnapshot.MapRevision))
                {
                    RollBackFailedFrame(activeStore, priorSnapshot, source);
                    return Fail("live-world snapshot CAS was stale");
                }
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                transactionObserver?.Invoke("commit");
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                Volatile.Write(ref hasDurableEvidence, true);
                lastPersistenceAt = request.MonotonicTimestamp;
                if (doorwayAligned) newRoomDoorway = null;
                if (localRecoveryPlaced) placementPending = false;
                placedRoomStartedAt ??= request.MonotonicTimestamp;
                var committed = durableSnapshot.Observations[^1];
                IReadOnlyList<GroundHypothesisAtlasLine> publishedGround = [];
                if (!placementPending && (!projectedWalkedGround.TryGetValue(resolvedRoomId, out publishedGround!)
                    || requiresRebuild))
                {
                    publishedGround = LiveWorldWalkedGround.ProjectRoom(durableSnapshot, resolvedRoomId);
                }
                else if (!placementPending && groundEvidence.Count != 0)
                {
                    publishedGround = GroundStrokeGeometry.Coalesce(
                        publishedGround.Concat(LiveWorldWalkedGround.ProjectObservation(committed)));
                }
                if (!placementPending) projectedWalkedGround[resolvedRoomId] = publishedGround;
                walkedScope = scope;
                committedLocalWalk = groundEvidence.Count == 0
                    ? priorWalk : GroundStrokeGeometry.Coalesce(priorWalk.Concat(walked));

                if (requiresBasisUpdate
                    && (!IsWorkCurrent(processEpoch)
                        || !worldBasis.ConfirmPlacement(
                            resolvedObservation.Value.Ticket,
                            committed.OptimizedPose)))
                {
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    throw new InvalidOperationException(
                        "The committed Atlas correction belonged to an expired world basis.");
                }
                if (requiresBasisUpdate)
                {
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    transactionObserver?.Invoke(doorwayAligned ? "doorway-basis" : "closure-basis");
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                }

                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                var basis = worldBasis.Snapshot.WorldFromLocal;
                if (!placementPending) lastPlacedVerticalBasis = basis.Y;
                LastError = null;
                var projected = savedGround;
                if (!placementPending && (requiresRebuild || committed.GroundGeometry is not null
                    || groundEvidence.Count != 0 || !projected.PathRooms.ContainsKey(resolvedRoomId)))
                    projected = LiveWorldGroundOverlay.ProjectWorld(durableSnapshot);
                else if (!placementPending && durableSnapshot.TrackingIslands.LastOrDefault() is { Placement: LiveIslandPlacement.Estimated })
                    projected = projected with { Placement = LiveWorldGroundOverlay.ProjectPlacement(durableSnapshot) };
                transactionObserver?.Invoke("publish");
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (requiresRebuild) pathGeometryEpoch++;
                Volatile.Write(ref savedGround, projected);
                return new WindowsLiveWorldPipelineResult(
                    WindowsLiveWorldPipelineState.Committed,
                    request.CaptureGeneration,
                    Corrected(request.LocalPose, basis),
                    new AtlasPoint(committed.OptimizedPose.X - worldPose.X, committed.OptimizedPose.Y - worldPose.Y),
                    basis,
                    durableSnapshot.MapRevision,
                    acceptedClosure,
                    null,
                    rebuiltAtlas,
                    placementPending ? (groundClosure is null ? "live-world measured evidence saved; placement pending"
                        : "live-world measured sections linked; placement pending")
                        : doorwayAligned ? "live-world doorway floor aligned"
                        : groundClosure is not null ? "live-world ground-verified recovery placed"
                        : restored ? "live-world local recovery placed"
                        : update.AcceptedClosure is null ? "live-world committed" : "live-world loop closure committed")
                {
                    PlacementPending = placementPending,
                    WalkedGround = publishedGround,
                    SavedWorld = projected,
                    PathRegistration = PathRegistrationFor(resolvedRoomId, basis),
                    WalkedCoordinateEpoch = request.WalkedCoordinateEpoch,
                    RecordedLocalWalk = committedLocalWalk
                };
            }
            catch (Exception error)
            {
                RollBackFailedFrame(activeStore, priorSnapshot, source);
                return Fail($"live-world transaction failed: {error.Message}");
            }
        }
    }

    internal void NotifyTrackingLoss(ulong requestCaptureGeneration)
    {
        if (!Volatile.Read(ref acceptsWork)) return;
        lock (gate)
        {
            if (!Volatile.Read(ref acceptsWork)
                || captureGeneration != requestCaptureGeneration
                || tracker is null
                || worldBasis.TrackingState == WorldBasisTrackingState.Recovering)
            {
                return;
            }
            worldBasis.LoseLocalTracking();
            ResetLocalRecovery();
            canResumeLocalTracking = roomId.HasValue && placedRoomStartedAt.HasValue;
            tracker.BeginRecoveryEpoch();
            recoveryDisposition = roomId is { } activeRoom
                                  && tracker.HasRecoveryReference(activeRoom)
                ? RecoveryDisposition.SearchReference
                : tracker.Snapshot.Observations.Count == 0 ? RecoveryDisposition.AwaitSafeNewWorld
                : RecoveryDisposition.AwaitNewWorldBoundary;
            lastGlobalSearchAt = double.NegativeInfinity;
        }
    }

    private static bool HasMeasuredLocalReference(WindowsLiveWorldFrame request) =>
        request.WorldEvidence is { LocalReferenceTimestamp: { } reference } evidence
        && evidence.CaptureTimestamp == request.MonotonicTimestamp
        && request.MonotonicTimestamp - reference is > 0 and <= .75;

    private bool ObserveLocalRecovery(WindowsLiveWorldFrame request)
    {
        if (!HasMeasuredLocalReference(request))
        {
            recoverySamples = null;
            return false;
        }
        var now = request.MonotonicTimestamp;
        var epoch = request.WalkedCoordinateEpoch;
        (double First, double Last, int Count, long CoordinateEpoch) samples =
            recoverySamples is { } previous && previous.CoordinateEpoch == epoch && now - previous.Last is > 0 and <= .25
                ? (previous.First, now, previous.Count + 1, epoch)
                : (now, now, 1, epoch);
        recoverySamples = samples;
        return samples.Count >= 3 && now - samples.First >= .08;
    }

    private void BeginLocalRecovery(LiveWorldTracker activeTracker, long coordinateEpoch)
    {
        // A relative solve can keep collecting images. Its inherited display
        // offset has no authority over the already composed mosaic.
        worldBasis.BeginLocalEpoch(worldBasis.Snapshot.WorldFromLocal);
        activeTracker.BeginRecoveryEpoch();
        ResetLocalRecovery();
        placementPending = true;
        recoveryCoordinateEpoch = coordinateEpoch;
        recoveryDisposition = RecoveryDisposition.None;
    }

    private void ResetLocalRecovery()
    {
        placementPending = false;
        canResumeLocalTracking = false;
        recoverySamples = null;
    }

    private bool IsWorkCurrent(long processEpoch) =>
        Volatile.Read(ref acceptsWork)
        && Volatile.Read(ref workEpoch) == processEpoch;

    private WindowsLiveWorldPipelineResult Invalidated(
        WindowsLiveWorldFrame request,
        LiveWorldSnapshot? fallback = null)
    {
        if (fallback is not null) RollBackTracker(fallback);
        return new WindowsLiveWorldPipelineResult(
            WindowsLiveWorldPipelineState.PendingRecovery,
            request.CaptureGeneration,
            request.LocalPose,
            new AtlasPoint(0, 0),
            worldBasis.Snapshot.WorldFromLocal,
            tracker?.Snapshot.MapRevision ?? fallback?.MapRevision ?? 0,
            null,
            null,
            null,
            "live-world cancelled: active atlas generation changed") { Discarded = true };
    }

    private WindowsLiveWorldPipelineResult PendingRecovery(
        WindowsLiveWorldFrame request,
        LiveWorldTracker activeTracker,
        string diagnostic) => new(
            WindowsLiveWorldPipelineState.PendingRecovery,
            request.CaptureGeneration,
            request.LocalPose,
            new AtlasPoint(0, 0),
            worldBasis.Snapshot.WorldFromLocal,
            activeTracker.Snapshot.MapRevision,
            null,
            null,
            null,
            diagnostic);

    private WindowsLiveWorldPipelineResult ConfirmedRecovery(
        WindowsLiveWorldFrame request,
        LiveWorldTracker activeTracker,
        LiveWorldRecoveryPlacement placement) => new(
            WindowsLiveWorldPipelineState.PendingRecovery,
            request.CaptureGeneration,
            placement.CameraPose,
            placement.Correction,
            worldBasis.Snapshot.WorldFromLocal,
            activeTracker.Snapshot.MapRevision,
            null,
            placement.CameraPose,
            null,
            "live-world recovery confirmed; awaiting a normal frame");

    private WindowsLiveWorldPipelineResult Throttled(
        WindowsLiveWorldFrame request,
        LiveWorldTracker activeTracker,
        int committedRoomId) => new WindowsLiveWorldPipelineResult(
            WindowsLiveWorldPipelineState.Throttled,
            request.CaptureGeneration,
            Corrected(request.LocalPose, worldBasis.Snapshot.WorldFromLocal),
            new AtlasPoint(0, 0),
            worldBasis.Snapshot.WorldFromLocal,
            activeTracker.Snapshot.MapRevision,
            null,
            null,
            null,
            "live-world skipped: persistence cadence")
        {
            PlacementPending = placementPending,
            WalkedGround = !placementPending && projectedWalkedGround.TryGetValue(committedRoomId, out var ground) ? ground : [],
            SavedWorld = SavedWorld,
            PathRegistration = PathRegistrationFor(committedRoomId, worldBasis.Snapshot.WorldFromLocal),
            WalkedCoordinateEpoch = request.WalkedCoordinateEpoch,
            RecordedLocalWalk = walkedScope == (request.CaptureGeneration, committedRoomId, request.WalkedCoordinateEpoch)
                ? committedLocalWalk : []
        };

    private GameplayPathRegistration? PathRegistrationFor(int roomId, AtlasPoint worldFromLocal) =>
        !placementPending && savedGround.PathRooms.TryGetValue(roomId, out var room)
            ? new(room, worldFromLocal, pathGeometryEpoch) : null;

    private WindowsLiveWorldPipelineResult Failed(
        WindowsLiveWorldFrame request,
        string diagnostic)
    {
        LastError = diagnostic;
        return new WindowsLiveWorldPipelineResult(
            WindowsLiveWorldPipelineState.Failed,
            request.CaptureGeneration,
            request.LocalPose,
            new AtlasPoint(0, 0),
            worldBasis.Snapshot.WorldFromLocal,
            tracker?.Snapshot.MapRevision ?? 0,
            null,
            null,
            null,
            diagnostic);
    }

    private void RollBackTracker(LiveWorldSnapshot fallback)
    {
        if (!ReloadLocked()) tracker = new LiveWorldTracker(fallback);
    }

    private void RollBackFailedFrame(LiveWorldSessionStore activeStore, LiveWorldSnapshot priorSnapshot,
        StoredFrameObservation? source)
    {
        try
        {
            var committedTracker = activeStore.LoadTracker();
            if (committedTracker.Snapshot.MapRevision == priorSnapshot.MapRevision)
            {
                // The already committed source and arrival images still belong to this visit.
                // Preserve the calibrated basis and pending doorway for the next captured frame.
                committedTracker.ResumeCommittedVisit(persistentCaptureGeneration);
                tracker = committedTracker;
                if (source is not null) compositionExclusions.Remove(source.Id);
                return;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LiveWorldSessionStoreException
            or SceneSessionStoreException or LiveWorldModelException or LiveWorldTrackerException)
        {
            // Reopen below so a store which changed on disk gets its own fresh basis.
        }
        RollBackTracker(priorSnapshot);
    }

    private bool ReloadLocked()
    {
        try
        {
            var reopened = new LiveWorldSessionStore(rootDirectory);
            var reopenedTracker = reopened.LoadTracker();
            var snapshot = reopenedTracker.Snapshot;
            var restoredGround = LiveWorldGroundOverlay.ProjectWorld(snapshot);
            var manifest = new SceneSessionStore(reopened.RootDirectory).Manifest;
            var reopenedCompositionExclusions = manifest.Observations.ToDictionary(
                observation => observation.Id,
                observation => CompositionRects(observation.ExcludedRects));
            store = reopened;
            tracker = reopenedTracker;
            placedRoomIds.Clear();
            compositionExclusions.Clear();
            projectedWalkedGround.Clear();
            walkedScope = null;
            committedLocalWalk = [];
            foreach (var (observationId, exclusions) in reopenedCompositionExclusions)
                compositionExclusions.Add(observationId, exclusions);
            var maximumGeneration = snapshot.Observations.Count == 0
                ? 0
                : snapshot.Observations.Max(value => value.CaptureGeneration);
            persistentGenerationBase = maximumGeneration;
            persistentCaptureGeneration = 0;
            worldBasis = new WorldBasisController();
            lastPlacedVerticalBasis = 0;
            captureGeneration = null;
            roomId = null;
            receiverRoomId = null;
            placedRoomStartedAt = null;
            doorwaySource = null;
            newRoomDoorway = null;
            ResetLocalRecovery();
            recoveryDisposition = RecoveryDisposition.None;
            lastGlobalSearchAt = double.NegativeInfinity;
            lastPersistenceAt = double.NegativeInfinity;
            Volatile.Write(ref hasDurableEvidence, snapshot.Observations.Count != 0);
            pathGeometryEpoch++;
            Volatile.Write(ref savedGround, restoredGround);
            LastError = null;
            return true;
        }
        catch (Exception error)
        {
            store = null;
            tracker = null;
            Volatile.Write(ref savedGround, LiveWorldGroundProjection.Empty);
            LastError = $"live-world reopen failed: {error.Message}";
            return false;
        }
    }

    internal static int RoomIdentifier(string roomName) => BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(roomName))) & int.MaxValue;

    private int RoomId(string roomName)
    {
        var identifier = RoomIdentifier(roomName);
        if (roomNames.TryGetValue(identifier, out var existing)
            && !string.Equals(existing, roomName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Receiver scenes '{existing}' and '{roomName}' share a room identifier.");
        }
        roomNames[identifier] = roomName;
        return identifier;
    }

    private bool TryPlaceNewDoorwayRoom(WindowsLiveWorldFrame request, int requestedRoomId, LiveWorldSnapshot snapshot,
        LiveWorldRecoveryUpdate? recovery = null)
    {
        if (recovery is not null && (recovery.MatchedReferenceKeyframeId is not null
            || recovery.Review.Rejections.Contains(LiveWorldTrackerRejection.AmbiguousPlacement)))
            arrivalHasCompetingPlacement = true;

        if (arrivalHasCompetingPlacement || !request.StableTrackingVerified
            || request.DoorwayCut is not { } cut || doorwaySource is not { } source
            || !double.IsFinite(cut.DepartureTimestamp) || !double.IsFinite(cut.ArrivalTimestamp)
            || cut.DepartureTimestamp < source.StartedAt || cut.ArrivalTimestamp <= cut.DepartureTimestamp
            || request.MonotonicTimestamp - cut.ArrivalTimestamp is < 0 or > 3
            || cut.Direction is not (VisualRoomDirection.Left or VisualRoomDirection.Right))
        {
            return false;
        }

        if (examinedDoorway != cut)
        {
            examinedDoorway = cut;
            unmatchedArrivalSearches = 0;
        }
        if (recovery is not null && ++unmatchedArrivalSearches < 3) return false;

        var targetId = requestedRoomId;
        var usedRoomIds = snapshot.Observations.Select(observation => observation.RoomId).ToHashSet();
        if (usedRoomIds.Contains(targetId))
        {
            if (!request.RoomIdentityIsVisual) return false;
            targetId = 0;
            while (usedRoomIds.Contains(targetId)) targetId = checked(targetId + 1);
        }

        var unplaced = snapshot.UnplacedObservationIds();
        var placed = snapshot.Observations.Where(observation => !unplaced.Contains(observation.Id)).ToArray();
        if (!placed.Any(observation => observation.RoomId == source.RoomId)) return false;

        // The measured cut supplies direction, and the visible Atlas supplies
        // spacing until there is direct evidence for a relative placement.
        var margin = request.SolveWidth * .1;
        var worldX = cut.Direction == VisualRoomDirection.Right
            ? placed.Max(value => value.OptimizedPose.X + value.SolveWidth * .5) + margin + request.SolveWidth * .5
            : placed.Min(value => value.OptimizedPose.X - value.SolveWidth * .5) - margin - request.SolveWidth * .5;
        // An unplaced component may rebase its own poses between doorway visits.
        var basis = new AtlasPoint(worldX - request.LocalPose.X, lastPlacedVerticalBasis);
        if (!double.IsFinite(basis.X)) return false;

        worldBasis.BeginLocalEpoch(basis);
        newRoomDoorway = new LiveDoorwayTransition(source.RoomId, targetId, persistentCaptureGeneration,
            cut.DepartureTimestamp, cut.ArrivalTimestamp, cut.Direction);
        placedRoomIds[requestedRoomId] = targetId;
        roomId = targetId;
        recoveryDisposition = RecoveryDisposition.None;
        return true;
    }

    private static AtlasPoint Corrected(AtlasPoint pose, AtlasPoint correction) => new(
        pose.X + correction.X,
        pose.Y + correction.Y);

    private LiveWorldSnapshot WithCompositionExclusions(LiveWorldSnapshot snapshot)
    {
        var observations = snapshot.Observations.Select(observation =>
        {
            if (!compositionExclusions.TryGetValue(
                    observation.SourceObservationId,
                    out var exclusions))
            {
                throw new InvalidDataException(
                    $"Live-world observation {observation.SourceObservationId} has no composition mask.");
            }
            return observation with { ExcludedRects = exclusions };
        }).ToArray();
        return new LiveWorldSnapshot(
            snapshot.SchemaVersion,
            snapshot.MapRevision,
            snapshot.AnchoredObservationId,
            observations,
            snapshot.Keyframes,
            snapshot.Landmarks,
            snapshot.RelativeMotionEdges,
            snapshot.LoopClosureEdges,
            snapshot.TrackingIslands,
            snapshot.DoorwayFloorConstraints);
    }

    private static IReadOnlyList<WorldRect> CompositionRects(
        IReadOnlyList<StencilRect> exclusions) => exclusions.Select(rectangle =>
            new WorldRect(
                new AtlasPoint(rectangle.X, rectangle.Y),
                new WorldSize(rectangle.Width, rectangle.Height))).ToArray();

    internal static double StoredCaptureTimestamp(WindowsLiveWorldFrame request, LiveWorldSnapshot snapshot)
    {
        if (!snapshot.UsesSwiftWireFormat) return request.MonotonicTimestamp;

        var recordedAt = (request.Frame.ObservedAt - SwiftDateEpoch).TotalSeconds;
        // A reopened Mac atlas uses Apple's date epoch. Live clock adjustments
        // must still leave newly saved frames ordered after the previous visit.
        return snapshot.Observations.Count == 0 ? recordedAt
            : Math.Max(recordedAt, Math.BitIncrement(snapshot.Observations.Max(value => value.Timestamp)));
    }

    internal static GroundTruthAtlasMosaic RebuildAtlas(
        LiveWorldSnapshot snapshot,
        Func<int, BgraFrame> loadSource,
        double pixelsPerWorldUnit,
        int tileSize)
    {
        if (!double.IsFinite(pixelsPerWorldUnit) || pixelsPerWorldUnit <= 0 || tileSize <= 0)
            throw new InvalidDataException("Live-world atlas geometry is invalid.");

        var rebuilt = new GroundTruthAtlasMosaic(pixelsPerWorldUnit, tileSize, sourceTopInsetFraction: 0);
        var unplaced = snapshot.UnplacedObservationIds();
        foreach (var observation in snapshot.Observations.OrderBy(value => value.Id))
        {
            if (unplaced.Contains(observation.Id)) continue;
            var source = loadSource(observation.SourceObservationId);
            if (source.Width != observation.SourceWidth || source.Height != observation.SourceHeight)
                throw new InvalidDataException($"Live-world observation {observation.Id} has incorrect source dimensions.");
            var masks = SceneRegions.PixelRectangles(source.Width, source.Height, snapshot.CompositionMasks(observation));
            if (!rebuilt.AddFrame(source, AtlasTelemetry(source, observation), masks))
                throw new InvalidDataException($"Live-world observation {observation.Id} could not be rebuilt.");
        }
        return rebuilt;
    }

    private static ReceiverGroundTruthSample AtlasTelemetry(
        BgraFrame frame,
        LiveWorldObservation observation) => new(
            Version: 1,
            Type: "groundTruth",
            SessionId: Guid.Empty,
            Sequence: (ulong)observation.Id,
            UnityFrame: observation.Id,
            // A saved capture can have a wall-clock timestamp. The renderer
            // only needs the stable identity and geometry for newest-pixel composition.
            UnityRealtime: 0,
            SceneName: "",
            HeroAvailable: false,
            HeroX: 0,
            HeroY: 0,
            HeroZ: 0,
            VelocityX: 0,
            VelocityY: 0,
            FacingRight: true,
            Grounded: false,
            CameraAvailable: true,
            CameraX: observation.OptimizedPose.X,
            CameraY: observation.OptimizedPose.Y,
            CameraZ: 0,
            CameraTargetX: observation.OptimizedPose.X,
            CameraTargetY: observation.OptimizedPose.Y,
            CameraTargetZ: 0,
            OrthographicSize: frame.Height * observation.SolveWidth / (2 * frame.Width),
            PixelsPerWorldUnitX: frame.Width / observation.SolveWidth,
            PixelsPerWorldUnitY: frame.Width / observation.SolveWidth,
            HeroScreenX: null,
            HeroScreenY: null,
            ProjectionPixelWidth: frame.Width,
            ProjectionPixelHeight: frame.Height,
            ScreenWidth: frame.Width,
            ScreenHeight: frame.Height);
}
