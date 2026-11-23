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
    public IReadOnlyList<GroundHypothesisAtlasLine> WalkedGround { get; init; } = [];
    internal long WalkedCoordinateEpoch { get; init; }
    internal IReadOnlyList<GroundHypothesisAtlasLine> RecordedLocalWalk { get; init; } = [];

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
        FindImportedRoom,
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
    private readonly Dictionary<int, int> importedRoomIds = [];
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
    private long lastCaptureId = -1;
    private double lastGlobalSearchAt = double.NegativeInfinity;
    private double lastPersistenceAt = double.NegativeInfinity;
    private double lastWorldTrackerTimestamp = double.NegativeInfinity;
    private static readonly DateTimeOffset SwiftDateEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long workEpoch;
    // A constructor-time reopen failure remains a processable persistence
    // failure so Process can report (and retry) the real store error. Only an
    // explicit InvalidatePendingWork call closes admission until Reopen succeeds.
    private bool acceptsWork = true;
    private bool importedAtlas;

    internal bool HasImportedAtlas => Volatile.Read(ref importedAtlas);

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
            var hasImportedRoomId = importedRoomIds.TryGetValue(requestedRoomId, out var importedRoomId);
            var resolvedRoomId = hasImportedRoomId ? importedRoomId : requestedRoomId;
            if (captureGeneration is { } currentGeneration
                && request.CaptureGeneration < currentGeneration)
            {
                return Publish(PendingRecovery(
                    request,
                    activeTracker,
                    "live-world skipped: stale capture generation"));
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
                activeTracker.BeginVisit();
                worldBasis = new WorldBasisController();
                worldBasis.BeginCapture();
                worldBasis.LoseLocalTracking();
                roomId = null;
                receiverRoomId = null;
                placedRoomStartedAt = null;
                doorwaySource = null;
                newRoomDoorway = null;
                recoveryDisposition = RecoveryDisposition.AwaitSafeNewWorld;
                lastCaptureId = -1;
                lastGlobalSearchAt = double.NegativeInfinity;
                lastPersistenceAt = double.NegativeInfinity;
                lastWorldTrackerTimestamp = double.NegativeInfinity;
            }
            if (receiverRoomId != requestedRoomId)
            {
                var changesRoom = receiverRoomId is not null;
                doorwaySource = roomId is { } departingRoom && placedRoomStartedAt is { } started
                    ? (departingRoom, started) : null;
                placedRoomStartedAt = null;
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
                // Swift stores visual room IDs. Prove the receiver scene's location
                // before adding a capture to a loaded Atlas.
                if (activeTracker.Snapshot.UsesSwiftWireFormat && !hasImportedRoomId
                    && activeTracker.Snapshot.Observations.Count != 0)
                {
                    recoveryDisposition = RecoveryDisposition.FindImportedRoom;
                }
                else if (HasSearchableReference(activeTracker, resolvedRoomId))
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
            if (request.Frame.CaptureId <= lastCaptureId)
            {
                return Publish(new WindowsLiveWorldPipelineResult(
                    WindowsLiveWorldPipelineState.PendingRecovery,
                    request.CaptureGeneration,
                    request.LocalPose,
                    new AtlasPoint(0, 0),
                    worldBasis.Snapshot.WorldFromLocal,
                    activeTracker.Snapshot.MapRevision,
                    null,
                    null,
                    null,
                    "live-world skipped: stale analysis frame"));
            }
            lastCaptureId = request.Frame.CaptureId;

            // Capture delivery and receiver joins are ordered independently.
            // Never let delayed world evidence rewind the active visit or its
            // motion chain, even when its capture identifier is newer.
            if (request.MonotonicTimestamp <= lastWorldTrackerTimestamp)
            {
                return Publish(PendingRecovery(
                    request,
                    activeTracker,
                    "live-world skipped: stale world timestamp"));
            }

            if (recoveryDisposition == RecoveryDisposition.AwaitNewWorldBoundary)
            {
                return Publish(PendingRecovery(
                    request,
                    activeTracker,
                    "recovery pending: no searchable reference or new-world boundary"));
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
            var findImportedRoom = recoveryDisposition == RecoveryDisposition.FindImportedRoom;
            if (findImportedRoom || recoveryDisposition == RecoveryDisposition.SearchReference)
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
                        findImportedRoom ? null : resolvedRoomId);
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                }
                catch (Exception error)
                {
                    RollBackTracker(activeTracker.Snapshot);
                    return Fail($"live-world recovery failed: {error.Message}");
                }
                if (recovery.ConfirmedPlacement is not { } placement)
                {
                    if (findImportedRoom && TryPlaceNewDoorwayRoom(request, requestedRoomId, activeTracker.Snapshot, recovery))
                    {
                        return Publish(PendingRecovery(request, activeTracker,
                            "new Atlas room provisionally placed; awaiting a normal frame"));
                    }
                    return Publish(PendingRecovery(
                        request,
                        activeTracker,
                        recovery.Review.Rejections.Count == 0
                            ? "recovery pending"
                            : $"recovery pending: {string.Join(',', recovery.Review.Rejections)}"));
                }
                if (captureGeneration != request.CaptureGeneration)
                {
                    return Publish(PendingRecovery(
                        request,
                        activeTracker,
                        "recovery generation expired"));
                }
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (!worldBasis.ConfirmPlacement(basisTicket.Value, placement.CameraPose))
                {
                    return Publish(PendingRecovery(
                        request,
                        activeTracker,
                        "recovery basis expired"));
                }
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (findImportedRoom)
                {
                    importedRoomIds[requestedRoomId] = placement.RoomId;
                    roomId = placement.RoomId;
                }
                recoveryDisposition = RecoveryDisposition.None;
                placedRoomStartedAt ??= request.MonotonicTimestamp;
                LastError = null;
                return Publish(ConfirmedRecovery(request, activeTracker, placement));
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
            lastPersistenceAt = request.MonotonicTimestamp;

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
            try
            {
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (transactionFault?.Invoke("append") == true)
                {
                    throw new IOException("Injected live-world append failure.");
                }
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                var source = activeStore.Append(
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
                    captureEvidence: request.WorldEvidence);
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                transactionObserver?.Invoke("ingest");
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                if (newRoomDoorway is { } pendingDoor && request.MonotonicTimestamp - pendingDoor.ArrivalCaptureTimestamp > 3)
                    newRoomDoorway = null;

                var doorwayAligned = update.AcceptedClosure is null && newRoomDoorway is { } doorway
                    && activeTracker.AlignNewRoomThroughDoorway(doorway);
                if (doorwayAligned) newRoomDoorway = null;
                var requiresRebuild = doorwayAligned || update.AcceptedClosure is not null;
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
                if (!activeStore.Commit(
                        durableSnapshot,
                        priorSnapshot.MapRevision,
                        durableSnapshot.MapRevision))
                {
                    RollBackTracker(priorSnapshot);
                    return Fail("live-world snapshot CAS was stale");
                }
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                transactionObserver?.Invoke("commit");
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                lastWorldTrackerTimestamp = request.MonotonicTimestamp;
                placedRoomStartedAt ??= request.MonotonicTimestamp;
                var committed = durableSnapshot.Observations[^1];
                if (!projectedWalkedGround.TryGetValue(resolvedRoomId, out var publishedGround)
                    || requiresRebuild)
                {
                    publishedGround = LiveWorldWalkedGround.ProjectRoom(durableSnapshot, resolvedRoomId);
                }
                else if (groundEvidence.Count != 0)
                {
                    publishedGround = GroundStrokeGeometry.Coalesce(
                        publishedGround.Concat(LiveWorldWalkedGround.ProjectObservation(committed)));
                }
                projectedWalkedGround[resolvedRoomId] = publishedGround;
                walkedScope = scope;
                committedLocalWalk = groundEvidence.Count == 0
                    ? priorWalk : GroundStrokeGeometry.Coalesce(priorWalk.Concat(walked));

                if (requiresRebuild
                    && (!IsWorkCurrent(processEpoch)
                        || !worldBasis.ConfirmPlacement(
                            resolvedObservation.Value.Ticket,
                            committed.OptimizedPose)))
                {
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    throw new InvalidOperationException(
                        "The committed Atlas correction belonged to an expired world basis.");
                }
                if (requiresRebuild)
                {
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                    transactionObserver?.Invoke(doorwayAligned ? "doorway-basis" : "closure-basis");
                    if (!IsWorkCurrent(processEpoch)) return Cancelled();
                }

                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                var basis = worldBasis.Snapshot.WorldFromLocal;
                LastError = null;
                transactionObserver?.Invoke("publish");
                if (!IsWorkCurrent(processEpoch)) return Cancelled();
                return new WindowsLiveWorldPipelineResult(
                    WindowsLiveWorldPipelineState.Committed,
                    request.CaptureGeneration,
                    Corrected(request.LocalPose, basis),
                    new AtlasPoint(committed.OptimizedPose.X - worldPose.X, committed.OptimizedPose.Y - worldPose.Y),
                    basis,
                    durableSnapshot.MapRevision,
                    update.AcceptedClosure,
                    null,
                    rebuiltAtlas,
                    doorwayAligned ? "live-world doorway floor aligned"
                        : update.AcceptedClosure is null ? "live-world committed" : "live-world loop closure committed")
                {
                    WalkedGround = publishedGround,
                    WalkedCoordinateEpoch = request.WalkedCoordinateEpoch,
                    RecordedLocalWalk = committedLocalWalk
                };
            }
            catch (Exception error)
            {
                RollBackTracker(priorSnapshot);
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
            tracker.BeginRecoveryEpoch();
            recoveryDisposition = roomId is { } activeRoom
                                  && HasSearchableReference(tracker, activeRoom)
                ? RecoveryDisposition.SearchReference
                : tracker.Snapshot.Observations.Count == 0 ? RecoveryDisposition.AwaitSafeNewWorld
                : RecoveryDisposition.AwaitNewWorldBoundary;
            lastGlobalSearchAt = double.NegativeInfinity;
        }
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
            "live-world cancelled: active atlas generation changed");
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
            WalkedGround = projectedWalkedGround.TryGetValue(committedRoomId, out var ground) ? ground : [],
            WalkedCoordinateEpoch = request.WalkedCoordinateEpoch,
            RecordedLocalWalk = walkedScope == (request.CaptureGeneration, committedRoomId, request.WalkedCoordinateEpoch)
                ? committedLocalWalk : []
        };

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

    private bool ReloadLocked()
    {
        try
        {
            var reopened = new LiveWorldSessionStore(rootDirectory);
            var reopenedTracker = reopened.LoadTracker();
            var snapshot = reopenedTracker.Snapshot;
            var manifest = new SceneSessionStore(reopened.RootDirectory).Manifest;
            var reopenedCompositionExclusions = manifest.Observations.ToDictionary(
                observation => observation.Id,
                observation => CompositionRects(observation.ExcludedRects));
            store = reopened;
            tracker = reopenedTracker;
            importedRoomIds.Clear();
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
            captureGeneration = null;
            roomId = null;
            receiverRoomId = null;
            placedRoomStartedAt = null;
            doorwaySource = null;
            newRoomDoorway = null;
            recoveryDisposition = RecoveryDisposition.None;
            lastCaptureId = -1;
            lastGlobalSearchAt = double.NegativeInfinity;
            lastPersistenceAt = double.NegativeInfinity;
            lastWorldTrackerTimestamp = snapshot.Observations.Count == 0
                ? double.NegativeInfinity
                : snapshot.Observations.Max(value => value.Timestamp);
            Volatile.Write(ref importedAtlas, snapshot.UsesSwiftWireFormat);
            LastError = null;
            return true;
        }
        catch (Exception error)
        {
            store = null;
            tracker = null;
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

    private bool TryPlaceNewDoorwayRoom(WindowsLiveWorldFrame request, int targetId, LiveWorldSnapshot snapshot,
        LiveWorldRecoveryUpdate recovery)
    {
        if (recovery.MatchedReferenceKeyframeId is not null
            || recovery.Review.Rejections.Contains(LiveWorldTrackerRejection.AmbiguousPlacement))
            arrivalHasCompetingPlacement = true;

        if (arrivalHasCompetingPlacement || !request.StableTrackingVerified
            || request.DoorwayCut is not { } cut || doorwaySource is not { } source
            || !double.IsFinite(cut.DepartureTimestamp) || !double.IsFinite(cut.ArrivalTimestamp)
            || cut.DepartureTimestamp < source.StartedAt || cut.ArrivalTimestamp <= cut.DepartureTimestamp
            || request.MonotonicTimestamp - cut.ArrivalTimestamp is < 0 or > 3
            || cut.Direction is not (VisualRoomDirection.Left or VisualRoomDirection.Right)
            || snapshot.Observations.All(observation => observation.RoomId != source.RoomId)
            || snapshot.Observations.Any(observation => observation.RoomId == targetId))
        {
            return false;
        }

        if (examinedDoorway != cut)
        {
            examinedDoorway = cut;
            unmatchedArrivalSearches = 0;
        }
        if (++unmatchedArrivalSearches < 3) return false;

        // Keep an unverified room outside the saved images. A confirmed cut
        // supplies direction while the relative positions still need evidence.
        var margin = request.SolveWidth * .1;
        var worldX = cut.Direction == VisualRoomDirection.Right
            ? snapshot.Observations.Max(value => value.OptimizedPose.X + value.SolveWidth * .5)
                + margin + request.SolveWidth * .5
            : snapshot.Observations.Min(value => value.OptimizedPose.X - value.SolveWidth * .5)
                - margin - request.SolveWidth * .5;
        var basis = new AtlasPoint(worldX - request.LocalPose.X, worldBasis.Snapshot.WorldFromLocal.Y);
        if (!double.IsFinite(basis.X)) return false;

        worldBasis.BeginLocalEpoch(basis);
        newRoomDoorway = new LiveDoorwayTransition(source.RoomId, targetId, persistentCaptureGeneration,
            cut.DepartureTimestamp, cut.ArrivalTimestamp, cut.Direction);
        importedRoomIds[targetId] = targetId;
        roomId = targetId;
        recoveryDisposition = RecoveryDisposition.None;
        return true;
    }

    private static bool HasSearchableReference(LiveWorldTracker tracker, int roomId)
    {
        var snapshot = tracker.Snapshot;
        if (snapshot.UsesSwiftWireFormat) return tracker.HasRecoveryReference(roomId);

        var observations = snapshot.Observations
            .Where(value => value.RoomId == roomId)
            .Select(value => value.Id)
            .ToHashSet();
        return snapshot.Keyframes.Any(keyframe =>
            observations.Contains(keyframe.ObservationId)
            && snapshot.Landmarks.Count(value => value.KeyframeId == keyframe.Id) >= 6);
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
        foreach (var observation in snapshot.Observations.OrderBy(value => value.Id))
        {
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
