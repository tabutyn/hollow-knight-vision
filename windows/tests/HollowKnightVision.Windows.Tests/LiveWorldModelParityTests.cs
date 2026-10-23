using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.Core;

internal static class LiveWorldModelParityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static void Check(bool value, string message = "Live-world model parity assertion failed.")
    { if (!value) throw new Exception(message); }

    public static void TestRoundTripAndPoseReplacementPreserveRawEvidence()
    {
        var original = Fixture();
        var encoded = JsonSerializer.SerializeToUtf8Bytes(original, JsonOptions);
        var decoded = JsonSerializer.Deserialize<LiveWorldSnapshot>(encoded, JsonOptions)
            ?? throw new Exception("Live-world snapshot did not decode.");
        CheckSnapshotsEqual(decoded, original);

        var replaced = original.ReplaceOptimizedPoses(
            new Dictionary<int, AtlasPoint>
            {
                [10] = new(0, 0),
                [11] = new(5, -2)
            },
            7);
        Check(replaced.MapRevision == 8);
        Check(replaced.Observations.Select(value => value.RawPose)
            .SequenceEqual(original.Observations.Select(value => value.RawPose)));
        Check(replaced.Observations.Select(value => value.LocalPose)
            .SequenceEqual(original.Observations.Select(value => value.LocalPose)));
        Check(replaced.Observations.Select(value => value.LocalEpoch)
            .SequenceEqual(original.Observations.Select(value => value.LocalEpoch)));
        Check(replaced.Observations.Select(value => value.SourceObservationId)
            .SequenceEqual(original.Observations.Select(value => value.SourceObservationId)));
        Check(replaced.Keyframes.SequenceEqual(original.Keyframes));
        Check(replaced.Landmarks.Select(value => value.Descriptor)
            .Zip(original.Landmarks.Select(value => value.Descriptor))
            .All(pair => pair.First.SequenceEqual(pair.Second)));
        Check(replaced.Landmarks[2].WorldPoint == new AtlasPoint(40, 20));
        Check(replaced.Landmarks[3].WorldPoint == new AtlasPoint(44.5, 18));
        Check(replaced.RelativeMotionEdges.SequenceEqual(original.RelativeMotionEdges));
        CheckLoopEdgesEqual(replaced.LoopClosureEdges, original.LoopClosureEdges);
        Check(replaced.Observations.Select(value => value.OptimizedPose)
            .SequenceEqual([new AtlasPoint(0, 0), new AtlasPoint(5, -2)]));
    }

    public static void TestLegacyObservationDecodesIntoEpochZeroWithoutMovingItsWorldPose()
    {
        var current = Fixture();
        var root = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions))?.AsObject()
            ?? throw new Exception("Live-world snapshot did not encode as an object.");
        var observations = root["observations"]?.AsArray()
            ?? throw new Exception("Live-world observations were not encoded.");
        foreach (var node in observations)
        {
            var observation = node?.AsObject()
                ?? throw new Exception("Live-world observation did not encode as an object.");
            observation.Remove("localPose");
            observation.Remove("captureGeneration");
            observation.Remove("localEpoch");
            observation.Remove("basisRevision");
        }

        var migrated = JsonSerializer.Deserialize<LiveWorldSnapshot>(root.ToJsonString(), JsonOptions)
            ?? throw new Exception("Legacy live-world snapshot did not decode.");
        Check(migrated.Observations.Select(value => value.LocalPose)
            .SequenceEqual(current.Observations.Select(value => value.RawPose)),
            "Legacy observations did not restore local pose from raw pose.");
        Check(migrated.Observations.Select(value => value.CaptureGeneration).SequenceEqual([0UL, 0UL]));
        Check(migrated.Observations.Select(value => value.LocalEpoch).SequenceEqual([0UL, 0UL]));
        Check(migrated.Observations.Select(value => value.BasisRevision).SequenceEqual([0UL, 0UL]));
    }

    public static void TestInvalidModelsAreRejected()
    {
        var observation = MakeObservation(10, 100);
        var error = Throws(() => _ = new LiveWorldSnapshot(schemaVersion: 99));
        Check(error.Error == LiveWorldModelError.UnsupportedSchema && error.RelatedId == 99);

        error = Throws(() => _ = new LiveWorldSnapshot(observations: [observation]));
        Check(error.Error == LiveWorldModelError.UnanchoredWorld);

        error = Throws(() => _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation, observation]));
        Check(error.Error == LiveWorldModelError.DuplicateObservationId && error.RelatedId == 10);

        error = Throws(() => _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation],
            keyframes: [new LiveWorldKeyframe(1, 99, 100)]));
        Check(error.Error == LiveWorldModelError.DanglingObservationId && error.RelatedId == 99);

        error = Throws(() => _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation],
            keyframes: [new LiveWorldKeyframe(1, 10, 999)]));
        Check(error.Error == LiveWorldModelError.InvalidGeometry);

        error = Throws(() => _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [observation],
            relativeMotionEdges: [new LiveWorldRelativeMotionEdge(1, 10, 99, 1, 0, 1)]));
        Check(error.Error == LiveWorldModelError.DanglingObservationId && error.RelatedId == 99);

        var invalid = LiveWorldObservation.Create(
            10, 100, double.NaN, 640, 360, 640, 240, [],
            new AtlasPoint(0, 0), new AtlasPoint(0, 0));
        error = Throws(() => _ = new LiveWorldSnapshot(
            anchoredObservationId: 10,
            observations: [invalid]));
        Check(error.Error == LiveWorldModelError.InvalidGeometry);
    }

    public static void TestStaleOrIncompleteReplacementIsRejected()
    {
        var world = Fixture();
        var error = Throws(() => _ = world.ReplaceOptimizedPoses(
            new Dictionary<int, AtlasPoint>
            {
                [10] = new(0, 0),
                [11] = new(0, 0)
            },
            6));
        Check(error.Error == LiveWorldModelError.StaleRevision
            && error.ExpectedRevision == 7 && error.ActualRevision == 6);

        error = Throws(() => _ = world.ReplaceOptimizedPoses(
            new Dictionary<int, AtlasPoint> { [10] = new(0, 0) },
            7));
        Check(error.Error == LiveWorldModelError.InvalidRevision);
    }

    public static void TestRoomTranslationMovesOnlySelectedRoomOptimizedEvidence()
    {
        var roomOne = MakeObservation(1, 1, raw: new(-180, 0), optimized: new(-180, 0), roomId: 0);
        var roomTwo = MakeObservation(2, 2, raw: new(-820, 0), optimized: new(-820, 0), roomId: 1);
        var world = new LiveWorldSnapshot(
            anchoredObservationId: 1,
            observations: [roomOne, roomTwo]);

        var translated = world.TranslateRoom(1, new CameraVector(320, 5));
        Check(translated.MapRevision == 1);
        Check(translated.Observations[0] == roomOne);
        Check(translated.Observations[1].RawPose == roomTwo.RawPose);
        Check(translated.Observations[1].OptimizedPose == new AtlasPoint(-500, 5));
        Check(translated.Observations[1].LocalPose == roomTwo.LocalPose);
    }

    private static LiveWorldSnapshot Fixture() => new(
        mapRevision: 7,
        anchoredObservationId: 10,
        observations:
        [
            MakeObservation(10, 100),
            MakeObservation(11, 101, raw: new(4, 0), optimized: new(4.5, 0))
        ],
        keyframes:
        [
            new LiveWorldKeyframe(20, 10, 100),
            new LiveWorldKeyframe(21, 11, 101)
        ],
        landmarks:
        [
            new LiveWorldLandmark(30, 20, 100, new(20, 30), new(20, 30), [0.1f, 0.2f], 1, 0.8),
            new LiveWorldLandmark(31, 20, 100, new(25, 30), new(25, 30), [0.3f, 0.4f], 1, 0.8),
            new LiveWorldLandmark(32, 20, 100, new(40, 20), new(40, 20), [0.5f, 0.6f], 1, 0.8),
            new LiveWorldLandmark(33, 21, 101, new(40, 20), new(44, 20), [0.7f, 0.8f], 1, 0.8)
        ],
        relativeMotionEdges:
        [
            new LiveWorldRelativeMotionEdge(40, 10, 11, 4, 0, 8)
        ],
        loopClosureEdges:
        [
            new LiveWorldLoopClosureEdge(50, 20, 21, [30, 31, 32], 4, 0, 3, 0.1)
        ]);

    private static LiveWorldObservation MakeObservation(
        int id,
        int sourceId,
        AtlasPoint? raw = null,
        AtlasPoint? optimized = null,
        int roomId = 0)
    {
        var rawPose = raw ?? new AtlasPoint(0, 0);
        return LiveWorldObservation.Create(
            id, sourceId, id,
            640, 360, 640, 240,
            [new WorldRect(new AtlasPoint(1, 2), new WorldSize(3, 4))],
            rawPose,
            optimized ?? new AtlasPoint(0, 0),
            roomId: roomId);
    }

    private static LiveWorldModelException Throws(Action action)
    {
        try
        {
            action();
        }
        catch (LiveWorldModelException error)
        {
            return error;
        }
        throw new Exception("Expected LiveWorldModelException.");
    }

    private static void CheckSnapshotsEqual(LiveWorldSnapshot actual, LiveWorldSnapshot expected)
    {
        Check(actual.SchemaVersion == expected.SchemaVersion);
        Check(actual.MapRevision == expected.MapRevision);
        Check(actual.AnchoredObservationId == expected.AnchoredObservationId);
        Check(actual.Observations.Count == expected.Observations.Count);
        for (var index = 0; index < actual.Observations.Count; index++)
        {
            var left = actual.Observations[index];
            var right = expected.Observations[index];
            Check(left with { ExcludedRects = right.ExcludedRects } == right);
            Check(left.ExcludedRects.SequenceEqual(right.ExcludedRects));
        }
        Check(actual.Keyframes.SequenceEqual(expected.Keyframes));
        Check(actual.Landmarks.Count == expected.Landmarks.Count);
        for (var index = 0; index < actual.Landmarks.Count; index++)
        {
            var left = actual.Landmarks[index];
            var right = expected.Landmarks[index];
            Check(left with { Descriptor = right.Descriptor } == right);
            Check(left.Descriptor.SequenceEqual(right.Descriptor));
        }
        Check(actual.RelativeMotionEdges.SequenceEqual(expected.RelativeMotionEdges));
        CheckLoopEdgesEqual(actual.LoopClosureEdges, expected.LoopClosureEdges);
    }

    private static void CheckLoopEdgesEqual(
        IReadOnlyList<LiveWorldLoopClosureEdge> actual,
        IReadOnlyList<LiveWorldLoopClosureEdge> expected)
    {
        Check(actual.Count == expected.Count);
        for (var index = 0; index < actual.Count; index++)
        {
            var left = actual[index];
            var right = expected[index];
            Check(left with { LandmarkIds = right.LandmarkIds } == right);
            Check(left.LandmarkIds.SequenceEqual(right.LandmarkIds));
        }
    }
}
