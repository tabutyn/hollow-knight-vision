using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.Core;

internal static class InputPathRecorderTests
{
    public static void TestPlaybackMaintainsLeaseAcrossLongHolds()
    {
        var recorder = new InputPathRecorder();
        recorder.Start(timestamp: 0);
        recorder.RecordInput(RecordedGameButton.Right, true, .1);
        recorder.RecordInput(RecordedGameButton.A, true, .8);
        recorder.RecordInput(RecordedGameButton.A, false, 1);
        recorder.RecordInput(RecordedGameButton.Right, false, 1.5);
        var path = recorder.Stop(2)!;
        var states = path.PlaybackStates().ToArray();
        // Model Unity ticks and the documented receiver lease, including a
        // 1.4-second hold that used to expire after its first 250ms.
        for (var tick = 0; tick <= 120; tick++)
        {
            var at = tick / 60d;
            var latest = states.Last(state => state.Offset <= at + 1e-9);
            var actual = at - latest.Offset <= .25 ? latest.HeldButtons : InputButtons.None;
            var expected = at >= .1 && at < 1.5 ? InputButtons.Right : InputButtons.None;
            if (at >= .8 && at < 1) expected |= InputButtons.ActionA;
            Check(actual == expected, $"Replay lease changed held input at {at}s.");
        }
        Check(states.Where((state, index) => index > 0 && state.HeldButtons != states[index - 1].HeldButtons)
            .SequenceEqual(path.StateChanges()), "Heartbeat changed input transition timing/order.");
        Check(states[^1].Offset == 2 && states[^1].HeldButtons == InputButtons.None);
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static void Check(bool value, string message = "Input path recorder assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Close(double expected, double actual, double tolerance = .000_001)
    {
        Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");
    }

    public static void TestRecordsDeduplicatedTransitionsAndClosesHeldButtons()
    {
        var recorder = new InputPathRecorder();
        var identifier = Guid.Parse("2B87556A-6D7A-4183-81A1-F93784E4290E");
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var checkpoint = MakeCheckpoint();
        Check(recorder.Start(100, createdAt, identifier, startCheckpoint: checkpoint));
        Check(!recorder.Start(101));
        recorder.RecordInput(RecordedGameButton.Right, true, 100.1);
        recorder.RecordInput(RecordedGameButton.Right, true, 100.2);
        recorder.RecordInput(RecordedGameButton.Up, true, 100.4);
        recorder.RecordInput(RecordedGameButton.Right, false, 101);

        var tracking = InputPathTrackingObservation.Empty with
        {
            CameraPosition = new AtlasPoint(42, -7),
            PoseVerified = true,
            HasConfirmedGround = true
        };
        recorder.RecordTracking(tracking, 7_777, groundTrackingMilliseconds: 4.5,
            floorlessCoarsePosition: new AtlasPoint(50, -8),
            floorlessMaskedPosition: new AtlasPoint(48, -9),
            floorlessExpectedDirection: VisualRoomDirection.Right,
            floorlessSelectionSource: "maskedRegistration", observedAt: 101.25);
        var truth = MakeGroundTruth(7);
        recorder.RecordGroundTruth(truth, 101.3);
        recorder.RecordCoarseMotion(7_778, 88, new AtlasPoint(44, -6),
            new AtlasPoint(43.5, -6.5),
            new LowResolutionRoomMotionEstimate(VisualRoomDirection.Right, .8, -1),
            true, false, 2, 3, 12, .2,
            placeMatch: new InputPathPlaceMatch(4, new AtlasPoint(43.5, -6.5), .12, .19),
            motionGrid: new LowResolutionMotionGrid(2, 2, [1, 2, 3, 4]),
            coarsePoseRevision: 42, observedAt: 101.35);

        var path = recorder.Stop(102) ?? throw new Exception("Recorder did not stop.");
        Check(!recorder.IsRecording);
        Check(path.Id == identifier);
        Check(path.CreatedAt == createdAt);
        Close(2, path.Duration);
        Check(path.StartCheckpoint == checkpoint);
        Check(path.RuntimeMetadata?["lowResolutionFrameTrace"]
            == "64x36-20hz-measured-ground-reliability-v3");
        Check(path.RuntimeMetadata?["lowResolutionMotionBridge"]
            == "tentative-transition-handoff-v14");
        Check(path.RuntimeMetadata?["pathPlaybackPreparation"]
            == "none");
        Check(path.RuntimeMetadata?["groundRelocalization"]
            == "floorless-background-search-continuity-v1");
        Check(path.Events.Select(item => item.Button).SequenceEqual([
            RecordedGameButton.Right, RecordedGameButton.Up,
            RecordedGameButton.Right, RecordedGameButton.Up]));
        Check(path.Events.Select(item => item.Transition).SequenceEqual([
            RecordedInputTransition.Pressed, RecordedInputTransition.Pressed,
            RecordedInputTransition.Released, RecordedInputTransition.Released]));
        Close(.1, path.Events[0].Offset);
        Close(.4, path.Events[1].Offset);
        Close(1, path.Events[2].Offset);
        Close(2, path.Events[3].Offset);
        var sample = path.TrackingSamples.Single();
        Close(1.25, sample.Offset);
        Close(7_777, sample.CaptureTimestamp);
        Close(42, sample.CameraX ?? double.NaN);
        Close(-7, sample.CameraY ?? double.NaN);
        Check(sample.PoseVerified);
        Check(sample.HasConfirmedGround);
        Close(4.5, sample.GroundTrackingMilliseconds ?? double.NaN);
        Close(50, sample.FloorlessCoarseX ?? double.NaN);
        Close(-8, sample.FloorlessCoarseY ?? double.NaN);
        Close(48, sample.FloorlessMaskedX ?? double.NaN);
        Close(-9, sample.FloorlessMaskedY ?? double.NaN);
        Check(sample.FloorlessExpectedDirection == (int)VisualRoomDirection.Right);
        Check(sample.FloorlessSelectionSource == "maskedRegistration");
        Check(path.UnverifiedSampleCount == 0);
        Check(path.GlobalCorrectionCount == 0);
        var recordedTruth = path.GroundTruthTrace.Single();
        Close(1.3, recordedTruth.Offset);
        Close(101.3, recordedTruth.ReceivedTimestamp);
        Check(recordedTruth.Sample == truth);
        var coarse = path.CoarseMotionTrace.Single();
        Close(1.35, coarse.Offset);
        Check(coarse.RenderedGameFrame == 88);
        Close(44, coarse.PresentedCameraX);
        Close(43.5, coarse.CoarseCameraX ?? double.NaN);
        Check(coarse.Direction == (int)VisualRoomDirection.Right);
        Check(coarse.ScreenShift == -1);
        Check(coarse.PlaceMatchKeyframeId == 4);
        Close(.12, coarse.PlaceMatchScore ?? double.NaN);
        Close(.19, coarse.PlaceMatchMargin ?? double.NaN);
        Check(coarse.IsControlling);
        Check(coarse.RoomId == 2);
        Check(coarse.CoarsePoseRevision == 42);
        var lowResolution = path.LowResolutionFrameTrace.Single();
        Close(1.35, lowResolution.Offset);
        Check(lowResolution.RenderedGameFrame == 88);
        Check(lowResolution.RoomId == 2);
        Check(lowResolution.Width == 2);
        Check(lowResolution.Height == 2);
        Check(lowResolution.Luma.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Check(!lowResolution.GroundTrackingReliable);
        var decodedPath = JsonSerializer.Deserialize<RecordedInputPath>(
            JsonSerializer.Serialize(path, JsonOptions), JsonOptions)
            ?? throw new Exception("Path did not decode.");
        var decodedLowResolution = decodedPath.LowResolutionFrameTrace.Single();
        CheckLowResolution(lowResolution, decodedLowResolution);
        Check(recorder.Stop(103) is null);
    }

    public static void TestPathWithoutCaptureDiagnosticsRemainsReadable()
    {
        var path = MakePath(1);
        var legacy = JsonSerializer.SerializeToNode(path, JsonOptions)!.AsObject();
        legacy.Remove("coarseMotionSamples");
        legacy.Remove("lowResolutionFrames");
        var decoded = legacy.Deserialize<RecordedInputPath>(JsonOptions)
            ?? throw new Exception("Legacy path did not decode.");
        Check(decoded.CoarseMotionTrace.Count == 0);
        Check(decoded.LowResolutionFrameTrace.Count == 0);
    }

    public static void TestLowResolutionFrameTraceIsCappedAtTwentyHertz()
    {
        var recorder = new InputPathRecorder();
        Check(recorder.Start(100));
        var grid = new LowResolutionMotionGrid(2, 2, [1, 2, 3, 4]);
        void Record(double timestamp) => recorder.RecordCoarseMotion(timestamp, null,
            new AtlasPoint(0, 0), null, null, false, false, 0, 0, 12, .2,
            motionGrid: grid, observedAt: timestamp);
        Record(100);
        Record(100.02);
        Record(100.051);
        var path = recorder.Stop(101) ?? throw new Exception("Recorder did not stop.");
        Check(path.CoarseMotionTrace.Count == 3);
        Check(path.LowResolutionFrameTrace.Count == 2);
        Close(0, path.LowResolutionFrameTrace[0].Offset);
        Close(.051, path.LowResolutionFrameTrace[1].Offset);
    }

    public static void TestLowResolutionReliabilityUsesOnlyExactCompletedGroundSolve()
    {
        var recorder = new InputPathRecorder();
        Check(recorder.Start(100));
        var grid = new LowResolutionMotionGrid(2, 2, [1, 2, 3, 4]);
        void RecordFrame(double captureTimestamp) => recorder.RecordCoarseMotion(
            captureTimestamp, null, new AtlasPoint(0, 0), null, null,
            false, false, 0, 0, 12, .2, motionGrid: grid, observedAt: captureTimestamp);
        RecordFrame(100);
        var reliable = InputPathTrackingObservation.Empty with
        {
            PoseVerified = true, HasConfirmedGround = true, LocalTextureSupport = 6
        };
        recorder.RecordTracking(reliable, 100, observedAt: 100.01);
        RecordFrame(100.051);
        recorder.RecordTracking(InputPathTrackingObservation.Empty, 100.051, observedAt: 100.06);
        recorder.RecordTracking(reliable, 100.07, observedAt: 100.08);
        var path = recorder.Stop(101) ?? throw new Exception("Recorder did not stop.");
        Check(path.LowResolutionFrameTrace.Select(item => item.GroundTrackingReliable)
            .SequenceEqual([true, false]));
    }

    public static void TestCoarseMotionWithoutPlaceMatchFieldsRemainsReadable()
    {
        var recorder = new InputPathRecorder();
        Check(recorder.Start(20));
        recorder.RecordCoarseMotion(20.1, null, new AtlasPoint(0, 0), null, null,
            false, false, 0, 0, 12, .2, observedAt: 20.2);
        var path = recorder.Stop(21) ?? throw new Exception("Recorder did not stop.");
        var legacy = JsonSerializer.SerializeToNode(path.CoarseMotionTrace[0], JsonOptions)!.AsObject();
        legacy.Remove("placeMatchKeyframeID");
        legacy.Remove("placeMatchScore");
        legacy.Remove("placeMatchMargin");
        legacy.Remove("coarsePoseRevision");
        var decoded = legacy.Deserialize<RecordedCoarseMotionSample>(JsonOptions)
            ?? throw new Exception("Legacy coarse sample did not decode.");
        Check(decoded.PlaceMatchKeyframeId is null);
        Check(decoded.PlaceMatchScore is null);
        Check(decoded.PlaceMatchMargin is null);
        Check(decoded.CoarsePoseRevision is null);
    }

    public static void TestRecordsUnverifiedTrackingForLaterFailureAnalysis()
    {
        var recorder = new InputPathRecorder();
        Check(recorder.Start(20));
        recorder.RecordTracking(InputPathTrackingObservation.Empty, 19.8, observedAt: 20.5);
        var path = recorder.Stop(21) ?? throw new Exception("Recorder did not stop.");
        Check(path.TrackingSamples.Count == 1);
        Check(path.UnverifiedSampleCount == 1);
        Check(!path.TrackingSamples[0].PoseVerified);
        Check(path.TrackingSamples[0].CameraX is null);
    }

    public static void TestAtlasAdmissionBelongsToExactCaptureAndOlderSamplesRemainReadable()
    {
        var recorder = new InputPathRecorder();
        Check(recorder.Start(20));
        recorder.RecordTracking(InputPathTrackingObservation.Empty, 20.1,
            placementRecoveryState: "verifying", observedAt: 20.2);
        recorder.RecordAtlasAdmission(20.1, false);
        recorder.RecordAtlasAdmission(20, true);
        var path = recorder.Stop(21) ?? throw new Exception("Recorder did not stop.");
        Check(path.TrackingSamples[0].PlacementRecoveryState == "verifying");
        Check(path.TrackingSamples[0].AtlasWriteAllowed == false);
        var legacy = JsonSerializer.SerializeToNode(path.TrackingSamples[0], JsonOptions)!.AsObject();
        legacy.Remove("placementRecoveryState");
        legacy.Remove("atlasWriteAllowed");
        var decoded = legacy.Deserialize<RecordedTrackingSample>(JsonOptions)
            ?? throw new Exception("Legacy tracking sample did not decode.");
        Check(decoded.PlacementRecoveryState is null);
        Check(decoded.AtlasWriteAllowed is null);
    }

    public static void TestStoreRoundTripsReplayReadyPath()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        try
        {
            var store = new RecordedInputPathStore(root);
            var original = new RecordedInputPath(RecordedInputPath.CurrentSchemaVersion,
                Guid.NewGuid(), DateTimeOffset.FromUnixTimeSeconds(1_700_123_456), 3.5,
                null, null, MakeCheckpoint(),
                [new(0, RecordedGameButton.Left, RecordedInputTransition.Pressed),
                 new(3.5, RecordedGameButton.Left, RecordedInputTransition.Released)], []);
            var file = store.Save(original);
            Check(File.Exists(file));
            CheckPath(original, store.Load(file));
            CheckPath(original, store.LoadNamed(Path.GetFileName(file)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static void TestRecordedButtonsRoundTripBridgeBits()
    {
        foreach (var button in Enum.GetValues<RecordedGameButton>())
            Check(button.ToInputButton().ToRecordedGameButton() == button);
        Check((InputButtons.Left | InputButtons.Right).ToRecordedGameButton() is null);
    }

    public static void TestPlaybackValidationAllowsOverlappingBalancedKeys()
    {
        var path = MakePath(2,
            [new(.1, RecordedGameButton.Right, RecordedInputTransition.Pressed),
             new(.4, RecordedGameButton.Up, RecordedInputTransition.Pressed),
             new(1.2, RecordedGameButton.Right, RecordedInputTransition.Released),
             new(1.5, RecordedGameButton.Up, RecordedInputTransition.Released)]);
        Check(path.IsValidPlaybackPath());
        var duplicatePress = MakePath(2,
            [new(.1, RecordedGameButton.Right, RecordedInputTransition.Pressed),
             new(.2, RecordedGameButton.Right, RecordedInputTransition.Pressed),
             new(1, RecordedGameButton.Right, RecordedInputTransition.Released)]);
        Check(!duplicatePress.IsValidPlaybackPath());
    }

    public static void TestPlaybackValidationAcceptsRecordedWorldPoseTrace()
    {
        var truth = MakeGroundTruth(1);
        var path = MakePath(1, groundTruthSamples:
            [new(.25, 100.25, truth), new(.75, 100.75, MakeGroundTruth(2))]);
        Check(path.IsValidPlaybackPath());
        var outOfOrder = MakePath(1, groundTruthSamples: path.GroundTruthTrace.Reverse().ToArray());
        Check(!outOfOrder.IsValidPlaybackPath());
    }

    public static void TestPlaybackValidationAcceptsPlayableRoomsAcrossHeroLessCutscene()
    {
        var path = new RecordedInputPath(RecordedInputPath.CurrentSchemaVersion,
            Guid.NewGuid(), DateTimeOffset.UtcNow, 2, null, null, MakeCheckpoint(), [], [],
            [new(.25, 100.25, MakeGroundTruth(1, "Tutorial_01")),
             new(1, 101, MakeGroundTruth(2, "Opening_Cutscene", false)),
             new(1.75, 101.75, MakeGroundTruth(3, "Tutorial_02"))]);
        Check(path.IsValidPlaybackPath());
    }

    public static void TestPlaybackCommandAcceptsOnlySavedFileNamesAndBoundedIteration()
    {
        const string name = "path-20260919-204216-test.json";
        Check(GamePathPlaybackCommand.Parse($"replay-path:{name}:5")
            == new GamePathPlaybackCommand(name, 5));
        Check(GamePathPlaybackCommand.Parse("replay-path:../secret.json:1") is null);
        Check(GamePathPlaybackCommand.Parse($"replay-path:{name}:0") is null);
        Check(GamePathPlaybackCommand.Parse($"replay-path:{name}:101")
            == new GamePathPlaybackCommand(name, 101));
        Check(GamePathPlaybackCommand.Parse($"replay-path:{name}:10001") is null);
    }

    private static RecordedInputPath MakePath(double duration,
        IReadOnlyList<RecordedInputPathEvent>? events = null,
        IReadOnlyList<RecordedGroundTruthSample>? groundTruthSamples = null) =>
        new(RecordedInputPath.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
            duration, null, null, null, events ?? [], [], groundTruthSamples);

    private static RecordedGameCheckpoint MakeCheckpoint() => new(
        "Crossroads_01", null, 10, 4, 0, 0, 0, true, true,
        11, 6, -38.1, 11, 6, 0, null,
        null, null, null, null, null, null, null);

    private static ReceiverGroundTruthSample MakeGroundTruth(ulong sequence,
        string sceneName = "Tutorial_01", bool heroAvailable = true) => new(
        2, "groundTruth", Guid.NewGuid(), sequence, 99, 12.5, sceneName,
        heroAvailable, 36.25, 11.5, .004, -2, 0, false, true,
        true, 37.25, 14.1, -38.1, 36.25, 14.1, .004, 14.0625,
        null, null, null, null, null, null, 1920, 1080);

    private static void CheckLowResolution(RecordedLowResolutionFrame expected,
        RecordedLowResolutionFrame actual)
    {
        Close(expected.Offset, actual.Offset);
        Close(expected.CaptureTimestamp, actual.CaptureTimestamp);
        Check(expected.RenderedGameFrame == actual.RenderedGameFrame);
        Check(expected.RoomId == actual.RoomId);
        Check(expected.Width == actual.Width && expected.Height == actual.Height);
        Check(expected.Luma.SequenceEqual(actual.Luma));
        Check(expected.GroundTrackingReliable == actual.GroundTrackingReliable);
    }

    private static void CheckPath(RecordedInputPath expected, RecordedInputPath actual)
    {
        Check(expected.SchemaVersion == actual.SchemaVersion);
        Check(expected.Id == actual.Id);
        Check(expected.CreatedAt == actual.CreatedAt);
        Close(expected.Duration, actual.Duration);
        Check(expected.SourcePathId == actual.SourcePathId);
        Check(expected.ReplayIteration == actual.ReplayIteration);
        Check(expected.StartCheckpoint == actual.StartCheckpoint);
        Check(expected.Events.SequenceEqual(actual.Events));
        Check(expected.TrackingSamples.Count == actual.TrackingSamples.Count);
        Check(expected.GroundTruthTrace.Count == actual.GroundTruthTrace.Count);
        Check(expected.CoarseMotionTrace.Count == actual.CoarseMotionTrace.Count);
        Check(expected.LowResolutionFrameTrace.Count == actual.LowResolutionFrameTrace.Count);
        Check(expected.LoopClosureEvents.Count == actual.LoopClosureEvents.Count);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
