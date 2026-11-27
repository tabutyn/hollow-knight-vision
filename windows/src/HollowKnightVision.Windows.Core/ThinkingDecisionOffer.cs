using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record ThinkingMenuElement(string Id, string Label, StencilRect Bounds, bool CanSelect);
public sealed record ThinkingMenuPage(string State, string? Selected, IReadOnlyList<ThinkingMenuElement> Elements)
{
    public string Key { get; } = string.Join('\u001f', new[] { State, Selected ?? "" }.Concat(Elements.Select(e => e.Id).Order(StringComparer.Ordinal)));

    public static ThinkingMenuPage? From(MenuStencilResult? stencil)
    {
        if (stencil is not { IsMatch: true }) return null;
        var elements = new List<ThinkingMenuElement>();
        foreach (var region in stencil.SelectorSearchRegions.DistinctBy(r => r.OptionIdentifier))
        {
            var anchor = stencil.Anchors.FirstOrDefault(a => a.ClassIdentifier == region.OptionIdentifier || a.Name == region.OptionName);
            if (anchor is null || !anchor.Rect.IsFinite || anchor.Rect.IsEmpty) continue;
            var id = region.OptionIdentifier;
            var selectable = !id.StartsWith("select-profile.clear-save", StringComparison.Ordinal)
                && (stencil.ContextIdentifier != "clear-save" || id is "shared.no" or "shared.back");
            elements.Add(new(id, region.OptionName, anchor.Rect, selectable));
        }
        var focus = stencil.SelectorSearchRegions.FirstOrDefault(r => r.IsSolved && r.OptionName == stencil.SelectedOption)?.OptionIdentifier;
        return new(stencil.ContextIdentifier, elements.Any(e => e.Id == focus) ? focus : null, elements);
    }

    public InputButtons? Next(string? target)
    {
        var focus = Elements.FirstOrDefault(e => e.Id == Selected);
        if (focus is null) return null;
        if (target is null)
            return Elements.Max(e => e.Bounds.MidY) - Elements.Min(e => e.Bounds.MidY) < 5
                && Elements.Max(e => e.Bounds.MidX) - Elements.Min(e => e.Bounds.MidX) > 5 ? InputButtons.Right : InputButtons.Down;
        var item = Elements.FirstOrDefault(e => e.Id == target && e.CanSelect);
        if (item is null) return null;
        if (item.Id == focus.Id) return InputButtons.ActionZ;
        var dx = item.Bounds.MidX - focus.Bounds.MidX;
        var dy = item.Bounds.MidY - focus.Bounds.MidY;
        if (Math.Abs(dx) + Math.Abs(dy) <= 1) return null;
        return Math.Abs(dy) >= Math.Abs(dx) ? dy > 0 ? InputButtons.Down : InputButtons.Up : dx > 0 ? InputButtons.Right : InputButtons.Left;
    }
}

/// <summary>Bind exactly one model reply to a choice offered on its captured frame.</summary>
public sealed class ThinkingDecisionOffer
{
    private sealed record Option(JsonElement Selection, object Evidence, ThinkingCandidate Candidate);
    private readonly List<Option> options = [];
    private readonly ThinkingSight sight;
    public long DecisionId { get; } = ThinkingPolicyOffer.NextDecisionId();
    public IReadOnlyList<ThinkingCandidate> Candidates => options.Select(o => o.Candidate).ToArray();
    public static JsonElement GameplayContract { get; } = LoadSchema("thinking-policy.schema.json");
    public static JsonElement MenuContract { get; } = LoadSchema("thinking-menu.schema.json");

    private static JsonElement LoadSchema(string name)
    {
        using var stream = typeof(ThinkingDecisionOffer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("Missing Thinking schema " + name);
        return JsonSerializer.Deserialize<JsonElement>(stream);
    }

    public ThinkingDecisionOffer(ThinkingSight sight, bool allowsMore = true)
    {
        this.sight = sight;
        if (sight.Page is { } page)
        {
            foreach (var element in page.Elements.Where(e => e.CanSelect))
                Menu(new { menu = "select", elementId = element.Id }, ThinkingAction.MenuSelect, element.Id);
            if (options.Count == 0) Menu(new { menu = "wait" }, ThinkingAction.MenuWait);
            else if (allowsMore && page.Selected is not null) Menu(new { menu = "more" }, ThinkingAction.MenuMore);
            void Menu(object selection, ThinkingAction action, string? element = null)
            {
                options.Add(new(JsonSerializer.SerializeToElement(selection), new { }, new ThinkingCandidate($"c{options.Count}",
                    "menu", new Dictionary<string, object>(), action) { MenuPage = page, ElementId = element }));
            }
            return;
        }
        if (!sight.Gameplay) return;
        var routes = sight.Scene?.Routes ?? [];
        for (var i = 0; i < routes.Count; i++)
        {
            var r = routes[i];
            Add("traverse", new() { ["waypointId"] = i }, ThinkingAction.Traverse,
                new { roomId = sight.Scene!.Room, chart = r.Chart, waypoint = new[] { r.Waypoint.X, r.Waypoint.Y },
                    goal = new[] { r.Objective.X, r.Objective.Y }, road_dx = r.Road.Dx, road_dy = r.Road.Dy,
                    road_distance = r.Road.Distance, road_frontier = r.Road.Frontier }, r);
        }
        foreach (var cue in sight.Cues.Where(c => c.Text is "inspect" or "listen").DistinctBy(c => c.Text))
        {
            var inspect = cue.Text == "inspect";
            Add("interact", new() { ["verb"] = inspect ? "inspect" : "talk", ["targetId"] = inspect ? 0 : 1 },
                inspect ? ThinkingAction.Inspect : ThinkingAction.Listen, new { }, prompt: cue.FirstSeen);
        }
        if (options.Count == 0 || routes.Count > 0)
        {
            var frontier = routes.FirstOrDefault(r => r.Road.Frontier);
            Add("explore", new(), ThinkingAction.Explore, frontier is null ? new { } :
                new { chart = frontier.Chart, frontierX = frontier.Objective.X, frontierY = frontier.Objective.Y }, frontier);
        }

        void Add(string policy, Dictionary<string, object> parameters, ThinkingAction action, object evidence,
            ThinkingRoute? route = null, double? prompt = null) =>
            options.Add(new(JsonSerializer.SerializeToElement(new { policy, parameters }), evidence,
                new($"c{options.Count}", policy, parameters, action, sight.Scene?.Room, Route: route, PromptStart: prompt)));
    }

    public object Wire
    {
        get
        {
            var candidates = options.Select(o => sight.Page is null ? (object)new { selection = o.Selection, evidence = o.Evidence }
                : new { selection = o.Selection });
            return sight.Page is { } page ? new { objective = "expandAtlas", candidates, stateId = page.State, highlight = page.Selected,
                elements = page.Elements.Select(e => new { id = e.Id, label = e.Label }) } : new { objective = "expandAtlas", candidates };
        }
    }

    public string SelectionSpecification => "Gameplay reply schema\n" + GameplayContract.GetRawText() + "\nMenu script reply schema\n"
        + MenuContract.GetRawText() + "\nCopy exactly one offered selection as JSON. "
        + (sight.Page is null ? "Inspect new signs. Explore seeks unmapped ground. Traverse follows an offered waypoint."
            : "Select the visible element that advances the objective. More advances the highlight. Wait leaves controls idle.")
        + "\n" + JsonSerializer.Serialize(Wire);

    public object Schema => options.Count switch
    {
        0 => new { @not = new { } }, 1 => Exact(options[0].Selection), _ => new { oneOf = options.Select(o => Exact(o.Selection)) }
    };

    private static object Exact(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? new { type = "object", properties = value.EnumerateObject().ToDictionary(p => p.Name, p => Exact(p.Value)),
            required = value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal), additionalProperties = false }
        : new { @const = value };

    public ThinkingPlan Resolve(JsonElement reply)
    {
        var selected = options.FirstOrDefault(o => Matches(o.Selection, reply))
            ?? throw new InvalidDataException("The reply was not offered for this screenshot.");
        var points = selected.Candidate.Action switch
        {
            ThinkingAction.Explore => 40, ThinkingAction.Traverse => 55, _ => 80
        };
        return new(DecisionId, sight.Scope, [new(selected.Candidate, points)]);
    }

    public string SaveDecision(string root, Guid? profile, Guid sessionId, JsonElement reply)
    {
        _ = Resolve(reply);
        var path = Path.Combine(root, "Thinking", profile?.ToString("D").ToUpperInvariant() ?? "before-save",
            sessionId.ToString("D"), "latest-policy.json");
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(reply, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static bool Matches(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().Count() == actual.EnumerateObject().Count()
                && expected.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var value) && Matches(p.Value, value)),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.Number => actual.TryGetInt32(out var n) && expected.GetInt32() == n,
            _ => false
        };
    }
}
