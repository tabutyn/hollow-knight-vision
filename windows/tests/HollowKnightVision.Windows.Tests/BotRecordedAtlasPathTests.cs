using System.Diagnostics;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotRecordedAtlasPathTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void TestRecoveryIsCausalAndUsesRecordedTerrain()
    {
        var observations = new[] { Row(1, 150), Row(1.1, 160), Row(1.2, 170) };
        var recovered = BotRecordedAtlasPath.Recover(observations, BotCaptureDomain.Gameplay);
        var prefix = BotRecordedAtlasPath.Recover(observations[..2], BotCaptureDomain.Gameplay);
        Check(recovered.Select(item => item.Index).SequenceEqual([0, 1, 2])
            && recovered.Take(2).SequenceEqual(prefix) && recovered[0].RoadHint?.Dx == 1
            && recovered[1].PathProgress is > .3 and < .32 && recovered[2].PathProgress is > .3 and < .32,
            "A later frame changed earlier evidence, or measured travel did not receive its course credit.");
    }

    internal static void TestMissingPoseBreaksProgressAndCurrentPathsArePreserved()
    {
        var recorded = new[]
        {
            Row(1, 150) with { AtlasPathVersion = 1 },
            Row(1.1, 160) with { BlockadePose = null },
            Row(1.2, 170),
            Row(1.3, 180) with { HackerPathVersion = 1 },
        };
        var recovered = BotRecordedAtlasPath.Recover(recorded, BotCaptureDomain.Gameplay);
        Check(recovered is [{ Index: 2, PathProgress: 0 }], "The replay overwrote current paths or scored motion across an unmeasured interval.");
    }

    internal static void TestReacquisitionDoesNotEarnAlreadyMeasuredDistance()
    {
        var recovered = BotRecordedAtlasPath.Recover([
            Row(1, 150), Row(1.1, 166), Row(1.2, 150) with { BlockadePose = null },
            Row(1.3, 150), Row(1.4, 160), Row(1.5, 176),
        ], BotCaptureDomain.Gameplay);
        Check(recovered.Select(item => item.PathProgress).SequenceEqual([0, .5, 0, 0, .3125]),
            "Reacquiring a camera granted progress for backing up and covering the same distance again.");
    }

    internal static void TestUnreliableTerrainAndStaleDetectionDoNotCarryIntoLaterFrames()
    {
        var noTerrain = Row(1.1, 160);
        var features = noTerrain.Features.ToArray();
        features[6] = 0;
        var observations = new[]
        {
            Row(1, 150), noTerrain with { Features = features }, Row(1.2, 170),
            Row(1.3, 180) with { DetectionTimestamp = .25 }, Row(1.4, 190),
            Row(1.5, 200) with { KnightPrediction = new(1.4, .1, .01, true, ["missing-knight"]) }, Row(1.6, 210),
        };
        var recovered = BotRecordedAtlasPath.Recover(observations, BotCaptureDomain.Gameplay);
        Check(recovered.Select(item => item.Index).SequenceEqual([0, 2, 4, 6])
            && recovered.All(item => item.PathProgress == 0), "Malformed observations affected the following recovered motion.");
    }

    internal static void TestHackerUsesTheRecordedGroundAndWorldPose()
    {
        var observations = new[] { Row(1, 150, authored: true), Row(1.1, 160, authored: true) };
        var recovered = BotRecordedAtlasPath.Recover(observations, BotCaptureDomain.Hacker);
        Check(recovered.Select(item => item.Index).SequenceEqual([0, 1]) && recovered[0].RoadHint?.Dx == 1
            && recovered.All(row => row.RoadHint?.Frontier == false), "The recovered Hacker route ignored its recorded terrain.");
        Check(BotRecordedAtlasPath.Recover(observations, BotCaptureDomain.Gameplay).Count == 0,
            "Game Play replay borrowed terrain or pose data from the Hacker domain.");
    }

    internal static void TestOnlyTheLongestUnplacedChartEarnsProgressInEachRoom()
    {
        var recovered = BotRecordedAtlasPath.Recover([
            Row(1, 150), Row(1.1, 166),
            Row(1.2, 480, space: "visual/1/1/2"), Row(1.3, 496, space: "visual/1/1/2"),
            Row(1.4, 512, space: "visual/1/1/2"), Row(1.5, 500, space: "visual/1/2/1") with { RoomId = 2 },
            Row(1.6, 516, space: "visual/1/2/1") with { RoomId = 2 },
        ], BotCaptureDomain.Gameplay);
        Check(recovered.Select(item => item.PathProgress).SequenceEqual([0, .5, 0, 0, .5, 0, .5])
            && recovered.All(row => row.RoadHint?.Dx == 1 && row.RoadHint.Frontier == false),
            "A new local chart gained duplicate travel credit or lost another room's independent progress.");
    }

    internal static void TestAnUnreachableUpperRightGoalDoesNotBecomeAnExplorationHint()
    {
        var lower = Row(1, 150);
        var frames = BotRecordedAtlasPath.Recover([lower with
        {
            DetectedTerrain = lower.DetectedTerrain! with { Floors = [new(0, 360, 80, 1), new(400, 640, 250, 1)] }
        }], BotCaptureDomain.Gameplay);
        Check(frames is [{ RoadHint: null, PathProgress: null }], "An unreachable endpoint became a substitute objective.");
    }

    internal static void TestFallbackKnightSizeKeepsTheOriginalGap()
    {
        var measured = Row(1, 150);
        measured = measured with
        {
            DetectedTerrain = measured.DetectedTerrain! with { Floors = [new(0, 200, 80, 1), new(218, 360, 80, 1)] }
        };
        var incomplete = measured.Features.ToArray();
        incomplete[3] = .01;
        var result = BotRecordedAtlasPath.Recover([measured with { Features = incomplete }], BotCaptureDomain.Gameplay);
        Check(result.Single().RoadHint?.Distance == 226d / (4 * 640),
            "The fallback Knight width filled a gap before a valid size was measured.");
    }

    internal static void TestRecordedPathReaderSupportsLegacyFeaturesAndChecksCountsWithoutChangingFiles()
    {
        using var temp = new TempDirectory();
        var path = temp.Directory.FullName;
        var source = Row(1, 150) with { Features = Row(1, 150).Features.Take(BotRecordingContract.LegacyWidth).ToArray() };
        var header = new
        {
            schemaVersion = 1, id = Guid.NewGuid(), status = "completed", observationCount = 1, inputEventCount = 1,
            featureNames = BotRecordingContract.FeatureNames.Take(BotRecordingContract.LegacyWidth)
        };
        var manifest = Path.Combine(path, "manifest.json");
        var events = Path.Combine(path, "events.jsonl");
        File.WriteAllText(manifest, JsonSerializer.Serialize(header, Json));
        File.WriteAllLines(events, [JsonSerializer.Serialize(source, Json),
            JsonSerializer.Serialize(new BotCaptureInput(1, "right", true), Json)]);
        var original = File.ReadAllBytes(events);
        Check(BotRecordedAtlasPath.Recover(path) is [{ RoadHint.Dx: 1 }]
            && File.ReadAllBytes(events).SequenceEqual(original), "A legacy clip could not be read without mutation.");
        File.WriteAllText(manifest, JsonSerializer.Serialize(header with { inputEventCount = 0 }, Json));
        try { _ = BotRecordedAtlasPath.Recover(path); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Inconsistent capture counts were accepted.");
    }

    internal static void TestHostExecutable(string executable)
    {
        TestHostCommands((arguments, output, error) =>
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native Host.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The native Host did not finish the recorded-path command.");
            }
            output.Write(stdout.GetAwaiter().GetResult());
            error.Write(stderr.GetAwaiter().GetResult());
            return process.ExitCode;
        });
    }

    private static void TestHostCommands(Func<string[], TextWriter, TextWriter, int> run)
    {
        using var temp = new TempDirectory();
        var root = temp.Directory.FullName;
        var folder = Path.Combine(root, "clip");
        Directory.CreateDirectory(folder);
        HackerGymObservationTests.Export(root);
        var labels = Path.Combine(root, "labels.json");
        var links = Path.Combine(root, "connections.json");
        File.WriteAllText(links, "{\"rooms\":{},\"scenes\":{}}");

        var id = Guid.NewGuid();
        var rows = Enumerable.Range(0, 6).Select(i => Row(1 + i * .1, 150 + i * 10)).ToArray();
        var manifest = new BotCaptureManifest(BotRecordingContract.SchemaVersion, Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, 1, "modded", "visual", "receiver", "completed", 1,
            rows.Length, rows.Length, 0, 0, "gameplay", id, null);
        var events = Path.Combine(folder, "events.jsonl");
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        File.WriteAllLines(events, rows.Select(row => JsonSerializer.Serialize(row, Json)));
        var original = File.ReadAllBytes(events);

        var actors = File.ReadLines(Path.Combine(root, "frames.jsonl")).Take(rows.Length).Select((line, index) =>
        {
            using var document = JsonDocument.Parse(line);
            var sample = document.RootElement.GetProperty("sample").Deserialize<ReceiverGroundTruthSample>(Json)!;
            return new RecordedGroundTruthSample(index * .1, rows[index].CapturedAt, sample);
        }).ToArray();
        var frames = actors.Select((actor, index) => new VisualReplayFrame(index, rows[index].CaptureTimestamp,
            rows[index].CapturedAt, index == 3 ? 2UL : 1UL, 192, 192, 128,
            HackerFrameSynchronizer.Key(actor.Sample.UnityFrame), null, null, "writerBackpressure")).ToArray();
        var summary = new RecordedVisualFrameArchive($"frames-{id:D}", rows.Length, 0, rows.Length, 0, 0, false);
        var archive = Directory.CreateDirectory(Path.Combine(root, summary.DirectoryName));
        File.WriteAllText(Path.Combine(archive.FullName, "manifest.json"), JsonSerializer.Serialize(
            new VisualReplayManifest(1, id, new Dictionary<string, string>(), frames, [], [], summary), Json));
        var replay = Path.Combine(root, "replay.json");
        new RecordedInputPathStore(root).Save(new RecordedInputPath(1, id, DateTimeOffset.UnixEpoch, 1,
            null, null, null, [], [], [.. actors, actors[2]], visualFrameArchive: summary), replay);

        WithOutput(["--recover-bot-atlas-path", folder], json =>
        {
            var row = json[1];
            Check(json.GetArrayLength() == 6 && row.GetProperty("pathProgress").GetDouble() > 0,
                "The native command lost causal recorded path evidence.");
        });
        WithOutput(["--recover-bot-exit-path", folder, replay, labels, links, "12", "1.28"], json =>
        {
            Check(json.GetArrayLength() == 6 && json[0].GetProperty("roadHint").GetProperty("dx").GetDouble() == 1
                && json[0].GetProperty("hackerPathVersion").GetInt32() == 1
                && json[2].GetProperty("roadHint").ValueKind == JsonValueKind.Null
                && json[3].GetProperty("roadHint").ValueKind == JsonValueKind.Null
                && json[4].GetProperty("pathProgress").GetDouble() == 0
                && json[5].GetProperty("pathProgress").GetDouble() > 0,
                "Exit reconstruction accepted ambiguous telemetry, crossed a capture generation, or lost later matched progress.");
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        Check(run(["--recover-bot-exit-path", folder, replay, labels, links, "NaN", "1"], output, error) == 64
            && string.IsNullOrEmpty(output.ToString()), "An invalid exit objective reached the route solver.");
        Check(run(["--recover-bot-atlas-path", Path.Combine(root, "absent")], output, error) == 1
            && File.ReadAllBytes(events).SequenceEqual(original), "The host changed the recorded capture or hid a missing input.");

        void WithOutput(string[] args, Action<JsonElement> assertion)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Check(run(args, output, error) == 0, error.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            assertion(document.RootElement);
        }
    }

    internal static BotCaptureObservation Row(double time, double x, bool authored = false, string? space = null)
    {
        var features = new double[BotRecordingContract.FeatureNames.Count];
        features[0] = x / 640;
        features[1] = (authored ? 112d : 96d) / 640;
        features[2] = 18d / 640;
        features[3] = 32d / 640;
        features[4] = .9;
        features[6] = 1;
        var geometry = new BotScreenTerrain(640, 360, [new(0, 640, 80, 1)], []);
        return new(time, time, time, 1, 1, 1, features, [], authored ? "human-authored" : "detected")
        {
            DetectionTimestamp = time,
            KnightSource = authored ? "hacker-frame" : "visual",
            AuthoredTerrain = authored ? geometry : null,
            DetectedTerrain = authored ? null : geometry,
            BlockadePose = new(space ?? (authored ? "hacker/room" : "visual/1/1/1"), 0, 0, authored ? 32 : 640),
            DriveDirection = 1,
        };
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TempDirectory : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("hkv-recorded-atlas-path-");
        public void Dispose() => Directory.Delete(recursive: true);
    }
}
