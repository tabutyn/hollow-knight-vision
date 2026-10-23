using HollowKnightVision.Windows.Core;

internal static class PlaybackDivergenceMonitorTests
{
    private static readonly PlaybackFrameSize FrameSize = new(640, 360);

    public static void TestConnectedTruthJoinsNewSceneAtKnightDoorwayAndReusesKnownAnchor()
    {
        var tracker = new ConnectedGroundTruthCameraTracker();
        var first = Required(tracker.Observe(
            Sample(1, "RoomA", 10, heroScreenX: 600), 1, FrameSize));
        Close(0, first.CameraPosition.X);

        var departure = Required(tracker.Observe(
            Sample(2, "RoomA", 20, heroScreenX: 600), 2, FrameSize));
        Close(10, departure.CameraPosition.X);

        var arrival = Required(tracker.Observe(
            Sample(3, "RoomB", 1_000, heroScreenX: 40), 3, FrameSize));
        Close(570, arrival.CameraPosition.X);

        var revisit = Required(tracker.Observe(
            Sample(4, "RoomA", 30, heroScreenX: 50), 4, FrameSize));
        Close(20, revisit.CameraPosition.X);
    }

    public static void TestConnectedTruthKeepsKnownSceneCameraWhileHeroUnavailable()
    {
        var tracker = new ConnectedGroundTruthCameraTracker();
        _ = Required(tracker.Observe(Sample(1, cameraX: 10), 1, FrameSize));
        var hiddenHero = Required(tracker.Observe(
            Sample(2, cameraX: 16, heroAvailable: false), 2, FrameSize));
        Close(6, hiddenHero.CameraPosition.X);
    }

    public static void TestConnectedTruthCannotInitializeNewSceneWhileHeroUnavailable()
    {
        var tracker = new ConnectedGroundTruthCameraTracker();
        _ = Required(tracker.Observe(
            Sample(1, "Tutorial_01", 10), 1, FrameSize));
        Null(tracker.Observe(
            Sample(2, "Town", 1_000, heroAvailable: false), 2, FrameSize));
    }

    public static void TestDivergenceRequiresSustainedExactFrameErrorAfterGrace()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        _ = monitor.AppendGroundTruth(Sample(1, cameraX: 0), 0, FrameSize);
        monitor.BeginEvaluation(1);

        Null(Pair(monitor, 2, 1, 0, 100));
        for (var index = 0; index < PlaybackDivergenceMonitor.RequiredConsecutiveSamples - 1; index++)
        {
            Null(Pair(monitor, 3 + index, 6.1 + index * 0.02, index, index + 500));
        }
        var decision = Pair(
            monitor,
            2 + PlaybackDivergenceMonitor.RequiredConsecutiveSamples,
            6.4,
            20,
            520);
        Equal(PlaybackDivergenceMonitor.RequiredConsecutiveSamples,
            Required(decision).ConsecutiveSamples);
        Close(400, decision!.ErrorPixels);
        Equal("Tutorial_01", decision.SceneName);
    }

    public static void TestAlignedMotionDoesNotTriggerDivergence()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        for (var index = 0; index < 40; index++)
        {
            var truthX = index * 12;
            Null(Pair(monitor, index + 1, 6 + index * 0.02, truthX, truthX + 75));
        }
    }

    public static void TestSubthresholdOffsetDoesNotAbortPlayback()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        Null(Pair(monitor, 1, 0, 0, 0));
        for (var index = 0; index < PlaybackDivergenceMonitor.RequiredConsecutiveSamples * 2; index++)
        {
            Null(Pair(monitor, index + 2, 6 + index * 0.02, index, index + 150));
        }
    }

    public static void TestTransitionFramesResetDivergenceEvidence()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        Null(Pair(monitor, 1, 0, 0, 0));
        for (var index = 0; index < PlaybackDivergenceMonitor.RequiredConsecutiveSamples * 2; index++)
        {
            Null(Pair(monitor, index + 2, 6 + index * 0.02, 0, 500, isTransitioning: true));
        }
    }

    public static void TestEvaluationDiscardsPreviousEndpointSceneAnchor()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        _ = monitor.AppendGroundTruth(
            Sample(1, "Town", 0, heroScreenX: 320), 0, FrameSize);
        monitor.BeginEvaluation(1);
        Null(Pair(monitor, 2, 1, 0, 0, scene: "Tutorial_01", heroScreenX: 600));
        Null(Pair(monitor, 3, 6, 10, 10, scene: "Tutorial_01", heroScreenX: 600));
        Null(Pair(monitor, 4, 6.1, 1_000, 570, scene: "Town", heroScreenX: 40));
        for (var index = 0; index < PlaybackDivergenceMonitor.RequiredConsecutiveSamples; index++)
        {
            Null(Pair(
                monitor,
                index + 5,
                8.2 + index * 0.02,
                1_001 + index,
                571 + index,
                scene: "Town",
                heroScreenX: 40));
        }
    }

    public static void TestNewSceneDivergenceStopsSoonAfterShortGrace()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        Null(Pair(monitor, 1, 0, 0, 0, scene: "Tutorial_01", heroScreenX: 600));
        Null(Pair(monitor, 2, 6, 10, 10, scene: "Tutorial_01", heroScreenX: 600));
        Null(Pair(monitor, 3, 6.1, 1_000, 1_070, scene: "Town", heroScreenX: 40));

        PlaybackDivergenceDecision? decision = null;
        double? decisionTime = null;
        for (var index = 0; index < 40 && decision is null; index++)
        {
            var timestamp = 6.12 + index * 0.02;
            decision = Pair(
                monitor,
                index + 4,
                timestamp,
                1_001 + index,
                1_071 + index,
                scene: "Town",
                heroScreenX: 40);
            if (decision is not null) decisionTime = timestamp;
        }

        Equal(PlaybackDivergenceMonitor.RequiredConsecutiveSamples,
            Required(decision).ConsecutiveSamples);
        Check(decisionTime < 6.7);
    }

    public static void TestMovingHackerTruthAbortsWhenVisionStopsPublishing()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        Null(Pair(monitor, 1, 0, 0, 0));

        Null(monitor.AppendGroundTruth(Sample(2, cameraX: 30), 5.6, FrameSize));
        var decision = Required(monitor.AppendGroundTruth(
            Sample(3, cameraX: 60), 5.8, FrameSize));
        Equal(PlaybackDivergenceCause.VisionStall, decision.Cause);
        Close(60, decision.ErrorPixels);
        Check(decision.SilenceSeconds >= PlaybackDivergenceMonitor.MaximumVisionSilence);
    }

    public static void TestStationaryHackerTruthDoesNotTreatMissingVisionAsDivergence()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        Null(Pair(monitor, 1, 0, 0, 0));

        for (var index = 0; index < 20; index++)
        {
            Null(monitor.AppendGroundTruth(
                Sample(index + 2, cameraX: 0), 6 + index, FrameSize));
        }
    }

    public static void TestExactVisionPairResetsMissingVisionWatchdog()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        Null(Pair(monitor, 1, 0, 0, 0));
        Null(Pair(monitor, 2, 6, 50, 50));
        Null(monitor.AppendGroundTruth(Sample(3, cameraX: 90), 6.6, FrameSize));
        Null(monitor.AppendGroundTruth(Sample(4, cameraX: 100), 6.7, FrameSize));
    }

    public static void TestMissingVisionWatchdogRebasesAtSceneChange()
    {
        var monitor = new PlaybackDivergenceMonitor();
        monitor.Prepare();
        monitor.BeginEvaluation(0);
        Null(Pair(monitor, 1, 0, 0, 0, scene: "Tutorial_01", heroScreenX: 600));

        Null(monitor.AppendGroundTruth(
            Sample(2, "Town", 1_000, heroScreenX: 40), 6, FrameSize));
        Null(monitor.AppendGroundTruth(
            Sample(3, "Town", 1_030, heroScreenX: 40), 6.8, FrameSize));
        var decision = Required(monitor.AppendGroundTruth(
            Sample(4, "Town", 1_060, heroScreenX: 40), 6.9, FrameSize));
        Equal(PlaybackDivergenceCause.VisionStall, decision.Cause);
        Equal("Town", decision.SceneName);
        Close(60, decision.ErrorPixels);
    }

    public static IReadOnlyList<(string Name, Action Run)> All =>
    [
        (nameof(TestConnectedTruthJoinsNewSceneAtKnightDoorwayAndReusesKnownAnchor), TestConnectedTruthJoinsNewSceneAtKnightDoorwayAndReusesKnownAnchor),
        (nameof(TestConnectedTruthKeepsKnownSceneCameraWhileHeroUnavailable), TestConnectedTruthKeepsKnownSceneCameraWhileHeroUnavailable),
        (nameof(TestConnectedTruthCannotInitializeNewSceneWhileHeroUnavailable), TestConnectedTruthCannotInitializeNewSceneWhileHeroUnavailable),
        (nameof(TestDivergenceRequiresSustainedExactFrameErrorAfterGrace), TestDivergenceRequiresSustainedExactFrameErrorAfterGrace),
        (nameof(TestAlignedMotionDoesNotTriggerDivergence), TestAlignedMotionDoesNotTriggerDivergence),
        (nameof(TestSubthresholdOffsetDoesNotAbortPlayback), TestSubthresholdOffsetDoesNotAbortPlayback),
        (nameof(TestTransitionFramesResetDivergenceEvidence), TestTransitionFramesResetDivergenceEvidence),
        (nameof(TestEvaluationDiscardsPreviousEndpointSceneAnchor), TestEvaluationDiscardsPreviousEndpointSceneAnchor),
        (nameof(TestNewSceneDivergenceStopsSoonAfterShortGrace), TestNewSceneDivergenceStopsSoonAfterShortGrace),
        (nameof(TestMovingHackerTruthAbortsWhenVisionStopsPublishing), TestMovingHackerTruthAbortsWhenVisionStopsPublishing),
        (nameof(TestStationaryHackerTruthDoesNotTreatMissingVisionAsDivergence), TestStationaryHackerTruthDoesNotTreatMissingVisionAsDivergence),
        (nameof(TestExactVisionPairResetsMissingVisionWatchdog), TestExactVisionPairResetsMissingVisionWatchdog),
        (nameof(TestMissingVisionWatchdogRebasesAtSceneChange), TestMissingVisionWatchdogRebasesAtSceneChange)
    ];

    private static PlaybackDivergenceDecision? Pair(
        PlaybackDivergenceMonitor monitor,
        long frame,
        double timestamp,
        double truthX,
        double visualX,
        bool isTransitioning = false,
        string scene = "Tutorial_01",
        double heroScreenX = 320)
    {
        var first = monitor.AppendVision(
            ConnectedGroundTruthCameraTracker.FrameKey(frame),
            new AtlasPoint(visualX, 0),
            timestamp,
            isTransitioning);
        var second = monitor.AppendGroundTruth(
            Sample(frame, scene, truthX, heroScreenX: heroScreenX),
            timestamp,
            FrameSize);
        return first ?? second;
    }

    private static ReceiverGroundTruthSample Sample(
        long frame,
        string scene = "Tutorial_01",
        double cameraX = 0,
        double cameraY = 0,
        double heroScreenX = 320,
        double heroScreenY = 180,
        bool heroAvailable = true) => new(
            ReceiverProtocol.ControlVersion,
            "groundTruth",
            Guid.NewGuid(),
            checked((ulong)frame),
            frame,
            frame,
            scene,
            heroAvailable,
            0, 0, 0,
            1, 0,
            true,
            true,
            true,
            cameraX,
            cameraY,
            0,
            cameraX,
            cameraY,
            0,
            180,
            1,
            1,
            heroScreenX,
            heroScreenY,
            640,
            360,
            640,
            360);

    private static T Required<T>(T? value) where T : class =>
        value ?? throw new Exception("Expected a value.");

    private static ConnectedGroundTruthCameraSample Required(
        ConnectedGroundTruthCameraSample? value) =>
        value ?? throw new Exception("Expected a value.");

    private static void Null<T>(T? value)
    {
        if (value is not null) throw new Exception($"Expected null; received {value}.");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Expected condition to be true.");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected {expected}; received {actual}.");
        }
    }

    private static void Close(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 0.001)
        {
            throw new Exception($"Expected {expected}; received {actual}.");
        }
    }
}
