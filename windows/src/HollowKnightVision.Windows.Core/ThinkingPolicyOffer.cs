using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public enum ThinkingAction { Traverse, Fight, Focus, Lifeblood, Inspect, Listen, Continue }

public sealed record ThinkingRoute(string Chart, AtlasPathPoint Objective, AtlasPathPoint Waypoint, BotRoadHint Road)
{
    public bool Matches(ThinkingRoute other) => Chart == other.Chart && Objective == other.Objective
        && Waypoint == other.Waypoint && Road.Frontier == other.Road.Frontier;

    public static ThinkingRoute FromPath(string chart, AtlasPathHint path, int knightHeight, double frameWidth, bool frontier) =>
        new(chart, path.Objective, path.Next, BotPathMotion.Road(path, knightHeight, frameWidth, frontier));
}

public sealed record ThinkingActor(int Id, double Dx, double Dy)
{
    public double DistanceSquared => Dx * Dx + Dy * Dy;
}

public sealed record ThinkingScene(int Room, bool Grounded, ReceiverRecovery? Recovery,
    IReadOnlyList<ThinkingActor> Enemies, IReadOnlyList<ThinkingActor> Cocoons, IReadOnlyList<ThinkingRoute> Routes)
{
    public bool FocusOpening => Grounded && !Enemies.Any(actor => Math.Abs(actor.Dx) < .20 && Math.Abs(actor.Dy) < .16);

    public bool CanStartFocus => Recovery is { CanFocus: true, Soul: >= 33 and <= 99, Health: > 0 } state
        && state.Health < state.MaxHealth && FocusOpening;

    /// <summary>Call only with telemetry already joined to this rendered image.</summary>
    public static ThinkingScene? FromMatchedReceiver(int room, ReceiverGroundTruthSample? sample, IReadOnlyList<ThinkingRoute>? routes)
    {
        if (sample is not { AtlasGameplayActive: true, HeroAvailable: true, ProjectionPixelWidth: > 0,
                Actors: { Version: 1, Hero: { IsValid: true } hero, Enemies: { Count: <= 64 } enemies } actors }
            || !enemies.All(actor => actor.IsValid) || enemies.Select(actor => actor.Id).Distinct().Count() != enemies.Count)
            return null;
        var width = sample.ProjectionPixelWidth.Value;
        return new(room, sample.Grounded, actors.Recovery, Targets(enemies),
            Targets(actors.LifebloodCocoons ?? []), routes ?? []);

        ThinkingActor[] Targets(IReadOnlyList<ReceiverActorState> input) => input.Where(actor => actor.IsValid)
            .Select(actor => new ThinkingActor(actor.Id, (actor.ScreenX - hero.ScreenX) / width, (actor.ScreenY - hero.ScreenY) / width))
            .OrderBy(actor => actor.DistanceSquared).ThenBy(actor => actor.Id).ToArray();
    }
}

public sealed record ThinkingCue(string Text, double FirstSeen);
public sealed record ThinkingScope(long Generation, BotCaptureDomain Domain, string Context, Guid? ReceiverSession);
public sealed record ThinkingSight(ThinkingScope Scope, double CapturedAt, bool Gameplay, string? Menu, string? Selection,
    IReadOnlyList<ThinkingCue> Cues, ThinkingScene? Scene)
{
    public bool IsFresh(double now) => now - CapturedAt is >= 0 and <= .45;
}

public sealed record ThinkingCandidate(string Id, string Policy, IReadOnlyDictionary<string, object> Parameters,
    ThinkingAction Action, int? Room = null, int? ActorId = null, ThinkingRoute? Route = null, double? PromptStart = null)
{
    public bool IsEligible(ThinkingSight sight)
    {
        if (Action == ThinkingAction.Continue) return sight.Menu == "pause" && sight.Selection == "pause.continue";
        if (!sight.Gameplay) return false;
        if (Action is ThinkingAction.Inspect or ThinkingAction.Listen)
        {
            var cue = Action == ThinkingAction.Inspect ? "inspect" : "listen";
            return sight.Cues.Any(value => value.Text == cue && value.FirstSeen == PromptStart);
        }
        if (sight.Scene is not { } scene || scene.Room != Room) return false;
        return Action switch
        {
            ThinkingAction.Traverse => Route is { } route && scene.Routes.Any(current => current.Matches(route)),
            ThinkingAction.Fight => scene.Enemies.Any(actor => actor.Id == ActorId),
            ThinkingAction.Lifeblood => scene.Cocoons.Any(actor => actor.Id == ActorId),
            ThinkingAction.Focus => scene.CanStartFocus,
            _ => false
        };
    }
}

public sealed record ThinkingScore(ThinkingCandidate Candidate, int Points);
public sealed record ThinkingPlan(long DecisionId, ThinkingScope Scope, IReadOnlyList<ThinkingScore> Scores)
{
    public ThinkingCandidate? Pick(ThinkingSight sight, double now) => sight.Scope == Scope && sight.IsFresh(now)
        ? Scores.OrderByDescending(score => score.Points).FirstOrDefault(score => score.Candidate.IsEligible(sight))?.Candidate
        : null;
}

/// <summary>The model scores handles. The actions and their target bindings stay in Vision.</summary>
public sealed class ThinkingPolicyOffer
{
    private static long sequence;
    private readonly ThinkingScope scope;
    public long DecisionId { get; } = Interlocked.Increment(ref sequence);
    public IReadOnlyList<ThinkingCandidate> Candidates { get; }

    public ThinkingPolicyOffer(ThinkingSight sight)
    {
        scope = sight.Scope;
        var offered = new List<ThinkingCandidate>();
        if (sight.Gameplay)
        {
            if (sight.Scene is { } scene)
            {
                foreach (var route in scene.Routes.Select((route, index) => (route, index)))
                    Add("traverse", ThinkingAction.Traverse, new()
                    {
                        ["roomId"] = scene.Room, ["routeId"] = route.index, ["waypointId"] = route.index,
                        ["road_dx"] = route.route.Road.Dx, ["road_dy"] = route.route.Road.Dy,
                        ["road_distance"] = route.route.Road.Distance, ["road_frontier"] = route.route.Road.Frontier
                    }, scene.Room, route: route.route);
                if (scene.Enemies.FirstOrDefault() is { } enemy)
                    Add("fight", ThinkingAction.Fight, new() { ["enemyId"] = 0 }, scene.Room, enemy.Id);
                if (scene.CanStartFocus)
                    Add("recover", ThinkingAction.Focus, new() { ["method"] = "focus" }, scene.Room);
                if (scene.Cocoons.FirstOrDefault() is { } cocoon)
                    Add("recover", ThinkingAction.Lifeblood, new() { ["method"] = "lifeblood", ["sourceId"] = 0 }, scene.Room, cocoon.Id);
            }
            foreach (var cue in sight.Cues)
            {
                if (cue.Text == "inspect")
                    Add("interact", ThinkingAction.Inspect, new() { ["verb"] = "inspect", ["targetId"] = 0 }, prompt: cue.FirstSeen);
                if (cue.Text == "listen")
                    Add("interact", ThinkingAction.Listen, new() { ["verb"] = "talk", ["targetId"] = 1 }, prompt: cue.FirstSeen);
            }
        }
        else if (sight.Menu == "pause" && sight.Selection == "pause.continue")
            Add("menu", ThinkingAction.Continue, new() { ["menuId"] = 0, ["entryId"] = 0 });
        Candidates = offered.ToArray();

        void Add(string policy, ThinkingAction action, Dictionary<string, object> parameters, int? room = null,
            int? actor = null, ThinkingRoute? route = null, double? prompt = null) =>
            offered.Add(new($"c{offered.Count}", policy, parameters, action, room, actor, route, prompt));
    }

    public object Wire => new
    {
        version = 2, decisionId = DecisionId, objective = "expandAtlas",
        candidates = Candidates.ToDictionary(candidate => candidate.Id,
            candidate => new { policy = candidate.Policy, @params = candidate.Parameters })
    };

    public string SelectionSpecification => """
        Score useful candidates from this offer. Return only scores keyed by offered IDs, as integers from 1 to 100.
        Omit unsuitable candidates. An empty scores object abstains. Higher wins, then earlier offer order.
        Consider immediate danger and supported quest evidence. Traverse follows the offered waypoint.
        road_frontier marks an exploration target. Recover with focus heals a missing mask.
        """ + "\n" + JsonSerializer.Serialize(Wire);

    public object Schema => ResponseSchema(Candidates.Select(candidate => candidate.Id));

    public static object ResponseSchema(IEnumerable<string> ids) => new
    {
        type = "object", additionalProperties = false, required = new[] { "scores" },
        properties = new
        {
            scores = new
            {
                type = "object", additionalProperties = false,
                properties = ids.ToDictionary(id => id,
                    _ => new { type = "integer", minimum = 1, maximum = 100 })
            }
        }
    };

    public ThinkingPlan Resolve(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object || reply.EnumerateObject().Count() != 1
            || !reply.TryGetProperty("scores", out var raw) || raw.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected scores keyed by IDs from this decision.");
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in raw.EnumerateObject())
        {
            if (!Candidates.Any(candidate => candidate.Id == field.Name)
                || field.Value.ValueKind != JsonValueKind.Number
                || !field.Value.TryGetDouble(out var value) || value is < 1 or > 100 || value != Math.Truncate(value)
                || !scores.TryAdd(field.Name, (int)value))
                throw new InvalidDataException("Policy scores must be integers from 1 to 100 for offered IDs.");
        }
        return new(DecisionId, scope, Candidates.Where(candidate => scores.ContainsKey(candidate.Id))
            .Select(candidate => new ThinkingScore(candidate, scores[candidate.Id])).ToArray());
    }
}
