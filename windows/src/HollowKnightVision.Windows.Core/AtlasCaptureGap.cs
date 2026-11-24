namespace HollowKnightVision.Windows.Core;

internal enum AtlasCaptureGapState
{
    None,
    AwaitingRegistration,
    RequiresRecovery
}

/// <summary>Checks a capture against the last accepted image, allowing one missed registration.</summary>
internal sealed class AtlasCaptureGap(double maximumReferenceAge = CaptureGroundReliability.MaximumAge)
{
    private sealed record Reference(BgraFrame Frame, IReadOnlyList<StencilRect> Exclusions,
        PixelRect? Foreground, long Generation, string Room, double Timestamp, AtlasPoint Pose);

    private Reference? lastAccepted;
    private bool missedCapture;

    internal TransitionMotionBridgeResult? Match(BgraFrame frame, IReadOnlyList<StencilRect> exclusions,
        PixelRect? foreground, long generation, string room, double timestamp, bool requireMiss = true)
    {
        if ((requireMiss && !missedCapture) || !Eligible(generation, room, timestamp) || lastAccepted is not { } reference)
            return null;
        var registration = MaskedFrameMotionTracker.Match(reference.Frame, reference.Exclusions, frame, exclusions);
        if (registration?.ExactIdentity != true
            && registration?.Motion is not { Confidence: >= VisualGameplayTracker.MinimumRecoveryConfidence })
            return null;
        var bridge = new TransitionMotionBridge();
        bridge.Anchor(reference.Pose, reference.Foreground);
        var measured = bridge.Track(registration, frame.Width, frame.Height, frame.Width, movingForeground: foreground);
        return measured is null ? null : measured with { ReferenceTimestamp = reference.Timestamp };
    }

    internal bool HasImmediateReference(long generation, string room, double timestamp) =>
        !missedCapture && lastAccepted is { } reference && Eligible(generation, room, timestamp)
        && timestamp - reference.Timestamp <= CaptureGroundReliability.MaximumAge;

    internal AtlasCaptureGapState Complete(BgraFrame frame, IReadOnlyList<StencilRect> exclusions,
        PixelRect? foreground, long generation, string room, double timestamp, AtlasPoint pose,
        bool currentReferenceAccepted, bool continuedAcrossGap, bool atlasFrameHeld)
    {
        var waiting = missedCapture;
        if (!waiting && atlasFrameHeld && !currentReferenceAccepted && Eligible(generation, room, timestamp))
        {
            missedCapture = true;
            return AtlasCaptureGapState.AwaitingRegistration;
        }

        Reset();
        if (currentReferenceAccepted)
            lastAccepted = new(frame, exclusions, foreground, generation, room, timestamp, pose);
        return waiting && !continuedAcrossGap ? AtlasCaptureGapState.RequiresRecovery : AtlasCaptureGapState.None;
    }

    internal void Reset()
    {
        lastAccepted = null;
        missedCapture = false;
    }

    private bool Eligible(long generation, string room, double timestamp) =>
        lastAccepted is { } reference && reference.Generation == generation && reference.Room == room
        && timestamp - reference.Timestamp is > 0 && timestamp - reference.Timestamp <= maximumReferenceAge;
}
