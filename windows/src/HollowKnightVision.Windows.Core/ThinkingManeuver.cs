namespace HollowKnightVision.Windows.Core;

public sealed record ThinkingMotion(InputButtons Buttons, double Deadline, string Status,
    bool Finished = false, bool BeginReading = false);

public sealed class ThinkingManeuver(ThinkingCandidate candidate, ThinkingSight began, double startedAt)
{
    private bool sent;
    private double lastNativeAt = double.NegativeInfinity;
    private InputButtons nativeButtons;
    public ThinkingCandidate Candidate { get; } = candidate;
    public double StartedAt { get; } = startedAt;

    public ThinkingMotion Step(ThinkingSight sight, double now, InputButtons previous, bool receivedPassage, double blockage)
    {
        if (sight.Scope != began.Scope || !sight.IsFresh(now))
            return Finish("Policy context changed.");
        if (!sent && !Candidate.IsEligible(sight))
            return Finish("The chosen target is no longer available.");
        var elapsed = now - StartedAt;
        var duration = Candidate.Action switch
        {
            ThinkingAction.Focus => 1.55,
            ThinkingAction.Traverse or ThinkingAction.Fight or ThinkingAction.Lifeblood => 2,
            _ => .12
        };
        if (Candidate.Action == ThinkingAction.Focus)
        {
            if (sight.Scene is not { } scene || scene.Room != Candidate.Room || scene.Recovery is not { } recovery)
                return Finish("Focus telemetry expired.");
            if (sent && recovery.Health > began.Scene?.Recovery?.Health)
                return Finish("Focus restored a mask.");
            if (!scene.FocusOpening) return Finish("Focus opening closed.");
        }
        if (elapsed >= duration)
        {
            if (Candidate.Action is ThinkingAction.Inspect or ThinkingAction.Listen)
            {
                if (receivedPassage) return Finish("Remembered the passage.");
                if (elapsed < 12)
                    return new(InputButtons.None, 0, "Reading sign or dialogue…");
                return Finish("No new text verified.");
            }
            return Finish(Candidate.Action == ThinkingAction.Focus ? "No healing verified." : "Policy window ended.");
        }
        if (Candidate.Action is ThinkingAction.Traverse or ThinkingAction.Fight or ThinkingAction.Lifeblood
            && !Candidate.IsEligible(sight))
            return Finish("Bound target expired.");
        if (Candidate.Action == ThinkingAction.Continue && !Candidate.IsEligible(sight))
            return Finish("Menu selection changed.");
        InputButtons buttons;
        if (Candidate.Action is ThinkingAction.Traverse or ThinkingAction.Fight or ThinkingAction.Lifeblood)
        {
            if (now - lastNativeAt >= .075)
            {
                nativeButtons = Candidate.Action == ThinkingAction.Traverse
                    ? Traverse(sight.Scene!, previous, blockage) : Approach(sight.Scene!, previous);
                lastNativeAt = now;
            }
            buttons = nativeButtons;
        }
        else
            buttons = Candidate.Action == ThinkingAction.Focus ? InputButtons.ActionA
                : Candidate.Action == ThinkingAction.Continue ? InputButtons.ActionZ : InputButtons.Up;
        var beginReading = !sent && Candidate.Action is ThinkingAction.Inspect or ThinkingAction.Listen;
        sent = true;
        return new(buttons, Math.Min(now + .14, Math.Min(sight.CapturedAt + .45, StartedAt + duration)),
            "Policy · " + Candidate.Action, BeginReading: beginReading);
    }

    private InputButtons Traverse(ThinkingScene scene, InputButtons previous, double blockage)
    {
        var road = scene.Routes.First(route => route.Matches(Candidate.Route!)).Road;
        var move = Horizontal(road.Dx);
        return (road.Dy > .20 || Math.Abs(blockage) > .5) && !previous.HasFlag(InputButtons.ActionZ)
            ? move | InputButtons.ActionZ : move;
    }

    private InputButtons Approach(ThinkingScene scene, InputButtons previous)
    {
        var source = Candidate.Action == ThinkingAction.Fight ? scene.Enemies : scene.Cocoons;
        var target = source.First(actor => actor.Id == Candidate.ActorId);
        var buttons = Horizontal(target.Dx);
        return Math.Abs(target.Dx) < .11 && Math.Abs(target.Dy) < .13 && !previous.HasFlag(InputButtons.ActionX)
            ? buttons | InputButtons.ActionX : buttons;
    }

    private static InputButtons Horizontal(double dx) => dx < -.01 ? InputButtons.Left
        : dx > .01 ? InputButtons.Right : InputButtons.None;

    private static ThinkingMotion Finish(string status) => new(InputButtons.None, 0, status, Finished: true);
}
