namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Adds transition edge mattes to the top-origin exclusions shared by the
/// durable world pipeline and the direct atlas compositor.
/// </summary>
public static class LiveAtlasTransitionMaskPolicy
{
    public static IReadOnlyList<PixelRect> Merge(
        IReadOnlyList<PixelRect> existingExclusions,
        int frameWidth,
        int frameHeight,
        FrameSignalProfile signal,
        bool transitionBridgeActive)
    {
        ArgumentNullException.ThrowIfNull(existingExclusions);
        ArgumentNullException.ThrowIfNull(signal);

        var merged = new List<PixelRect>(existingExclusions.Count + 2);
        var seen = new HashSet<PixelRect>();
        foreach (var exclusion in existingExclusions)
        {
            if (seen.Add(exclusion)) merged.Add(exclusion);
        }

        if (!transitionBridgeActive
            || frameWidth <= 0
            || frameHeight <= 0
            || signal.EdgeBlackBands is not { } transitionBands)
        {
            return merged;
        }

        var omissions = transitionBands.RectangularOmissions(frameWidth, frameHeight);
        if (omissions.Count == 0) return merged;

        foreach (var exclusion in SceneRegions.PixelRectangles(
                     frameWidth,
                     frameHeight,
                     omissions))
        {
            if (seen.Add(exclusion)) merged.Add(exclusion);
        }
        return merged;
    }
}
