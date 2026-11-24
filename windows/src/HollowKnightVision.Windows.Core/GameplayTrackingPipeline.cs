using System.Diagnostics;

namespace HollowKnightVision.Windows.Core;

public sealed record GameplayTrackingPreparation(
    GameplayTerrainAnalysis? Terrain, LowResolutionTranslationDiagnostic? Motion,
    IReadOnlyList<PixelRect> ProtectedOcclusions, IReadOnlyList<PixelRect> GroundExclusions)
{
    internal IReadOnlyList<StencilRect>? MotionExclusions { get; init; }
}

public sealed record GameplayTrackedFrame(
    VisualGameplayObservation Observation, GroundHypothesisTrackingResult Ground, GroundReferenceEstimate? Reference,
    AtlasPoint DisplayCamera, string RoomId, TerrainWallDetection Terrain, KnightTerrainContact Contact,
    IReadOnlyList<PixelRect> PlatformPatches, IReadOnlyList<GroundHypothesisAtlasLine> WalkedGround,
    long WalkedCoordinateEpoch, double CameraMilliseconds, long AtlasStartedTimestamp)
{
    public bool MotionBridgeActive =>
        Observation.Source is "transition-bridge" or "calibrated-scene" or "coarse-presentation";
    public bool RoomAllowsAtlasWrite { get; init; } = true;
    public bool AllowsAtlasWrite => Observation.AllowsAtlasWrite || HasMeasuredSceneryContinuity;
    public bool HasMeasuredSceneryContinuity { get; init; }
    public AtlasPoint? MeasuredSceneryCamera => HasMeasuredSceneryContinuity ? DisplayCamera : null;
    public long? MeasuredLineage { get; init; }
    public long? WalkedGroundLineage { get; init; }
    public bool RequiresWorldRecovery => !RoomAllowsAtlasWrite || SceneCut is not null
        || CaptureGap == AtlasCaptureGapState.RequiresRecovery
        || (!AllowsAtlasWrite && !Observation.RetainsWorldBasisDuringPlacementCheck
            && CaptureGap != AtlasCaptureGapState.AwaitingRegistration);
    internal AtlasCaptureGapState CaptureGap { get; init; }
    public VisualSceneDiscontinuityTracker.Cut? SceneCut { get; init; }
    public VisualSceneDiscontinuityTracker.Cut? RecentDoorwayCut { get; init; }
    public LiveWorldCaptureEvidence? WorldEvidence { get; init; }
}

/// <summary>Ground, camera and terrain state shared by the capture worker and offline archive replay.</summary>
public sealed class GameplayTrackingPipeline(VisualGameplayTracker camera, MaskedFrameMotionTracker motion,
    bool deterministicGlobalSearch = false)
{
    private readonly GroundHypothesisTracker tracker = new(minimumObservationSeconds: .2,
        backgroundGlobalSearch: true, confirmedLossGraceSeconds: 3, productionCameraSolver: true,
        deterministicGlobalSearch: deterministicGlobalSearch);
    private readonly GroundSemanticAtlas semanticAtlas = new();
    private readonly GroundReferenceTracker reference = new();
    private readonly WalkedGroundTracker walked = new();
    private readonly GroundPlatformPatches patches = new();
    private readonly NormalAtlasRoomIdentity roomIdentity = new();
    private readonly MeasuredAtlasPoseLineage measuredAtlasPose = new();
    private readonly CaptureIslandLineage captureIslands = new();
    private readonly AtlasCaptureGap atlasCaptureGap = new();
    private bool needsCameraAnchor = true;
    private string? lastKnownRoom;
    private int frameWidth, frameHeight;
    private long walkedCoordinateEpoch;

    public GameplayPathTracker Paths { get; } = new();

    public void ResetMotionReference()
    {
        motion.Reset();
        SuspendTracking();
    }

    public GameplayTrackingPreparation Prepare(BgraFrame frame, GameplaySceneMasks masks, bool admitted,
        string? knownRoom = null, GroundTheoryTuning? tuning = null, GroundTheoryTuning? semanticTuning = null)
    {
        if (frameWidth != 0 && (frameWidth != frame.Width || frameHeight != frame.Height))
            Reset();
        else if (!string.IsNullOrWhiteSpace(knownRoom) && lastKnownRoom is not null && knownRoom != lastKnownRoom)
            ResetTracking(preserveWalkedGround: false, preserveLoadingObservation: true);
        frameWidth = frame.Width;
        frameHeight = frame.Height;
        if (!string.IsNullOrWhiteSpace(knownRoom)) lastKnownRoom = knownRoom;
        if (needsCameraAnchor)
        {
            tracker.ReanchorLocalCamera(camera.Position);
            needsCameraAnchor = false;
        }

        var protectedOcclusions = masks.GroundExclusions.ToList();
        if (masks.GroundKnight is { } knight) protectedOcclusions.Add(knight);
        if (admitted)
        {
            var terrain = GameplayTerrainAnalysis.Analyze(frame, masks, tuning, semanticTuning);
            var motionExclusions = masks.GroundRegions.OmittedRects;
            return new(terrain, motion.Observe(frame, motionExclusions), protectedOcclusions, masks.GroundExclusions)
            {
                MotionExclusions = motionExclusions
            };
        }

        ResetMotionReference();
        return new(null, null, protectedOcclusions, masks.GroundExclusions);
    }

    public GameplayTrackedFrame Track(BgraFrame frame, GameplayTrackingPreparation prepared, PixelRect? knight,
        GameplayFrameDecision admission, FrameSignalProfile signal, double timestamp, long generation, string? knownRoom,
        bool hasCommittedAtlas, InputButtons? buttons, bool resetWorldCoordinatesForCorrection,
        bool visualRoomObservationEnabled = true, bool observeLoadingInKnownRoom = false,
        bool captureWorldEvidence = false)
    {
        var groundStarted = Stopwatch.GetTimestamp();
        var terrainAnalysis = prepared.Terrain;
        var drive = KnightLevelWalk.Direction(buttons ?? InputButtons.None);
        VisualRoomDirection? direction = drive < 0 ? VisualRoomDirection.Left : drive > 0 ? VisualRoomDirection.Right : null;
        var hasKnownRoom = !string.IsNullOrWhiteSpace(knownRoom);
        var sceneGrid = !hasKnownRoom && roomIdentity.HasUnresolvedLoading ? null : signal.MotionGrid;
        var motionExclusions = prepared.MotionExclusions;
        if (!hasCommittedAtlas || !admission.AdmitsTracking || admission.Transition.ResumedAfterTransition
            || sceneGrid is null || motionExclusions is null)
            atlasCaptureGap.Reset();
        var skippedCaptureRegistration = motionExclusions is null ? null
            : atlasCaptureGap.Match(frame, motionExclusions, knight, generation,
                hasKnownRoom ? knownRoom! : roomIdentity.RoomId, timestamp);
        var observation = terrainAnalysis?.Edges.SourceLuma is { } sourceLuma
            ? camera.ObserveGroundAnchored(frame, prepared.Motion, sourceLuma,
                terrainAnalysis.CameraReferences, prepared.ProtectedOcclusions, hasCommittedAtlas, knight, tracker, timestamp,
                sceneGrid, direction, skippedCaptureRegistration)
            : camera.ObserveUnverified(frame, prepared.Motion);
        if (!admission.AdmitsTracking) camera.PauseGroundVerification();
        var cameraMilliseconds = Stopwatch.GetElapsedTime(groundStarted).TotalMilliseconds;
        var atlasStarted = Stopwatch.GetTimestamp();

        var floors = terrainAnalysis?.Floors ?? [];
        var ground = observation.GroundTracking;
        var semanticLines = semanticAtlas.Update(ground.AtlasLines, floors.Select(line => line.ToSemanticLine()).ToArray(),
            ground.CameraPosition, frame.Width, frame.Height, ground.PoseVerified);
        ground = ground with { AtlasLines = semanticLines };
        var localCamera = new AtlasPoint(observation.Telemetry.CameraX, observation.Telemetry.CameraY);
        var estimate = reference.CurrentEstimate();
        if (observation.Registration?.State == CameraUpdateState.Accepted && terrainAnalysis is not null)
            estimate = reference.Observe(frame, localCamera, frame.Width, knight,
                prepared.GroundExclusions, terrainAnalysis.Theory);

        var observeLoading = visualRoomObservationEnabled && (!hasKnownRoom || observeLoadingInKnownRoom);
        var visualRoom = roomIdentity.Observe(generation, observeLoading, signal.MotionGrid,
            timestamp, buttons, localCamera);
        var room = hasKnownRoom ? knownRoom! : visualRoom.Id;
        if (visualRoom.Cut is not null)
        {
            // The loading cut establishes ownership independently of camera alignment.
            walked.BreakContinuity();
            patches.Reset();
            Paths.Reset();
            camera.ResetFloorlessMotion();
        }
        var groundAdjusted = ground.GlobalCorrection is { } correction
            && correction.X * correction.X + correction.Y * correction.Y > 9;
        var measuredGround = observation.GroundVerified && GroundMotionEvidence.IsMeasured(ground);
        if (groundAdjusted)
        {
            var sourceEpoch = walkedCoordinateEpoch;
            if (resetWorldCoordinatesForCorrection) ResetWalkedCoordinateEpoch();
            else walked.BreakContinuity();
            patches.Reset();
            if (resetWorldCoordinatesForCorrection && measuredGround)
                Paths.RecoverMeasuredBasis(room, generation, sourceEpoch, walkedCoordinateEpoch, timestamp, ground);
            else
                Paths.Reset();
        }
        var selectedScene = observation.SelectedScenery;
        var canContinueAtlasPose = admission.AdmitsTracking && visualRoom.AllowsAtlasWrite
            && measuredAtlasPose.Observe(generation, room, timestamp, localCamera,
                measuredGround, selectedScene,
                groundAdjusted || admission.Transition.ResumedAfterTransition);
        if (!admission.AdmitsTracking || !visualRoom.AllowsAtlasWrite)
            measuredAtlasPose.Reset();
        var publishesScenery = canContinueAtlasPose && hasCommittedAtlas && camera.AllowsAtlasPlacement;
        var captureGap = AtlasCaptureGapState.None;
        if (hasCommittedAtlas && sceneGrid is not null && motionExclusions is not null
            && admission.AdmitsTracking && visualRoom.AllowsAtlasWrite
            && !groundAdjusted && !admission.Transition.ResumedAfterTransition)
        {
            var acceptedMotionReference = measuredGround || publishesScenery
                || observation.FloorlessEvidence?.SelectedSource == FloorlessPoseCandidateSource.MaskedRegistration
                    && observation.MotionBridgeConfidence is >= VisualGameplayTracker.MinimumRecoveryConfidence;
            var continuedAcrossGap = observation.RecoveredAtlasReferenceTimestamp is not null || publishesScenery
                || measuredGround && skippedCaptureRegistration is { CameraPosition: var matched }
                    && CameraMath.Length(localCamera.X - matched.X, localCamera.Y - matched.Y) <= 2 * frame.Width / 640d;
            captureGap = atlasCaptureGap.Complete(frame, motionExclusions, knight, generation, room, timestamp,
                localCamera, acceptedMotionReference, continuedAcrossGap,
                atlasFrameHeld: !observation.AllowsAtlasWrite && !publishesScenery);
        }
        else atlasCaptureGap.Reset();

        var referenceTimestamp = measuredGround ? ground.LocalReferenceTimestamp : selectedScene?.ReferenceTimestamp;
        var lineage = captureIslands.Observe(generation, room, timestamp,
            admission.AdmitsTracking && visualRoom.AllowsAtlasWrite && (measuredGround || publishesScenery),
            referenceTimestamp, groundAdjusted || admission.Transition.ResumedAfterTransition || visualRoom.Cut is not null);
        var terrain = TerrainWallDetection.Empty;
        KnightTerrainContact contact = default;
        IReadOnlyList<PixelRect> platformPatches = [];
        if (terrainAnalysis is not null)
        {
            var edges = terrainAnalysis.Edges;
            var verifiedCamera = observation.GroundVerified && visualRoom.AllowsAtlasWrite
                ? ground.CameraPosition : null;
            terrain = TerrainWallDetector.Detect(edges, floors);
            var contactCamera = lineage is null ? null : publishesScenery ? localCamera : verifiedCamera;
            contact = walked.Observe(room, edges, knight, contactCamera,
                buttons, timestamp, floors, terrain.Walls,
                generation, walkedCoordinateEpoch, lineage);
            terrain = TerrainWallDetector.ApplyContact(terrain, contact, floors, edges);
            platformPatches = patches.Observe(edges, floors, prepared.ProtectedOcclusions, verifiedCamera, timestamp);
        }
        LiveWorldCaptureEvidence? worldEvidence = null;
        if (captureWorldEvidence && admission.AllowsAtlasWrite && visualRoom.AllowsAtlasWrite)
        {
            if (measuredGround)
            {
                worldEvidence = LiveWorldCaptureEvidence.FromGround(timestamp, frame.Width, frame.Height,
                    floors, ground.LineReviews, knight, contact.MeasuredFoot, contact.Floor) with
                { LocalReferenceTimestamp = referenceTimestamp, MeasuredLineage = lineage };
            }
            else if (publishesScenery)
            {
                worldEvidence = new LiveWorldCaptureEvidence(timestamp)
                {
                    LocalReferenceTimestamp = referenceTimestamp,
                    HasMeasuredSceneryContinuity = true,
                    MeasuredLineage = lineage
                };
            }
            if (worldEvidence is { MeasuredLineage: { } owner })
                worldEvidence = worldEvidence with { OtherWalks = walked.OtherOwners(room, owner) };
        }
        return new(observation, ground, estimate, localCamera, room, terrain, contact,
            platformPatches, walked.InRoom(room), walkedCoordinateEpoch, cameraMilliseconds, atlasStarted)
        {
            RoomAllowsAtlasWrite = visualRoom.AllowsAtlasWrite,
            HasMeasuredSceneryContinuity = publishesScenery,
            MeasuredLineage = lineage,
            WalkedGroundLineage = walked.ActiveLineage,
            CaptureGap = captureGap,
            SceneCut = visualRoom.Cut,
            RecentDoorwayCut = roomIdentity.RecentDoorwayCut(timestamp),
            WorldEvidence = worldEvidence
        };
    }

    public void Reset(bool preserveWalkedGround = false) =>
        ResetTracking(preserveWalkedGround, preserveLoadingObservation: false);

    private void ResetTracking(bool preserveWalkedGround, bool preserveLoadingObservation)
    {
        if (preserveWalkedGround) walked.BreakContinuity();
        else ResetWalkedCoordinateEpoch();
        patches.Reset();
        Paths.Reset();
        needsCameraAnchor = true;
        tracker.Reset();
        semanticAtlas.Reset();
        reference.Reset();
        measuredAtlasPose.Reset();
        captureIslands.Reset();
        atlasCaptureGap.Reset();
        camera.ResetFloorlessMotion();
        if (!preserveLoadingObservation) roomIdentity.ResetObservation();
        lastKnownRoom = null;
        frameWidth = 0;
        frameHeight = 0;
    }

    private void SuspendTracking()
    {
        tracker.Reset(keepingGlobalFeatures: true);
        reference.Reset();
        measuredAtlasPose.Reset();
        atlasCaptureGap.Reset();
        walked.BreakContinuity();
        patches.Reset();
        Paths.Suspend();
    }

    private void ResetWalkedCoordinateEpoch()
    {
        walkedCoordinateEpoch++;
        walked.Reset();
    }

    public static IReadOnlyList<StencilRect> CreateGroundAlignmentExclusions(
        BgraFrame frame,
        AtlasPoint cameraPosition,
        double solveWidth,
        IReadOnlyList<DetectedFloorLine> ground,
        IReadOnlyList<PixelRect> foregroundExclusions,
        FrameSignalProfile signal,
        VisualGameplayObservation observation,
        bool transitionBridgeActive,
        GroundReferenceEstimate? trackedEstimate)
    {
        // Calibrated scenery can continue the camera pose without supplying
        // floor features for loop closures or future ground relocalization.
        if (!observation.GroundVerified && observation.FloorlessEvidence is
            { SelectedSource: FloorlessPoseCandidateSource.CalibratedScene })
            return [new StencilRect(0, 0, frame.Width, frame.Height)];

        var existing = foregroundExclusions.Select(rectangle => new StencilRect(
            rectangle.X,
            frame.Height - rectangle.Bottom,
            rectangle.Width,
            rectangle.Height)).ToList();
        if (transitionBridgeActive && signal.EdgeBlackBands is { } transitionBands)
        {
            existing.AddRange(transitionBands.RectangularOmissions(frame.Width, frame.Height));
        }

        var estimate = trackedEstimate;
        // A verified ground solve owns ordinary frames. During a measured
        // transition, current semantic line evidence may still define the
        // narrow matcher band, with the detected edge matte excluded above.
        // No current line evidence still produces the full-frame exclusion.
        if (estimate is null
            && (observation.GroundVerified || transitionBridgeActive)
            && double.IsFinite(solveWidth)
            && solveWidth > 0)
        {
            var scale = frame.Width / solveWidth;
            var segments = ground.Select(line =>
            {
                // Detector rows and foreground rectangles address the top-down
                // BGRA capture. Ground feature geometry, like the Mac tracker,
                // is world-up; retain the half-pixel line-center convention.
                var frameY = frame.Height - line.Row - 0.5;
                return new GroundReferenceSegment(
                    cameraPosition.X + line.XSpan.Start / scale,
                    cameraPosition.X + (line.XSpan.EndInclusive + 1) / scale,
                    cameraPosition.Y + frameY / scale);
            }).Where(segment =>
                double.IsFinite(segment.MinimumWorldX)
                && double.IsFinite(segment.MaximumWorldX)
                && segment.MaximumWorldX > segment.MinimumWorldX
                && double.IsFinite(segment.WorldY ?? double.NaN))
                .ToArray();
            if (segments.Length > 0)
            {
                var confidence = observation.GroundError is { } error
                    && double.IsFinite(error)
                    ? 1 / (1 + Math.Max(0, error))
                    : 1;
                estimate = new GroundReferenceEstimate(
                    segments[0].WorldY!.Value,
                    segments.Min(segment => segment.MinimumWorldX),
                    segments.Max(segment => segment.MaximumWorldX),
                    segments,
                    segments.SelectMany(segment => new[]
                        {
                            segment.MinimumWorldX,
                            segment.MaximumWorldX
                        })
                        .Distinct()
                        .Order()
                        .ToArray(),
                    Math.Max(segments.Length, observation.GroundSupport),
                    confidence);
            }
        }

        return GroundFeatureEligibility.AlignmentExclusions(
            new WorldSize(frame.Width, frame.Height),
            cameraPosition,
            solveWidth,
            estimate,
            existing);
    }

    internal sealed class NormalAtlasRoomIdentity
    {
        private readonly VisualSceneDiscontinuityTracker cuts = new();
        private long captureGeneration = long.MinValue;
        private long roomEpoch;
        private VisualSceneDiscontinuityTracker.Cut? lastCut;

        internal bool HasUnresolvedLoading => cuts.IsPending;

        internal (string Id, bool AllowsAtlasWrite, VisualSceneDiscontinuityTracker.Cut? Cut) Observe(
            long currentCaptureGeneration, bool enabled, LowResolutionMotionGrid? grid,
            double timestamp, InputButtons? buttons, AtlasPoint worldPose)
        {
            if (captureGeneration != currentCaptureGeneration)
            {
                ResetObservation();
                captureGeneration = currentCaptureGeneration;
                roomEpoch = 0;
            }
            if (!enabled)
            {
                ResetObservation();
                return (RoomId, true, null);
            }

            var drive = KnightLevelWalk.Direction(buttons ?? InputButtons.None);
            var direction = drive < 0 ? VisualRoomDirection.Left
                : drive > 0 ? VisualRoomDirection.Right : (VisualRoomDirection?)null;
            var cut = cuts.Observe(grid, timestamp, direction, worldPose);
            // Settled frames can resume tracking in the same room after a fade or capture gap.
            if (cut is not null)
            {
                lastCut = cut;
                roomEpoch = checked(roomEpoch + 1);
            }
            return (RoomId, !cuts.IsPending && cut is null, cut);
        }

        internal VisualSceneDiscontinuityTracker.Cut? RecentDoorwayCut(double timestamp) =>
            lastCut is { } cut && timestamp - cut.ArrivalTimestamp is >= 0 and <= 3 ? cut : null;

        internal void ResetObservation()
        {
            cuts.Reset();
            lastCut = null;
        }

        internal string RoomId => FormattableString.Invariant($"normal:{captureGeneration}:room:{roomEpoch}");
    }
}
