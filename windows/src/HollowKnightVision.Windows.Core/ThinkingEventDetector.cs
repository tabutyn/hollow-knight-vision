namespace HollowKnightVision.Windows.Core;

public sealed class ThinkingEventDetector
{
    private ThinkingSight? previous;
    private double? signGone, npcGone, blockedSince;
    private bool signPresent, npcPresent;
    private bool deadEnd, routesAvailable, focus;
    private int? enemy;
    private string? highlight;
    private string? elements;
    private ThinkingRoute? observedRoute;
    private bool lost;
    private readonly HashSet<long> signs = [], npcs = [];
    public bool NewSign { get; private set; }
    public bool ChangedState { get; private set; }
    public string? PreviousState { get; private set; }

    public string? Observe(ThinkingSight sight, double now)
    {
        ChangedState = lost || previous?.Scope != sight.Scope || previous.Gameplay != sight.Gameplay || previous.Menu != sight.Menu;
        NewSign = false;
        if (!sight.Gameplay && sight.Page is null) { lost = true; ChangedState = false; return null; }
        lost = false;
        List<string> events = [];
        if (ChangedState)
        {
            PreviousState = previous?.Scope.Generation == sight.Scope.Generation ? previous.Scope.Context : null;
            events.Add(sight.Page is { } page ? "Menu entered · " + page.State
                : previous is null ? "First observation" : "Room or game state changed");
            signGone = npcGone = blockedSince = null;
            signPresent = npcPresent = false;
            deadEnd = routesAvailable = focus = false;
            enemy = null;
            highlight = elements = null;
            observedRoute = null;
            signs.Clear();
            npcs.Clear();
        }
        if (sight.Gameplay)
        {
            NewSign = sight.SignTracks.Any(id => !signs.Contains(id));
            var newNpc = sight.NpcTracks.Any(id => !npcs.Contains(id));
            signs.UnionWith(sight.SignTracks);
            npcs.UnionWith(sight.NpcTracks);
            Cue(sight.Cues.Any(c => c.Text == "inspect"), ref signPresent, ref signGone, "Sign visible", NewSign);
            Cue(sight.Cues.Any(c => c.Text == "listen"), ref npcPresent, ref npcGone, "NPC visible", newNpc);
        }
        if (sight.Scene is { } scene)
        {
            var nextEnemy = scene.Enemies.FirstOrDefault()?.Id;
            if (nextEnemy.HasValue && nextEnemy != enemy) events.Add("Enemy encountered");
            if (scene.CanStartFocus && !focus) events.Add("Focus available");
            enemy = nextEnemy;
            focus = scene.CanStartFocus;
            if (sight.NavigationFeet is { } feet && sight.Chart is { } chart)
            {
                if (sight.ActiveRoute is { } active && previous?.ActiveRoute?.Matches(active) != true) observedRoute = active;
                else if (previous?.ActiveRoute is not null && sight.ActiveRoute is null) observedRoute = null;
                if (observedRoute is { } target && target.Chart == chart && Arrived(target))
                {
                    events.Add(target.Road.Frontier ? "Explore destination reached" : "Target reached");
                    observedRoute = null;
                }
                if ((sight.ActiveRoute ?? scene.Routes.FirstOrDefault()) is { } route && route.Chart == chart && !Arrived(route))
                    observedRoute = route;
                bool Arrived(ThinkingRoute r) => Math.Pow(feet.X - r.Objective.X, 2) + Math.Pow(feet.Y - r.Objective.Y, 2)
                    <= sight.ArrivalRadius * sight.ArrivalRadius;
            }
            if (!ChangedState && !routesAvailable && scene.Routes.Count > 0) events.Add("Route available");
            routesAvailable = scene.Routes.Count > 0;
            if (scene.Grounded && !routesAvailable)
            {
                blockedSince ??= now;
                if (!deadEnd && (sight.Blocked || now - blockedSince >= 1.5)) { events.Add("Dead end"); deadEnd = true; }
            }
            else { blockedSince = null; deadEnd = false; }
        }
        else blockedSince = null;
        if (sight.Page is { } current)
        {
            var ids = string.Join('\u001f', current.Elements.Select(e => e.Id).Order(StringComparer.Ordinal));
            if (!ChangedState && current.Selected is not null && current.Selected != highlight) events.Add("Menu selection changed");
            else if (!ChangedState && ids.Length != 0 && ids != elements) events.Add("Menu elements changed");
            highlight = current.Selected ?? highlight;
            if (ids.Length != 0) elements = ids;
        }
        if (!ChangedState && sight.OutcomeId is { } id && id != previous?.OutcomeId && sight.Outcome is { } outcome)
            events.Add(outcome);
        previous = sight;
        return events.Count == 0 ? null : string.Join(" · ", events);

        void Cue(bool visible, ref bool present, ref double? gone, string reason, bool newTrack)
        {
            if (newTrack || visible && !present) events.Add(reason);
            if (visible)
            {
                present = true;
                gone = null;
            }
            else
            {
                gone ??= now;
                if (now - gone >= 2.5) present = false;
            }
        }
    }
}
