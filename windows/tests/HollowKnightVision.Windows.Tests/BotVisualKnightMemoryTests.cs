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
        var replayed = System.Text.Json.JsonSerializer.Deserialize<BotCaptureObservation>(
            System.Text.Json.JsonSerializer.Serialize(predicted));
        Require(replayed is { KnightSource: "visual-predicted", KnightPrediction: not null }
            && replayed.KnightPrediction.ObservedIssues.SequenceEqual(raw.Issues)
            && replayed.Features.SequenceEqual(predicted.Features),
            "A recorded prediction must keep its features and the original observation issues.");
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
        var batch = new LiveObjectDetectionBatch([box], frame.DetectionFrameId.Value, 1.2, 1, 0);
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
            SourceTimestamp = 1.22,
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

    internal static void TestWeakJumpCueUsesTheSameFreshDetectionBatch()
    {
        var memory = NewTrack();
        var cueTime = 1.2;
        var row = Row(cueTime, null, camera: 0);
        var corrected = memory.Resolve(row, detections: Cue(row));
        Require(corrected is { KnightSource: "visual-predicted", KnightPrediction.UsedWeakDetection: true },
            "A fresh compatible cue should correct an existing estimate.");
        Near(corrected.KnightPrediction!.LastSeenAt, 1.1);
        Near(corrected.Features[1], .38);
        Near(corrected.Features[97], .29 - .38);
        Require(new BotVisualKnightMemory().Resolve(row, detections: Cue(row)).KnightPrediction is null,
            "A weak cue cannot create a track.");
        Require(NewTrack().Resolve(row, detections: Cue(row, x: .82)).KnightPrediction?.UsedWeakDetection is null
            && NewTrack().Resolve(row, detections: Cue(row, confidence: .1f)).KnightPrediction?.UsedWeakDetection is null
            && NewTrack().Resolve(row, detections: Cue(row) with { SourceTimestamp = 1.01 }).KnightPrediction?.UsedWeakDetection is null
            && NewTrack().Resolve(row with { DetectionTimestamp = 1.14 }, detections: Cue(row)).KnightPrediction?.UsedWeakDetection is null,
            "Distant, implausibly weak, older, and mismatched batches must leave motion estimates alone.");
        var expired = Row(1.71, null, camera: 0);
        Require(!memory.Resolve(expired, detections: Cue(expired)).CanTrain,
            "Weak cues must leave the original sighting deadline intact.");

        static LiveObjectDetectionBatch Cue(BotCaptureObservation row, double x = .34, float confidence = .3f)
        {
            var id = row.DetectionFrameId!.Value;
            var candidate = new ObjectDetection("game.playable-knight", new(x - .02, 1 - .41 / .5625, .04, .06 / .5625), confidence)
            { SourceFrameIdentifier = id, ModelVersion = new(8, 0) };
            return new([candidate], id, row.DetectionSourceTimestamp!.Value, row.Generation, 0);
        }

        static BotVisualKnightMemory NewTrack()
        {
            var memory = new BotVisualKnightMemory();
            memory.Resolve(Row(1, .30, y: .30, camera: 0) with { ModelVersion = "v8.0" });
            memory.Resolve(Row(1.1, .32, y: .30, camera: 0) with { ModelVersion = "v8.0" });
            return memory;
        }
    }

    internal static void TestOnlyFreshMeasuredSightingsExtendTheDeadline()
    {
        var memory = new BotVisualKnightMemory();
        memory.Resolve(Row(1, .30, camera: 0));
        var last = Row(1.1, .32, camera: 0);
        memory.Resolve(last);
        memory.Resolve(last with { CaptureTimestamp = 1.15, CapturedAt = 1.15, AvailableAt = 1.15 });
        for (var i = 2; i <= 6; i++)
        {
            var predicted = memory.Resolve(Row(1 + i / 10d, null, camera: 0));
            Require(predicted.CanTrain, "Fresh frames should bridge a brief occlusion.");
            Near(predicted.KnightPredictionDeadline, 1.7);
        }
        Require(!memory.Resolve(Row(1.71, null, camera: 0)).CanTrain,
            "The prediction and a replayed detection must expire at the original deadline.");
        var reacquired = Row(1.8, .45, camera: 0);
        Require(memory.Resolve(reacquired) == reacquired && memory.Resolve(Row(1.9, null, camera: 0)).CanTrain,
            "A new sighting should restore observations and seed a new prediction.");
    }

    internal static void TestVisualCameraAndNoCameraHaveDifferentHorizons()
    {
        var compensated = new BotVisualKnightMemory();
        compensated.Resolve(Row(1, .3, camera: 0));
        compensated.Resolve(Row(1.1, .3, camera: .02));
        var moved = compensated.Resolve(Row(1.2, null, camera: .04));
        Require(moved.KnightPrediction is { CameraCompensated: true }, "Use the current visual camera during follow motion.");
        Near(moved.Features[0], .3);
        Near(moved.KnightPredictionDeadline, 1.7);

        var screen = new BotVisualKnightMemory();
        screen.Resolve(Row(1, .3));
        screen.Resolve(Row(1.1, .32));
        Require(screen.Resolve(Row(1.4, null)).KnightPrediction is { CameraCompensated: false }
            && screen.Resolve(Row(1.5, null)).KnightPrediction is null,
            "A screen-space estimate must have a shorter horizon.");
    }

    internal static void TestPredictionLandsOnCurrentFloorsAndKeepsPitsOpen()
    {
        Near(Fall(64, 448).Features[1], .22);
        Near(Fall(32, 128).Features[1], .20);
        foreach (var prediction in new[] { Fall(64, 448), Fall(32, 128) })
            Require(prediction is { CanTrain: true, Grounded: null },
                "An estimated landing is usable without claiming measured contact.");

        static BotCaptureObservation Fall(int left, int right)
        {
            var memory = new BotVisualKnightMemory();
            memory.Resolve(Row(1, .3, y: .30, camera: 0));
            memory.Resolve(Row(1.1, .3, y: .25, camera: 0));
            return memory.Resolve(Row(1.2, null, camera: 0) with
            {
                DetectedTerrain = new(640, 360, [new(left, right, .19 * 640, 1)], [])
            });
        }
    }

    internal static void TestChangingControlWidensUncertaintyWithoutChangingMeasuredMotion()
    {
        var moving = new BotPolicyAction(2, 0, 0, 0);
        var changed = new BotPolicyAction(1, 0, 1, 0);
        var memory = new BotVisualKnightMemory();
        memory.Resolve(Row(1, .3), moving);
        memory.Resolve(Row(1.1, .32), moving);
        var continued = memory.Resolve(Row(1.2, null), moving);
        var uncertain = memory.Resolve(Row(1.3, null), changed);
        Require(continued.KnightPrediction is { } first && uncertain.KnightPrediction is { } second
            && second.Uncertainty > first.Uncertainty + .018,
            "A direction or jump change should widen uncertainty.");
        Near(uncertain.Features[0], .36);
        Near(uncertain.Features[1], continued.Features[1]);
    }

    internal static void TestStaleAndForeignEvidenceCannotExtendAPrediction()
    {
        var missing = Row(1.2, null, camera: 0);
        foreach (var invalid in new[]
        {
            missing with { Issues = ["missing-knight", "room-transition"] },
            missing with { Generation = 2 }, missing with { RoomId = 2 },
            missing with { CapturedAt = .1 },
            missing with { TerrainSource = "human-authored", KnightSource = "hacker-frame", Issues = ["missing-hacker-knight"] }
        })
        {
            var memory = new BotVisualKnightMemory();
            memory.Resolve(Row(1, .3, camera: 0));
            Require(memory.Resolve(invalid) == invalid && memory.Resolve(Row(1.3, null, camera: 0)).KnightPrediction is null,
                "A stale frame, discontinuity, or authored frame must clear visual memory without changing that frame.");
        }

        var delayed = new BotVisualKnightMemory();
        delayed.Resolve(Row(1, .3, camera: 0));
        delayed.Resolve(Row(1.1, .32, camera: 0));
        var usable = delayed.Resolve(Row(1.6, null, camera: 0));
        var late = Row(1.65, null, camera: 0) with { AvailableAt = 1.75 };
        Require(usable.CanTrain && delayed.Resolve(late) == late,
            "A late result must not extend the deadline even if its capture is recent enough.");
    }

    internal static void TestPredictionChoosesCurrentTerrainNearTheEstimatedKnight()
    {
        var memory = new BotVisualKnightMemory();
        memory.Resolve(Row(1, .3, y: .3, camera: 0));
        memory.Resolve(Row(1.1, .32, y: .3, camera: 0));
        var row = Row(1.2, null, camera: 0);
        var features = row.Features.ToArray();
        features[16] = .01; features[17] = .1; features[18] = .19; features[19] = features[20] = 1;
        var predicted = memory.Resolve(row with
        {
            Features = features, DetectedTerrain = new(640, 360, [new(192, 256, 128, 1)], [])
        });
        Near(predicted.Features[16], -.04);
        Near(predicted.Features[17], .06);
        Near(predicted.Features[18], -.1);
        Require(predicted.Features[25] == 0, "Only the current selected terrain should remain.");
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
            DetectionSourceTimestamp = time,
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
