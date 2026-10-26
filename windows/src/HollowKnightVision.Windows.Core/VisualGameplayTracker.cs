namespace HollowKnightVision.Windows.Core;

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
}

/// <summary>
/// Converts image translation into the camera-shaped sample consumed by the tiled
/// atlas. Ground texture is the only atlas-writing pose source. Coarse room motion
/// may keep presentation moving, but never establishes world location by itself.
/// </summary>
public sealed class VisualGameplayTracker
{
    private readonly CameraAccumulator camera = new()
    {
        Smoothing = 0.65,
        MinimumConfidence = 0.10f
    };
    private readonly Lazy<GroundHypothesisTracker> defaultGroundTracker = new(() => new(
        minimumObservationSeconds: .2, backgroundGlobalSearch: true,
        confirmedLossGraceSeconds: 3, productionCameraSolver: true));
    public GroundPatchSearchExecution? LastPatchSearchExecution { get; private set; }
    public AtlasPoint Position => new(camera.Position.X, -camera.Position.Y);
    private readonly FreshAtlasBootstrapGate bootstrap = new();
    private readonly TransitionMotionBridge transition = new();
    private readonly GroundPlacementRecovery placement = new();
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
        double? captureTimestamp = null)
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
        previousTimestamp = timestamp;
        var verified = tracking.PoseVerified && tracking.HasConfirmedGround
            && tracking.CameraPosition is not null;
        CameraUpdate? update;
        TransitionMotionBridgeResult? bridged = null;
        if (verified)
        {
            update = ApplyPose(tracking.CameraPosition!.Value);
            transition.Anchor(tracking.CameraPosition.Value, movingForeground);
        }
        else
        {
            var resumedAfterTransition = transition.AwaitingSceneResume;
            bridged = transition.Track(diagnostic, frame.Width, frame.Height, frame.Width,
                movingForeground: movingForeground);
            if (bridged is not null)
            {
                update = ApplyPose(bridged.CameraPosition);
                // Preserve independent provisional motion and pending placement
                // votes; repeatedly replacing them can prevent reacquisition.
                if (!tracking.PoseVerified && !tracking.ProvisionalContinuityActive
                    && !placement.RetainsCandidate(timestamp))
                    tracker.ReanchorLocalCamera(bridged.CameraPosition, sourceLuma,
                        frame.Width, frame.Height, lines, timestamp, exclusions,
                        allowsProvisionalPromotion: bridged.Confidence >= .8f);
            }
            else update = resumedAfterTransition ? null : ApplyCoarseMotion(frame, diagnostic);
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
                : firstFrame ? "ground-seed"
                : tracking.PoseVerified ? "ground-observing"
                : tracking.ProvisionalContinuityActive ? "ground-provisional"
                : update is null ? "held" : "coarse-presentation")
        { GroundTracking = tracking };
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
        placement.Reset();
        previousTimestamp = null;
        sequence = 0;
        startedAt = null;
    }
}
