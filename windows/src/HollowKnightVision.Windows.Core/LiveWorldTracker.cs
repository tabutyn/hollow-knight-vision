namespace HollowKnightVision.Windows.Core;

public enum LiveWorldTrackerError
{
    DuplicateObservationId,
    InvalidScan,
    InvalidSnapshot
}

public sealed class LiveWorldTrackerException(
    LiveWorldTrackerError error,
    string message,
    int? observationId = null) : Exception(message)
{
    public LiveWorldTrackerError Error { get; } = error;
    public int? ObservationId { get; } = observationId;
}

public enum LiveWorldTrackerRejection
{
    NoEligibleReference,
    MatcherRejected,
    RecentOrOverlappingReference,
    ConfirmationRequired,
    InconsistentConfirmation,
    AmbiguousPlacement
}

public sealed record LiveWorldTrackerReviewPoint(
    int ObservationId,
    AtlasPoint RawPose,
    AtlasPoint OptimizedPose,
    bool IsKeyframe);

public sealed record LiveWorldTrackerMatchLine(
    int FromObservationId,
    int ToObservationId,
    AtlasPoint FromPose,
    AtlasPoint ToPose);

public sealed record LiveWorldTrackerReview(
    IReadOnlyList<LiveWorldTrackerReviewPoint> Points,
    IReadOnlyList<LiveWorldTrackerMatchLine> MatchLines,
    IReadOnlyList<LiveWorldTrackerRejection> Rejections);

public sealed record LiveWorldTrackerUpdate(
    AtlasPoint Correction,
    LiveWorldLoopClosureEdge? AcceptedClosure,
    LiveWorldSnapshot Snapshot,
    int Revision,
    LiveWorldTrackerReview Review);

/// <summary>
/// A confirmed placement of a live frame against the committed world.
/// Recovery evidence is transient and never becomes graph evidence.
/// </summary>
public sealed record LiveWorldRecoveryPlacement(
    AtlasPoint CameraPose,
    AtlasPoint Correction,
    IReadOnlyList<int> ConfirmationReferenceKeyframeIds)
{
    public int RoomId { get; init; }
}

public sealed record LiveWorldRecoveryUpdate(
    LiveWorldRecoveryPlacement? ConfirmedPlacement,
    int? MatchedReferenceKeyframeId,
    LiveWorldSnapshot Snapshot,
    int Revision,
    LiveWorldTrackerReview Review);

public static class LiveWorldRecoveryConfirmation
{
    public static IReadOnlyList<int>? ReferenceIds(
        int firstReferenceId,
        AtlasPoint firstCorrection,
        int secondReferenceId,
        AtlasPoint secondCorrection,
        double tolerance)
    {
        var correctionDistance = Distance(firstCorrection, secondCorrection);
        if (!double.IsFinite(tolerance)
            || tolerance < 0
            || !(correctionDistance <= tolerance))
        {
            return null;
        }
        return [firstReferenceId, secondReferenceId];
    }

    private static double Distance(AtlasPoint left, AtlasPoint right) => Math.Sqrt(
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
}

/// <summary>
/// In-memory coordinator for one room's sparse global camera graph. Captured
/// pixels are consumed only while accepting a scan; the snapshot retains
/// descriptors and pose evidence without retaining image bytes.
/// </summary>
public sealed class LiveWorldTracker
{
    private readonly record struct LocalEpochKey(
        ulong CaptureGeneration,
        ulong LocalEpoch);

    private sealed record CachedReference(
        int KeyframeId,
        int ObservationId,
        int RoomId,
        GlobalFeatureReference Reference);

    private sealed record PendingClosure(
        int ReferenceKeyframeId,
        AtlasPoint Correction);

    private sealed record PendingRecovery(
        int Epoch,
        int ReferenceKeyframeId,
        int RoomId,
        AtlasPoint Correction,
        int MissedFrames,
        double? CaptureTimestamp = null);

    private sealed record GroundRecoveryVote(int ReferenceIslandId, int TargetIslandId,
        double Timestamp, AtlasPoint Correction);

    private const double KeyframeTravelFraction = 0.25;
    private const double MinimumClosureTravelFraction = 0.75;
    private const int MaximumMissedRecoveryFrames = 4;
    private const double MaximumGroundConfirmationAge = 1.25;

    private readonly GlobalFeatureMatcher matcher = new();
    private readonly LiveGroundClosureSearch groundClosureSearch = new();
    private readonly Guid visitId = Guid.NewGuid();
    private readonly Dictionary<int, CachedReference> references = [];
    private readonly Dictionary<int, CachedReference?> restoredRecoveryReferences = [];
    private readonly Dictionary<int, double> cumulativeTravel = [];
    private readonly HashSet<int> closedReferenceKeyframeIds = [];
    private readonly HashSet<int> currentVisitObservationIds = [];
    private PendingClosure? pendingClosure;
    private PendingRecovery? pendingRecovery;
    private PendingRecovery? pendingGroundPlacement;
    private GroundRecoveryVote? groundRecoveryVote;
    private int recoveryEpoch;
    private double? lastAcceptedClosureTravel;
    private int? lastObservationId;

    public LiveWorldTracker()
        : this(new LiveWorldSnapshot()) { }

    public LiveWorldTracker(LiveWorldSnapshot snapshot) : this(snapshot, loadSavedFrame: null) { }

    internal LiveWorldTracker(LiveWorldSnapshot snapshot, Func<int, BgraFrame>? loadSavedFrame)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Snapshot.Validate();
        RestoreRuntimeState();
        if (snapshot.UsesSwiftWireFormat && loadSavedFrame is not null)
            RestoreRecoveryReferences(loadSavedFrame);
    }

    public LiveWorldSnapshot Snapshot { get; private set; }
    public bool LatestIslandAwaitsPlacement => Snapshot.TrackingIslands.LastOrDefault()?.RequiresPlacement == true;

    public bool HasRecoveryReference(int roomId) =>
        RecoveryReferences().Any(reference => reference.RoomId == roomId);

    public bool HasPlacedGroundReference(int roomId, double solveWidth) =>
        PlacedGroundReferences(roomId, solveWidth).Any();

    public bool CanConfirmGroundRecovery(double timestamp) =>
        groundRecoveryVote is { } vote && timestamp - vote.Timestamp is > 0 and <= .5;

    public void BeginVisit()
    {
        pendingClosure = null;
        BeginRecoveryEpoch();
        currentVisitObservationIds.Clear();
        closedReferenceKeyframeIds.Clear();
        lastAcceptedClosureTravel = null;
        lastObservationId = null;
    }

    /// <summary>
    /// Starts a fresh two-frame relocalization attempt. Recovery searches all
    /// committed references independently of pose-graph closure cooldowns.
    /// </summary>
    public void BeginRecoveryEpoch()
    {
        recoveryEpoch = recoveryEpoch < int.MaxValue ? recoveryEpoch + 1 : 0;
        pendingRecovery = null;
        pendingGroundPlacement = null;
        groundRecoveryVote = null;
        pendingClosure = null;
    }

    /// <summary>
    /// Searches committed keyframes for a transient world placement without
    /// retaining the frame or adding observations, keyframes, or graph edges.
    /// </summary>
    public LiveWorldRecoveryUpdate Recover(
        BgraFrame frame,
        AtlasPoint proposedCameraPose,
        double solveWidth,
        IReadOnlyList<StencilRect>? exclusions = null,
        int? roomId = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var excluded = exclusions?.ToArray() ?? [];
        if (frame.Width <= 0 || frame.Height <= 0
            || !Finite(proposedCameraPose)
            || !double.IsFinite(solveWidth) || solveWidth <= 0
            || excluded.Any(rectangle => !Valid(rectangle)))
        {
            throw Error(LiveWorldTrackerError.InvalidScan, "Live-world recovery scan is invalid.");
        }
        if (roomId < 0)
            throw Error(LiveWorldTrackerError.InvalidScan, "Live-world recovery room is invalid.");

        var eligible = RecoveryReferences()
            .Where(reference => roomId is null || reference.RoomId == roomId)
            .OrderBy(reference => reference.KeyframeId)
            .ToArray();
        if (eligible.Length == 0)
        {
            pendingRecovery = null;
            return RecoveryUpdate(null, null, [LiveWorldTrackerRejection.NoEligibleReference]);
        }

        var candidate = RecoveryMatch(frame, solveWidth, excluded, eligible, searchAcrossRooms: roomId is null);
        if (candidate is null)
        {
            MissedRecovery(ref pendingRecovery);
            return RecoveryUpdate(
                null,
                null,
                [roomId is null && matcher.LastMatchWasAmbiguous
                    ? LiveWorldTrackerRejection.AmbiguousPlacement : LiveWorldTrackerRejection.MatcherRejected]);
        }

        var (reference, match) = candidate.Value;
        return ConfirmRecovery(ref pendingRecovery, reference.KeyframeId, reference.RoomId,
            match.ProposedCameraPose, proposedCameraPose, solveWidth);
    }

    /// <summary>Uses two distinct, floor-verified captures to locate placed imagery without writing either capture.</summary>
    public LiveWorldRecoveryUpdate RecoverFromPlacedGround(
        BgraFrame maskedFrame, AtlasPoint proposedCameraPose, double solveWidth,
        LiveWorldCaptureEvidence evidence, IReadOnlyList<StencilRect> exclusions,
        Func<int, BgraFrame> loadImage, Func<int, IReadOnlyList<WorldRect>> loadExclusions,
        int? roomId)
    {
        var ground = evidence.FloorCandidates?.Lines ?? evidence.GroundGeometry?.Lines;
        if (!double.IsFinite(evidence.CaptureTimestamp) || ground is not { Count: > 0 }
            || !Finite(proposedCameraPose) || !double.IsFinite(solveWidth) || solveWidth <= 0 || roomId < 0)
            return RecoveryUpdate(null, null, [LiveWorldTrackerRejection.NoEligibleReference]);

        var timestamp = evidence.CaptureTimestamp;
        if (pendingGroundPlacement is { CaptureTimestamp: { } previous } vote && timestamp <= previous)
            return RecoveryUpdate(vote.ReferenceKeyframeId, null, [LiveWorldTrackerRejection.ConfirmationRequired]);

        var sources = PlacedGroundReferences(roomId, solveWidth).ToArray();
        if (sources.Length == 0)
        {
            pendingGroundPlacement = null;
            return RecoveryUpdate(null, null, [LiveWorldTrackerRejection.NoEligibleReference]);
        }

        var match = groundClosureSearch.Find(sources, maskedFrame, exclusions, ground, solveWidth,
            loadImage, loadExclusions, searchAcrossRooms: roomId is null);
        if (match is null)
        {
            if (groundClosureSearch.LastSearchWasAmbiguous)
            {
                pendingGroundPlacement = null;
                return RecoveryUpdate(null, null, [LiveWorldTrackerRejection.AmbiguousPlacement]);
            }
            MissedRecovery(ref pendingGroundPlacement);
            return RecoveryUpdate(null, null, [LiveWorldTrackerRejection.MatcherRejected]);
        }

        var matchedRoom = sources.First(source => source.KeyframeId == match.KeyframeId).Observation.RoomId;
        return ConfirmRecovery(ref pendingGroundPlacement, match.KeyframeId, matchedRoom,
            match.CameraPose, proposedCameraPose, solveWidth, timestamp);
    }

    private LiveWorldRecoveryUpdate ConfirmRecovery(ref PendingRecovery? pending, int keyframeId, int roomId,
        AtlasPoint measuredPose, AtlasPoint proposedPose, double solveWidth, double? timestamp = null)
    {
        var correction = Difference(measuredPose, proposedPose);
        var previous = pending;
        pending = new(recoveryEpoch, keyframeId, roomId, correction, 0, timestamp);
        if (previous is null || previous.Epoch != recoveryEpoch
            || (timestamp - previous.CaptureTimestamp) > MaximumGroundConfirmationAge)
            return RecoveryUpdate(keyframeId, null, [LiveWorldTrackerRejection.ConfirmationRequired]);

        var references = LiveWorldRecoveryConfirmation.ReferenceIds(previous.ReferenceKeyframeId,
            previous.Correction, keyframeId, correction, Math.Max(solveWidth * 0.0125, 4));
        if (references is null || previous.RoomId != roomId)
            return RecoveryUpdate(keyframeId, null, [LiveWorldTrackerRejection.InconsistentConfirmation]);

        pending = null;
        return RecoveryUpdate(keyframeId, new(measuredPose, correction, references) { RoomId = roomId }, []);
    }

    private void MissedRecovery(ref PendingRecovery? state)
    {
        state = state is { } pending && pending.Epoch == recoveryEpoch
            && pending.MissedFrames < MaximumMissedRecoveryFrames
            ? pending with { MissedFrames = pending.MissedFrames + 1 } : null;
    }

    public LiveWorldTrackerUpdate Ingest(
        int observationId,
        double timestamp,
        BgraFrame frame,
        AtlasPoint proposedCameraPose,
        double solveWidth,
        int? sourceObservationId = null,
        IReadOnlyList<StencilRect>? exclusions = null,
        bool searchForClosure = true,
        AtlasPoint? localPose = null,
        ulong captureGeneration = 0,
        ulong localEpoch = 0,
        ulong basisRevision = 0,
        int roomId = 0,
        AtlasPoint? placedCameraPose = null,
        IReadOnlyList<LiveWorldWalkedFloor>? walkedGround = null,
        LiveWorldCaptureEvidence? captureEvidence = null,
        bool placementPending = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var excluded = exclusions?.ToArray() ?? [];
        if (observationId < 0
            || !double.IsFinite(timestamp)
            || frame.Width <= 0 || frame.Height <= 0
            || !Finite(proposedCameraPose)
            || placedCameraPose is { } placement && !Finite(placement)
            || !double.IsFinite(solveWidth) || solveWidth <= 0
            || roomId < 0
            || (placementPending && captureEvidence is null)
            || excluded.Any(rectangle => !Valid(rectangle)))
        {
            throw Error(LiveWorldTrackerError.InvalidScan, "Live-world scan is invalid.");
        }
        if (Snapshot.Observations.Any(value => value.Id == observationId))
        {
            throw Error(
                LiveWorldTrackerError.DuplicateObservationId,
                $"Observation {observationId} already exists.",
                observationId);
        }

        var sourceId = sourceObservationId ?? observationId;
        if (sourceId < 0)
        {
            throw Error(LiveWorldTrackerError.InvalidScan, "Source observation is invalid.");
        }

        var travel = NextCumulativeTravel(proposedCameraPose, roomId);
        var observation = LiveWorldObservation.Create(
            observationId,
            sourceId,
            timestamp,
            frame.Width,
            frame.Height,
            solveWidth,
            160,
            excluded.Select(rectangle => new WorldRect(
                new AtlasPoint(rectangle.X, rectangle.Y),
                new WorldSize(rectangle.Width, rectangle.Height))).ToArray(),
            proposedCameraPose,
            placedCameraPose ?? proposedCameraPose,
            localPose ?? proposedCameraPose,
            captureGeneration,
            localEpoch,
            basisRevision,
            roomId) with
        {
            WalkedGround = walkedGround is { Count: > 0 } ? walkedGround.ToArray() : null,
            CaptureTimestamp = captureEvidence?.CaptureTimestamp,
            GroundGeometry = captureEvidence?.GroundGeometry,
            ObservedFloorCandidates = captureEvidence?.FloorCandidates,
            ObservedPlayerBounds = captureEvidence?.PlayerBounds
        };
        ReplaceSnapshot(observations: [.. Snapshot.Observations, observation],
            trackingIslands: CurrentTrackingIslands(observation, placementPending));
        currentVisitObservationIds.Add(observationId);
        cumulativeTravel[observationId] = travel;
        lastObservationId = observationId;

        if (Snapshot.Keyframes.Count == 0)
        {
            AddKeyframe(observation, frame);
            pendingClosure = null;
            return Update(
                new AtlasPoint(0, 0),
                null,
                [LiveWorldTrackerRejection.NoEligibleReference]);
        }

        if (!searchForClosure)
        {
            if (ShouldCreateKeyframe(observation, travel)) AddKeyframe(observation, frame);
            return Update(new AtlasPoint(0, 0), null, []);
        }

        var rejections = new List<LiveWorldTrackerRejection>();
        var eligible = EligibleReferences(observation, travel);
        var allOlder = references.Values.Where(value =>
            value.ObservationId != observationId && value.RoomId == roomId).ToArray();
        if (eligible.Count == 0)
        {
            rejections.Add(allOlder.Length == 0
                ? LiveWorldTrackerRejection.NoEligibleReference
                : LiveWorldTrackerRejection.RecentOrOverlappingReference);
            if (ShouldCreateKeyframe(observation, travel)) AddKeyframe(observation, frame);
            pendingClosure = null;
            return Update(new AtlasPoint(0, 0), null, rejections);
        }

        Guid? preferredReferenceId = pendingClosure is null
            ? null
            : ReferenceId(pendingClosure.ReferenceKeyframeId);
        var match = matcher.Match(
            frame,
            eligible.Select(value => value.Reference).ToArray(),
            preferredReferenceId,
            solveWidth,
            excluded);
        var reference = match is null
            ? null
            : eligible.FirstOrDefault(value => value.Reference.Id == match.ReferenceId);
        if (match is null || reference is null)
        {
            rejections.Add(LiveWorldTrackerRejection.MatcherRejected);
            if (ShouldCreateKeyframe(observation, travel)) AddKeyframe(observation, frame);
            pendingClosure = null;
            return Update(new AtlasPoint(0, 0), null, rejections);
        }

        var proposedCorrection = new AtlasPoint(
            match.ProposedCameraPose.X - observation.RawPose.X,
            match.ProposedCameraPose.Y - observation.RawPose.Y);
        var consistency = Math.Max(solveWidth * 0.0125, 4);
        if (pendingClosure is null)
        {
            pendingClosure = new PendingClosure(reference.KeyframeId, proposedCorrection);
            rejections.Add(LiveWorldTrackerRejection.ConfirmationRequired);
            if (ShouldCreateKeyframe(observation, travel)) AddKeyframe(observation, frame);
            return Update(
                new AtlasPoint(0, 0),
                null,
                rejections,
                reference,
                match);
        }
        if (pendingClosure.ReferenceKeyframeId != reference.KeyframeId
            || Distance(pendingClosure.Correction, proposedCorrection) > consistency)
        {
            pendingClosure = new PendingClosure(reference.KeyframeId, proposedCorrection);
            rejections.Add(LiveWorldTrackerRejection.InconsistentConfirmation);
            if (ShouldCreateKeyframe(observation, travel)) AddKeyframe(observation, frame);
            return Update(
                new AtlasPoint(0, 0),
                null,
                rejections,
                reference,
                match);
        }

        pendingClosure = null;
        LiveIslandComponents? components = null;
        LiveIslandComponents.LinkPlan? plan = null;
        if (placementPending || Snapshot.TrackingIslands.LastOrDefault()?.Placement == LiveIslandPlacement.Connected)
        {
            components = new(Snapshot);
            plan = PlanLink(components, reference.ObservationId, observation, match.ProposedCameraPose);
            if (plan is null)
                return Update(default, null, [LiveWorldTrackerRejection.InconsistentConfirmation], reference, match);
        }
        var currentKeyframeId = AddKeyframe(observation, frame);
        if (!Snapshot.Observations.Any(value => value.Id == reference.ObservationId)
            || match.InlierReferenceFeatureIds.Count < 3)
        {
            throw Error(
                LiveWorldTrackerError.InvalidSnapshot,
                "Accepted closure references invalid graph evidence.");
        }
        var closure = new LiveWorldLoopClosureEdge(
            NextId(Snapshot.LoopClosureEdges.Select(value => value.Id)),
            reference.KeyframeId,
            currentKeyframeId,
            match.InlierReferenceFeatureIds.ToArray(),
            match.ProposedCameraPose.X - reference.Reference.CameraPose.X,
            match.ProposedCameraPose.Y - reference.Reference.CameraPose.Y,
            match.InlierReferenceFeatureIds.Count,
            match.AmbiguityRatio);
        closedReferenceKeyframeIds.Add(reference.KeyframeId);
        lastAcceptedClosureTravel = travel;
        ReplaceSnapshot(loopEdges: [.. Snapshot.LoopClosureEdges, closure]);
        AtlasPoint correction;
        if (components is not null && plan is not null)
        {
            ApplyIslandLink(components, plan);
            correction = Difference(Snapshot.Observations[^1].OptimizedPose, observation.RawPose);
        }
        else
            correction = Snapshot.TrackingIslands.Count != 0
                ? PlaceNewObservation(observation, match.ProposedCameraPose) : Optimize(observationId);
        return Update(correction, closure, [], reference, match);
    }

    public bool AlignNewRoomThroughDoorway(LiveDoorwayTransition transition)
    {
        if (Snapshot.TrackingIslands.Count == 0 || LiveDoorwayFloorEvidence.Propose(Snapshot, transition) is not { } floor)
            return false;

        var observations = Snapshot.Observations.ToDictionary(value => value.Id);
        var arrivalIsland = Snapshot.TrackingIslands.First(island =>
            island.Members.Any(member => member.ObservationId == floor.ArrivalObservationId));
        // Only the current, previously unmapped island can move here. Its
        // predecessors and any already connected islands keep their poses.
        var members = arrivalIsland.Members.Select(member => member.ObservationId).ToHashSet();
        var endpointIsCurrent = members.Contains(Snapshot.Observations[^1].Id);
        if (!endpointIsCurrent || arrivalIsland.Placement != LiveIslandPlacement.Estimated
            || Snapshot.DoorwayFloorConstraints.Any(edge =>
                members.Contains(edge.DepartureObservationId) || members.Contains(edge.ArrivalObservationId)))
        {
            return false;
        }

        var source = observations[floor.DepartureObservationId];
        var target = observations[floor.ArrivalObservationId];
        var translationY = source.OptimizedPose.Y + floor.DepartureFloor.Y - target.OptimizedPose.Y - floor.ArrivalFloor.Y;
        var poses = observations.ToDictionary(pair => pair.Key, pair => members.Contains(pair.Key)
            ? pair.Value.OptimizedPose with { Y = pair.Value.OptimizedPose.Y + translationY }
            : pair.Value.OptimizedPose);
        var aligned = Snapshot.ReplaceOptimizedPoses(poses, Snapshot.MapRevision);
        Snapshot = new LiveWorldSnapshot(aligned.UsesSwiftWireFormat ? LiveWorldSnapshot.CurrentSchemaVersion : aligned.SchemaVersion,
            aligned.MapRevision, aligned.AnchoredObservationId,
            aligned.Observations, aligned.Keyframes, aligned.Landmarks, aligned.RelativeMotionEdges, aligned.LoopClosureEdges,
            aligned.TrackingIslands, [.. aligned.DoorwayFloorConstraints, floor]);
        RefreshReferences();
        return true;
    }

    private IReadOnlyList<LiveTrackingIsland> CurrentTrackingIslands(LiveWorldObservation observation, bool placementPending)
    {
        var islands = Snapshot.TrackingIslands;
        if (islands.Count == 0 && !Snapshot.UsesSwiftWireFormat)
        {
            if (observation.CaptureTimestamp is null) return islands;

            // Previously saved Windows observations have a placement but no
            // durable island membership. Keep those placements as independent
            // owners until there is evidence to relate them.
            islands = Snapshot.Observations.Select(value => LiveTrackingIsland.Legacy(value.Id, value)).ToArray();
        }

        if (observation.CaptureTimestamp is not { } captureTimestamp)
            return [.. islands, LiveTrackingIsland.Legacy(NextId(islands.Select(value => value.Id)), observation)];

        var member = new LiveIslandMember(observation.Id, observation.LocalPose, Measured: true);
        var basis = new AtlasPoint(
            observation.OptimizedPose.X - observation.LocalPose.X,
            observation.OptimizedPose.Y - observation.LocalPose.Y);
        var last = islands.LastOrDefault();
        var previous = Snapshot.Observations.LastOrDefault();
        if (last is { Placement: LiveIslandPlacement.Estimated or LiveIslandPlacement.Connected }
            && last.RequiresPlacement == placementPending && previous is { CaptureTimestamp: { } priorTime }
            && currentVisitObservationIds.Contains(previous.Id)
            && last.Members[^1].ObservationId == previous.Id
            && last.RoomId == observation.RoomId
            && previous.CaptureGeneration == observation.CaptureGeneration
            && previous.LocalEpoch == observation.LocalEpoch
            && captureTimestamp - priorTime is > 0 and <= .35
            && Distance(last.WorldFromLocal, basis) <= .000001)
        {
            return [.. islands.Take(islands.Count - 1), last with { Members = [.. last.Members, member] }];
        }

        var id = NextId(islands.Select(value => value.Id));
        return [.. islands, new LiveTrackingIsland(id, observation.RoomId, observation.Timestamp,
            basis, LiveIslandPlacement.Estimated, default,
            placementPending ? LiveTrackingIsland.RecoveryBreakReason : "measured-local-tracking", [member])];
    }

    private IReadOnlyList<CachedReference> EligibleReferences(
        LiveWorldObservation observation,
        double travelDistance)
    {
        var minimumTravel = Math.Max(
            observation.SolveWidth * MinimumClosureTravelFraction,
            1);
        var minimumTravelAfterClosure = Math.Max(
            observation.SolveWidth * KeyframeTravelFraction,
            1);
        var placementPending = Snapshot.TrackingIslands.LastOrDefault()?.RequiresPlacement == true;
        if (!placementPending && lastAcceptedClosureTravel is { } lastClosure
            && travelDistance - lastClosure < minimumTravelAfterClosure)
        {
            return [];
        }

        var observations = Snapshot.Observations.ToDictionary(value => value.Id);
        var keyframes = Snapshot.Keyframes.ToDictionary(value => value.Id);
        var currentIslandMembers = Snapshot.TrackingIslands.Count != 0
            ? Snapshot.TrackingIslands[^1].Members.Select(member => member.ObservationId).ToHashSet() : null;
        if (currentIslandMembers is not null && Snapshot.DoorwayFloorConstraints.Any(edge =>
            currentIslandMembers.Contains(edge.DepartureObservationId)
            || currentIslandMembers.Contains(edge.ArrivalObservationId)))
        {
            return [];
        }
        var unplaced = Snapshot.UnplacedObservationIds();
        return references.Values.Where(candidate =>
        {
            if (!observations.TryGetValue(candidate.ObservationId, out var referenced)
                || !keyframes.TryGetValue(candidate.KeyframeId, out var keyframe)
                || keyframe.ObservationId != candidate.ObservationId
                || referenced.Id >= observation.Id
                || !cumulativeTravel.TryGetValue(candidate.ObservationId, out var referenceTravel))
            {
                return false;
            }
            return candidate.RoomId == observation.RoomId
                && currentIslandMembers?.Contains(candidate.ObservationId) != true
                && !unplaced.Contains(candidate.ObservationId)
                && (placementPending || (!closedReferenceKeyframeIds.Contains(candidate.KeyframeId)
                    && (!currentVisitObservationIds.Contains(candidate.ObservationId)
                        || travelDistance - referenceTravel >= minimumTravel)));
        }).OrderBy(value => value.KeyframeId).ToArray();
    }

    private bool ShouldCreateKeyframe(
        LiveWorldObservation observation,
        double travel)
    {
        var roomObservationIds = Snapshot.Observations
            .Where(value => value.RoomId == observation.RoomId)
            .Select(value => value.Id)
            .ToHashSet();
        var last = Snapshot.Keyframes.LastOrDefault(value =>
            currentVisitObservationIds.Contains(value.ObservationId)
            && value.ObservationId != observation.Id
            && roomObservationIds.Contains(value.ObservationId));
        if (last is null
            || !cumulativeTravel.TryGetValue(last.ObservationId, out var lastTravel))
        {
            return true;
        }
        return travel - lastTravel >= observation.SolveWidth * KeyframeTravelFraction;
    }

    private int AddKeyframe(LiveWorldObservation observation, BgraFrame frame)
    {
        var existing = Snapshot.Keyframes.FirstOrDefault(
            value => value.ObservationId == observation.Id);
        if (existing is not null) return existing.Id;

        var roomObservationIds = Snapshot.Observations
            .Where(value => value.RoomId == observation.RoomId)
            .Select(value => value.Id)
            .ToHashSet();
        var keyframeId = observation.Id;
        var prior = Snapshot.Keyframes.LastOrDefault(value =>
            currentVisitObservationIds.Contains(value.ObservationId)
            && value.ObservationId != observation.Id
            && roomObservationIds.Contains(value.ObservationId));
        var keyframe = new LiveWorldKeyframe(
            keyframeId,
            observation.Id,
            observation.SourceObservationId);
        var motionEdges = Snapshot.RelativeMotionEdges.ToList();
        if (prior is not null)
        {
            var priorObservation = Snapshot.Observations.FirstOrDefault(
                value => value.Id == prior.ObservationId);
            if (priorObservation is not null
                && priorObservation.RoomId == observation.RoomId
                && priorObservation.CaptureGeneration == observation.CaptureGeneration
                && priorObservation.LocalEpoch == observation.LocalEpoch)
            {
                motionEdges.Add(new LiveWorldRelativeMotionEdge(
                    NextId(motionEdges.Select(value => value.Id)),
                    priorObservation.Id,
                    observation.Id,
                    observation.LocalPose.X - priorObservation.LocalPose.X,
                    observation.LocalPose.Y - priorObservation.LocalPose.Y,
                    1));
            }
        }

        var landmarks = Snapshot.Landmarks.ToList();
        var featureReference = matcher.MakeReference(
            ReferenceId(keyframeId),
            frame,
            observation.OptimizedPose,
            observation.SolveWidth,
            excluding: observation.ExcludedRects.Select(rectangle => new StencilRect(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height)).ToArray());
        if (featureReference is not null)
        {
            foreach (var feature in featureReference.Features)
            {
                var landmarkId = NextId(landmarks.Select(value => value.Id));
                var sampleScale = observation.SolveWidth / featureReference.SampleWidth;
                landmarks.Add(new LiveWorldLandmark(
                    landmarkId,
                    keyframeId,
                    observation.SourceObservationId,
                    feature.SamplePoint,
                    new AtlasPoint(
                        observation.OptimizedPose.X + feature.SamplePoint.X * sampleScale,
                        observation.OptimizedPose.Y
                            + (featureReference.SampleHeight - feature.SamplePoint.Y) * sampleScale),
                    feature.Descriptor.Select(value => (float)value).ToArray(),
                    feature.Depth,
                    feature.DepthConfidence));
            }
        }
        ReplaceSnapshot(
            keyframes: [.. Snapshot.Keyframes, keyframe],
            landmarks: landmarks,
            relativeEdges: motionEdges);
        var rebuilt = ReferenceFor(keyframe);
        if (rebuilt is not null) references[keyframe.Id] = rebuilt;
        return keyframeId;
    }

    /// <summary>
    /// Records a rigid link after two captures independently verify the floor
    /// of another component in the current room.
    /// </summary>
    public LiveWorldLoopClosureEdge? LinkGroundRecovery(
        BgraFrame maskedFrame, IReadOnlyList<StencilRect> exclusions,
        Func<int, BgraFrame> loadImage, Func<int, IReadOnlyList<WorldRect>> loadExclusions,
        bool isConfirmationProbe)
    {
        if (Snapshot.TrackingIslands.LastOrDefault() is not { RequiresPlacement: true } target
            || Snapshot.Observations.LastOrDefault() is not { CaptureTimestamp: { } timestamp } current
            || (current.ObservedFloorCandidates?.Lines ?? current.GroundGeometry?.Lines) is not { Count: > 0 } ground)
            return null;
        if (groundRecoveryVote is { } previous && timestamp <= previous.Timestamp) return null;

        var components = new LiveIslandComponents(Snapshot);
        var observations = Snapshot.Observations.Where(observation => observation.RoomId == current.RoomId
            && observation.SolveWidth == current.SolveWidth && observation.GroundGeometry?.Lines.Count > 0
            && components.Owner(observation.Id) is { } owner
            && !components.SameRigidComponent(owner.Id, target.Id)).ToDictionary(observation => observation.Id);
        var sources = new List<LiveGroundClosureSearch.Source>();
        foreach (var keyframe in Snapshot.Keyframes)
            if (observations.TryGetValue(keyframe.ObservationId, out var observation))
                sources.Add(new(keyframe.Id, observation));

        var match = groundClosureSearch.Find(sources, maskedFrame, exclusions,
            ground, current.SolveWidth, loadImage, loadExclusions);
        if (match is null)
        {
            if (!isConfirmationProbe || !CanConfirmGroundRecovery(timestamp))
                groundRecoveryVote = null;
            return null;
        }

        var source = sources.First(candidate => candidate.KeyframeId == match.KeyframeId).Observation;
        var referenceIsland = components.Owner(source.Id)!;
        var correction = Difference(match.CameraPose, current.RawPose);
        var consistency = Math.Max(current.SolveWidth * .0125, 4);
        var confirmed = groundRecoveryVote is { } vote && vote.TargetIslandId == target.Id
            && timestamp - vote.Timestamp <= .5 && Distance(vote.Correction, correction) <= consistency
            && components.SameRigidComponent(vote.ReferenceIslandId, referenceIsland.Id);
        groundRecoveryVote = confirmed ? null : new(referenceIsland.Id, target.Id, timestamp, correction);
        if (!confirmed || PlanLink(components, source.Id, current, match.CameraPose) is not { } plan)
            return null;

        var targetKeyframe = AddKeyframe(current, maskedFrame);
        var landmarks = Snapshot.Landmarks.ToList();
        var sourceLandmarks = landmarks.Where(landmark => landmark.KeyframeId == match.KeyframeId).ToList();
        var support = new List<int>();
        var nextLandmark = NextId(landmarks.Select(landmark => landmark.Id));
        var inliers = match.Inliers.ToHashSet();
        foreach (var feature in match.Reference.Features.Where(feature => inliers.Contains(feature.Id)))
        {
            var descriptor = feature.Descriptor.Select(value => (float)value).ToArray();
            var landmark = sourceLandmarks.FirstOrDefault(item =>
                item.SamplePoint == feature.SamplePoint && item.Descriptor.SequenceEqual(descriptor));
            if (landmark is null)
            {
                var scale = source.SolveWidth / match.Reference.SampleWidth;
                landmark = new LiveWorldLandmark(nextLandmark++, match.KeyframeId, source.SourceObservationId,
                    feature.SamplePoint, new AtlasPoint(
                        source.OptimizedPose.X + feature.SamplePoint.X * scale,
                        source.OptimizedPose.Y + (match.Reference.SampleHeight - feature.SamplePoint.Y) * scale),
                    descriptor, feature.Depth, feature.DepthConfidence) { GroundClosureOnly = true };
                landmarks.Add(landmark);
                sourceLandmarks.Add(landmark);
            }
            support.Add(landmark.Id);
        }
        var delta = Difference(match.CameraPose, source.OptimizedPose);
        var closure = new LiveWorldLoopClosureEdge(NextId(Snapshot.LoopClosureEdges.Select(edge => edge.Id)),
            match.KeyframeId, targetKeyframe, support, delta.X, delta.Y, support.Count, match.Ambiguity)
            { ResidualPixels = match.ResidualPixels };
        ReplaceSnapshot(landmarks: landmarks, loopEdges: [.. Snapshot.LoopClosureEdges, closure]);
        ApplyIslandLink(components, plan);
        return closure;
    }

    public bool PlaceRecoveredIsland(LiveWorldRecoveryPlacement placement)
    {
        if (Snapshot.TrackingIslands.LastOrDefault()?.RequiresPlacement != true
            || Snapshot.Observations.LastOrDefault() is not { } frame
            || placement.RoomId != frame.RoomId)
        {
            return false;
        }

        var components = new LiveIslandComponents(Snapshot);
        if (placement.ConfirmationReferenceKeyframeIds.Count == 0
            || Snapshot.Keyframes.FirstOrDefault(keyframe => keyframe.Id == placement.ConfirmationReferenceKeyframeIds[^1])
                is not { } reference
            || PlanLink(components, reference.ObservationId, frame, placement.CameraPose) is not { } plan)
            return false;
        ApplyIslandLink(components, plan);
        return true;
    }

    private static LiveIslandComponents.LinkPlan? PlanLink(LiveIslandComponents components,
        int source, LiveWorldObservation target, AtlasPoint cameraPose) =>
        components.Owner(source) is { } from && components.Owner(target.Id) is { } to
            ? components.Plan(from.Id, to.Id, Difference(cameraPose, target.OptimizedPose),
                Math.Max(target.SolveWidth * (4.0 / 640), 0.5))
            : null;

    private void ApplyIslandLink(LiveIslandComponents components, LiveIslandComponents.LinkPlan plan)
    {
        if (plan.Shifts.Values.Any(shift => shift != default))
        {
            var poses = Snapshot.Observations.ToDictionary(observation => observation.Id, observation =>
            {
                var shift = plan.Shifts.GetValueOrDefault(components.Owner(observation.Id)!.Id);
                return new AtlasPoint(observation.OptimizedPose.X + shift.X, observation.OptimizedPose.Y + shift.Y);
            });
            Snapshot = Snapshot.ReplaceOptimizedPoses(poses, Snapshot.MapRevision);
        }
        if (Snapshot.TrackingIslands.Any(island => island.RequiresPlacement && plan.PlacedIslands.Contains(island.Id)))
        {
            ReplaceSnapshot(trackingIslands: Snapshot.TrackingIslands.Select(island =>
                island.RequiresPlacement && plan.PlacedIslands.Contains(island.Id)
                    ? island with { Placement = LiveIslandPlacement.Connected } : island).ToArray());
        }
        RefreshReferences();
    }

    private static AtlasPoint Difference(AtlasPoint a, AtlasPoint b) => new(a.X - b.X, a.Y - b.Y);

    private AtlasPoint PlaceNewObservation(LiveWorldObservation observation, AtlasPoint measuredPose)
    {
        // Move the current island as a unit. The stored members keep their
        // relative poses and older islands retain their saved placement.
        var poses = Snapshot.Observations.ToDictionary(value => value.Id, value => value.OptimizedPose);
        var correction = new AtlasPoint(measuredPose.X - observation.OptimizedPose.X,
            measuredPose.Y - observation.OptimizedPose.Y);
        foreach (var member in Snapshot.TrackingIslands[^1].Members)
        {
            var previous = poses[member.ObservationId];
            poses[member.ObservationId] = new AtlasPoint(previous.X + correction.X, previous.Y + correction.Y);
        }
        Snapshot = Snapshot.ReplaceOptimizedPoses(poses, Snapshot.MapRevision);
        RefreshReferences();
        return new AtlasPoint(measuredPose.X - observation.RawPose.X, measuredPose.Y - observation.RawPose.Y);
    }

    private AtlasPoint Optimize(int observationId)
    {
        var currentObservation = Snapshot.Observations.FirstOrDefault(
            value => value.Id == observationId)
            ?? throw Error(
                LiveWorldTrackerError.InvalidSnapshot,
                "Current observation is missing from the graph.");
        var roomId = currentObservation.RoomId;
        var roomObservationIds = Snapshot.Observations
            .Where(value => value.RoomId == roomId)
            .Select(value => value.Id)
            .ToHashSet();
        var roomKeyframes = Snapshot.Keyframes.Where(value =>
            roomObservationIds.Contains(value.ObservationId)).ToArray();
        if (roomKeyframes.Length == 0)
        {
            throw Error(LiveWorldTrackerError.InvalidSnapshot, "Room has no keyframe anchor.");
        }
        var anchor = roomKeyframes[0].ObservationId;
        var keyframeObservationIds = roomKeyframes
            .Select(value => value.ObservationId).ToHashSet();
        var nodes = roomKeyframes.Select(keyframe =>
        {
            var observation = Snapshot.Observations.FirstOrDefault(
                value => value.Id == keyframe.ObservationId)
                ?? throw Error(
                    LiveWorldTrackerError.InvalidSnapshot,
                    "Keyframe observation is missing.");
            return new TranslationPoseNode(
                observation.Id,
                observation.RawPose,
                observation.OptimizedPose);
        }).ToArray();
        var constraints = new List<TranslationPoseConstraint>();
        foreach (var pair in Snapshot.RelativeMotionEdges
                     .OrderBy(value => value.Id)
                     .Select((edge, index) => (Edge: edge, Index: index)))
        {
            if (!keyframeObservationIds.Contains(pair.Edge.FromObservationId)
                || !keyframeObservationIds.Contains(pair.Edge.ToObservationId))
            {
                continue;
            }
            constraints.Add(new TranslationPoseConstraint(
                pair.Index,
                pair.Edge.FromObservationId,
                pair.Edge.ToObservationId,
                new AtlasPoint(pair.Edge.DeltaX, pair.Edge.DeltaY),
                pair.Edge.Support,
                TranslationPoseConstraintKind.Motion));
        }
        var keyframes = Snapshot.Keyframes.ToDictionary(value => value.Id);
        foreach (var edge in Snapshot.LoopClosureEdges.OrderBy(value => value.Id))
        {
            if (!keyframes.TryGetValue(edge.FromKeyframeId, out var from)
                || !keyframes.TryGetValue(edge.ToKeyframeId, out var to))
            {
                throw Error(
                    LiveWorldTrackerError.InvalidSnapshot,
                    "Loop closure references a missing keyframe.");
            }
            if (!keyframeObservationIds.Contains(from.ObservationId)
                || !keyframeObservationIds.Contains(to.ObservationId))
            {
                continue;
            }
            constraints.Add(new TranslationPoseConstraint(
                constraints.Count,
                from.ObservationId,
                to.ObservationId,
                new AtlasPoint(edge.DeltaX, edge.DeltaY),
                Math.Max(1, edge.Support * 8.0),
                TranslationPoseConstraintKind.LoopClosure));
        }

        var solution = new TranslationPoseGraph(
            nodes,
            constraints,
            anchor,
            Snapshot.MapRevision).Solve();
        var roomObservations = Snapshot.Observations
            .Where(value => value.RoomId == roomId).ToArray();
        var epochIds = roomObservations
            .Select(value => new LocalEpochKey(
                value.CaptureGeneration,
                value.LocalEpoch))
            .Distinct()
            .ToDictionary(value => value, _ => Guid.NewGuid());
        var samples = roomKeyframes.Select(keyframe =>
        {
            var observation = Snapshot.Observations.FirstOrDefault(
                value => value.Id == keyframe.ObservationId);
            if (observation is null
                || !solution.Positions.TryGetValue(observation.Id, out var position)
                || !epochIds.TryGetValue(new LocalEpochKey(
                    observation.CaptureGeneration,
                    observation.LocalEpoch), out var epochId))
            {
                return (PoseCorrectionSample?)null;
            }
            return new PoseCorrectionSample(
                observation.Id,
                epochId,
                observation.Timestamp,
                new AtlasPoint(
                    position.X - observation.RawPose.X,
                    position.Y - observation.RawPose.Y));
        }).OfType<PoseCorrectionSample>().ToArray();
        var optimized = Snapshot.Observations.ToDictionary(
            value => value.Id,
            value => value.OptimizedPose);
        var sampledEpochs = samples.Select(sample => sample.VisitId).ToHashSet();
        foreach (var observation in roomObservations)
        {
            var epochId = epochIds.GetValueOrDefault(
                new LocalEpochKey(
                    observation.CaptureGeneration,
                    observation.LocalEpoch),
                visitId);
            if (!sampledEpochs.Contains(epochId)) continue;
            optimized[observation.Id] = PoseCorrectionInterpolator.RefinedPose(
                observation.RawPose,
                observation.Timestamp,
                epochId,
                samples);
        }
        Snapshot = Snapshot.ReplaceOptimizedPoses(optimized, Snapshot.MapRevision);
        RefreshReferences();
        var current = Snapshot.Observations.FirstOrDefault(value => value.Id == observationId)
            ?? throw Error(
                LiveWorldTrackerError.InvalidSnapshot,
                "Optimized observation is missing.");
        return new AtlasPoint(
            current.OptimizedPose.X - current.RawPose.X,
            current.OptimizedPose.Y - current.RawPose.Y);
    }

    private LiveWorldTrackerUpdate Update(
        AtlasPoint correction,
        LiveWorldLoopClosureEdge? closure,
        IReadOnlyList<LiveWorldTrackerRejection> rejections,
        CachedReference? matchedReference = null,
        GlobalFeatureMatch? match = null)
    {
        var keyframeObservationIds = Snapshot.Keyframes
            .Select(value => value.ObservationId).ToHashSet();
        var points = Snapshot.Observations.Select(value =>
            new LiveWorldTrackerReviewPoint(
                value.Id,
                value.RawPose,
                value.OptimizedPose,
                keyframeObservationIds.Contains(value.Id))).ToArray();
        var keyframes = Snapshot.Keyframes.ToDictionary(value => value.Id);
        var observations = Snapshot.Observations.ToDictionary(value => value.Id);
        var lines = new List<LiveWorldTrackerMatchLine>();
        foreach (var edge in Snapshot.LoopClosureEdges)
        {
            if (!keyframes.TryGetValue(edge.FromKeyframeId, out var from)
                || !keyframes.TryGetValue(edge.ToKeyframeId, out var to)
                || !observations.TryGetValue(from.ObservationId, out var fromObservation)
                || !observations.TryGetValue(to.ObservationId, out var toObservation))
            {
                continue;
            }
            lines.Add(new LiveWorldTrackerMatchLine(
                fromObservation.Id,
                toObservation.Id,
                fromObservation.OptimizedPose,
                toObservation.OptimizedPose));
        }
        if (matchedReference is not null
            && match is not null
            && Snapshot.Observations.LastOrDefault() is { } current)
        {
            lines.Add(new LiveWorldTrackerMatchLine(
                matchedReference.ObservationId,
                current.Id,
                matchedReference.Reference.CameraPose,
                match.ProposedCameraPose));
        }
        return new LiveWorldTrackerUpdate(
            correction,
            closure,
            Snapshot,
            Snapshot.MapRevision,
            new LiveWorldTrackerReview(points, lines, rejections.ToArray()));
    }

    private IEnumerable<CachedReference> RecoveryReferences()
    {
        var unplaced = Snapshot.UnplacedObservationIds();
        return restoredRecoveryReferences.Values.OfType<CachedReference>()
            .Concat(references.Values.Where(value => !restoredRecoveryReferences.ContainsKey(value.KeyframeId)))
            .Where(reference => !unplaced.Contains(reference.ObservationId));
    }

    private IEnumerable<LiveGroundClosureSearch.Source> PlacedGroundReferences(int? roomId, double solveWidth)
    {
        var unplaced = Snapshot.UnplacedObservationIds();
        var observations = Snapshot.Observations.Where(observation =>
            (roomId is null || observation.RoomId == roomId) && observation.SolveWidth == solveWidth
            && observation.GroundGeometry?.Lines.Count > 0 && !unplaced.Contains(observation.Id))
            .ToDictionary(observation => observation.Id);
        foreach (var keyframe in Snapshot.Keyframes)
            if (observations.TryGetValue(keyframe.ObservationId, out var observation))
                yield return new(keyframe.Id, observation);
    }

    private (CachedReference Reference, GlobalFeatureMatch Match)? RecoveryMatch(
        BgraFrame frame,
        double solveWidth,
        IReadOnlyList<StencilRect> exclusions,
        IReadOnlyList<CachedReference> eligible,
        bool searchAcrossRooms)
    {
        var features = eligible.Select(value => value.Reference).ToArray();
        var match = searchAcrossRooms
            ? matcher.MatchAcrossRooms(frame, features, solveWidth, exclusions,
                eligible.ToDictionary(value => value.Reference.Id, value => value.RoomId))
            : matcher.Match(frame, features, currentSolveWidth: solveWidth, excluding: exclusions);
        var reference = match is null
            ? null
            : eligible.FirstOrDefault(value => value.Reference.Id == match.ReferenceId);
        return match is null || reference is null ? null : (reference, match);
    }

    private LiveWorldRecoveryUpdate RecoveryUpdate(
        int? referenceKeyframeId,
        LiveWorldRecoveryPlacement? placement,
        IReadOnlyList<LiveWorldTrackerRejection> rejections) => new(
            placement,
            referenceKeyframeId,
            Snapshot,
            Snapshot.MapRevision,
            Update(new AtlasPoint(0, 0), null, rejections).Review);

    private void ReplaceSnapshot(
        IReadOnlyList<LiveWorldObservation>? observations = null,
        IReadOnlyList<LiveWorldKeyframe>? keyframes = null,
        IReadOnlyList<LiveWorldLandmark>? landmarks = null,
        IReadOnlyList<LiveWorldRelativeMotionEdge>? relativeEdges = null,
        IReadOnlyList<LiveWorldLoopClosureEdge>? loopEdges = null,
        IReadOnlyList<LiveTrackingIsland>? trackingIslands = null)
    {
        Snapshot = new LiveWorldSnapshot(
            Snapshot.SchemaVersion,
            checked(Snapshot.MapRevision + 1),
            Snapshot.AnchoredObservationId ?? observations?.FirstOrDefault()?.Id,
            observations ?? Snapshot.Observations,
            keyframes ?? Snapshot.Keyframes,
            landmarks ?? Snapshot.Landmarks,
            relativeEdges ?? Snapshot.RelativeMotionEdges,
            loopEdges ?? Snapshot.LoopClosureEdges,
            trackingIslands ?? Snapshot.TrackingIslands,
            Snapshot.DoorwayFloorConstraints);
    }

    private void RestoreRuntimeState()
    {
        cumulativeTravel.Clear();
        LiveWorldObservation? previous = null;
        var travel = 0.0;
        foreach (var observation in Snapshot.Observations)
        {
            if (previous is not null && previous.RoomId == observation.RoomId)
            {
                travel += Distance(observation.RawPose, previous.RawPose);
            }
            else if (previous is not null)
            {
                travel = 0;
            }
            cumulativeTravel[observation.Id] = travel;
            previous = observation;
        }
        lastObservationId = null;
        RefreshReferences();
    }

    private void RestoreRecoveryReferences(Func<int, BgraFrame> loadSavedFrame)
    {
        var observations = Snapshot.Observations.ToDictionary(value => value.Id);
        foreach (var keyframe in Snapshot.Keyframes)
        {
            var observation = observations[keyframe.ObservationId];
            var frame = loadSavedFrame(observation.SourceObservationId);
            if (frame.Width != observation.SourceWidth || frame.Height != observation.SourceHeight)
                throw Error(LiveWorldTrackerError.InvalidSnapshot, "Saved keyframe dimensions have changed.", observation.Id);

            // Swift descriptors use Core Graphics sampling. Derive Windows features
            // for recovery only, so the original landmark IDs and closures stay intact.
            var masks = observation.ExcludedRects.Select(rectangle =>
                new StencilRect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height)).ToArray();
            var reference = matcher.MakeReference(
                ReferenceId(keyframe.Id), frame, observation.OptimizedPose, observation.SolveWidth, excluding: masks);
            restoredRecoveryReferences[keyframe.Id] = reference is null ? null
                : new CachedReference(keyframe.Id, observation.Id, observation.RoomId, reference);
        }
    }

    private void RefreshReferences()
    {
        references.Clear();
        foreach (var keyframe in Snapshot.Keyframes)
        {
            var reference = ReferenceFor(keyframe);
            if (reference is not null) references[keyframe.Id] = reference;
        }
    }

    private CachedReference? ReferenceFor(LiveWorldKeyframe keyframe)
    {
        var observation = Snapshot.Observations.FirstOrDefault(
            value => value.Id == keyframe.ObservationId);
        if (observation is null) return null;
        var storedLandmarks = Snapshot.Landmarks
            .Where(value => value.KeyframeId == keyframe.Id && !value.GroundClosureOnly)
            .OrderBy(value => value.Id)
            .ToArray();
        if (storedLandmarks.Length < 6) return null;
        var sampleHeight = Math.Max(
            1,
            (int)Math.Round(
                observation.SourceHeight * (double)observation.SampleWidth
                    / observation.SourceWidth,
                MidpointRounding.AwayFromZero));
        var features = storedLandmarks.Select(landmark =>
            new GlobalFeatureReference.Feature(
                landmark.Id,
                landmark.SamplePoint,
                landmark.Descriptor.Select(value => (byte)Math.Clamp(
                    (int)Math.Round(value, MidpointRounding.AwayFromZero),
                    byte.MinValue,
                    byte.MaxValue)).ToArray(),
                255,
                landmark.Depth,
                landmark.DepthConfidence)).ToArray();
        if (features.Any(value => value.Descriptor.Count != 25)) return null;
        return new CachedReference(
            keyframe.Id,
            observation.Id,
            observation.RoomId,
            new GlobalFeatureReference(
                ReferenceId(keyframe.Id),
                observation.OptimizedPose,
                observation.SolveWidth,
                observation.SourceWidth,
                observation.SourceHeight,
                observation.SampleWidth,
                sampleHeight,
                features));
    }

    private double NextCumulativeTravel(AtlasPoint pose, int roomId)
    {
        if (lastObservationId is not { } previousId
            || Snapshot.Observations.FirstOrDefault(
                value => value.Id == previousId) is not { } previous
            || previous.RoomId != roomId
            || !cumulativeTravel.TryGetValue(previousId, out var previousTravel))
        {
            return 0;
        }
        return previousTravel + Distance(pose, previous.RawPose);
    }

    private static int NextId(IEnumerable<int> ids)
    {
        var values = ids.ToArray();
        if (values.Length == 0) return 0;
        var maximum = values.Max();
        return maximum < int.MaxValue ? maximum + 1 : 0;
    }

    private static Guid ReferenceId(int keyframeId)
    {
        var value = unchecked((ulong)keyframeId);
        var bytes = new byte[16];
        for (var index = 0; index < 8; index++)
        {
            bytes[15 - index] = (byte)(value >> (index * 8));
        }
        return new Guid(bytes);
    }

    private static double Distance(AtlasPoint left, AtlasPoint right) => Math.Sqrt(
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool Valid(StencilRect rectangle) =>
        rectangle.IsFinite && rectangle.Width >= 0 && rectangle.Height >= 0;

    private static LiveWorldTrackerException Error(
        LiveWorldTrackerError error,
        string message,
        int? observationId = null) => new(error, message, observationId);
}
