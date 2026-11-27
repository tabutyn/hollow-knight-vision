namespace HollowKnightVision.Windows.Core;

public sealed record ThinkingMotion(InputButtons Buttons, double Deadline, string Status,
    bool Finished = false, bool BeginReading = false);

public sealed class ThinkingManeuver(ThinkingCandidate candidate, ThinkingSight began, double startedAt)
{
    private bool sent;
    private double lastNativeAt = double.NegativeInfinity;
    private InputButtons nativeButtons;
    private readonly HashSet<string> visited = new(StringComparer.Ordinal);
    private string? pulsedFrom;
    private double pulseAt;
    private InputButtons menuPulse;
    public ThinkingCandidate Candidate { get; } = candidate;
    public double StartedAt { get; } = startedAt;

    public ThinkingMotion Step(ThinkingSight sight, double now, InputButtons previous, bool receivedPassage, double blockage)
    {
        if (sight.Scope != began.Scope || !sight.IsFresh(now))
            return Finish("Policy context changed.");
        if (Candidate.MenuPage is not null) return MenuStep(sight, now);
        if (Candidate.Route is { } target && sight.Chart == target.Chart && sight.NavigationFeet is { } feet)
        {
            bool Arrived(AtlasPathPoint point) => Math.Pow(feet.X - point.X, 2) + Math.Pow(feet.Y - point.Y, 2)
                <= sight.ArrivalRadius * sight.ArrivalRadius;
            if (Arrived(target.Objective)) return Finish(target.Road.Frontier ? "Explore destination reached" : "Target reached");
            if (Arrived(target.Waypoint)) return Finish("Waypoint reached");
        }
        if (!sent && !Candidate.IsEligible(sight))
            return Finish("The chosen target is no longer available.");
        var elapsed = now - StartedAt;
        var duration = Candidate.Action switch
        {
            ThinkingAction.Focus => 1.55,
            ThinkingAction.Explore or ThinkingAction.Traverse or ThinkingAction.Fight or ThinkingAction.Lifeblood => 2,
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
        if (Candidate.Action is ThinkingAction.Explore or ThinkingAction.Traverse or ThinkingAction.Fight or ThinkingAction.Lifeblood
            && !Candidate.IsEligible(sight))
            return Finish("Bound target expired.");
        if (Candidate.Action == ThinkingAction.Continue && !Candidate.IsEligible(sight))
            return Finish("Menu selection changed.");
        InputButtons buttons;
        if (Candidate.Action is ThinkingAction.Explore or ThinkingAction.Traverse or ThinkingAction.Fight or ThinkingAction.Lifeblood)
        {
            if (now - lastNativeAt >= .075)
            {
                nativeButtons = Candidate.Action switch
                {
                    ThinkingAction.Explore when Candidate.Route is null => InputButtons.None,
                    ThinkingAction.Explore or ThinkingAction.Traverse => Traverse(sight.Scene!, previous, blockage),
                    _ => Approach(sight.Scene!, previous)
                };
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

    private ThinkingMotion MenuStep(ThinkingSight sight, double now)
    {
        if (Candidate.Action == ThinkingAction.MenuWait) return Finish("Waiting for new menu evidence.");
        if (sight.Gameplay || sight.Page is not { } page || page.State != Candidate.MenuPage!.State)
            return Finish("Menu changed.");
        if (pulsedFrom is null && !Candidate.IsEligible(sight)) return Finish("Menu or highlight changed.");
        if (pulsedFrom is not null)
        {
            var elapsed = now - pulseAt;
            if (elapsed < .1 && page.Selected == pulsedFrom)
                return new(menuPulse, pulseAt + .1, "Menu input");
            if (menuPulse == InputButtons.ActionZ) return Finish("Sent confirm · " + pulsedFrom);
            if (elapsed > 2) return Finish("No menu advance observed.");
            if (elapsed < .15 || page.Selected is null || page.Selected == pulsedFrom)
                return new(InputButtons.None, 0, "Waiting for a new highlight.");
            if (Candidate.Action == ThinkingAction.MenuMore) return Finish("Menu advanced · " + page.Selected);
        }
        if (page.Selected is null) return now - StartedAt < 2 ? new(InputButtons.None, 0, "Waiting for a verified highlight.")
            : Finish("No verified menu highlight.");
        if (!visited.Add(page.Selected) || page.Next(Candidate.ElementId) is not { } button) return Finish("Menu navigation ended.");
        pulsedFrom = page.Selected;
        menuPulse = button;
        pulseAt = now;
        return new(button, now + .1, "Menu input");
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
