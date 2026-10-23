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
    InconsistentConfirmation
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
    IReadOnlyList<int> ConfirmationReferenceKeyframeIds);

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
        AtlasPoint Correction,
        int MissedFrames);

    private const double KeyframeTravelFraction = 0.25;
    private const double MinimumClosureTravelFraction = 0.75;
    private const int MaximumMissedRecoveryFrames = 4;

    private readonly GlobalFeatureMatcher matcher = new();
    private readonly Guid visitId = Guid.NewGuid();
    private readonly Dictionary<int, CachedReference> references = [];
    private readonly Dictionary<int, double> cumulativeTravel = [];
    private readonly HashSet<int> closedReferenceKeyframeIds = [];
    private readonly HashSet<int> currentVisitObservationIds = [];
    private PendingClosure? pendingClosure;
    private PendingRecovery? pendingRecovery;
    private int recoveryEpoch;
    private double? lastAcceptedClosureTravel;
    private int? lastObservationId;

    public LiveWorldTracker()
        : this(new LiveWorldSnapshot()) { }

    public LiveWorldTracker(LiveWorldSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Snapshot.Validate();
        RestoreRuntimeState();
    }

    public LiveWorldSnapshot Snapshot { get; private set; }

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
        int roomId = 0)
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
        if (references.Count == 0)
        {
            pendingRecovery = null;
            return RecoveryUpdate(
                null,
                null,
                [LiveWorldTrackerRejection.NoEligibleReference]);
        }
        if (roomId < 0)
        {
            throw Error(LiveWorldTrackerError.InvalidScan, "Live-world recovery room is invalid.");
        }

        var candidate = RecoveryMatch(frame, solveWidth, excluded, roomId);
        if (candidate is null)
        {
            if (pendingRecovery is { } pending
                && pending.Epoch == recoveryEpoch
                && pending.MissedFrames < MaximumMissedRecoveryFrames)
            {
                pendingRecovery = pending with
                {
                    MissedFrames = pending.MissedFrames + 1
                };
            }
            else
            {
                pendingRecovery = null;
            }
            return RecoveryUpdate(
                null,
                null,
                [LiveWorldTrackerRejection.MatcherRejected]);
        }

        var (reference, match) = candidate.Value;
        var correction = new AtlasPoint(
            match.ProposedCameraPose.X - proposedCameraPose.X,
            match.ProposedCameraPose.Y - proposedCameraPose.Y);
        var consistency = Math.Max(solveWidth * 0.0125, 4);
        if (pendingRecovery is not { } confirmation
            || confirmation.Epoch != recoveryEpoch)
        {
            pendingRecovery = new PendingRecovery(
                recoveryEpoch,
                reference.KeyframeId,
                correction,
                0);
            return RecoveryUpdate(
                reference,
                null,
                [LiveWorldTrackerRejection.ConfirmationRequired]);
        }

        var confirmationReferenceIds = LiveWorldRecoveryConfirmation.ReferenceIds(
            confirmation.ReferenceKeyframeId,
            confirmation.Correction,
            reference.KeyframeId,
            correction,
            consistency);
        if (confirmationReferenceIds is null)
        {
            pendingRecovery = new PendingRecovery(
                recoveryEpoch,
                reference.KeyframeId,
                correction,
                0);
            return RecoveryUpdate(
                reference,
                null,
                [LiveWorldTrackerRejection.InconsistentConfirmation]);
        }

        pendingRecovery = null;
        return RecoveryUpdate(
            reference,
            new LiveWorldRecoveryPlacement(
                match.ProposedCameraPose,
                correction,
                confirmationReferenceIds),
            []);
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
        int roomId = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var excluded = exclusions?.ToArray() ?? [];
        if (observationId < 0
            || !double.IsFinite(timestamp)
            || frame.Width <= 0 || frame.Height <= 0
            || !Finite(proposedCameraPose)
            || !double.IsFinite(solveWidth) || solveWidth <= 0
            || roomId < 0
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
            proposedCameraPose,
            localPose ?? proposedCameraPose,
            captureGeneration,
            localEpoch,
            basisRevision,
            roomId);
        ReplaceSnapshot(observations: [.. Snapshot.Observations, observation]);
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
        var correction = Optimize(observationId);
        return Update(correction, closure, [], reference, match);
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
        if (lastAcceptedClosureTravel is { } lastClosure
            && travelDistance - lastClosure < minimumTravelAfterClosure)
        {
            return [];
        }

        var observations = Snapshot.Observations.ToDictionary(value => value.Id);
        var keyframes = Snapshot.Keyframes.ToDictionary(value => value.Id);
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
                && !closedReferenceKeyframeIds.Contains(candidate.KeyframeId)
                && (!currentVisitObservationIds.Contains(candidate.ObservationId)
                    || travelDistance - referenceTravel >= minimumTravel);
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
            observation.RawPose,
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
                        observation.RawPose.X + feature.SamplePoint.X * sampleScale,
                        observation.RawPose.Y
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
        foreach (var observation in roomObservations)
        {
            var epochId = epochIds.GetValueOrDefault(
                new LocalEpochKey(
                    observation.CaptureGeneration,
                    observation.LocalEpoch),
                visitId);
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

    private (CachedReference Reference, GlobalFeatureMatch Match)? RecoveryMatch(
        BgraFrame frame,
        double solveWidth,
        IReadOnlyList<StencilRect> exclusions,
        int roomId)
    {
        var eligible = references.Values
            .Where(value => value.RoomId == roomId)
            .OrderBy(value => value.KeyframeId)
            .ToArray();
        var match = matcher.Match(
            frame,
            eligible.Select(value => value.Reference).ToArray(),
            currentSolveWidth: solveWidth,
            excluding: exclusions);
        var reference = match is null
            ? null
            : eligible.FirstOrDefault(value => value.Reference.Id == match.ReferenceId);
        return match is null || reference is null ? null : (reference, match);
    }

    private LiveWorldRecoveryUpdate RecoveryUpdate(
        CachedReference? reference,
        LiveWorldRecoveryPlacement? placement,
        IReadOnlyList<LiveWorldTrackerRejection> rejections) => new(
            placement,
            reference?.KeyframeId,
            Snapshot,
            Snapshot.MapRevision,
            Update(new AtlasPoint(0, 0), null, rejections).Review);

    private void ReplaceSnapshot(
        IReadOnlyList<LiveWorldObservation>? observations = null,
        IReadOnlyList<LiveWorldKeyframe>? keyframes = null,
        IReadOnlyList<LiveWorldLandmark>? landmarks = null,
        IReadOnlyList<LiveWorldRelativeMotionEdge>? relativeEdges = null,
        IReadOnlyList<LiveWorldLoopClosureEdge>? loopEdges = null)
    {
        Snapshot = new LiveWorldSnapshot(
            Snapshot.SchemaVersion,
            checked(Snapshot.MapRevision + 1),
            Snapshot.AnchoredObservationId ?? observations?.FirstOrDefault()?.Id,
            observations ?? Snapshot.Observations,
            keyframes ?? Snapshot.Keyframes,
            landmarks ?? Snapshot.Landmarks,
            relativeEdges ?? Snapshot.RelativeMotionEdges,
            loopEdges ?? Snapshot.LoopClosureEdges);
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
            .Where(value => value.KeyframeId == keyframe.Id)
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
