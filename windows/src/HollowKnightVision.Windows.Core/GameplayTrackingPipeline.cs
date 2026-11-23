using System.Diagnostics;

namespace HollowKnightVision.Windows.Core;

public sealed record GameplayTrackingPreparation(
    GameplayTerrainAnalysis? Terrain, LowResolutionTranslationDiagnostic? Motion,
    IReadOnlyList<PixelRect> ProtectedOcclusions, IReadOnlyList<PixelRect> GroundExclusions);

public sealed record GameplayTrackedFrame(
    VisualGameplayObservation Observation, GroundHypothesisTrackingResult Ground, GroundReferenceEstimate? Reference,
    AtlasPoint DisplayCamera, string RoomId, TerrainWallDetection Terrain, KnightTerrainContact Contact,
    IReadOnlyList<PixelRect> PlatformPatches, IReadOnlyList<GroundHypothesisAtlasLine> WalkedGround,
    long WalkedCoordinateEpoch, double CameraMilliseconds, long AtlasStartedTimestamp)
{
    public bool MotionBridgeActive => Observation.Source is "transition-bridge" or "coarse-presentation";
    public bool RoomAllowsAtlasWrite { get; init; } = true;
    public VisualSceneDiscontinuityTracker.Cut? SceneCut { get; init; }
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
        if (frameWidth != 0 && (frameWidth != frame.Width || frameHeight != frame.Height)
            || !string.IsNullOrWhiteSpace(knownRoom) && lastKnownRoom is not null && knownRoom != lastKnownRoom)
            Reset();
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
            return new(terrain, motion.Observe(frame, masks.GroundRegions.OmittedRects), protectedOcclusions, masks.GroundExclusions);
        }

        ResetMotionReference();
        return new(null, null, protectedOcclusions, masks.GroundExclusions);
    }

    public GameplayTrackedFrame Track(BgraFrame frame, GameplayTrackingPreparation prepared, PixelRect? knight,
        GameplayFrameDecision admission, FrameSignalProfile signal, double timestamp, long generation, string? knownRoom,
        bool hasCommittedAtlas, InputButtons? buttons, bool resetWorldCoordinatesForCorrection,
        bool visualRoomObservationEnabled = true)
    {
        var groundStarted = Stopwatch.GetTimestamp();
        var terrainAnalysis = prepared.Terrain;
        var observation = terrainAnalysis?.Edges.SourceLuma is { } sourceLuma
            ? camera.ObserveGroundAnchored(frame, prepared.Motion, sourceLuma,
                terrainAnalysis.CameraReferences, prepared.ProtectedOcclusions, hasCommittedAtlas, knight, tracker, timestamp)
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

        var hasKnownRoom = !string.IsNullOrWhiteSpace(knownRoom);
        var visualRoom = roomIdentity.Observe(generation, visualRoomObservationEnabled && !hasKnownRoom,
            admission.Transition, signal.MotionGrid, timestamp, buttons, localCamera);
        var room = hasKnownRoom ? knownRoom! : visualRoom.Id;
        if (visualRoom.Cut is not null)
        {
            // The loading cut establishes ownership independently of camera alignment.
            walked.BreakContinuity();
            patches.Reset();
            Paths.Reset();
        }
        if (ground.GlobalCorrection is { } correction && correction.X * correction.X + correction.Y * correction.Y > 9)
        {
            if (resetWorldCoordinatesForCorrection) ResetWalkedCoordinateEpoch();
            else walked.BreakContinuity();
            patches.Reset();
            Paths.Reset();
        }

        var terrain = TerrainWallDetection.Empty;
        KnightTerrainContact contact = default;
        IReadOnlyList<PixelRect> platformPatches = [];
        if (terrainAnalysis is not null)
        {
            var edges = terrainAnalysis.Edges;
            var verifiedCamera = observation.GroundVerified && visualRoom.AllowsAtlasWrite
                ? ground.CameraPosition : null;
            terrain = TerrainWallDetector.Detect(edges, floors);
            contact = walked.Observe(room, edges, knight, verifiedCamera, buttons, timestamp, floors, terrain.Walls);
            terrain = TerrainWallDetector.ApplyContact(terrain, contact, floors, edges);
            platformPatches = patches.Observe(edges, floors, prepared.ProtectedOcclusions, verifiedCamera, timestamp);
        }
        return new(observation, ground, estimate, ground.CameraPosition ?? localCamera, room, terrain, contact,
            platformPatches, walked.InRoom(room), walkedCoordinateEpoch, cameraMilliseconds, atlasStarted)
        {
            RoomAllowsAtlasWrite = visualRoom.AllowsAtlasWrite,
            SceneCut = visualRoom.Cut
        };
    }

    public void Reset(bool preserveWalkedGround = false)
    {
        if (preserveWalkedGround) walked.BreakContinuity();
        else ResetWalkedCoordinateEpoch();
        patches.Reset();
        Paths.Reset();
        needsCameraAnchor = true;
        tracker.Reset();
        semanticAtlas.Reset();
        reference.Reset();
        roomIdentity.ResetObservation();
        lastKnownRoom = null;
        frameWidth = 0;
        frameHeight = 0;
    }

    private void SuspendTracking()
    {
        tracker.Reset(keepingGlobalFeatures: true);
        reference.Reset();
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
        private bool transitionResumeActive;
        private bool loadingOwnsResume;

        internal (string Id, bool AllowsAtlasWrite, VisualSceneDiscontinuityTracker.Cut? Cut) Observe(
            long currentCaptureGeneration, bool enabled, TransitionFrameDecision transition, LowResolutionMotionGrid? grid,
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

            var wasPending = cuts.IsPending;
            var drive = KnightLevelWalk.Direction(buttons ?? InputButtons.None);
            var direction = drive < 0 ? VisualRoomDirection.Left
                : drive > 0 ? VisualRoomDirection.Right : (VisualRoomDirection?)null;
            var cut = cuts.Observe(grid, timestamp, direction, worldPose);
            loadingOwnsResume |= wasPending || cuts.IsPending || cut is not null;
            if (cut is not null
                || (transition.ResumedAfterTransition && !transitionResumeActive && !loadingOwnsResume))
                roomEpoch = checked(roomEpoch + 1);
            if (transition.AdmitsTracking && !cuts.IsPending) loadingOwnsResume = false;
            transitionResumeActive = transition.ResumedAfterTransition;
            return (RoomId, !cuts.IsPending && cut is null, cut);
        }

        internal void ResetObservation()
        {
            cuts.Reset();
            transitionResumeActive = false;
            loadingOwnsResume = false;
        }

        private string RoomId => FormattableString.Invariant($"normal:{captureGeneration}:room:{roomEpoch}");
    }
}
