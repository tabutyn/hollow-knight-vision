namespace HollowKnightVision.Windows.Core;

/// <summary>Retains the placed basis while Gym crosses a floorless view with measured image motion.</summary>
internal sealed class BotGymAtlasMotion
{
    // Gym supplies one image per six simulation ticks. A single skipped image
    // therefore needs up to twice the ordinary capture interval for a match.
    private readonly AtlasCaptureGap gap = new(2 * CaptureGroundReliability.MaximumAge);
    private bool usedMaskedMotion;

    internal TransitionMotionBridgeResult? MatchSkipped(BgraFrame frame, GameplaySceneMasks masks,
        long generation, string room, double time) =>
        gap.Match(frame, masks.GroundRegions.OmittedRects, masks.GroundKnight, generation, room, time);

    internal (bool RetainBasis, bool RequiresRecovery) Observe(BgraFrame frame, GameplaySceneMasks masks,
        long generation, string room, double time, AtlasPoint pose, VisualGameplayObservation observed,
        bool hasBasis, bool measuredGround, bool measuredScenery, bool corrected)
    {
        if (!hasBasis || corrected) Reset();
        var exclusions = masks.GroundRegions.OmittedRects;
        var floorless = !observed.GroundVerified;
        var skipped = observed.RecoveredAtlasReferenceTimestamp.HasValue;
        var mask = hasBasis && floorless
            && observed.FloorlessEvidence?.SelectedSource == FloorlessPoseCandidateSource.MaskedRegistration
            && observed.MotionBridgeConfidence is >= VisualGameplayTracker.MinimumRecoveryConfidence
            && (skipped || gap.HasImmediateReference(generation, room, time));
        var groundMatches = measuredGround && gap.Match(frame, exclusions, masks.GroundKnight, generation, room, time,
            requireMiss: !usedMaskedMotion) is { } measured
            && CameraMath.Length(measured.CameraPosition.X - pose.X, measured.CameraPosition.Y - pose.Y) <= 4 * frame.Width / 640d;
        var mustRecover = usedMaskedMotion && observed.GroundVerified && !groundMatches;
        var state = gap.Complete(frame, exclusions, masks.GroundKnight, generation, room, time, pose,
            currentReferenceAccepted: measuredGround || mask || measuredScenery,
            continuedAcrossGap: (mask && skipped) || measuredScenery || groundMatches,
            atlasFrameHeld: hasBasis && floorless && !mask && !measuredScenery);
        if (mustRecover || state == AtlasCaptureGapState.RequiresRecovery)
        {
            Reset();
            return (false, true);
        }

        usedMaskedMotion = floorless && (mask || usedMaskedMotion);
        return (hasBasis && (mask || measuredScenery || state == AtlasCaptureGapState.AwaitingRegistration), false);
    }

    internal void Reset()
    {
        usedMaskedMotion = false;
        gap.Reset();
    }
}
