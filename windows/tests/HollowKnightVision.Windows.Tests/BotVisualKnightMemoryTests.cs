using HollowKnightVision.Windows.Core;

internal static class BotVisualKnightMemoryTests
{
    internal static void TestVisualPredictionUsesMeasuredMotionAndCurrentGeometry()
    {
        var memory = new BotVisualKnightMemory();
        var observed = new BotPolicyAction(2, 0, 0, 0);
        memory.Resolve(Row(1, .32, .24, camera: 0), observed);
        memory.Resolve(Row(1.1, .31, .20, camera: .025), observed);
        var raw = Row(1.2, null, camera: .035) with
        {
            DetectedTerrain = new(640, 360, [new(180, 260, 100, 1)], [new(270, 89, 192, 1)]),
            DetectionTimestamp = null,
            Issues = ["missing-knight", "stale-or-missing-detection"],
            RoadHint = new(.3, .2, 0, 0, false),
            MeasuredHealth = 5
        };
        var predicted = memory.Resolve(raw, observed);
        Require(predicted is { KnightSource: "visual-predicted", KnightPrediction.CameraCompensated: true }
            && predicted.CanTrain && predicted.EnemySource == "visual-unavailable" && predicted.Grounded is null
            && predicted.RoadHint == raw.RoadHint && predicted.MeasuredHealth == raw.MeasuredHealth,
            "A brief visual gap should produce evidence with current measurements and no implied ground contact.");
        Near(predicted.Features[0], .315);
        Near(predicted.Features[1], 100d / 640 + .03);
        Near(predicted.Features[16], 180d / 640 - .315);
        Near(predicted.Features[96], .62 - .315);
        Near(predicted.Features[150], 270d / 640 - .315);
        Near(predicted.KnightPredictionDeadline, 1.7);
        var history = new BotObservationHistory(BotPolicyContract.LandingVersion);
        memory.Reset();
        memory.Resolve(Row(1.3, .34, camera: .035), observed);
        var continued = memory.Resolve(Row(1.8, null, camera: .035), observed);
        Require(history.Append(predicted, observed, 1.21) is not null, "A brief detector interruption must allow bounded input.");
        Require(continued.Features[4] < .5 && history.Append(continued, observed, 1.81) is not null
            && history.Append(continued, observed, 1.91) is null, "Policy input must expire at the last sighting's deadline.");
        Require(memory.Resolve(Row(1.9, null, camera: .035), observed).KnightPrediction is null,
            "At the policy deadline, the producer must stop recording predicted rows.");
    }

    internal static void TestWeakCueNeverRenewsTrackAndRoomChangeClearsIt()
    {
        var memory = new BotVisualKnightMemory();
        var frame = Row(1.2, null);
        var box = new ObjectDetection("game.playable-knight", new(.32, .36, .04, .105), .35f)
            { SourceFrameIdentifier = frame.DetectionFrameId!.Value };
        var batch = new LiveObjectDetectionBatch([box], frame.DetectionFrameId.Value, 1999, 1, 0);
        Require(memory.Resolve(frame, detections: batch).KnightPrediction is null, "A weak cue cannot seed the track.");
        memory.Reset();
        memory.Resolve(Row(1, .30));
        var anchor = Row(1.1, .31);
        memory.Resolve(anchor);
        memory.Resolve(anchor with { CaptureTimestamp = 1.17, CapturedAt = 1.17, AvailableAt = 1.17, DetectionTimestamp = 1.17 });
        var wrongFrame = memory.Resolve(frame, detections: batch with { SourceFrameIdentifier = batch.SourceFrameIdentifier - 1 });
        Require(wrongFrame.KnightPrediction is { UsedWeakDetection: null }, "A cue must belong to the current measured frame.");
        frame = Row(1.22, null);
        var matched = batch with
        {
            SourceFrameIdentifier = frame.DetectionFrameId!.Value,
            Detections = [box with { SourceFrameIdentifier = frame.DetectionFrameId.Value }]
        };
        var guided = memory.Resolve(frame, detections: matched);
        Require(guided.KnightPrediction is { UsedWeakDetection: true }, "A matching weak visual cue may correct an existing estimate.");
        Near(guided.Features[0], .34);
        Near(guided.KnightPrediction!.LastSeenAt, 1.1);
        Near(guided.KnightPredictionDeadline, 1.45);
        Require(memory.Resolve(Row(1.46, null)).KnightPrediction is null, "Uncompensated predictions must expire within .35 seconds.");

        memory.Resolve(Row(2, .3), coordinateEpoch: 1);
        Require(memory.Resolve(Row(2.1, null), coordinateEpoch: 2).KnightPrediction is null
            && memory.Resolve(Row(2.2, null), coordinateEpoch: 1).KnightPrediction is null,
            "A replaced camera coordinate system must discard the saved sighting.");
        memory.Resolve(Row(3, .3));
        Require(memory.Resolve(Row(3.1, null) with { RoomId = 2 }).KnightPrediction is null,
            "A new room needs its own measured actor.");
        memory.Resolve(Row(4, .3));
        Require(memory.Resolve(Row(4.1, null) with { Issues = ["missing-knight", "missing-terrain-analysis"] }).KnightPrediction is null
            && memory.Resolve(Row(4.2, null)).KnightPrediction is null,
            "Missing terrain must invalidate a prediction until another measurement.");
    }

    internal static BotCaptureObservation Row(double time, double? x, double y = .21, double? camera = null)
    {
        var features = new double[BotRecordingContract.FeatureNames.Count];
        features[0] = x ?? 0;
        features[1] = x.HasValue ? y : 0;
        features[2] = x.HasValue ? .04 : 0;
        features[3] = x.HasValue ? .06 : 0;
        features[4] = x.HasValue ? .9 : 0;
        features[6] = 1;
        features[96] = .62 - features[0];
        features[97] = .29 - features[1];
        features[101] = 1;
        return new(time, time, time, 1, 1, 0, features, x.HasValue ? [] : ["missing-knight"], "detected")
        {
            DetectionTimestamp = time,
            DetectionFrameId = (long)Math.Round(time * 100),
            KnightSource = x.HasValue ? "visual" : null,
            VisualContext = new(640, 360d / 640, camera, camera.HasValue ? 0 : null)
        };
    }

    private static void Near(double actual, double expected) =>
        Require(Math.Abs(actual - expected) < .00001, $"Expected {expected:R} but saw {actual:R}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
