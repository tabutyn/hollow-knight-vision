namespace HollowKnightVision.Windows.Core;

/// <summary>Applies a checked ability model to causal observations from one bound maneuver.</summary>
public sealed class ThinkingAbilityDrive(BotLearnedSkill? skill, BotNativePolicy? motor, ThinkingCandidate candidate,
    BotStarterPolicy? starter = null)
{
    private readonly BotObservationHistory history = new(skill?.ObservationSchema ?? motor?.Version ?? BotPolicyContract.CurrentVersion);
    private readonly BotObservationHistory? starterHistory = starter is not null &&
        (skill?.ObservationSchema ?? motor?.Version ?? BotPolicyContract.CurrentVersion) != starter.ObservationSchema
        ? new(starter.ObservationSchema) : null;
    private double lastSample = double.NegativeInfinity;
    private double validUntil;
    private InputButtons ability;
    private InputButtons? trainedMovement;

    public InputButtons Apply(ThinkingMotion motion, ThinkingSight sight, BotCaptureObservation? row,
        InputButtons? previous, double now, bool skillControlsAvailable)
    {
        var fallback = starter is null ? motion.Buttons : InputButtons.None;
        if ((!skillControlsAvailable && starter is null) || previous is null || row is null
            || row.Generation != sight.Scope.Generation || row.CaptureTimestamp != sight.CapturedAt
            || row.TerrainSource != (sight.Scope.Domain == BotCaptureDomain.Hacker ? "human-authored" : "detected")
            || !BotObservationHistory.IsFresh(row, now) || row.Issues.Count != 0)
        {
            history.Reset();
            starterHistory?.Reset();
            lastSample = double.NegativeInfinity;
            validUntil = 0;
            return fallback;
        }
        if (now >= validUntil || row.CapturedAt - lastSample >= BotRecordingContract.SampleInterval - .012)
        {
            lastSample = row.CapturedAt;
            if (candidate.Route is { } bound && sight.Scene?.Routes.FirstOrDefault(route => route.Matches(bound)) is { } current)
                row = row with { RoadHint = current.Road, HackerPathVersion = 1 };
            var previousAction = BotPolicyAction.FromButtons(previous.Value);
            var features = history.Append(row, previousAction, now);
            if (features is null)
            {
                validUntil = 0;
                return fallback;
            }
            ability = skillControlsAvailable && skill is not null ? skill.Predict(features) : InputButtons.None;
            // Combat keeps its bound actor approach. A trained traversal motor sees the exact selected route.
            var motorAction = motor is not null && candidate.Action is ThinkingAction.Traverse or ThinkingAction.Explore
                ? BotNativePolicy.Action(motor.Logits(features)) : (BotPolicyAction?)null;
            trainedMovement = motorAction?.Buttons;
            if (starter is not null)
            {
                var raw = starterHistory is null ? features : starterHistory.Append(row, previousAction, now);
                trainedMovement = raw is null ? InputButtons.None : starter.Decide(raw, motorAction, row.RoadHint).Buttons;
            }
            validUntil = Math.Min(now + .14, Math.Min(row.CapturedAt + BotRecordingContract.MaximumCaptureAge,
                Math.Min(row.AvailableAt + .25, row.KnightPredictionDeadline)));
        }
        var movement = trainedMovement ?? motion.Buttons;
        return now < validUntil ? movement | (ability & InputButtons.SkillAbilities) : fallback;
    }

    public double Deadline(double motionDeadline, double now) => validUntil > now ? Math.Min(motionDeadline, validUntil) : motionDeadline;
}
