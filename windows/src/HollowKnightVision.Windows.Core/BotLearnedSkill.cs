namespace HollowKnightVision.Windows.Core;

public enum BotAbilitySkill { Explore, Traverse, Fight, Recover, Progress }

/// <summary>An optional classifier trained on recorded ability presses and releases.</summary>
public sealed class BotLearnedSkill
{
    private sealed record Document(int Version, string Domain, string Skill, int ObservationSchema,
        int HistoryLength, string[] Labels, BotNativePolicy.Layer[] Layers, int[]? InputColumns);

    private readonly Document document;
    private readonly int rawWidth;
    public int ObservationSchema => document.ObservationSchema;

    private BotLearnedSkill(Document document, int rawWidth)
    {
        this.document = document;
        this.rawWidth = rawWidth;
    }

    public static BotLearnedSkill? Load(string botRoot, BotCaptureDomain domain, BotAbilitySkill skill)
    {
        var domainName = domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay";
        var skillName = skill.ToString().ToLowerInvariant();
        Document data;
        try
        {
            var path = Path.Combine(botRoot, "skill-actors", domainName, skillName + ".json");
            data = BotNativePolicy.Decode<Document>(BotNativePolicy.ReadBounded(path, 4 * 1024 * 1024));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if (data.Domain != domainName || data.Skill != skillName || data.HistoryLength != BotRecordingContract.HistoryLength
            || BotPolicyContract.Names(data.ObservationSchema) is not { } names
            || data.Labels is not { Length: >= 2 } labels || labels[0] != "none"
            || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length
            || labels.Skip(1).Any(label => (Button(label) & Allowed(skill)) == 0))
            throw new InvalidDataException("The ability model does not match the selected mode, policy, or controls.");
        var rawWidth = names.Count * data.HistoryLength;
        BotNativePolicy.ValidateLayers(data.Layers, BotNativePolicy.InputWidth(data.Version, data.InputColumns, rawWidth),
            labels.Length, maximumWidth: 128);
        return new(data, rawWidth);
    }

    public InputButtons Predict(float[] observation)
    {
        if (observation.Length != rawWidth || observation.Any(value => !float.IsFinite(value) || Math.Abs(value) > 4))
            throw new InvalidDataException("Invalid ability model observation.");
        var input = document.InputColumns is { } columns ? columns.Select(index => observation[index]).ToArray() : observation;
        var logits = BotNativePolicy.Forward(input, document.Layers);
        return Button(document.Labels[BotNativePolicy.ArgMax(logits, 0, logits.Length)]);
    }

    private static InputButtons Allowed(BotAbilitySkill skill) => skill switch
    {
        BotAbilitySkill.Explore or BotAbilitySkill.Traverse => InputButtons.Dash | InputButtons.SuperDash,
        BotAbilitySkill.Fight => InputButtons.QuickCast | InputButtons.Dash | InputButtons.DreamNail,
        BotAbilitySkill.Recover => InputButtons.ActionA,
        BotAbilitySkill.Progress => InputButtons.DreamNail,
        _ => InputButtons.None
    };

    private static InputButtons Button(string label) => label switch
    {
        "dash" => InputButtons.Dash,
        "superDash" => InputButtons.SuperDash,
        "quickCast" => InputButtons.QuickCast,
        "dreamNail" => InputButtons.DreamNail,
        "a" => InputButtons.ActionA,
        _ => InputButtons.None
    };
}
