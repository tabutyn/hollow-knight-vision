using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotAtlasRunTests
{
    internal static void TestDamageDwellAndPathProgress()
    {
        var run = new BotAtlasRun();
        for (var tick = 0; tick <= 28; tick++)
        {
            var time = tick / 10d;
            var view = new BotAtlasView(1, 1, [0, 0], 640, 40, tick > 4 ? 112 : 100, 3,
                tick >= 22 ? 4 : tick == 7 ? 3 : 5);
            run.Observe(Row(time) with
            {
                AtlasView = view, HackerPathVersion = 1,
                PathProgress = tick == 5 ? .75 : 0,
                PathAlignment = tick == 9 ? -.5 : 5
            }, InputButtons.Right, InputButtons.Right, "first");
            if (tick < 25) Check(run.Score.Damage == 0, "A brief stencil change must not count as damage.");
        }
        Check(run.Score is { Damage: 1, NewCells: 12, DwellDecisions: 9, PathProgress: .75, PathAlignment: -.5 }
            && Math.Abs(run.Score.Reward - (2 - 32 - 9 * .12)) < 1e-8,
            "The Windows Atlas score must match the Mac damage, dwell and path weights.");

        run.Observe(Row(2.9) with { AtlasView = new(1, 1, [0, 0], 640, 300, 300, 4, 1) },
            InputButtons.Right, InputButtons.Right, "next");
        run.Observe(Row(3.0), InputButtons.None, InputButtons.Right, "next");
        run.Observe(Row(3.1) with { AtlasView = new(1, 1, [0, 0], 640, 301, 301, 4, 1) },
            InputButtons.Right, InputButtons.None, "next");
        Check(run.Score is { Damage: 1, NewCells: 12, DwellDecisions: 9 },
            "A room transition and a missing Atlas view must start a new baseline.");
    }

    internal static void TestOutcomesAndHeldAttack()
    {
        var epoch = Guid.NewGuid();
        var run = new BotAtlasRun();
        var previous = InputButtons.None;
        const InputButtons attack = InputButtons.Right | InputButtons.ActionX;
        foreach (var (time, buttons, gained) in new (double, InputButtons, int?)[]
        {
            (1, attack, 0), (1.1, attack, 2), (1.2, attack, 2), (1.3, InputButtons.Right, 1),
            (1.4, attack, 2), (1.5, InputButtons.None, null), (1.6, attack, 5)
        })
        {
            var row = Row(time) with { BlueHealthOutcomes = gained is { } value ? new(epoch, value) : null };
            run.Observe(row, buttons, previous, null);
            run.Observe(row, buttons, previous, null);
            previous = buttons;
        }
        Check(run.Score is { BlueHealthGained: 2, HeldAttackDecisions: 2 }
            && Math.Abs(run.Score.Reward - 1.9) < 1e-8,
            "Duplicate or regressed blue-health counters and the initial attack press must not count twice.");
    }

    internal static void TestLegacyAtlasScore()
    {
        var score = JsonSerializer.Deserialize<BotAtlasScore>(
            """{"newCells":1,"damage":2,"dwellDecisions":3}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(score is { NewCells: 1, Damage: 2, DwellDecisions: 3, BlueHealthGained: 0, HeldAttackDecisions: 0 },
            "Older Atlas scores must load with default counters for blue health and held attack.");
    }

    internal static void TestHackerCommittedCoverage()
    {
        const int extent = LiveTiledAtlas.TileSize;
        var alpha = new byte[extent * extent * 4];
        for (var y = extent / 2; y < extent; y++)
        for (var x = 0; x < extent; x++)
            alpha[(y * extent + x) * 4 + 3] = 128;
        var image = new BgraFrame(extent, extent, extent * 4, alpha, 1, DateTimeOffset.UnixEpoch);
        LiveTiledAtlasTile Tile(int x) => new(x, x, x, 0, image, new(x * extent, 0, extent, extent), []);

        var coverage = new BotHackerAtlasCoverage();
        var restored = new LiveTiledAtlasSnapshot([Tile(0)], 1, default, default);
        var size = new WorldSize(640, 360);
        Check(coverage.Snapshot.View(default, size, null) is null, "An absent Atlas should not create coverage.");
        coverage.Update(restored);
        coverage.Update(restored, default(AtlasPoint), 640);
        var baseline = coverage.Snapshot;
        var extended = restored with { Tiles = [Tile(0), Tile(1)], Revision = 2 };
        coverage.Update(extended, new(100, 0), 640);
        coverage.Update(extended, default(AtlasPoint), 640);
        Check(baseline.View(default, size, 5) is { CoveredCells: 8, EarnedCells: 0, Health: 5 }
            && !baseline.Cells.Contains((4, 0))
            && coverage.Snapshot.View(default, size, null) is { CoveredCells: 16, EarnedCells: 8 },
            "Only new opaque tile cells reached through measured motion should earn credit.");

        coverage.Interrupt();
        coverage.Update(extended with { Tiles = [Tile(0), Tile(1), Tile(2)], Revision = 3 }, new(100, 0), 640);
        Check(coverage.Snapshot.View(default, size, null) is { CoveredCells: 24, EarnedCells: 8 },
            "The first placement after returning to a room cannot earn points from a stale camera.");
        var restoredPipeline = HackerAtlasPipeline.FromArchive(new(
            [new(Guid.NewGuid(), "first", new(10, 20), new(5, 8), new(new(1, 1), default),
                new(0, 0, 256, 128), [new(new(0, 0), image)])], [], new Dictionary<string, AtlasPoint>(),
            1, DateTimeOffset.UnixEpoch));
        var room = restoredPipeline.Snapshot().Rooms.Single();
        var camera = new GroundTruthCameraTransform("first", new(40, 60), 1, 1, new(320, 180));
        var view = room.BotView(camera, 5);
        Check(view is { CoveredCells: 8, EarnedCells: 0, Width: 640, Health: 5 }
            && view.Camera.SequenceEqual([50d, 64d])
            && room.BotView(camera with { SceneName = "elsewhere" }, 5) is null,
            "Restored Hacker tiles must be queried using their room-local camera basis.");
    }

    internal static void TestAtlasMapAndHealthReachPolicyInputs()
    {
        const double time = 1;
        var view = BotAtlasFrontiers.View(default, new(640, 360), revision: 1, health: 4,
            covered: 20, earned: 0, contains: (x, _) => x is >= 0 and < 40);
        Check(view.Left > view.Right && view.Missing is { Count: 15 } grid && grid[5] == 1 && grid[9] == 0,
            "Known space to the right must bias the frontier left and populate the missing-area grid.");
        var features = new double[BotRecordingContract.FeatureNames.Count];
        features[4] = features[6] = 1;
        var row = Row(time) with { Features = features, AtlasView = view, DetectionTimestamp = time };
        float[] expected = [1, (float)view.Left, (float)view.Right, 1, .25f, 1, .. view.Missing!.Select(value => (float)value)];
        foreach (var version in new[] { 7, BotPolicyContract.CurrentVersion, BotPolicyContract.LandingVersion })
        {
            var schema = BotPolicyContract.Names(version)!;
            var start = schema.ToList().IndexOf("atlas_present");
            var input = new BotObservationHistory(version).Append(row, default, time);
            Check(input is not null && input.Length == BotRecordingContract.HistoryLength * schema.Count
                && start >= 0 && input.AsSpan(input.Length - schema.Count + start, expected.Length).SequenceEqual(expected),
                $"The v{version} actor must receive the frontier, normalized health, and complete missing-area grid.");
        }
    }

    private static BotCaptureObservation Row(double time) =>
        new(time, time, time, 1, 0, 1, new double[BotRecordingContract.LegacyWidth], [], "human-authored");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
