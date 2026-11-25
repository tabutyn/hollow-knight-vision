using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotHumanGroundProjectionTests
{
    private const string Scene = "Tutorial_01";
    private static readonly WorldSize Size = new(100, 80);
    private static readonly GroundTruthCameraTransform Camera = new(Scene, new(5, 5), 10, 20, Size);
    private static readonly BotCaptureTiming Timing = new(100, 100.02, 1, 1, 0);
    private static readonly ReceiverGroundTruthSample Sample = new(
        2, "groundTruth", Guid.NewGuid(), 10, 10, 100, Scene, true, 5, 5, 0, 0, 0, true, true, true,
        5, 5, -38, 5, 5, 0, 2, 10, 20, 40, 40, 100, 80, 100, 80)
    {
        AtlasGameplayActive = true,
        Actors = new(1, new(1, "Knight", 40, 40, 10, 20), [new(2, "Crawler", 70, 30, 12, 8)])
    };

    internal static void TestCameraProjectionClipsOnlyVisibleAuthoredGround()
    {
        var labels = new GroundTruthLineDocument
        {
            Lines = [Floor(), Floor(scene: "Tutorial_02"), Floor(kind: GroundTruthLineKind.Negative),
                Floor(y: 100), Floor(y: double.NaN), Floor(-20, 20, 5), Floor(y: 4.5, seeded: true)]
        };
        var observed = BotObservationBuilder.ProjectAuthored(labels, Camera);
        Require(observed.Floors.SequenceEqual([new(10, 90, 20, 1), new(0, 100, 40, 1)]),
            "Projection must clip ground and exclude other rooms, negatives, nonfinite and detector-seeded marks.");
        var twice = BotObservationBuilder.Hacker(new WorldSize(200, 160), 10, Sample, labels, Timing, InputButtons.None);
        Require(twice?.AuthoredTerrain is { Width: 200, Height: 160 } terrain
            && terrain.Floors.SequenceEqual([new(20, 180, 40, 1), new(0, 200, 80, 1)]),
            "The receiver transform must independently scale both pixel axes with the actual capture.");

        var features = new double[BotRecordingContract.FeatureNames.Count];
        BotObservationBuilder.WriteFloorFeatures(features, [observed.Floors[0]], 45, 40, Size.Width);
        Require(features.AsSpan(16, 5).SequenceEqual([-.35, .45, -.2, 1, 1]),
            "The common terrain encoder must preserve the projected coordinates relative to the supplied Knight.");
    }

    internal static void TestHackerWallCoordinatesAndPolicySlots()
    {
        var labels = new GroundTruthLineDocument
        {
            Lines = [Floor()],
            Walls = [Wall(1), Wall(3), Wall(8), Wall(11), Wall(4, "other"), Wall(5, seeded: true)]
        };
        var row = Observe(labels);
        Require(row.CanTrain && row.AuthoredTerrain is { Floors.Count: 1 } terrain
            && terrain.Walls.SequenceEqual([new(10, 0, 60, 0), new(30, 0, 60, 0), new(80, 0, 60, 0)]),
            "The exact receiver frame must clip the visible authored walls without assigning a solid side.");
        double[] expected = [1, -.1, -.4, .2, 0, 1, .4, -.4, .2, 0, 1];
        Require(row.Features.Skip(144).Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-10)
            && row.Features.Count == 144 + expected.Length, "The policy wall slots must choose the closest face on each side.");
        Require(Reopen(row).AuthoredTerrain!.Walls.SequenceEqual(row.AuthoredTerrain!.Walls),
            "The clip representation should preserve both endpoints of each authored wall.");
        labels.Walls = null;
        var noWalls = Observe(labels);
        Require(noWalls.AuthoredTerrain?.Walls.Count == 0 && noWalls.Features.Skip(145).All(value => value == 0),
            "A new observation with no authored walls must leave all nearest-wall slots empty.");
    }

    internal static void TestHackerDoorAndSpikeAxesReachCurrentPolicyInputs()
    {
        var labels = new GroundTruthLineDocument
        {
            Lines = [Floor()],
            Doors = [new(Scene, 1, 5, 4, GroundTruthLineKind.Door, false),
                new GroundTruthWorldLine(Scene, 8, 8, 3, GroundTruthLineKind.DoorVertical, false) { EndWorldY = 6 },
                new("other", 1, 5, 4, GroundTruthLineKind.Door, false)],
            Spikes = [Floor(kind: GroundTruthLineKind.Spike)]
        };
        var row = Observe(labels);
        Require(row.AuthoredTerrain is { Doors: { } doors, Spikes: { } spikes }
            && doors.SequenceEqual([new(10, 20, 50, 20), new(80, 0, 80, 60)])
            && spikes.SequenceEqual([new(10, 20, 90, 20)]),
            "The receiver projection must preserve clipped horizontal and vertical doors and horizontal spikes.");
        var fields = BotPolicyContract.Names(BotPolicyContract.CurrentVersion)!.ToList();
        var input = new BotObservationHistory(BotPolicyContract.CurrentVersion).Append(row, default, Timing.AvailableAt)!;
        Require(Value("marking_door_known") == 1 && Value("marking_spike_known") == 1,
            "Authored door and spike geometry must reach the current policy.");
        var saved = Reopen(row).AuthoredTerrain!;
        Require(saved.Doors!.SequenceEqual(row.AuthoredTerrain!.Doors!) && saved.Spikes!.SequenceEqual(row.AuthoredTerrain.Spikes!),
            "The wire format must preserve the authored door axes and spike extent.");

        float Value(string name) => input[input.Length - fields.Count + fields.IndexOf(name)];
    }

    internal static void TestInvalidCameraCannotProjectAuthoredGeometry()
    {
        var labels = new GroundTruthLineDocument { Lines = [Floor()] };
        foreach (var invalid in new[]
        {
            Camera with { PixelsPerWorldUnitX = -10 },
            Camera with { PixelsPerWorldUnitY = 0 },
            Camera with { CameraWorld = new(double.PositiveInfinity, 5) },
            Camera with { FrameSize = new WorldSize(double.PositiveInfinity, 80) }
        })
        {
            var geometry = BotObservationBuilder.ProjectAuthored(labels, invalid);
            Require(geometry.Floors.Count == 0 && geometry.Walls.Count == 0,
                "An invalid camera must not mirror or collapse an authored floor into the policy input.");
        }
    }

    internal static void TestHackerDistinguishesMalformedActorsFromMissingTelemetry()
    {
        var labels = new GroundTruthLineDocument { Lines = [Floor()] };
        var oversized = Sample with
        {
            Actors = Sample.Actors! with
            {
                Enemies = Enumerable.Range(2, 65).Select(id => new ReceiverActorState(id, "Crawler", 70, 30, 12, 8)).ToArray()
            }
        };
        var malformed = BotObservationBuilder.Hacker(Size, 10, oversized, labels, Timing, InputButtons.None);
        var absent = BotObservationBuilder.Hacker(Size, 10, Sample with { Actors = null }, labels, Timing, InputButtons.None);
        Require(malformed is { CanTrain: false } && malformed.Issues.Contains("invalid-hacker-actors")
            && absent is { CanTrain: false } && absent.Issues.Contains("missing-hacker-actor-telemetry"),
            "An oversized actor collection must be rejected as malformed, distinct from missing receiver data.");
        var invalidPosition = Sample with
        {
            Actors = Sample.Actors! with { Enemies = [new(2, "Crawler", double.NaN, 30, 12, 8)] }
        };
        var invalid = BotObservationBuilder.Hacker(Size, 10, invalidPosition, labels, Timing, InputButtons.None);
        var missingHero = BotObservationBuilder.Hacker(Size, 10, Sample with { HeroAvailable = false }, labels, Timing, InputButtons.None);
        var stale = BotObservationBuilder.Hacker(Size, 10, Sample, labels, Timing with { AvailableAt = 100.5 }, InputButtons.None);
        Require(invalid is { CanTrain: false } && invalid.Issues.Contains("invalid-hacker-actors")
            && missingHero is { CanTrain: false } && missingHero.Issues.Contains("missing-hacker-knight")
            && stale is { CanTrain: false } && stale.Issues.Contains("stale-capture")
            && BotObservationBuilder.Hacker(Size, 11, Sample, labels, Timing, InputButtons.None) is null,
            "Invalid, missing, stale and mismatched receiver observations cannot drive policy.");
    }

    internal static void TestMalformedDimensionsCannotSupplyBotGeometry()
    {
        var labels = new GroundTruthLineDocument { Lines = [Floor()] };
        foreach (var size in new WorldSize[] { new(1e30, 1e30), new(100, 65_537), new(double.NaN, 80), new(100, double.PositiveInfinity) })
            Require(BotObservationBuilder.Hacker(size, 10, Sample, labels, Timing, InputButtons.None) is null,
                "Malformed Hacker dimensions must be rejected before projection.");
        Require(BotObservationBuilder.Hacker(new WorldSize(65_536, 80), 10, Sample, labels, Timing, InputButtons.None) is
            { CanTrain: true, AuthoredTerrain.Width: 65_536 }, "The supported dimension boundary should retain the valid observation.");

        foreach (var (width, height) in new[] { (65_537, 2), (2, 65_537) })
        {
            var frame = new BgraFrame(width, height, width * 4, new byte[width * height * 4], 1, DateTimeOffset.UnixEpoch);
            var detections = new LiveObjectDetectionBatch(
                [new ObjectDetection("game.playable-knight", new(.4, .4, .1, .2), .9f) { SourceFrameIdentifier = 1 }],
                1, 0, Timing.Generation, 0);
            var row = BotObservationBuilder.Gameplay(frame, Timing, detections, [new(1, new(0, 1))], [],
                true, default, null, null, InputButtons.None);
            Require(!row.CanTrain && row.Issues.SequenceEqual(["invalid-frame-size"]) && row.DetectedTerrain is null
                && row.Features[4] == 0 && row.Features[6] == 0 && row.Features.All(double.IsFinite),
                "An oversized detector frame must not become eligible even with a matched Knight and floor.");
        }
    }

    private static BotCaptureObservation Observe(GroundTruthLineDocument labels) =>
        BotObservationBuilder.Hacker(Size, 10, Sample, labels, Timing, InputButtons.None)
            ?? throw new InvalidOperationException("The exact Hacker fixture could not be observed.");

    private static BotCaptureObservation Reopen(BotCaptureObservation row) =>
        JsonSerializer.Deserialize<BotCaptureObservation>(JsonSerializer.Serialize(row, BotCaptureSession.Json), BotCaptureSession.Json)!;

    private static GroundTruthWorldLine Floor(double left = 1, double right = 9, double y = 4, string scene = Scene,
        GroundTruthLineKind kind = GroundTruthLineKind.Positive, bool seeded = false) => new(scene, left, right, y, kind, seeded);

    private static GroundTruthWorldLine Wall(double x, string scene = Scene, bool seeded = false) =>
        new(scene, x, x, 2, GroundTruthLineKind.Wall, seeded) { EndWorldY = 6 };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
