namespace HollowKnightVision.Windows.Core;

public sealed record VisualGameplayObservation(
    ReceiverGroundTruthSample Telemetry,
    CameraUpdate? Registration,
    bool GroundVerified,
    bool AllowsAtlasWrite,
    int GroundSupport,
    double? GroundError,
    string Source);

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
    private readonly GroundCameraSolver ground = new(
        keyframeSubpixelEnabled: true,
        calibratedMotionEnabled: true);
    private readonly FreshAtlasBootstrapGate bootstrap = new();
    private readonly TransitionMotionBridge transition = new();
    private readonly GroundPlacementRecovery placement = new();
    private readonly GroundLinePresence linePresence = new(
        minimumObservationSeconds: 0.2,
        confirmedLossGraceSeconds: 3);
    private bool hasEstablishedGround;
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
        PixelRect? movingForeground = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(sourceLuma);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(exclusions);
        var timestamp = Elapsed(frame);
        var textureLines = lines.Select(GroundTextureLine.FromDetected).ToArray();
        var protectedPixels = exclusions.Select(rect =>
            new StencilRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray();
        var solution = ground.Solve(sourceLuma, frame.Width, frame.Height,
            textureLines, timestamp, protectedPixels);
        var textureVerified = solution is not null && !ground.Seeded && solution.ReferenceTrusted;
        var placementAccepted = false;
        var presenceCamera = new AtlasPoint(camera.Position.X, -camera.Position.Y);
        if (textureVerified)
        {
            var candidate = new AtlasPoint(-ground.ImagePosition.X, ground.ImagePosition.Y);
            var current = new AtlasPoint(camera.Position.X, -camera.Position.Y);
            placementAccepted = placement.Accepts(new GroundPlacementProposal(
                    candidate, solution!.Support, solution.Error, 0, 0, false,
                    ground.DistinctFrame, hasEstablishedGround),
                current, frame.Width, timestamp,
                previousTimestamp is { } previous ? timestamp - previous : null);
            presenceCamera = candidate;
            if (!placementAccepted) ground.DeferReferenceUpdates(timestamp);
        }
        else placement.MissingObservation();
        linePresence.Update(
            placementAccepted
                ? lines.Select(line => line.ToSemanticLine()).ToArray()
                : [],
            presenceCamera,
            frame.Width,
            frame.Height,
            timestamp,
            placementAccepted,
            exclusions);
        var verified = placementAccepted
            && linePresence.Reviews(presenceCamera, frame.Width, frame.Height)
                .Any(review => review.State == GroundLinePresenceState.Confirmed);
        if (verified) hasEstablishedGround = true;
        previousTimestamp = timestamp;
        CameraUpdate? update;
        TransitionMotionBridgeResult? bridged = null;
        if (verified)
        {
            // Mac image coordinates are top-origin: camera.x -= image.dx and
            // camera.y += image.dy. CameraAccumulator stores both image axes,
            // while telemetry negates its Y below.
            var target = new AtlasPoint(-ground.ImagePosition.X, -ground.ImagePosition.Y);
            var step = new CameraVector(target.X - camera.Position.X, target.Y - camera.Position.Y);
            camera.ApplyGlobalCorrection(target);
            camera.ResetMotion();
            update = new CameraUpdate(CameraUpdateState.Accepted, step, step, camera.Position);
            transition.Anchor(new AtlasPoint(camera.Position.X, -camera.Position.Y), movingForeground);
        }
        else
        {
            var resumedAfterTransition = transition.AwaitingSceneResume;
            bridged = transition.Track(diagnostic, frame.Width, frame.Height, frame.Width,
                movingForeground: movingForeground);
            if (bridged is not null)
            {
                var target = new AtlasPoint(bridged.CameraPosition.X, -bridged.CameraPosition.Y);
                camera.ApplyGlobalCorrection(target);
                camera.ResetMotion();
                update = new CameraUpdate(CameraUpdateState.Accepted,
                    bridged.CameraStep, bridged.CameraStep, camera.Position);
                ground.FollowMeasuredCamera(bridged.CameraStep, sourceLuma,
                    frame.Width, frame.Height, textureLines, timestamp, protectedPixels);
            }
            else update = resumedAfterTransition ? null : ApplyCoarseMotion(frame, diagnostic);
        }
        var position = camera.Position;
        var bootstrapReady = bootstrap.AllowsFirstAtlasWrite(
            new AtlasPoint(position.X, -position.Y),
            registrationAccepted: verified || bridged is not null,
            groundVerified: verified,
            hasCommittedEvidence: hasCommittedEvidence);
        return new(BuildSample(frame, timestamp), update, verified,
            (verified || bridged is not null) && bootstrapReady,
            solution?.Support ?? 0,
            solution?.Error,
            verified ? "ground" : bridged is not null ? "transition-bridge"
                : placementAccepted ? "ground-observing"
                : ground.Seeded ? "ground-seed"
                : solution is { ReferenceTrusted: false } ? "ground-provisional"
                : update is null ? "held" : "coarse-presentation");
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
        ground.Reset();
        bootstrap.Reset();
        transition.Reset();
        placement.Reset();
        linePresence.Reset();
        hasEstablishedGround = false;
        previousTimestamp = null;
        sequence = 0;
        startedAt = null;
    }
}
