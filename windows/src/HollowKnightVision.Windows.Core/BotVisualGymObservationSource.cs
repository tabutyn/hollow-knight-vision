using System.Globalization;

namespace HollowKnightVision.Windows.Core;

public sealed record BotVisualGymReading(BotCaptureObservation? Observation, IReadOnlyList<string> Issues, float? KnightConfidence)
{
    public BotVisualCameraDiagnostic? Camera { get; init; }
    public bool? WeakKnightCorrection => Observation?.KnightPrediction?.UsedWeakDetection;
}

public sealed record BotVisualCameraDiagnostic(string PoseSource, bool GroundVerified, bool AllowsAtlasWrite,
    int ReferenceFloors, int GroundSupport, int GlobalFeatures, int GlobalMatches);

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
    private readonly GroundSemanticAtlas terrainAtlas = new();
    private readonly WalkedGroundTracker walked = new();
    private readonly BotVisualAtlasCoverage coverage = new();
    private readonly BotBlockadeVisit blockade = new();
    private readonly BotVisualKnightMemory knightMemory = new();
    private int chart;
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
        }
        if (!admitted.AdmitsTracking)
        {
            _ = ObserveRoom(camera.Position, groundVerified: false);
            camera.PauseGroundVerification();
            coverage.Interrupt();
            blockade.Interrupt();
            knightMemory.Reset();
            return new(null, [signal.HasGameplaySignal ? "waiting-for-visual-gameplay" : "transition-blackout"], confidence);
        }

        var analysis = GameplayTerrainAnalysis.Analyze(frame, masks);
        var textureExclusions = masks.GroundKnight is { } knight
            ? masks.GroundExclusions.Append(knight).ToArray() : masks.GroundExclusions;
        var measuredMotion = motion.Observe(frame, masks.GroundRegions.OmittedRects);
        var observed = camera.ObserveGroundAnchored(frame, measuredMotion, analysis.Edges.SourceLuma!,
            analysis.CameraReferences, textureExclusions, coverage.HasCoverage, masks.GroundKnight, ground, time);
        var placement = observed.GroundVerified ? observed.GroundTracking.CameraPosition : null;
        var room = ObserveRoom(placement ?? camera.Position, observed.GroundVerified);
        if (room.IsTransitioning || admitted.Transition.ResumedAfterTransition && activeRoom == room.RoomID)
        {
            walked.BreakContinuity();
            coverage.Interrupt();
            blockade.Interrupt();
            knightMemory.Reset();
            return new(null, ["room-transition"], confidence);
        }
        if (activeRoom.HasValue && activeRoom.Value != room.RoomID)
        {
            activeRoom = room.RoomID;
            chart++;
            coverage.Reset();
            blockade.Reset();
            knightMemory.Reset();
            ground.Reset(keepingGlobalFeatures: false);
            camera.Reset();
            motion.Reset();
            walked.Reset();
            terrainAtlas.Reset();
            return new(null, ["room-transition"], confidence);
        }
        activeRoom = room.RoomID;
        var terrain = TerrainWallDetector.Detect(analysis.Edges, analysis.Floors);
        var roomKey = chart.ToString(CultureInfo.InvariantCulture);
        var contact = walked.Observe(roomKey, analysis.Edges, masks.GroundKnight,
            placement, controls, time, analysis.Floors, terrain.Walls);
        terrain = TerrainWallDetector.ApplyContact(terrain, contact, analysis.Floors, analysis.Edges);
        var floors = analysis.Floors;
        if (placement is { } pose)
        {
            var confirmed = terrainAtlas.Update(observed.GroundTracking.AtlasLines,
                analysis.Floors.Select(line => line.ToSemanticLine()).ToArray(), pose,
                frame.Width, frame.Height, poseVerified: true);
            var strokes = GroundStrokeGeometry.VisibleLines(confirmed, observed.GroundTracking.LineReviews,
                walked.InRoom(roomKey), pose, frame.Height, masks.GroundKnight);
            floors = CombineFloors(floors, strokes, pose, frame.Width, frame.Height);
            // This episode owns an empty coverage grid. A verified pose can
            // register its local footprint without waiting for the live
            // desktop Atlas's stationary first-write bootstrap.
            if (admitted.AllowsAtlasWrite)
                coverage.Register(pose, size, masks.AtlasExclusions, room.CompositionBounds);
        }
        else coverage.Interrupt();

        var timing = new BotCaptureTiming(time, time, Generation, chart, room.Revision);
        var row = BotObservationBuilder.Gameplay(frame, timing, observations.MeasuredBatch, floors, terrain.Walls,
            terrainAnalyzed: true, placement, hud, contact.MeasuredFoot, controls);
        row = knightMemory.Resolve(row, BotPolicyAction.FromButtons(controls), observations.MeasuredBatch);
        row = blockade.AddEvidence(row, BotBlockadePose.Visual(row));
        row = row with { AtlasView = coverage.View(placement, size, (int)frame.CaptureId, row.MeasuredHealth) };
        return new(row, row.Issues, confidence)
        {
            Camera = new(observed.Source, observed.GroundVerified, observed.AllowsAtlasWrite, analysis.CameraReferences.Count,
                observed.GroundSupport, observed.GroundTracking.GlobalFeatureCount, observed.GroundTracking.GlobalMatchCount)
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

    private static IReadOnlyList<DetectedFloorLine> CombineFloors(IReadOnlyList<DetectedFloorLine> detected,
        IReadOnlyList<GroundHypothesisAtlasLine> persisted, AtlasPoint pose, int width, int height)
    {
        var lines = detected.ToList();
        foreach (var floor in persisted.Where(line => line.SegmentId == WalkedGroundMemory.WalkedSegmentId))
        {
            var row = AtlasPathPoint.Round(height + pose.Y - floor.AtlasStart.Y);
            var left = Math.Max(0, (int)Math.Floor(floor.AtlasStart.X - pose.X));
            var right = Math.Min(width - 1, (int)Math.Ceiling(floor.AtlasEnd.X - pose.X) - 1);
            if (row < 0 || row >= height || left >= right) continue;
            // An admitted walked extent extends the measured edge across local occlusion.
            var overlaps = lines.Where(line => Math.Abs(line.Row - row) <= 2
                && left <= line.XSpan.EndInclusive && right >= line.XSpan.Start).ToArray();
            if (overlaps.Length > 0)
            {
                left = Math.Min(left, overlaps.Min(line => line.XSpan.Start));
                right = Math.Max(right, overlaps.Max(line => line.XSpan.EndInclusive));
                lines.RemoveAll(overlaps.Contains);
            }
            lines.Add(new(row, new(left, right)));
        }
        return lines;
    }

}
