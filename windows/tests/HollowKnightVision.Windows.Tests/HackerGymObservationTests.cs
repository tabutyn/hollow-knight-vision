using System.Runtime.InteropServices;
using System.Text.Json;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;

internal static class HackerGymObservationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Episode = Guid.Parse("9d19bd78-9ca6-4eba-a555-69fd2b66256c");

    internal static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var document = new GroundTruthLineDocument
        {
            Lines = [new("Tutorial_01", -20, 30, 0, GroundTruthLineKind.Positive, false)],
            Walls = [new GroundTruthWorldLine("Tutorial_01", -4, -4, 0, GroundTruthLineKind.Wall, false) { EndWorldY = 4 }],
            Spikes = [new("Tutorial_01", -3.6, -3.1, 0, GroundTruthLineKind.Spike, false)],
            Doors = [new("Tutorial_01", 2, 3, 0, GroundTruthLineKind.Door, false)]
        };
        new GroundTruthLineStore(Path.Combine(directory, "labels.json")).Save(document);
        File.WriteAllLines(Path.Combine(directory, "frames.jsonl"), new[] { 0d, 0, 0, 1, 2, 2.5, 2.5, 3 }
            .Select((x, index) => JsonSerializer.Serialize(new
            {
                sample = Frame(index, x), previous = new[] { 2, 0, 0, 1 }, ticks = index * 6, health = index > 3 ? 6 : 5
            }, Json)));
    }

    internal static void TestFixedRouteAndCameraDropout()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-gym-encoder-");
        try
        {
            var work = temporary.FullName;
            Export(work);
            var labels = Path.Combine(work, "labels.json");
            var frames = File.ReadAllText(Path.Combine(work, "frames.jsonl"));
            var rows = Run(frames, labels, new(12, 1.28));
            if (rows.Length != 8 || !rows[0].GetProperty("pathReachable").GetBoolean()
                || rows[6].GetProperty("pathReachable").GetBoolean() || rows[6].TryGetProperty("input", out _)
                || rows[6].GetProperty("issues")[0].GetString() != "missing-camera")
                throw new InvalidOperationException("Reachability must come from a current camera and directed route.");
            Check(rows[0], "shared_path_dx", 1);
            Check(rows[0], "road_frontier", 0);
            Check(rows[0], "atlas_present", 1);
            Check(rows[0], "atlas_grid_present", 1);
            Check(rows[0], "health_fraction", 5 / 16f);
            Check(rows[0], "geo_cache_present", 1);
            Check(rows[0], "lifeblood_cocoon_present", 1);
            Check(rows[0], "marking_spike_known", 1);
            Check(rows[2], "blockade_direction", 1);
            Check(rows[4], "blockade_direction", 0);
            Check(rows[4], "previous_attack", 1);
            Check(rows[4], "health_fraction", 6 / 16f);
            Check(rows[7], "room_changed", 1);
            Check(rows[7], "previous_attack", 0);
            if (rows[3].GetProperty("pathProgress").GetDouble() <= 0 || rows[3].GetProperty("pathAlignment").GetDouble() <= 0)
                throw new InvalidOperationException("Progress must be backed by measured advance along the authored route.");

            var unreachable = Run(frames, labels, new(120, 90))[0];
            if (unreachable.GetProperty("pathReachable").GetBoolean())
                throw new InvalidOperationException("The encoder replaced an unreachable fixed objective.");
            Check(unreachable, "shared_path_present", 0);
            var automatic = Run(frames, labels, null)[0];
            Check(automatic, "road_frontier", 1);
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static JsonElement[] Run(string frames, string labels, AtlasPoint? goal)
    {
        using var input = new StringReader(frames);
        using var output = new StringWriter();
        HackerGymObservationEncoder.Run(BotPolicyContract.LandingVersion, labels, input, output, fixedObjective: goal);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    }

    private static void Check(JsonElement row, string name, float expected)
    {
        var names = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!;
        var input = MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(row.GetProperty("input").GetString()!));
        var index = Array.IndexOf(names.ToArray(), name);
        if (input.Length != names.Count * BotRecordingContract.HistoryLength || index < 0
            || Math.Abs(input[3 * names.Count + index] - expected) > .000001)
            throw new InvalidOperationException($"Incorrect Hacker Gym feature {name}.");
    }

    private static ReceiverGroundTruthSample Frame(int index, double heroX)
    {
        const double scale = 20;
        const double heroY = 1.28;
        const int width = 192, height = 128;
        const double cameraY = 2;
        var cameraX = index * .15;
        ReceiverActorState Actor(int id, string name, double x, double y, double w, double h) =>
            new(id, name, (x - cameraX) * scale + width / 2d, (y - cameraY) * scale + height / 2d, w * scale, h * scale);
        var hero = Actor(1, "Knight", heroX, heroY, .5, heroY);
        return new(ReceiverProtocol.ControlVersion, "groundTruth", Episode, (ulong)index, index * 6, index * .1,
            "Tutorial_01", true, heroX, heroY, 0, index >= 3 ? 8.3 : 0, 0, true, true, index != 6,
            cameraX, cameraY, 0, cameraX, cameraY, 0, height / (2 * scale), scale, scale, hero.ScreenX, hero.ScreenY, width, height, width, height)
        {
            Actors = new(1, hero, [Actor(2, "Crawlid", heroX + 2.5, .5, .6, .8)],
                [Actor(3, "Geo", heroX + .9, .3, .2, .4)], [Actor(4, "Cocoon", heroX + 1.25, 1.4, .5, .5)])
        };
    }
}
