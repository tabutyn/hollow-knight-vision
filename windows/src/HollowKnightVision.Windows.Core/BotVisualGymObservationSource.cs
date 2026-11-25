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
    // Visual Gym begins at a controlled Gameplay checkpoint, as on macOS.
    private readonly LiveGameStatusPipeline status = new(startsInGameplay: true);
    private readonly GameplayFrameAdmission admission = new();
    private readonly VisualRoomTransitionTracker rooms = new();
    private readonly MaskedFrameMotionTracker motion = new();
    private readonly VisualGameplayTracker camera = new();
    private readonly GroundHypothesisTracker ground = new(.2, backgroundGlobalSearch: true, confirmedLossGraceSeconds: 3,
        productionCameraSolver: true, deterministicGlobalSearch: true);
    private readonly WalkedGroundTracker walked = new();
    private readonly JumpContactGate jumpContact = new();
    private readonly MeasuredAtlasPoseLineage measuredAtlasPose = new();
    private readonly CaptureIslandLineage captureIslands = new();
    private readonly BotGymAtlasMotion atlasMotion = new();
    private readonly BotGymAtlasCoverage coverage = new();
    private readonly BotBlockadeVisit blockade = new();
    private readonly BotVisualKnightMemory knightMemory = new();
    private int chart;
    private long walkedEpoch;
    private int? activeRoom;
    private LiveDoorwayTransition? recentDoorway;

    public BotVisualGymReading Observe(BgraFrame frame, double time, InputButtons controls,
        Func<BgraFrame, IReadOnlyList<ObjectDetection>> detect, HudStencilResult? hud, MenuStencilResult? menu = null)
    {
        var recentJump = jumpContact.Observe((controls & InputButtons.ActionZ) != 0, time);
        var signal = FrameRegionRenderer.SignalProfile(frame);
        var frameTime = (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
        var observations = LiveObjectDetectionSnapshot.Empty;
        var presentation = status.Observe(menu, hud, time, inferObjects: signal.HasGameplaySignal ? Infer : null);
        var admitted = admission.Observe(signal, presentation.Status?.IsGameplay == true, Generation);
        var masks = GameplaySceneMasks.Create(frame, observations, hud, Generation);
        var contactKnight = masks.ContactKnight(frame.Width, frame.Height);
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
            captureIslands.Reset();
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
        var cameraReferences = admitted.Transition.ResumedAfterTransition ? analysis.CameraReferences
            : walked.CameraReferences(analysis.CameraReferences, roomKey, Generation, time,
                frame.Width, frame.Height, contactKnight);
        VisualRoomSnapshot? room = null;
        var observed = camera.ObserveGroundAnchored(frame, measuredMotion, analysis.Edges.SourceLuma!,
            cameraReferences, textureExclusions, coverage.HasPlacedRoom, masks.GroundKnight, ground, time,
            signal.MotionGrid, direction, skipped, (pose, verified) =>
            {
                room = ObserveRoom(pose, verified);
                return room.RoomID == activeRoom && !room.IsTransitioning && !admitted.Transition.ResumedAfterTransition
                    ? room.CoarsePlaceMatch : null;
            }, measuredKnight: masks.ObservedPlayerBounds);
        var placement = observed.GroundVerified ? observed.GroundTracking.CameraPosition : null;
        var roomState = room!;
        if (roomState.IsTransitioning || admitted.Transition.ResumedAfterTransition && activeRoom == roomState.RoomID)
        {
            walked.BreakContinuity();
            measuredAtlasPose.Reset();
            captureIslands.Reset();
            atlasMotion.Reset();
            camera.ResetFloorlessMotion();
            coverage.Interrupt();
            blockade.Interrupt();
            knightMemory.Reset();
            return new(null, ["room-transition"], confidence);
        }
        if (activeRoom.HasValue && activeRoom.Value != roomState.RoomID)
        {
            activeRoom = roomState.RoomID;
            chart++;
            coverage.EnterRoom(roomState.RoomID, recentDoorway);
            recentDoorway = null;
            blockade.Reset();
            knightMemory.Reset();
            ground.Reset(keepingGlobalFeatures: false);
            camera.Reset();
            rooms.ClearPlaceReferences();
            motion.Reset();
            walked.Reset();
            measuredAtlasPose.Reset();
            captureIslands.Reset();
            atlasMotion.Reset();
            return new(null, ["room-transition"], confidence);
        }
        activeRoom = roomState.RoomID;
        coverage.EnterRoom(roomState.RoomID);
        var terrain = TerrainWallDetector.Detect(analysis.Edges, analysis.Floors);
        var corrected = placement.HasValue && observed.GroundTracking.GlobalCorrection is { } correction
            && correction.X * correction.X + correction.Y * correction.Y > 9;
        if (corrected) walkedEpoch++;
        if (observed.ReanchoredAtRoomPlace) walked.BreakContinuity();
        var relocalized = corrected || observed.ReanchoredAtRoomPlace;
        var localCamera = camera.Position;
        var measuredGround = observed.GroundVerified && GroundMotionEvidence.IsMeasured(observed.GroundTracking);
        var measuredScenery = measuredAtlasPose.Observe(Generation, roomKey, time, localCamera,
            measuredGround, observed.SelectedScenery, relocalized);
        var continuity = atlasMotion.Observe(frame, masks, Generation, roomKey, time, localCamera, observed,
            coverage.CanContinueMeasuredScenery, measuredGround, measuredScenery, relocalized);
        if (continuity.RequiresRecovery) coverage.Interrupt(requireMatch: true);
        var continuesScenery = measuredScenery && coverage.CanContinueMeasuredScenery && camera.AllowsAtlasPlacement;
        var terrainCamera = continuesScenery ? localCamera : placement;
        var referenceTimestamp = measuredGround ? observed.GroundTracking.LocalReferenceTimestamp
            : observed.SelectedScenery?.ReferenceTimestamp;
        var lineage = captureIslands.Observe(Generation, roomKey, time, measuredGround || continuesScenery,
            referenceTimestamp, relocalized);
        var contactCamera = lineage is null ? null : terrainCamera;
        var contact = walked.Observe(roomKey, analysis.Edges, contactKnight,
            contactCamera, controls, time, analysis.Floors, terrain.Walls, Generation, walkedEpoch, lineage, recentJump);
        terrain = TerrainWallDetector.ApplyContact(terrain, contact, analysis.Floors, analysis.Edges);
        LiveWorldCaptureEvidence? captureEvidence = null;
        if (admitted.AllowsAtlasWrite && (measuredGround || continuesScenery))
        {
            captureEvidence = LiveWorldCaptureEvidence.FromGround(time, frame.Width, frame.Height, frame.Width,
                analysis.Floors, observed.GroundTracking.LineReviews, contact.MeasuredFoot,
                measuredGround ? contact.Floor : null, masks.ObservedPlayerBounds) with
            {
                LocalReferenceTimestamp = referenceTimestamp,
                HasMeasuredSceneryContinuity = !measuredGround,
                MeasuredLineage = lineage,
                OtherWalks = lineage is { } owner ? walked.OtherOwners(roomKey, owner) : []
            };
        }
        var localWalk = walked.InRoom(roomKey);
        coverage.Observe(frame, terrainCamera, time, masks.AtlasExclusions, roomState.CompositionBounds,
            observed.GroundTracking, admitted.AllowsAtlasWrite, continuesScenery, continuity.RetainBasis, captureEvidence,
            lineage is not null ? localWalk : null);
        var floors = GroundStrokeGeometry.MergeWalkedFloors(analysis.Floors,
            coverage.WalkedForPolicy(localWalk, lineage), contactCamera,
            frame.Width, frame.Height, contactKnight);

        var timing = new BotCaptureTiming(time, time, Generation, chart, roomState.Revision);
        var row = BotObservationBuilder.Gameplay(frame, timing, observations.MeasuredBatch, floors, terrain.Walls,
            terrainAnalyzed: true, placement, hud, contact.MeasuredFoot, controls,
            visualDoors: coverage.ProjectDoors(measuredGround || continuesScenery ? terrainCamera : null, size),
            supportingFloors: analysis.Floors, masks: masks);
        row = knightMemory.Resolve(row, BotPolicyAction.FromButtons(controls), observations.MeasuredBatch, walkedEpoch);
        row = blockade.AddAtlasEvidence(row, coverage.BlockadeProjection(lineage, time), contactCamera, frame.Width, lineage, walkedEpoch);
        row = row with { AtlasView = coverage.PolicyView(terrainCamera, size, row.MeasuredHealth) };
        return new(row, row.Issues, confidence)
        {
            Camera = new(observed.Source, observed.GroundVerified, observed.AllowsAtlasWrite || continuesScenery, cameraReferences.Count,
                observed.GroundSupport, observed.GroundTracking.GlobalFeatureCount, observed.GroundTracking.GlobalMatchCount)
            { AtlasPlacementPending = coverage.PlacementPending }
        };

        IReadOnlyList<ObjectDetection> Infer()
        {
            objects.Accept(detect(frame), frame, Generation);
            observations = objects.Snapshot(Generation, frameTime);
            return observations.MeasuredBatch!.Detections;
        }

        VisualRoomSnapshot ObserveRoom(AtlasPoint pose, bool groundVerified)
        {
            var snapshot = rooms.Observe(signal, admitted.Transition, direction, masks.GroundRegions.Knight,
                size, pose, frame.Width, time, groundTrackingReliable: groundVerified);
            if (snapshot.ConfirmedSceneCut is { } crossing)
            {
                var (sourceRoom, cut) = crossing;
                recentDoorway = new(sourceRoom, snapshot.RoomID, Generation, cut.DepartureTimestamp,
                    cut.ArrivalTimestamp, cut.Direction);
            }
            return snapshot;
        }
    }

}
