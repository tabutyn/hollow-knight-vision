namespace HollowKnightVision.Windows.Core;

public readonly record struct BotPlaybackDecision(BotPolicyAction Action, double Deadline);

/// <summary>Runs the selected model against fresh evidence and bounds each action to that evidence.</summary>
public sealed class BotPolicyPlayback(BotNativePolicy policy, BotCaptureDomain domain)
{
    private readonly BotObservationHistory history = new(policy.Version);
    private double lastAttempt = double.NegativeInfinity;

    public bool WantsObservation(double capturedAt)
    {
        // Leave scheduling headroom for 10 Hz sampling on a frame clock with jitter.
        if (!double.IsFinite(capturedAt) || capturedAt - lastAttempt < BotRecordingContract.SampleInterval - .012) return false;
        lastAttempt = capturedAt;
        return true;
    }

    public void ResetHistory() => history.Reset();

    public BotPlaybackDecision? Decide(BotCaptureObservation row, InputButtons previous, double now)
    {
        if (row.TerrainSource != (domain == BotCaptureDomain.Hacker ? "human-authored" : "detected"))
        {
            history.Reset();
            return null;
        }
        var features = history.Append(row, BotPolicyAction.FromButtons(previous), now,
            allowMaskedKnight: domain == BotCaptureDomain.Gameplay && policy.SupportsMaskedKnight);
        if (features is null) return null;
        var action = BotNativePolicy.Action(policy.Logits(features));
        var deadline = Math.Min(now + .18,
            Math.Min(row.CapturedAt + BotRecordingContract.MaximumCaptureAge, row.AvailableAt + .25));
        return now < deadline ? new(action, deadline) : null;
    }
}
