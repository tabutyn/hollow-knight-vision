using System.Globalization;

namespace HollowKnightVision.Windows.Core;

public sealed record BotVisualGymReading(BotCaptureObservation? Observation, IReadOnlyList<string> Issues, float? KnightConfidence)
{
    public BotVisualCameraDiagnostic? Camera { get; init; }
    public bool? WeakKnightCorrection => Observation?.KnightPrediction?.UsedWeakDetection;
}

public sealed record BotVisualCameraDiagnostic(string PoseSource, bool GroundVerified, bool AllowsAtlasWrite,
    int ReferenceFloors, int GroundSupport, int GlobalFeatures, int GlobalMatches)
{
    public bool AtlasPlacementPending { get; init; }
}

/// <summary>Produces Game Play policy observations using only pixels, the episode clock, and applied controls.</summary>
public sealed class BotVisualGymObservationSource
{
    private const long Generation = 1;
    private readonly LiveObjectDetectionPipeline objects = new();
    private readonly LiveGameStatusPipeline status = new();
    private readonly GameplayFrameAdmission admission = new();
    private readonly VisualRoomTransitionTracker rooms = new();
    private readonly MaskedFrameMotionTracker motion = new();
    private readonly VisualGameplayTracker camera = new();
    private readonly GroundHypothesisTracker ground = new(.2, backgroundGlobalSearch: true, confirmedLossGraceSeconds: 3,
        productionCameraSolver: true, deterministicGlobalSearch: true);
    private readonly WalkedGroundTracker walked = new();
    private readonly MeasuredAtlasPoseLineage measuredAtlasPose = new();
    private readonly BotGymAtlasMotion atlasMotion = new();
    private readonly BotGymAtlasCoverage coverage = new();
    private readonly BotBlockadeVisit blockade = new();
    private readonly BotVisualKnightMemory knightMemory = new();
    private int chart;
    private long walkedEpoch;
    private int? activeRoom;

    public BotVisualGymReading Observe(BgraFrame frame, double time, InputButtons controls,
        Func<BgraFrame, IReadOnlyList<ObjectDetection>> detect, HudStencilResult? hud, MenuStencilResult? menu = null)
    {
        var signal = FrameRegionRenderer.SignalProfile(frame);
        var frameTime = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
        var observations = LiveObjectDetectionSnapshot.Empty;
        var presentation = status.Observe(menu, hud, time, inferObjects: signal.HasGameplaySignal ? Infer : null);
        var admitted = admission.Observe(signal, presentation.Status?.IsGameplay == true, Generation);
        var masks = GameplaySceneMasks.Create(frame, observations.Tracked, hud, observations.Masks);
        var size = new WorldSize(frame.Width, frame.Height);
        var direction = KnightLevelWalk.Direction(controls) switch
        {
            -1 => VisualRoomDirection.Left, 1 => VisualRoomDirection.Right, _ => (VisualRoomDirection?)null
        };
        var confidence = observations.MeasuredBatch?.Detections.Where(item =>
            LabelingClassIdentity.CanonicalIdentifier(item.ClassIdentifier) == LiveObjectDetectionPostprocessor.Knight)
            .Select(item => (float?)item.Confidence).Max();
        if (admitted.ResetMotionReference)
        {
            motion.Reset();
            ground.Reset(keepingGlobalFeatures: true);
            walked.BreakContinuity();
            measuredAtlasPose.Reset();
            atlasMotion.Reset();
            camera.ResetFloorlessMotion();
        }
        if (!admitted.AdmitsTracking)
        {
            _ = ObserveRoom(camera.Position, groundVerified: false);
            camera.PauseGroundVerification();
            measuredAtlasPose.Reset();
            atlasMotion.Reset();
            coverage.Interrupt();
            blockade.Interrupt();
            knightMemory.Reset();
            return new(null, [signal.HasGameplaySignal ? "waiting-for-visual-gameplay" : "transition-blackout"], confidence);
        }

        var analysis = GameplayTerrainAnalysis.Analyze(frame, masks);
        var textureExclusions = masks.GroundKnight is { } knight
            ? masks.GroundExclusions.Append(knight).ToArray() : masks.GroundExclusions;
        var measuredMotion = motion.Observe(frame, masks.GroundRegions.OmittedRects);
        var roomKey = chart.ToString(CultureInfo.InvariantCulture);
        var skipped = !admitted.Transition.ResumedAfterTransition && coverage.CanContinueMeasuredScenery
            ? atlasMotion.MatchSkipped(frame, masks, Generation, roomKey, time) : null;
        var observed = camera.ObserveGroundAnchored(frame, measuredMotion, analysis.Edges.SourceLuma!,
            analysis.CameraReferences, textureExclusions, coverage.HasPlacedRoom, masks.GroundKnight, ground, time,
            signal.MotionGrid, direction, skipped);
        var placement = observed.GroundVerified ? observed.GroundTracking.CameraPosition : null;
        var room = ObserveRoom(placement ?? camera.Position, observed.GroundVerified);
        if (room.IsTransitioning || admitted.Transition.ResumedAfterTransition && activeRoom == room.RoomID)
        {
            walked.BreakContinuity();
            measuredAtlasPose.Reset();
            atlasMotion.Reset();
            camera.ResetFloorlessMotion();
            coverage.Interrupt();
            blockade.Interrupt();
            knightMemory.Reset();
            return new(null, ["room-transition"], confidence);
        }
        if (activeRoom.HasValue && activeRoom.Value != room.RoomID)
        {
            activeRoom = room.RoomID;
            chart++;
            coverage.EnterRoom(room.RoomID);
            blockade.Reset();
            knightMemory.Reset();
            ground.Reset(keepingGlobalFeatures: false);
            camera.Reset();
            motion.Reset();
            walked.Reset();
            measuredAtlasPose.Reset();
            atlasMotion.Reset();
            return new(null, ["room-transition"], confidence);
        }
        activeRoom = room.RoomID;
        coverage.EnterRoom(room.RoomID);
        var terrain = TerrainWallDetector.Detect(analysis.Edges, analysis.Floors);
        var corrected = placement.HasValue && observed.GroundTracking.GlobalCorrection is { } correction
            && correction.X * correction.X + correction.Y * correction.Y > 9;
        if (corrected) walkedEpoch++;
        var localCamera = camera.Position;
        var measuredGround = observed.GroundVerified && GroundMotionEvidence.IsMeasured(observed.GroundTracking);
        var measuredScenery = measuredAtlasPose.Observe(Generation, roomKey, time, localCamera,
            measuredGround, observed.SelectedScenery, corrected);
        var continuity = atlasMotion.Observe(frame, masks, Generation, roomKey, time, localCamera, observed,
            coverage.CanContinueMeasuredScenery, measuredGround, measuredScenery, corrected);
        if (continuity.RequiresRecovery) coverage.Interrupt(requireMatch: true);
        var continuesScenery = measuredScenery && coverage.CanContinueMeasuredScenery && camera.AllowsAtlasPlacement;
        var terrainCamera = continuesScenery ? localCamera : placement;
        var contact = walked.Observe(roomKey, analysis.Edges, masks.GroundKnight,
            terrainCamera, controls, time, analysis.Floors, terrain.Walls, Generation, walkedEpoch);
        terrain = TerrainWallDetector.ApplyContact(terrain, contact, analysis.Floors, analysis.Edges);
        var floors = GroundStrokeGeometry.MergeWalkedFloors(analysis.Floors, walked.InRoom(roomKey), terrainCamera,
            frame.Width, frame.Height, masks.GroundKnight);
        var groundEvidence = measuredGround && admitted.AllowsAtlasWrite
            ? LiveWorldCaptureEvidence.FromGround(time, frame.Width, frame.Height, analysis.Floors,
                observed.GroundTracking.LineReviews, masks.GroundKnight, contact.MeasuredFoot, contact.Floor)
            : null;
        coverage.Observe(frame, terrainCamera, time, masks.AtlasExclusions, room.CompositionBounds,
            observed.GroundTracking, admitted.AllowsAtlasWrite, continuesScenery, continuity.RetainBasis, groundEvidence);

        var timing = new BotCaptureTiming(time, time, Generation, chart, room.Revision);
        blockade.ObserveVisualBasis(Generation, chart, walkedEpoch, frame.Width, time, placement.HasValue,
            observed.GroundTracking, continuesScenery ? localCamera : null);
        var row = BotObservationBuilder.Gameplay(frame, timing, observations.MeasuredBatch, floors, terrain.Walls,
            terrainAnalyzed: true, placement, hud, contact.MeasuredFoot, controls);
        row = knightMemory.Resolve(row, BotPolicyAction.FromButtons(controls), observations.MeasuredBatch, walkedEpoch);
        row = blockade.AddVisualEvidence(row);
        row = row with { AtlasView = coverage.PolicyView(terrainCamera, size, row.MeasuredHealth) };
        return new(row, row.Issues, confidence)
        {
            Camera = new(observed.Source, observed.GroundVerified, observed.AllowsAtlasWrite || continuesScenery, analysis.CameraReferences.Count,
                observed.GroundSupport, observed.GroundTracking.GlobalFeatureCount, observed.GroundTracking.GlobalMatchCount)
            { AtlasPlacementPending = coverage.PlacementPending }
        };

        IReadOnlyList<ObjectDetection> Infer()
        {
            objects.Accept(detect(frame), frame, Generation);
            observations = objects.Snapshot(Generation, frameTime);
            return observations.MeasuredBatch!.Detections;
        }

        VisualRoomSnapshot ObserveRoom(AtlasPoint pose, bool groundVerified) =>
            rooms.Observe(signal, admitted.Transition, direction, masks.GroundRegions.Knight,
                size, pose, frame.Width, time, groundTrackingReliable: groundVerified);
    }

}
