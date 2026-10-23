namespace HollowKnightVision.Windows.Core;

public enum TransitionMotionSource
{
    MaskedRegistration
}

public sealed record TransitionMotionBridgeResult(
    AtlasPoint CameraPosition,
    CameraVector CameraStep,
    float Confidence,
    TransitionMotionSource Source);

/// <summary>
/// Mac's exceptional, union-masked odometry path for intervals where confirmed
/// floor texture is absent. The caller owns the two-frame registration so this
/// class can consume the same diagnostic already produced by live capture.
/// </summary>
public sealed class TransitionMotionBridge
{
    public const float MinimumConfidence = .2f;
    private readonly TransitionSmallMotionAccumulator smallHorizontalMotion = new();
    private AtlasPoint? position;
    private PixelRect? previousMovingForeground;
    private bool awaitingSceneResume;
    public bool IsAnchored => position is not null;
    public bool AwaitingSceneResume => awaitingSceneResume;

    public void Reset()
    {
        smallHorizontalMotion.Reset();
        position = null;
        previousMovingForeground = null;
        awaitingSceneResume = false;
    }

    public void Anchor(AtlasPoint cameraPosition, PixelRect? movingForeground = null)
    {
        if (!double.IsFinite(cameraPosition.X) || !double.IsFinite(cameraPosition.Y)) return;
        position = cameraPosition;
        previousMovingForeground = movingForeground;
        awaitingSceneResume = false;
        smallHorizontalMotion.Reset();
    }

    public void SuspendForTransition()
    {
        if (awaitingSceneResume) return;
        awaitingSceneResume = true;
        previousMovingForeground = null;
        smallHorizontalMotion.Reset();
    }

    public AtlasPoint? ResumeAfterTransition(PixelRect? movingForeground = null)
    {
        if (!awaitingSceneResume) return null;
        awaitingSceneResume = false;
        previousMovingForeground = movingForeground;
        smallHorizontalMotion.Reset();
        return position;
    }

    public TransitionMotionBridgeResult? Track(
        LowResolutionTranslationDiagnostic? diagnostic,
        int frameWidth,
        int frameHeight,
        double solveWidth,
        bool lockVertical = false,
        PixelRect? movingForeground = null)
    {
        if (awaitingSceneResume)
        {
            _ = ResumeAfterTransition(movingForeground);
            return null;
        }
        if (position is not { } current || frameWidth <= 0 || frameHeight <= 0
            || !double.IsFinite(solveWidth) || solveWidth <= 0)
        {
            previousMovingForeground = movingForeground;
            return null;
        }

        LowResolutionMotionVector? motion = diagnostic?.Motion;
        // Vision reports a high-confidence identity transform for a static
        // textured scene. Windows' estimator reports that as insignificant.
        if (motion is null
            && (diagnostic?.ExactIdentity == true
                || diagnostic?.FinalRejection == LowResolutionTranslationRejection.InsignificantSubcellMotion))
        {
            motion = new LowResolutionMotionVector(0, 0, 1);
        }
        var priorForeground = previousMovingForeground;
        previousMovingForeground = movingForeground;
        if (motion is null || motion.Confidence < MinimumConfidence) return null;

        var scaleX = solveWidth / 64.0;
        var scaleY = solveWidth / frameWidth * frameHeight / 36.0;
        var measured = new CameraVector(
            -motion.ScreenShiftX * scaleX,
            lockVertical ? 0 : motion.ScreenShiftY * scaleY);
        var step = TransitionForegroundResidualGate.Adjusted(
            measured, priorForeground, movingForeground, frameWidth, solveWidth);
        step = step with { X = smallHorizontalMotion.Ingest(step.X) };
        if (!Plausible(step, solveWidth)) return null;
        var next = new AtlasPoint(current.X + step.X, current.Y + step.Y);
        position = next;
        return new(next, step, (float)motion.Confidence,
            TransitionMotionSource.MaskedRegistration);
    }

    private static bool Plausible(CameraVector step, double solveWidth) =>
        double.IsFinite(step.X) && double.IsFinite(step.Y)
        && Math.Abs(step.X) <= Math.Max(8, solveWidth * .08)
        && Math.Abs(step.Y) <= Math.Max(6, solveWidth * .05);
}

public sealed class TransitionSmallMotionAccumulator
{
    private double pending;
    private int idleFrames;

    public void Reset()
    {
        pending = 0;
        idleFrames = 0;
    }

    public double Ingest(double motion)
    {
        if (!double.IsFinite(motion))
        {
            Reset();
            return motion;
        }
        if (Math.Abs(motion) > 3.25)
        {
            Reset();
            return motion;
        }
        if (Math.Abs(motion) < .25)
        {
            idleFrames++;
            if (idleFrames >= 3) Reset();
            return 0;
        }
        idleFrames = 0;
        if (pending != 0 && (pending < 0) != (motion < 0)) pending = 0;
        pending += motion;
        if (Math.Abs(pending) < 4) return 0;
        var accepted = pending;
        Reset();
        return accepted;
    }
}

public static class TransitionForegroundResidualGate
{
    public static CameraVector Adjusted(
        CameraVector step,
        PixelRect? previousForeground,
        PixelRect? currentForeground,
        double frameWidth,
        double solveWidth)
    {
        if (previousForeground is not { Width: > 0, Height: > 0 } previous
            || currentForeground is not { Width: > 0, Height: > 0 } current
            || !double.IsFinite(frameWidth) || frameWidth <= 0
            || !double.IsFinite(solveWidth) || solveWidth <= 0) return step;
        var previousMidX = previous.X + previous.Width / 2.0;
        var currentMidX = current.X + current.Width / 2.0;
        var foregroundMotion = Math.Abs(currentMidX - previousMidX) * solveWidth / frameWidth;
        return foregroundMotion >= 1.25 && Math.Abs(step.X) <= 3.25
            ? step with { X = 0 }
            : step;
    }
}

public static class GroundPoseContinuityGate
{
    public const int MinimumGlobalMatchSupport = 4;

    public static bool Accepts(
        AtlasPoint candidate,
        AtlasPoint current,
        double solveWidth,
        int globalMatchCount,
        bool hasGlobalCorrection,
        int localInlierCount = 0,
        int localTextureSupport = 0,
        double? localTextureError = null,
        double? captureElapsed = null)
    {
        if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y)
            || !double.IsFinite(current.X) || !double.IsFinite(current.Y)
            || !double.IsFinite(solveWidth) || solveWidth <= 0) return false;
        if (hasGlobalCorrection && globalMatchCount >= MinimumGlobalMatchSupport) return true;
        var deltaX = Math.Abs(candidate.X - current.X);
        var deltaY = Math.Abs(candidate.Y - current.Y);
        var maximumHorizontalStep = Math.Max(8, Math.Min(16, solveWidth * .025));
        var maximumVerticalStep = Math.Max(6, Math.Min(12, solveWidth * .019));
        if (deltaX <= maximumHorizontalStep && deltaY <= maximumVerticalStep) return true;
        if (captureElapsed is { } elapsed && double.IsFinite(elapsed)
            && elapsed is >= .08 and <= .25
            && localTextureSupport >= 8
            && localTextureError is { } elapsedError && double.IsFinite(elapsedError) && elapsedError <= 18)
        {
            var scale = solveWidth / 640;
            if (deltaX <= Math.Min(96, 16 * elapsed * 60) * scale
                && deltaY <= Math.Min(96, 12 * elapsed * 60) * scale) return true;
        }
        var supportedNewGround = localTextureSupport >= 6
            && localTextureError is { } error && double.IsFinite(error) && error <= 18;
        if (localInlierCount < MinimumGlobalMatchSupport && !supportedNewGround) return false;
        return deltaX <= Math.Max(32, Math.Min(64, solveWidth * .10))
            && deltaY <= Math.Max(16, Math.Min(32, solveWidth * .05));
    }
}
