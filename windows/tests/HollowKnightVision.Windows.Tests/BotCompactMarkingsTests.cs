using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotCompactMarkingsTests
{
    private const int MarkingWidth = 105;
    private sealed record Fixture(BotScreenTerrain? Geometry, double KnightX, double KnightY, float[] Expected);

    internal static void TestSharedMarkingFixtures()
    {
        using var stream = typeof(BotCompactMarkingsTests).Assembly.GetManifestResourceStream("compact-markings-cases.json")
            ?? throw new InvalidOperationException("The shared compact marking fixture is missing.");
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (fixtures is not { Length: > 0 })
            throw new InvalidOperationException("Expected shared compact marking cases.");

        for (var index = 0; index < fixtures.Length; index++)
        {
            var fixture = fixtures[index];
            var history = new BotObservationHistory(BotPolicyContract.CurrentVersion);
            var row = Observation(fixture.Geometry, x: fixture.KnightX, y: fixture.KnightY);
            var actual = Encode(history, row);
            EqualVector(actual.AsSpan(^MarkingWidth..), fixture.Expected, $"Compact marking fixture {index + 1}");
        }
    }

    internal static void TestAllGeometryTypesFitInSharedSlots()
    {
        var terrain = new BotScreenTerrain(640, 360,
            Enumerable.Range(0, 10).Select(i => new BotFloor(i * 20, i * 20 + 10, 100, 1)).ToArray(),
            Enumerable.Range(0, 3).Select(i => new BotWall(i * 40, 30, 90, 0)).ToArray(),
            Doors: [new(300, 40, 300, 90)], Spikes: [new(10, 80, 40, 80)]);
        var values = Markings(terrain);
        EqualVector(values.AsSpan(0, 9), [1, 1, 1, 1, 10f / 16, 3f / 16, 1f / 16, 1f / 16, 0],
            "Known channels, marking counts, and overflow");
        var kinds = Enumerable.Range(0, 16).Select(i => 13 + 6 * i).Where(i => values[i + 1] == 1)
            .Select(i => values[i]).Order().ToArray();
        EqualVector(kinds, [0, .33f, .33f, .33f, .33f, .33f, .33f, .33f, .33f, .33f, .33f, .66f, .66f, .66f, 1],
            "Kinds in occupied marking slots");
        var reversed = terrain with { Floors = terrain.Floors.Reverse().ToArray(), Walls = terrain.Walls.Reverse().ToArray() };
        EqualVector(Markings(reversed), values, "Reversed source ordering");

        static float[] Markings(BotScreenTerrain geometry) =>
            Encode(new(BotPolicyContract.CurrentVersion), Observation(geometry))[^MarkingWidth..];
    }

    internal static void TestCompactAndLegacyPolicyLayouts()
    {
        var names = BotPolicyContract.Names(BotPolicyContract.CurrentVersion);
        var contactNames = BotPolicyContract.Names(BotPolicyContract.GroundContactVersion);
        if (names is not { Count: 205 } || BotPolicyContract.Names(12) is not { Count: 1202 }
            || names.TakeWhile(name => !name.StartsWith("marking_", StringComparison.Ordinal)).Count() != 100
            || names.Any(name => name.StartsWith("joni_", StringComparison.Ordinal)
                || name.StartsWith("screen_", StringComparison.Ordinal)
                || name.StartsWith("terrain_0_", StringComparison.Ordinal)
                || name.StartsWith("wall_left_", StringComparison.Ordinal))
            || contactNames is null || !contactNames.SequenceEqual(names.Append("knight_ground_contact")))
            throw new InvalidOperationException("Policy feature names or legacy dimensions changed.");

        var row = Observation(new(640, 360, [], []));
        var compact = Encode(new(BotPolicyContract.CurrentVersion), row);
        if (compact.Length != 820 || compact[3 * 205 + 8] != 1)
            throw new InvalidOperationException("The compact input must start a four-frame history in the current room.");
        EqualVector(compact.AsSpan(^MarkingWidth..)[..4], [1, 1, 0, 0], "Known-empty ground and wall channels");
        if (Encode(new(12), row).Length != 4 * 1202)
            throw new InvalidOperationException("The legacy schema must still encode a four-frame input.");

        (bool? Grounded, float Expected)[] cases = [(true, 1), (false, -1), (null, 0)];
        foreach (var (grounded, expected) in cases)
        {
            var input = Encode(new(BotPolicyContract.GroundContactVersion), row with { Grounded = grounded });
            if (input.Length != 824 || input[^1] != expected)
                throw new InvalidOperationException($"Ground-contact input must end with {expected} for grounded={grounded}.");
            EqualVector(input.AsSpan(0, 618), new float[618], "Unused ground-contact history");
            EqualVector(input.AsSpan(618, 205), compact.AsSpan(615, 205), "Unchanged compact policy values");
        }
    }

    internal static void TestLandingFeaturesResetAtRoomBoundary()
    {
        var terrain = new BotScreenTerrain(640, 360, [new(328, 392, 128, 1), new(100, 520, 80, 1)], []);
        var history = new BotObservationHistory(BotPolicyContract.LandingVersion);
        (BotCaptureObservation Row, float[] Expected)[] cases =
        [
            (Observation(terrain, 10, 320d / 640, 175d / 640) with { Grounded = false },
                [1, .25f, 2.25f, .46875f, 0, 0]),
            (Observation(terrain, 10.1, 336d / 640, 182d / 640) with { Grounded = false },
                [1, -.25f, 1.75f, .6875f, .5f, .21875f]),
            (Observation(terrain, 10.2, 336d / 640, 182d / 640, room: 2) with { Grounded = false },
                [1, -.25f, 1.75f, .6875f, 0, 0]),
            (Observation(terrain, 10.3, 336d / 640, 182d / 640, room: 2) with { Grounded = true },
                [0, 0, 0, 0, 0, 0]),
        ];
        foreach (var (row, expected) in cases)
        {
            var actual = Encode(history, row);
            if (actual.Length != 848)
                throw new InvalidOperationException("The landing schema must encode four 212-feature frames.");
            EqualVector(actual.AsSpan(^6..), expected, $"Landing features at {row.AvailableAt}");
        }
    }

    private static BotCaptureObservation Observation(BotScreenTerrain? terrain, double time = 10,
        double x = .5, double y = .3, int room = 1)
    {
        var features = new double[BotRecordingContract.FeatureNames.Count];
        features[0] = x;
        features[1] = y;
        features[3] = 32d / 640;
        features[4] = 1;
        features[6] = 1;
        return new(time, time, time, 1, room, 0, features, [], "human-authored")
        {
            AuthoredTerrain = terrain,
            DetectionTimestamp = time,
        };
    }

    private static float[] Encode(BotObservationHistory history, BotCaptureObservation row) =>
        history.Append(row, default, row.AvailableAt)
        ?? throw new InvalidOperationException("Expected a fresh policy input.");

    private static void EqualVector(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected, string description)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"{description}: expected {expected.Length} values, got {actual.Length}.");
        for (var i = 0; i < actual.Length; i++)
            if (!(MathF.Abs(actual[i] - expected[i]) <= .000001f))
                throw new InvalidOperationException($"{description}: value {i} expected {expected[i]}, got {actual[i]}.");
    }
}
