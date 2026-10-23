namespace HollowKnightVision.Windows.Core;

public readonly record struct FrameFractionRange(double Lower, double Upper);
public sealed record FrameEdgeBlackBand(double WidthFraction, FrameFractionRange ContentYRangeFraction);

public sealed record FrameEdgeBlackBands(FrameEdgeBlackBand? Left, FrameEdgeBlackBand? Right)
{
    public FrameEdgeBlackBand? Band(VisualRoomDirection direction) => direction == VisualRoomDirection.Left ? Left : Right;

    // Loading mattes become straight masks, not holes around each black pixel.
    // Callers must first establish a transition; ordinary dark scenery is not a matte.
    public IReadOnlyList<StencilRect> RectangularOmissions(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return [];
        var padding = Math.Max(1, Math.Ceiling(width / 640));
        var leftWidth = Left is null ? 0 : Math.Clamp(Left.WidthFraction * width + padding, 0, width);
        var rightWidth = Right is null ? 0 : Math.Clamp(Right.WidthFraction * width + padding, 0, width);
        if (!double.IsFinite(leftWidth) || !double.IsFinite(rightWidth) || leftWidth + rightWidth > width * .90) return [];
        var rectangles = new List<StencilRect>();
        if (leftWidth > 0) rectangles.Add(new(0, 0, leftWidth, height));
        if (rightWidth > 0) rectangles.Add(new(width - rightWidth, 0, rightWidth, height));
        return rectangles;
    }
}

public sealed record FrameSignalProfile(int SampleCount, int SignaledCount, int VisibleCount, double MeanPeak,
    LowResolutionMotionGrid? MotionGrid = null, FrameEdgeBlackBands? EdgeBlackBands = null)
{
    public double SignaledFraction => SampleCount > 0 ? (double)SignaledCount / SampleCount : 0;
    public double VisibleFraction => SampleCount > 0 ? (double)VisibleCount / SampleCount : 0;
    public bool HasGameplaySignal => SignaledCount >= Math.Max(8, SampleCount / 20);
}

public readonly record struct TransitionFrameDecision(bool AdmitsTracking, bool ResumedAfterTransition);

/// <summary>Mac's image-only gate: normal darkening remains trackable; true blackouts require settling.</summary>
public sealed class TransitionFrameGate
{
    public const int ResumeStableFrames = 3;
    private double? previousMean;
    private bool suspended;
    private int stableFrames;

    public void Reset() { previousMean = null; suspended = false; stableFrames = 0; }
    public TransitionFrameDecision Observe(FrameSignalProfile profile)
    {
        if (!profile.HasGameplaySignal)
        {
            suspended = true; stableFrames = 0; previousMean = profile.MeanPeak;
            return new(false, false);
        }
        if (suspended)
        {
            var stable = previousMean is { } mean
                && Math.Abs(profile.MeanPeak - mean) <= Math.Max(2.5, Math.Max(profile.MeanPeak, mean) * .20);
            stableFrames = stable ? stableFrames + 1 : 0;
            previousMean = profile.MeanPeak;
            if (stableFrames < ResumeStableFrames) return new(false, false);
            suspended = false; stableFrames = 0;
            return new(true, true);
        }
        previousMean = profile.MeanPeak;
        return new(true, false);
    }
}

public readonly record struct GameplayFrameDecision(bool AdmitsTracking, bool AllowsAtlasWrite,
    bool ResetMotionReference, TransitionFrameDecision Transition);

/// <summary>Capture-worker-owned admission. Presentation status never substitutes for current pixels.</summary>
public sealed class GameplayFrameAdmission
{
    private readonly TransitionFrameGate transition = new();
    private long? generation;
    public GameplayFrameDecision Observe(FrameSignalProfile profile, bool hasFreshGameplayEvidence, long captureGeneration)
    {
        var changed = generation != captureGeneration;
        if (changed) { transition.Reset(); generation = captureGeneration; }
        var decision = transition.Observe(profile);
        var tracks = hasFreshGameplayEvidence && decision.AdmitsTracking;
        // No visual measurement spans a blackout. Its first settled frame is
        // reference-only, not permission to place new room pixels at the old pose.
        return new(tracks, tracks && !decision.ResumedAfterTransition,
            changed || !tracks || decision.ResumedAfterTransition, decision);
    }
}
