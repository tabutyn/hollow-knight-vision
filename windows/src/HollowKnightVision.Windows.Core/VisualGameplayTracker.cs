using System.Diagnostics;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record FloorlessCameraEvidence(AtlasPoint? CoarsePosition, AtlasPoint? MaskedPosition,
    VisualRoomDirection? ExpectedDirection, FloorlessPoseCandidateSource? SelectedSource,
    double? CoarseReferenceTimestamp, bool CoarseMeasured, CalibratedSceneMotionEvidence? Scene = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LowResolutionPlaceMatch? Place = null)
{
    public string SelectionName => SelectedSource switch
    {
        FloorlessPoseCandidateSource.CoarseCaptureRate => "coarseCaptureRate",
        FloorlessPoseCandidateSource.MaskedRegistration => "maskedRegistration",
        FloorlessPoseCandidateSource.CalibratedScene => "calibratedScene",
        FloorlessPoseCandidateSource.RoomPlaceMatch => "coarsePlaceMatch",
        _ => "held"
    };
}

public sealed record VisualGameplayObservation(
    ReceiverGroundTruthSample Telemetry,
    CameraUpdate? Registration,
    bool GroundVerified,
    bool AllowsAtlasWrite,
    int GroundSupport,
    double? GroundError,
    string Source)
{
    public GroundHypothesisTrackingResult GroundTracking { get; init; } = GroundHypothesisTrackingResult.Empty;
    public FloorlessCameraEvidence? FloorlessEvidence { get; init; }
    internal CalibratedSceneMotionEvidence? SelectedScenery =>
        FloorlessEvidence is { SelectedSource: FloorlessPoseCandidateSource.CalibratedScene } evidence ? evidence.Scene : null;
    internal bool ReanchoredAtRoomPlace =>
        FloorlessEvidence?.SelectedSource == FloorlessPoseCandidateSource.RoomPlaceMatch;
    public bool RetainsWorldBasisDuringPlacementCheck { get; init; }
    public double? RecoveredAtlasReferenceTimestamp { get; init; }
    public double? MotionBridgeConfidence { get; init; }
    public double? SceneMotionMilliseconds { get; init; }
}

/// <summary>
/// Converts image translation into the camera-shaped sample consumed by the tiled
/// atlas. Scenery and coarse room motion can maintain the displayed position
/// while ground references are absent.
/// </summary>
public sealed class VisualGameplayTracker
{
    internal const float MinimumRecoveryConfidence = .8f;
    private readonly CameraAccumulator camera = new()
    {
        Smoothing = 0.65,
        MinimumConfidence = 0.10f
    };
    private readonly Lazy<GroundHypothesisTracker> defaultGroundTracker = new(() => new(
        minimumObservationSeconds: .2, backgroundGlobalSearch: true,
        confirmedLossGraceSeconds: 3, productionCameraSolver: true));
    public GroundPatchSearchExecution? LastPatchSearchExecution { get; private set; }
    public string PlacementRecoveryState => placement.State;
    internal bool AllowsAtlasPlacement => placement.AllowsAtlasWrite;
    public AtlasPoint Position => new(camera.Position.X, -camera.Position.Y);
    private readonly FreshAtlasBootstrapGate bootstrap = new();
    private readonly TransitionMotionBridge transition = new();
    private readonly GroundPlacementRecovery placement = new();
    private readonly FloorlessCameraMotion floorless = new();
    private readonly CalibratedSceneMotionTracker calibratedScene = new();
    private VisualCameraPoseSample? measuredGround;
    private CameraVector? groundVelocity;
    private double? previousTimestamp;
    private ulong sequence;
    private DateTimeOffset? startedAt;

    public ReceiverGroundTruthSample Observe(
        BgraFrame frame,
        LowResolutionTranslationDiagnostic? diagnostic) =>
        ObserveUnverified(frame, diagnostic).Telemetry;

    public VisualGameplayObservation ObserveUnverified(
        BgraFrame frame,
        LowResolutionTranslationDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bootstrap.Reset();
        var update = ApplyCoarseMotion(frame, diagnostic);
        return new(BuildSample(frame), update, false, false, 0, null,
            update is null ? "held" : "coarse-presentation");
    }

    public VisualGameplayObservation ObserveGroundAnchored(
        BgraFrame frame,
        LowResolutionTranslationDiagnostic? diagnostic,
        byte[] sourceLuma,
        IReadOnlyList<DetectedFloorLine> lines,
        IReadOnlyList<PixelRect> exclusions,
        bool hasCommittedEvidence,
        PixelRect? movingForeground = null,
        GroundHypothesisTracker? groundTracker = null,
        double? captureTimestamp = null,
        LowResolutionMotionGrid? sceneGrid = null,
        VisualRoomDirection? expectedDirection = null,
        TransitionMotionBridgeResult? skippedCaptureRegistration = null,
        Func<AtlasPoint, bool, LowResolutionPlaceMatch?>? observeRoomPlace = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(sourceLuma);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(exclusions);
        var tracker = groundTracker ?? defaultGroundTracker.Value;
        var timestamp = captureTimestamp ?? Elapsed(frame);
        var evaluatedPlacement = false;
        // One transaction owns local texture, persistent recovery, placement
        // validation and line admission, exactly as in Mac's LiveCaptureModel.
        var tracking = tracker.Update(sourceLuma, frame.Width, frame.Height, lines,
            lineSeparation: 36, occlusionMergeGap: 180, timestamp: timestamp,
            protectedOcclusions: exclusions,
            placementValidator: proposal =>
            {
                evaluatedPlacement = true;
                return placement.Accepts(proposal,
                    new AtlasPoint(camera.Position.X, -camera.Position.Y),
                    frame.Width, timestamp,
                    previousTimestamp is { } previous ? timestamp - previous : null);
            });
        LastPatchSearchExecution = tracker.LatestPatchSearchExecution;
        if (!evaluatedPlacement) placement.MissingObservation();
        var firstFrame = previousTimestamp is null;
        var captureInterval = timestamp - previousTimestamp;
        previousTimestamp = timestamp;
        var verified = tracking.PoseVerified && tracking.HasConfirmedGround
            && tracking.CameraPosition is not null;
        var groundMeasured = verified && GroundMotionEvidence.IsMeasured(tracking);
        if (tracking.GlobalCorrection is not null) calibratedScene.Reset();
        var calibration = groundMeasured && tracking.GlobalCorrection is null
            && tracking.LocalReferenceTimestamp is { } reference
            && tracking.LocalMatchedImageDisplacement is { } displacement
            ? new CalibratedSceneMotionTracker.GroundCalibration(tracking.CameraPosition!.Value, reference, displacement)
            : null;
        CalibratedSceneMotionEvidence? scene = null;
        double? sceneMotionMilliseconds = null;
        if (sceneGrid is not null && (verified || transition.IsAnchored) && !transition.AwaitingSceneResume)
        {
            var started = Stopwatch.GetTimestamp();
            if (CalibratedSceneMotionTracker.FromLuma(sourceLuma, frame.Width, frame.Height, exclusions) is { } image)
                scene = calibratedScene.Observe(image, timestamp, frame.Width, calibration);
            sceneMotionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        CameraUpdate? update;
        TransitionMotionBridgeResult? bridged = null;
        FloorlessCameraEvidence? floorlessEvidence = null;
        var selectedCoarse = false;
        var selectedScene = false;
        var selectedPlace = false;
        if (verified)
        {
            var pose = tracking.CameraPosition!.Value;
            _ = observeRoomPlace?.Invoke(pose, true);
            update = ApplyPose(pose);
            transition.Anchor(pose, movingForeground);
            floorless.Observe(sceneGrid, timestamp, frame.Width, pose, true, expectedDirection, null);
            if (groundMeasured)
            {
                groundVelocity = measuredGround is { } previous
                    ? PresentationPosePrediction.Velocity(previous.CameraPosition, previous.Timestamp, pose, timestamp,
                        continuous: tracking.GlobalCorrection is null)
                    : null;
                measuredGround = new VisualCameraPoseSample(timestamp, pose);
            }
        }
        else
        {
            var current = Position;
            var resumedAfterTransition = transition.AwaitingSceneResume;
            var masked = skippedCaptureRegistration ?? transition.Track(diagnostic, frame.Width, frame.Height, frame.Width,
                movingForeground: movingForeground);
            AtlasPoint? acceptedFallback = null;
            if (sceneGrid is not null && transition.IsAnchored && !resumedAfterTransition)
            {
                var velocity = FloorlessMotionHandoff.Seed(groundVelocity, measuredGround?.Timestamp, timestamp);
                floorless.Observe(sceneGrid, timestamp, frame.Width, current, false, expectedDirection, velocity);
                // Both Windows matchers use the same grid resolution. A valid
                // registration with foreground and HUD removed owns the pose.
                // The broader scene can fill lost or skipped registrations.
                var coarsePosition = floorless.Odometry.LastObservationTimestamp == timestamp
                    ? floorless.PublishedPose : null;
                var coarseReference = coarsePosition is null ? null : floorless.Odometry.LastReferenceTimestamp;
                var coarseMeasured = coarsePosition is not null && floorless.Odometry.LastEstimateIsMeasured;
                var selection = SelectFloorlessPose(current, scene, masked, coarsePosition, expectedDirection);
                var match = observeRoomPlace?.Invoke(selection?.Position ?? current, false);
                if (match is { CameraPosition: { } place } && double.IsFinite(place.X) && double.IsFinite(place.Y))
                {
                    // A confirmed view supplies an absolute pose within this
                    // room. Future relative motion starts at that exact image.
                    selection = new(place, FloorlessPoseCandidateSource.RoomPlaceMatch);
                    floorless.ReanchorAtPlace(place, sceneGrid, timestamp);
                    transition.Anchor(place, movingForeground);
                    calibratedScene.Reset();
                    measuredGround = null;
                    groundVelocity = null;
                    selectedPlace = true;
                }
                floorlessEvidence = new(coarsePosition, masked?.CameraPosition, expectedDirection,
                    selection?.Source, coarseReference, coarseMeasured, scene, selectedPlace ? match : null);
                acceptedFallback = selection?.Position;
                selectedCoarse = selection?.Source == FloorlessPoseCandidateSource.CoarseCaptureRate;
                selectedScene = selection?.Source == FloorlessPoseCandidateSource.CalibratedScene;
                bridged = selectedScene || selectedPlace ? null : masked;
                if ((selectedScene || bridged is not null) && acceptedFallback is { } selected
                    && coarsePosition is { } coarse)
                {
                    // Rebase the two estimates at the same captured image. A
                    // later masked failure must not inherit discarded travel.
                    _ = floorless.ApplyCorrection(new CameraVector(
                        selected.X - coarse.X, selected.Y - coarse.Y), floorless.Revision);
                }
                if (selectedCoarse || selectedScene || bridged?.ReferenceTimestamp is not null)
                    transition.Anchor(acceptedFallback!.Value, movingForeground);
                update = acceptedFallback is { } pose ? ApplyPose(pose) : null;
            }
            else
            {
                bridged = masked;
                acceptedFallback = masked?.CameraPosition;
                if (masked is not null)
                    floorlessEvidence = new(null, masked.CameraPosition, expectedDirection,
                        FloorlessPoseCandidateSource.MaskedRegistration, null, false);
                update = acceptedFallback is { } pose ? ApplyPose(pose)
                    : resumedAfterTransition ? null : ApplyCoarseMotion(frame, diagnostic);
                _ = observeRoomPlace?.Invoke(Position, false);
            }

            if (acceptedFallback is { } fallback)
            {
                // Preserve independent provisional motion and pending placement
                // votes; repeatedly replacing them can prevent reacquisition.
                var measuredFallback = selectedPlace || bridged is not null
                    || selectedCoarse && floorless.Odometry.LastEstimateIsMeasured;
                if (measuredFallback && !tracking.PoseVerified && !tracking.ProvisionalContinuityActive
                    && !placement.RetainsCandidate(timestamp))
                    tracker.ReanchorLocalCamera(fallback, sourceLuma,
                        frame.Width, frame.Height, lines, timestamp, exclusions,
                        allowsProvisionalPromotion: selectedCoarse || bridged is { Confidence: >= MinimumRecoveryConfidence });
            }
            floorless.RecordPublishedPose(timestamp, Position);
        }
        var position = camera.Position;
        var bootstrapReady = bootstrap.AllowsFirstAtlasWrite(
            new AtlasPoint(position.X, -position.Y),
            registrationAccepted: verified || bridged is not null,
            groundVerified: verified,
            hasCommittedEvidence: hasCommittedEvidence);
        return new(BuildSample(frame), update, verified,
            (verified || bridged is not null) && bootstrapReady && placement.AllowsAtlasWrite,
            tracking.LocalTextureSupport, tracking.LocalTextureError,
            verified ? "ground" : bridged is not null ? "transition-bridge"
                : selectedPlace ? "coarse-place-match"
                : selectedScene ? "calibrated-scene"
                : selectedCoarse ? "coarse-presentation"
                : firstFrame ? "ground-seed"
                : tracking.PoseVerified ? "ground-observing"
                : tracking.ProvisionalContinuityActive ? "ground-provisional"
                : update is null ? "held" : "coarse-presentation")
        {
            GroundTracking = tracking,
            FloorlessEvidence = floorlessEvidence,
            RecoveredAtlasReferenceTimestamp = bridged?.ReferenceTimestamp,
            // Ground validation can pause writes while independent registration
            // continues to carry the existing coordinate basis on each capture.
            RetainsWorldBasisDuringPlacementCheck = !placement.AllowsAtlasWrite
                && bridged is { Confidence: >= MinimumRecoveryConfidence }
                && captureInterval is > 0 and <= CaptureGroundReliability.MaximumAge,
            SceneMotionMilliseconds = sceneMotionMilliseconds,
            MotionBridgeConfidence = selectedScene
                ? 1 / (1 + (scene!.HorizontalMeasured ? Math.Max(scene.ResidualX, scene.ResidualY) : scene.ResidualY)
                    / (frame.Width / 640d))
                : bridged?.Confidence
        };
    }

    internal static FloorlessPoseCandidate? SelectFloorlessPose(AtlasPoint current,
        CalibratedSceneMotionEvidence? scene, TransitionMotionBridgeResult? masked, AtlasPoint? coarse,
        VisualRoomDirection? direction)
    {
        if (scene is { VerticalMeasured: true })
        {
            var horizontal = scene.HorizontalMeasured ? scene.Position.X : (masked?.CameraPosition ?? coarse ?? current).X;
            return new(new AtlasPoint(horizontal, scene.Position.Y), FloorlessPoseCandidateSource.CalibratedScene);
        }
        return masked is not null
            ? new FloorlessPoseCandidate(masked.CameraPosition, FloorlessPoseCandidateSource.MaskedRegistration)
            : FloorlessPoseCandidateSelector.Select(current, coarse, null, direction, coarseIsPlaceMatch: false);
    }

    private CameraUpdate ApplyPose(AtlasPoint pose)
    {
        // Tracker/telemetry use world Y-up; CameraAccumulator uses image Y-down.
        var target = new AtlasPoint(pose.X, -pose.Y);
        var step = new CameraVector(target.X - camera.Position.X, target.Y - camera.Position.Y);
        camera.ApplyGlobalCorrection(target);
        camera.ResetMotion();
        return new(CameraUpdateState.Accepted, step, step, camera.Position);
    }

    /// <summary>
    /// Invalidates fresh-atlas stability across a fade while retaining the trusted
    /// texture references needed to verify the returning view.
    /// </summary>
    public void PauseGroundVerification()
    {
        bootstrap.Reset();
        camera.ResetMotion();
        transition.SuspendForTransition();
        placement.MissingObservation();
        ResetFloorlessMotion();
    }

    private CameraUpdate? ApplyCoarseMotion(
        BgraFrame frame,
        LowResolutionTranslationDiagnostic? diagnostic)
    {
        if (diagnostic?.Motion is not { } motion) return null;
        var pixelsPerCellX = (double)frame.Width / 64;
        var pixelsPerCellY = (double)frame.Height / 36;
        return camera.Ingest(
            -motion.ScreenShiftX * pixelsPerCellX,
            -motion.ScreenShiftY * pixelsPerCellY,
            (float)motion.Confidence,
            frame.Width);
    }

    private double Elapsed(BgraFrame frame) =>
        Math.Max(0, (frame.ObservedAt - (startedAt ??= frame.ObservedAt)).TotalSeconds);

    private ReceiverGroundTruthSample BuildSample(BgraFrame frame, double? elapsed = null)
    {
        var position = camera.Position;
        return new ReceiverGroundTruthSample(
            ReceiverProtocol.ControlVersion,
            "visualTracking",
            Guid.Empty,
            ++sequence,
            frame.CaptureId,
            elapsed ?? Elapsed(frame),
            "visual-gameplay",
            false,
            0, 0, 0,
            0, 0,
            true,
            false,
            true,
            position.X,
            -position.Y,
            0,
            position.X,
            -position.Y,
            0,
            frame.Height / 2.0,
            1,
            1,
            null,
            null,
            frame.Width,
            frame.Height,
            frame.Width,
            frame.Height);
    }

    public void Reset()
    {
        camera.Reset();
        if (defaultGroundTracker.IsValueCreated) defaultGroundTracker.Value.Reset();
        LastPatchSearchExecution = null;
        bootstrap.Reset();
        transition.Reset();
        ResetFloorlessMotion();
        placement.Reset();
        previousTimestamp = null;
        sequence = 0;
        startedAt = null;
    }

    internal void ResetFloorlessMotion()
    {
        floorless.Reset();
        calibratedScene.Reset();
        measuredGround = null;
        groundVelocity = null;
    }
}
