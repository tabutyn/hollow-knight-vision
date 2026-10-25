namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Capture owns the displayed pixels; ordered analysis owns their world placement.
/// Analysis need not catch up with capture to advance the live view's position.
/// This presentation state never supplies observations to the persistent atlas.
/// Owned by the UI thread and reset at every capture epoch.
/// </summary>
public sealed class GameplayPresentationState
{
    private long lastAnalysisCaptureId = -1;
    public BgraFrame? LatestFrame { get; private set; }

    public void Capture(BgraFrame frame)
    {
        if (LatestFrame is null || frame.CaptureId > LatestFrame.CaptureId)
            LatestFrame = frame;
    }

    public bool TryAcceptAnalysis(BgraFrame analyzedFrame)
    {
        if (LatestFrame is not { } latest
            || analyzedFrame.CaptureId <= lastAnalysisCaptureId
            || analyzedFrame.CaptureId > latest.CaptureId
            || analyzedFrame.Width != latest.Width || analyzedFrame.Height != latest.Height)
            return false;
        lastAnalysisCaptureId = analyzedFrame.CaptureId;
        return true;
    }

    public void Reset()
    {
        LatestFrame = null;
        lastAnalysisCaptureId = -1;
    }
}
