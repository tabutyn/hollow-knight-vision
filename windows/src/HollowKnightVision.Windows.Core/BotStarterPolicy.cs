namespace HollowKnightVision.Windows.Core;

/// <summary>Runs the shared built-in policies before a trained movement checkpoint exists.</summary>
public sealed class BotStarterPolicy
{
    private sealed record InputView(string[] Current, string[] History);
    private readonly BotAbilitySkill skill;
    private readonly int rawWidth;
    private readonly IReadOnlyDictionary<string, int> current;
    public int ObservationSchema { get; }

    private BotStarterPolicy(BotAbilitySkill skill, int schema, IReadOnlyList<string> names, InputView view)
    {
        this.skill = skill;
        ObservationSchema = schema;
        rawWidth = names.Count * BotRecordingContract.HistoryLength;
        var newest = rawWidth - names.Count;
        current = names.Select((name, index) => (name, index))
            .Where(entry => view.Current.Any(prefix => entry.name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToDictionary(entry => entry.name, entry => newest + entry.index, StringComparer.Ordinal);
    }

    public static BotStarterPolicy Load(BotAbilitySkill skill = BotAbilitySkill.Explore,
        int schema = BotPolicyContract.CurrentVersion, string? layoutPath = null)
    {
        if (schema is < BotPolicyContract.CurrentVersion or > BotPolicyContract.LandingVersion
            || BotPolicyContract.Names(schema) is not { } names)
            throw new InvalidDataException("The built-in bot requires a supported compact observation schema.");
        var file = layoutPath ?? Path.Combine(AppContext.BaseDirectory, "training", "playing-policy-inputs.json");
        var views = BotNativePolicy.Decode<Dictionary<string, InputView>>(BotNativePolicy.ReadBounded(file, 64 * 1024));
        if (!views.TryGetValue(skill.ToString().ToLowerInvariant(), out var view)
            || view?.Current is not { Length: > 0 } || view.Current.Any(prefix => prefix is null)
            || view.History is not { Length: > 0 } || view.History.Any(prefix => prefix is null))
            throw new InvalidDataException("The built-in bot movement layout is unavailable.");
        return new(skill, schema, names, view);
    }

    public BotPolicyAction Decide(IReadOnlyList<float> raw, BotPolicyAction? motor = null, BotRoadHint? waypoint = null)
    {
        if (raw.Count != rawWidth) throw new InvalidDataException("Incomplete built-in bot observation.");
        var enemy = Value("enemy_0_present") > .5f;
        var dx = Value("enemy_0_x");
        var nearby = enemy && Math.Abs(dx) < .17f && Math.Abs(Value("enemy_0_y")) < .16f;
        var blocked = Value("blockade_direction");

        if (skill == BotAbilitySkill.Fight)
            return enemy ? Move(dx, attack: nearby) : motor ?? default;
        if (skill == BotAbilitySkill.Recover)
        {
            if (nearby) return Move(dx >= 0 ? -1 : 1);
            if (Value("health_known") > .5f && Value("health_fraction") <= 2f / 16
                && Value("lifeblood_cocoon_present") > .5f)
            {
                var target = Value("lifeblood_cocoon_dx");
                return Move(target, attack: Math.Abs(target) < .10f && Math.Abs(Value("lifeblood_cocoon_dy")) < .12f);
            }
            return default;
        }
        if (skill == BotAbilitySkill.Traverse && waypoint is { } route)
            return Move(route.Dx, jump: route.Dy > .20 || Math.Abs(blocked) > .5f);
        if (skill == BotAbilitySkill.Progress && Value("geo_cache_present") > .5f)
        {
            var target = Value("geo_cache_dx");
            return Move(target, attack: Math.Abs(target) < .10f && Math.Abs(Value("geo_cache_dy")) < .12f);
        }
        if (Value("shared_path_present") > .5f) return Move(Value("shared_path_dx"), jump: Math.Abs(blocked) > .5f);
        if (Value("road_present") > .5f) return Move(Value("road_dx"), jump: Math.Abs(blocked) > .5f);
        if (Math.Abs(blocked) > .5f) return Move(blocked, jump: true);
        if (skill == BotAbilitySkill.Explore && Value("atlas_present") > .5f)
        {
            var target = Value("atlas_right") - Value("atlas_left");
            if (Math.Abs(target) > .01f) return Move(target);
        }
        return motor ?? (skill == BotAbilitySkill.Progress ? default : Move(1));

        float Value(string name) => current.TryGetValue(name, out var index) ? raw[index] : 0;
        BotPolicyAction Move(double direction, bool jump = false, bool attack = false) => new(
            direction < -.01 ? 1 : direction > .01 ? 2 : 0, 0,
            jump && Value("previous_jump") < .5f ? 1 : 0,
            attack && Value("previous_attack") < .5f ? 1 : 0);
    }
}
