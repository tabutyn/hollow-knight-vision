namespace HollowKnightVision.Windows.Core;

/// <summary>Applies a checked ability model to causal observations from one bound maneuver.</summary>
public sealed class ThinkingAbilityDrive(BotLearnedSkill skill, BotNativePolicy? motor, ThinkingCandidate candidate)
{
    private readonly BotObservationHistory history = new(skill.ObservationSchema);
    private double lastSample = double.NegativeInfinity;
    private double validUntil;
    private InputButtons ability;
    private InputButtons? trainedMovement;

    public InputButtons Apply(ThinkingMotion motion, ThinkingSight sight, BotCaptureObservation? row,
        InputButtons? previous, double now, bool skillControlsAvailable)
    {
        if (!skillControlsAvailable || previous is null || row is null
            || row.Generation != sight.Scope.Generation || row.CaptureTimestamp != sight.CapturedAt
            || row.TerrainSource != (sight.Scope.Domain == BotCaptureDomain.Hacker ? "human-authored" : "detected")
            || !BotObservationHistory.IsFresh(row, now) || row.Issues.Count != 0)
        {
            history.Reset();
            lastSample = double.NegativeInfinity;
            validUntil = 0;
            return motion.Buttons;
        }
        if (now >= validUntil || row.CapturedAt - lastSample >= BotRecordingContract.SampleInterval - .012)
        {
            lastSample = row.CapturedAt;
            if (candidate.Route is { } bound && sight.Scene?.Routes.FirstOrDefault(route => route.Matches(bound)) is { } current)
                row = row with { RoadHint = current.Road, HackerPathVersion = 1 };
            var features = history.Append(row, BotPolicyAction.FromButtons(previous.Value), now);
            if (features is null)
            {
                validUntil = 0;
                return motion.Buttons;
            }
            ability = skill.Predict(features);
            // Combat keeps its bound actor approach. A trained traversal motor sees the exact selected route.
            trainedMovement = motor is not null && candidate.Action == ThinkingAction.Traverse
                ? BotNativePolicy.Action(motor.Logits(features)).Buttons : null;
            validUntil = Math.Min(now + .14, Math.Min(row.CapturedAt + BotRecordingContract.MaximumCaptureAge,
                Math.Min(row.AvailableAt + .25, row.KnightPredictionDeadline)));
        }
        var movement = trainedMovement ?? motion.Buttons;
        return now < validUntil ? movement | (ability & InputButtons.SkillAbilities) : motion.Buttons;
    }

    public double Deadline(double motionDeadline, double now) => validUntil > now ? Math.Min(motionDeadline, validUntil) : motionDeadline;
}
