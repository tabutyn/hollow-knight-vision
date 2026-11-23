namespace HollowKnightVision.Windows.Core;

/// <summary>Feature ordering shared with BotPython/hkv_bot_dataset.py.</summary>
public static class BotPolicyContract
{
    public const int CurrentVersion = 14;
    public const int GroundContactVersion = 15;
    public const int LandingVersion = 16;
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> Schemas = Build();

    public static IReadOnlyList<string>? Names(int version) => Schemas.GetValueOrDefault(version);

    private static IReadOnlyDictionary<int, IReadOnlyList<string>> Build()
    {
        var schemas = new Dictionary<int, IReadOnlyList<string>>
        {
            [1] = Array.AsReadOnly(BotRecordingContract.FeatureNames.Take(BotRecordingContract.LegacyWidth).ToArray()),
            [2] = BotRecordingContract.FeatureNames
        };
        Add(3, schemas[2].Concat(Grid("floor")).Concat(Grid("wall")));
        var blockade = schemas[3].ToArray();
        blockade[13] = "blockade_dx";
        blockade[14] = "blockade_dy";
        blockade[15] = "blockade_direction";
        Add(4, blockade);
        Append(5, "geo_cache_dx", "geo_cache_dy", "geo_cache_present");
        Append(6, "atlas_present", "atlas_left", "atlas_right", "health_known", "health_fraction");
        Add(7, schemas[6].Append("atlas_grid_present").Concat(Enumerable.Range(0, 15).Select(i => $"atlas_missing_{i}")));
        Append(8, "road_present", "road_dx", "road_dy", "road_clearance", "road_distance", "road_frontier");
        Append(9, "joni_blessing_dx", "joni_blessing_dy", "joni_blessing_present");
        Append(10, "lifeblood_cocoon_dx", "lifeblood_cocoon_dy", "lifeblood_cocoon_present");
        Add(11, schemas[10].Concat(Grid("door")));
        Append(12, "shared_path_present", "shared_path_dx", "shared_path_dy");
        var retained = schemas[12].Where(name => (!name.StartsWith("terrain_", StringComparison.Ordinal) || name == "terrain_valid")
            && !name.StartsWith("wall_", StringComparison.Ordinal) && name != "walls_analyzed"
            && !name.StartsWith("screen_", StringComparison.Ordinal) && !name.StartsWith("joni_blessing_", StringComparison.Ordinal)).ToArray();
        Add(13, retained.Concat(Markings(64)));
        Add(CurrentVersion, retained.Concat(Markings(16)));
        Append(GroundContactVersion, "knight_ground_contact");
        Append(LandingVersion, "landing_present", "landing_left", "landing_right", "landing_clearance", "landing_dx", "landing_dy");
        return schemas;

        void Add(int version, IEnumerable<string> names) => schemas[version] = Array.AsReadOnly(names.ToArray());
        void Append(int version, params string[] names) => Add(version, schemas[version - 1].Concat(names));
    }

    private static IEnumerable<string> Grid(string kind) => Enumerable.Range(0, BotPolicyGeometry.Cells).Select(i => $"screen_{kind}_{i}");

    private static IEnumerable<string> Markings(int slots)
    {
        string[] kinds = ["ground", "wall", "spike", "door"];
        foreach (var kind in kinds) yield return $"marking_{kind}_known";
        foreach (var kind in kinds) yield return $"marking_{kind}_count";
        yield return "marking_overflow";
        string[] fields = ["start_dx", "start_dy", "end_dx", "end_dy", "kind", "present"];
        for (var i = 0; i < slots; i++)
        foreach (var field in fields)
            yield return $"marking_{i}_{field}";
    }
}
