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
            var defaultGoal = Run(frames, labels, null)[0];
            Check(defaultGoal, "road_frontier", 0);
            Check(defaultGoal, "road_distance", 600 / (4f * 192));
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static void TestDesktopRouteIsSharedOnlyWithExactlyMatchedVisualRows()
    {
        var folder = Directory.CreateTempSubdirectory("hkv-shared-bot-path-");
        try { CheckDesktopRoute(folder.FullName); }
        finally { folder.Delete(recursive: true); }
    }

    private static void CheckDesktopRoute(string root)
    {
        var labels = new GroundTruthLineDocument
        {
            Lines = [new("Tutorial_01", -20, 30, 0, GroundTruthLineKind.Positive, false),
                new("Other", -20, 30, 0, GroundTruthLineKind.Positive, false)]
        };
        var visit = new BotHackerVisit(labels);
        var size = new WorldSize(192, 128);
        var objective = new BotAtlasObjective("Tutorial_01", new(12, 1.28));
        var otherSide = new BotAtlasObjective("Tutorial_01", new(-2, 1.28));
        var replay = new InputPathRecorder();
        var recording = Guid.NewGuid();
        replay.Start(0, id: recording);
        replay.RecordInput(RecordedGameButton.Right, true, 0);
        var capture = new BotCaptureSession(root, "modded", "visual", 1, 0, labels, recording, paired: true);

        var first = Record(0, 0, objective);
        var moving = Record(1, 1, objective);
        var turned = Record(2, 1, otherSide);
        if (first.RoadHint?.Dx != 1 || moving.PathProgress is not > 0 || moving.PathAlignment is not > 0
            || turned.RoadHint?.Dx != -1 || turned.PathProgress != 0 || turned.PathAlignment is not null)
            throw new InvalidOperationException("Both domains should receive the current objective and only credit actual progress.");

        var missing = Frame(3, 1);
        if (visit.Observe(size, HackerFrameSynchronizer.Key(missing.UnityFrame - 1), missing, new(.32, .321, 1, 1, 0), InputButtons.Right, 3,
            objective: objective) is not null)
            throw new InvalidOperationException("The route borrowed a different rendered frame.");
        var afterGap = Record(4, 2, otherSide);
        if (afterGap.PathAlignment is not null)
            throw new InvalidOperationException("A missing match must interrupt movement credit.");
        var elsewhere = Frame(5, 2) with { SceneName = "Other" };
        var otherRoom = visit.Observe(size, HackerFrameSynchronizer.Key(elsewhere.UnityFrame), elsewhere,
            new(.52, .521, 1, 2, 0), InputButtons.Right, 5, objective: objective);
        if (otherRoom is not { RoadHint: null, AtlasView.Camera: [0, 0] })
            throw new InvalidOperationException("A different room cannot reuse the active target or the previous room's footprint.");

        var result = capture.FinishAsync(.8, replay.Stop(.8), completed: true).GetAwaiter().GetResult();
        foreach (var manifest in result.Published)
        {
            var events = Path.Combine(root, BotCaptureSession.DirectoryName(manifest.Id), "events.jsonl");
            using var line = JsonDocument.Parse(File.ReadLines(events).First());
            if (!line.RootElement.TryGetProperty("roadHint", out var hint) || hint.GetProperty("dx").GetDouble() != 1)
                throw new InvalidOperationException("The shared evidence was lost when the paired clip was saved.");
        }
        if (result.Published.Count != 2) throw new InvalidOperationException("Both routed clips must be published.");

        BotCaptureObservation Record(int index, double x, BotAtlasObjective target)
        {
            var sample = Frame(index, x) with { AtlasGameplayActive = true };
            var time = index * .1 + .02;
            var frame = new BgraFrame(192, 128, 768, new byte[192 * 128 * 4], index, DateTimeOffset.UnixEpoch.AddSeconds(time));
            var clock = new BotCaptureTiming(time, time + .001, 1, 1, 0);
            var exact = visit.Observe(size, HackerFrameSynchronizer.Key(sample.UnityFrame), sample, clock,
                InputButtons.Right, index, health: 5, objective: target)!;
            var detector = new LiveObjectDetectionBatch(
                [new ObjectDetection("game.playable-knight", new(.2, .3, .1, .22), .9f) { SourceFrameIdentifier = index }],
                index, (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds, 1, 0);
            var visual = BotObservationBuilder.Gameplay(frame, clock, detector, [new(90, new(1, 180))], [],
                terrainAnalyzed: true, new(10, 20), null, null, InputButtons.Right);
            var paired = visual.WithSharedHackerPath(exact);
            var stale = visual.WithSharedHackerPath(exact with { Generation = 2 });
            var vector = new BotObservationHistory(BotPolicyContract.LandingVersion)
                .Append(paired, new(2, 0, 0, 0), time + .01)!;
            var names = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!.ToList();
            if (paired.RoadHint != exact.RoadHint || paired.PathProgress != exact.PathProgress || paired.PathAlignment != exact.PathAlignment
                || paired.Features != visual.Features || paired.AuthoredTerrain is not null || paired.AtlasView is not null
                || paired.MeasuredHealth is not null || paired.EnemyOutcomes is not null || stale.RoadHint is not null
                || vector[3 * names.Count + names.IndexOf("shared_path_dx")] != exact.RoadHint?.Dx)
                throw new InvalidOperationException("Game Play must get the exact shared route without borrowing receiver perception.");
            if (!capture.TryRecord(frame, paired, exact))
                throw new InvalidOperationException("The paired route clip rejected a fresh observation.");
            return exact;
        }
    }

    internal static void TestClearedHackerObjectiveUsesRegisteredFrontierAndCreditsTravelOnce()
    {
        var labels = new GroundTruthLineDocument { Lines = [new("Tutorial_01", 0, 25, 0, GroundTruthLineKind.Positive, false)] };
        var uncovered = new BotHackerVisit(labels);
        var visit = new BotHackerVisit(labels);
        var registeredCells = System.Collections.Immutable.ImmutableHashSet.CreateRange(Enumerable.Range(0, 9).Select(x => (x, 0)));
        var saved = new HackerAtlasRoom(Guid.NewGuid(), "Tutorial_01", new(700, 20), default)
        {
            BotCoverage = new(registeredCells, 0, 1),
            TerrainProjection = new(new(20, 20), new(10, 10))
        };
        var local = Observe(uncovered, 0, 1, automatic: true, registered: null);
        var restored = Observe(visit, 0, 1, automatic: true, registered: saved);
        if (local.RoadHint is not { Frontier: true, Dx: 1 } first || restored.RoadHint is not { Frontier: true, Dx: 1 } withArchive
            || withArchive.Distance - first.Distance < .3 || restored.PathAlignment is not null)
            throw new InvalidOperationException("After clearing the objective the new frontier must stay on seen ground, including saved Atlas cells.");

        var forward = Observe(visit, 1, 2, automatic: true, registered: saved);
        var backward = Observe(visit, 2, 1, automatic: true, registered: saved);
        if (forward.PathProgress is not > 0 || forward.PathAlignment is not > 0 || backward.PathProgress != 0 || backward.PathAlignment is not < 0)
            throw new InvalidOperationException("Only movement toward the previous frontier earns progress. Opposite movement earns negative alignment.");
        var fixedAgain = Observe(visit, 3, 1, automatic: false, registered: saved);
        var automaticAgain = Observe(visit, 4, 1, automatic: true, registered: saved);
        var repeated = Observe(visit, 5, 2, automatic: true, registered: saved);
        if (fixedAgain.RoadHint?.Frontier != false || automaticAgain.PathAlignment is not null || automaticAgain.RoadHint?.Frontier != true
            || repeated.PathProgress != 0 || repeated.PathAlignment is not > 0)
            throw new InvalidOperationException("Changing objectives must clear the movement sample without re-crediting the same directed travel.");

        var thinking = new BotHackerVisit(labels, includeThinkingRoutes: true);
        _ = Observe(thinking, 0, 4, automatic: false, registered: saved,
            objective: new BotAtlasObjective("Tutorial_01", new(1, 1.28)));
        if (thinking.ThinkingRoutes is not [{ Road.Frontier: true } frontier, { Road.Frontier: false } fixedRoute]
            || frontier.Objective == fixedRoute.Objective)
            throw new InvalidOperationException("Thinking must offer the registered frontier and the independent fixed route. "
                + string.Join(", ", thinking.ThinkingRoutes));
        _ = Observe(thinking, 1, 1, automatic: true, registered: saved);
        if (thinking.ThinkingRoutes is not [{ Road.Frontier: true }])
            throw new InvalidOperationException("A cleared fixed goal must leave a single traversal offer.");

        BotCaptureObservation Observe(BotHackerVisit observer, int index, double x, bool automatic,
            HackerAtlasRoom? registered, BotAtlasObjective? objective = null)
        {
            var sample = Frame(index, x) with { Grounded = true };
            var time = index * .1 + .02;
            return observer.Observe(new(192, 128), HackerFrameSynchronizer.Key(sample.UnityFrame), sample,
                new(time, time + .001, 1, 1, 0), InputButtons.Right, index, objective: objective,
                automaticObjective: automatic, registeredRoom: registered) ?? throw new InvalidOperationException("Missing test observation.");
        }
    }

    internal static void TestVisualCoverageDoesNotCreditMasksOrPlacementGaps()
    {
        var visit = new BotVisualAtlasCoverage();
        var size = new WorldSize(192, 128);
        var pose = new AtlasPoint(0, 0);
        visit.Register(pose, size, [new(0, 0, 64, 128)]);
        if (visit.View(pose, size, 1, null) is not { CoveredCells: 4, Health: null, EarnedCells: 0 })
            throw new InvalidOperationException("A footprint must omit hidden pixels and preserve unknown health.");
        visit.Register(new(64, 0), size, []);
        if (visit.View(new(64, 0), size, 2, 3) is not { CoveredCells: 6, Health: 3, EarnedCells: 2 })
            throw new InvalidOperationException("Measured motion should credit each newly exposed cell once.");
        visit.Interrupt();
        visit.Register(new(128, 0), size, []);
        if (visit.View(null, size, 3, 3) is not null
            || visit.View(new(128, 0), size, 3, 3) is not { CoveredCells: 8, EarnedCells: 2 })
            throw new InvalidOperationException("An unplaced frame cannot claim coverage or travel credit.");
        visit.Reset();
        if (visit.View(pose, size, 4, 3) is not null)
            throw new InvalidOperationException("A new room cannot inherit old visible cells.");
    }

    internal static void TestSignedRouteMotionClearsAtObjectiveAndTrackingChanges()
    {
        var labels = new GroundTruthLineDocument { Lines = [new("Tutorial_01", 0, 25, 0, GroundTruthLineKind.Positive, false)] };
        var visit = new BotHackerVisit(labels);
        var right = new BotAtlasObjective("Tutorial_01", new(8, 1.28));
        var left = new BotAtlasObjective("Tutorial_01", new(0, 1.28));
        const double knightHeight = 26;

        Check(AlignmentAt(0, 1.5, right), null);
        Check(AlignmentAt(1, 1.8, right), 6 / knightHeight);
        Check(AlignmentAt(2, 1.8, right), 0);
        Check(AlignmentAt(3, 1.6, right), -4 / knightHeight);
        Check(AlignmentAt(4, 1.5, left), null);
        Check(AlignmentAt(5, 1.25, left), 5 / knightHeight);
        if (Observe(6, 1.15, left) is { PathAlignment: not null } or { RoadHint: not null })
            throw new InvalidOperationException("A missing camera cannot supply alignment or guidance.");
        Check(AlignmentAt(7, 1, left), null);
        Check(AlignmentAt(13, .5, left), null);
        Check(AlignmentAt(14, .4, left), 2 / knightHeight);

        double? AlignmentAt(int index, double x, BotAtlasObjective target) =>
            (Observe(index, x, target) ?? throw new InvalidOperationException($"Missing observation at route step {index}.")).PathAlignment;

        BotCaptureObservation? Observe(int index, double x, BotAtlasObjective target)
        {
            // Frame six drops the camera and all other frames carry measured actor and camera positions.
            var sample = Frame(index, x) with { Grounded = true };
            var time = index * .1 + .02;
            return visit.Observe(new(192, 128), HackerFrameSynchronizer.Key(sample.UnityFrame), sample,
                new(time, time + .001, 1, 1, 0), InputButtons.Left, index, objective: target);
        }

        static void Check(double? actual, double? expected)
        {
            if (expected is { } value ? actual is null || Math.Abs(actual.Value - value) > .000001 : actual is not null)
                throw new InvalidOperationException($"Expected path alignment {expected?.ToString() ?? "absent"}, got {actual?.ToString() ?? "absent"}.");
        }
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
        var scale = 20 + index * .000013;
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
