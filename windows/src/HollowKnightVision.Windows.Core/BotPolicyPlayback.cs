namespace HollowKnightVision.Windows.Core;

public readonly record struct BotPlaybackDecision(BotPolicyAction Action, double Deadline);

public sealed record BotPlaybackUpdate(string Status, BotPlaybackDecision? Decision = null)
{
    public const string WaitingForFrame = "Bot waiting for a fresh frame";
    internal const string WaitingForObservation = "Bot waiting for a fresh observation";

    public string StatusAt(double now) => Decision is { Deadline: var deadline } && now >= deadline
        ? WaitingForObservation : Status;
}

/// <summary>Runs the selected model against fresh evidence and bounds each action to that evidence.</summary>
public sealed class BotPolicyPlayback
{
    private readonly BotObservationHistory history;
    private readonly BotCaptureDomain domain;
    private readonly bool supportsMaskedKnight;
    private readonly Func<float[], BotPolicyAction> decide;
    private double lastAttempt = double.NegativeInfinity;
    private (long Generation, double Timestamp)? newestCapture;

    public BotPolicyPlayback(BotNativePolicy policy, BotCaptureDomain domain)
        : this(domain, policy.Version, policy.SupportsMaskedKnight, input => BotNativePolicy.Action(policy.Logits(input))) { }

    public BotPolicyPlayback(BotStarterPolicy starter, BotCaptureDomain domain)
        : this(domain, starter.ObservationSchema, supportsMaskedKnight: false, input => starter.Decide(input)) { }

    private BotPolicyPlayback(BotCaptureDomain domain, int schema, bool supportsMaskedKnight, Func<float[], BotPolicyAction> decide)
    {
        this.domain = domain;
        this.supportsMaskedKnight = supportsMaskedKnight;
        this.decide = decide;
        history = new(schema);
    }

    public bool WantsObservation(double capturedAt)
    {
        // Leave scheduling headroom for 10 Hz sampling on a frame clock with jitter.
        if (!double.IsFinite(capturedAt) || capturedAt - lastAttempt < BotRecordingContract.SampleInterval - .012) return false;
        lastAttempt = capturedAt;
        return true;
    }

    public void ResetHistory() => history.Reset();

    public BotPlaybackDecision? Decide(BotCaptureObservation row, InputButtons previous, double now) =>
        Evaluate(row, previous, now)?.Decision;

    // An absent update leaves the active action and its deadline alone.
    public BotPlaybackUpdate? Evaluate(BotCaptureObservation? row, InputButtons? previous, double now)
    {
        if (row is null)
            return WaitFor(domain == BotCaptureDomain.Hacker
                ? "Bot waiting for mod actor telemetry" : BotPlaybackUpdate.WaitingForFrame);
        var expectedTerrain = domain == BotCaptureDomain.Hacker ? "human-authored" : "detected";
        // The paired camera can publish first. Leave this policy's history and input deadline untouched.
        if (row.TerrainSource is "human-authored" or "detected" && row.TerrainSource != expectedTerrain)
            return null;
        if (!double.IsFinite(row.CaptureTimestamp) || row.CaptureTimestamp > now)
            return WaitFor("Bot waiting for a fresh capture");
        if (newestCapture is { } latest && row.Generation == latest.Generation && row.CaptureTimestamp <= latest.Timestamp)
            return null;
        newestCapture = (row.Generation, row.CaptureTimestamp);
        if (!previous.HasValue) return WaitFor("Bot waiting for current game controls");
        if (row.TerrainSource != expectedTerrain)
            return WaitFor(domain == BotCaptureDomain.Hacker
                ? "Bot waiting for authored terrain" : "Bot waiting for detected terrain");

        var features = history.Append(row, BotPolicyAction.FromButtons(previous.Value), now,
            allowMaskedKnight: domain == BotCaptureDomain.Gameplay && supportsMaskedKnight);
        if (features is null) return WaitFor(Reason(row, now));
        var deadline = Math.Min(now + .18,
            Math.Min(row.KnightPredictionDeadline,
                Math.Min(row.CapturedAt + BotRecordingContract.MaximumCaptureAge, row.AvailableAt + .25)));
        if (now >= deadline) return WaitFor(Reason(row, now));
        var action = decide(features);
        var tracking = row.KnightPrediction is not null ? " · estimating Knight"
            : row.HasMaskedKnightEvidence ? " · Knight occluded" : "";
        return new($"Bot · {action.DisplayName}{tracking}", new(action, deadline));
    }

    private BotPlaybackUpdate WaitFor(string status)
    {
        history.Reset();
        return new(status);
    }

    private static string Reason(BotCaptureObservation row, double now)
    {
        var issues = row.Issues;
        if (issues.Contains("stale-capture") || now - row.CapturedAt is not (>= 0 and <= BotRecordingContract.MaximumCaptureAge))
            return "Bot waiting for a fresh capture";
        if (issues.Contains("stale-or-missing-detection")) return "Bot waiting for fresh object detection";
        if (issues.Contains("missing-hacker-actor-telemetry") || issues.Contains("invalid-hacker-actors"))
            return "Bot waiting for mod actor telemetry";
        if (issues.Contains("missing-hacker-knight")) return "Bot waiting for the mod Knight position";
        if (issues.Contains("missing-hacker-enemies")) return "Bot waiting for mod enemy positions";
        if (issues.Contains("missing-knight") || now >= row.KnightPredictionDeadline) return "Bot waiting for Knight detection";
        if (issues.Contains("missing-terrain-analysis")) return "Bot waiting for ground lines";
        if (issues.Contains("room-transition")) return "Bot waiting for the room transition";
        return BotPlaybackUpdate.WaitingForObservation;
    }
}
