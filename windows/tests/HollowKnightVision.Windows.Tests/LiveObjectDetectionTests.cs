using HollowKnightVision.Windows.Core;

internal static class LiveObjectDetectionTests
{
    private const string Knight = "game.playable-knight";
    private static void Check(bool value, string message = "Object detection parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, double tolerance = .000001)
    { Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}."); }
    private static ObjectDetection Object(double x = .1, float confidence = .9f, long frame = 1,
        string id = "game.mana") => new(id, new(x, .1, .2, .2), confidence)
        { SourceFrameIdentifier = frame, ModelVersion = new(1, 0) };
    private static BgraFrame Frame(double time = 10, long id = 42, int width = 100, int height = 100, int padding = 12)
    {
        var stride = width * 4 + padding; var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++) pixels[y * stride + x * 4 + 3] = 255;
            Array.Fill(pixels, (byte)187, y * stride + width * 4, padding);
        }
        return new(width, height, stride, pixels, id, DateTimeOffset.UnixEpoch.AddSeconds(time));
    }
    private static void Paint(BgraFrame frame, int x, int y, int width, int height)
    {
        for (var yy = y; yy < y + height; yy++) for (var xx = x; xx < x + width; xx++)
        { var i = yy * frame.Stride + xx * 4; frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = 255; }
    }
    private static LiveObjectDetectionBatch Batch(ObjectDetection detection, double time, long generation = 1) =>
        new([detection], detection.SourceFrameIdentifier, time, generation, .03);

    public static void TestPostprocessing()
    {
        // Mac's Vision (bottom-origin) rectangle .10,.20,.30,.40 is already
        // top-origin .10,.40,.30,.40 in trainer/ONNX output on Windows.
        var first = Object(id: "enemies.crawlid", frame: 42) with { Box = new(.1, .4, .3, .4) };
        var result = LiveObjectDetectionPostprocessor.Process([first,
            first with { Box = new(.11, .39, .3, .4), Confidence = .8f },
            first with { ClassIdentifier = "world.sign", Confidence = .75f },
            first with { ClassIdentifier = "enemies.vengfly", Box = new(.7, .2, .1, .1), Confidence = .7f },
            first with { ClassIdentifier = "game.objects", Box = new(.4, .5, .1, .1), Confidence = .49f },
            first with { ClassIdentifier = Knight, Box = new(.45, .82, .05, .08), Confidence = .08f }]);
        Check(result.Count == 4 && result[0].ClassIdentifier == "enemies.crawlid" && result[1].ClassIdentifier == "world.sign");
        Check(result.Single(item => item.ClassIdentifier == Knight).Confidence == .08f);
        Check(result.All(item => item.SourceFrameIdentifier == 42 && item.ModelVersion?.DisplayName == "v1.0"));
        Near(first.Box.X, result[0].Box.X); Near(first.Box.Y, result[0].Box.Y);
        Near(first.Box.Width, result[0].Box.Width); Near(first.Box.Height, result[0].Box.Height);
        var image = SceneRegions.ImageRect(result[0], 100, 50)!.Value;
        Near(10, image.X); Near(20, image.Y); Near(30, image.Width); Near(20, image.Height);
    }

    public static void TestPolicyBoundsAndCaps()
    {
        var canonical = Object(id: "main-title.options");
        var result = LiveObjectDetectionPostprocessor.Process([canonical,
            canonical with { ClassIdentifier = "shared.options", Confidence = .8f },
            Object(id: "world.edge") with { Box = new(-.02, .9, .1, .2) },
            Object(id: "world.reverse") with { Box = new(.7, .7, -.1, -.2) },
            Object(id: "world.tiny") with { Box = new(0, 0, .01, .01) },
            Object(id: "world.huge") with { Box = new(0, 0, 1, 1) },
            Object(id: "world.nan") with { Box = new(double.NaN, 0, .1, .1) },
            Object(confidence: float.NaN), Object(confidence: float.PositiveInfinity),
            Object(id: Knight, confidence: .049f)]);
        Check(result.Count == 3 && result[0].ClassIdentifier == "shared.options");
        var clipped = result.Single(item => item.ClassIdentifier == "world.edge").Box;
        Near(0, clipped.X); Near(.08, clipped.Width); Near(.1, clipped.Height);
        var reverse = result.Single(item => item.ClassIdentifier == "world.reverse").Box;
        Near(.6, reverse.X); Near(.5, reverse.Y); Near(.1, reverse.Width); Near(.2, reverse.Height);
        var crowded = Enumerable.Range(0, 20).Select(i => Object(i * .04, id: "world.same") with { Box = new(i * .04, 0, .02, .02) });
        Check(LiveObjectDetectionPostprocessor.Process(crowded).Count == 12);
        var classes = Enumerable.Range(0, 120).Select(i => Object(id: $"world.{i}"));
        Check(LiveObjectDetectionPostprocessor.Process(classes).Count == 100);
        Check(LiveObjectDetectionPostprocessor.Process([Object(confidence: .5f), Object(.5, .05f, id: Knight)]).Count == 2);
    }

    public static void TestLiveTensorDecode()
    {
        // Independent class planes, tied local peaks, alias-aware NMS, and an
        // offscreen box that must be clipped before area/suppression limits.
        float[] scores = [.9f, .8f, .7f, .06f, .04f, .08f, .2f, .1f, .4f];
        float[] boxes = [-.02f, .2f, .6f, .2f, .2f, .2f, .1f, .1f, .1f, .1f, .1f, .1f];
        var classes = new[] { "enemies.crawlid", Knight, "world.sign" };
        var live = DetectionDecoder.Decode(scores, [1, 3, 1, 3], boxes, [1, 4, 1, 3], classes, useLivePolicy: true);
        Check(live.Count == 3 && live.Count(item => item.ClassIdentifier == Knight) == 2);
        Check(live.All(item => item.ClassIdentifier != "world.sign"));
        Near(0, live[0].Box.X); Near(.08, live[0].Box.Width);
        var diagnostic = DetectionDecoder.DecodeRaw(scores, [1, 3, 1, 3], boxes, [1, 4, 1, 3], classes);
        Check(diagnostic.Any(item => item.ClassIdentifier == "world.sign"), "Explicit tensor-debug API was incorrectly changed to the live policy.");
        Check(DetectionDecoder.Decode([.9f, .9f], [1, 1, 1, 2], [.1f, .6f, .2f, .2f, .1f, .1f, .1f, .1f],
            [1, 4, 1, 2], ["world.sign"], useLivePolicy: true).Count == 2);
    }

    public static void TestFreshness()
    {
        var batch = Batch(Object(frame: 7), 10, 3);
        Check(LiveObjectDetectionFreshness.Detections(batch, 3, 10.399).SequenceEqual(batch.Detections));
        foreach (var time in new[] { 10.401, 9.9, double.NaN, double.PositiveInfinity })
            Check(LiveObjectDetectionFreshness.Detections(batch, 3, time).Count == 0);
        Check(LiveObjectDetectionFreshness.Detections(batch, 4, 10.1).Count == 0);
        Check(LiveObjectDetectionFreshness.Detections(null, 3, 10).Count == 0);
    }

    public static void TestTrackerJitterAndMisses()
    {
        var tracker = new LiveObjectDetectionTracker();
        Check(tracker.Update([Object()], 2).Count == 1);
        var second = tracker.Update([Object(.2, frame: 2)], 2).Single();
        Near(.158, second.Box.X); Check(second.SourceFrameIdentifier == 2 && second.ModelVersion == new LabelingModelSemanticVersion(1, 0));
        var third = tracker.Update([Object(.25, .8f, 3) with { ModelVersion = new(2, 0) }], 2).Single();
        Near(.18928, third.Box.X); Near(.835, third.Confidence); Check(third.ModelVersion?.Major == 2);
        for (var i = 0; i < 4; i++)
        {
            var held = tracker.Update([], 2).Single();
            Check(held.SourceFrameIdentifier == 3); Near(.835 * Math.Pow(.82, i + 1), held.Confidence);
        }
        Check(tracker.Update([], 2).Count == 0);
    }

    public static void TestTrackerAdmissionAndGeneration()
    {
        var tracker = new LiveObjectDetectionTracker();
        Check(tracker.Update([Object(confidence: .7f)], 2).Count == 0);
        Check(tracker.Update([Object(confidence: .7f, frame: 2)], 2).Count == 1);
        Check(tracker.Update([Object(confidence: .7f, frame: 3)], 3).Count == 0);
        tracker.Reset(); Check(tracker.Update([], 3).Count == 0);
        Check(tracker.Update([Object(confidence: .08f, id: Knight)], 3).Count == 0);
        Check(tracker.Update([Object(confidence: .08f, id: Knight)], 3).Count == 0);
        Check(tracker.Update([Object(confidence: .89f)], 3).Count == 1);
    }

    public static void TestTrackerAssociations()
    {
        var tracker = new LiveObjectDetectionTracker();
        tracker.Update([Object(.1, id: "main-title.options"), Object(.7, id: "world.sign")], 1);
        var result = tracker.Update([Object(.2, id: "shared.options"), Object(.6, id: "world.sign")], 1);
        Near(.158, result.Single(item => item.ClassIdentifier == "shared.options").Box.X);
        Near(.642, result.Single(item => item.ClassIdentifier == "world.sign").Box.X);
        tracker.Reset();
        tracker.Update([Object(.1, .95f, id: "world.sign") with { Box = new(.1, .1, .02, .02) }], 1);
        var split = tracker.Update([Object(.11, .95f, id: "world.sign") with { Box = new(.11, .1, .02, .02) },
            Object(.8, .95f, id: "world.sign") with { Box = new(.8, .1, .02, .02) }], 1);
        Check(split.Count == 2); Near(.1058, split.Min(item => item.Box.X)); Near(.8, split.Max(item => item.Box.X));
    }

    public static void TestPixelBoundary()
    {
        var frame = Frame(); Paint(frame, 20, 60, 21, 21); var original = frame.Pixels.ToArray();
        var detection = Object(frame: 42) with { Box = new(.22, .62, .16, .16) };
        var refined = LiveObjectPixelRefiner.Refine([detection], frame, .6);
        var box = refined.Detections.Single().Box;
        Near(.19, box.X); Near(.59, box.Y); Near(.23, box.Width); Near(.23, box.Height);
        Check(refined.Detections[0].SourceFrameIdentifier == 42 && refined.Detections[0].ModelVersion == detection.ModelVersion);
        var diagnostic = refined.DiagnosticImage!;
        Check(diagnostic.CaptureId == frame.CaptureId && diagnostic.ObservedAt == frame.ObservedAt);
        var edge = (60 * 100 + 20) * 4;
        Check(diagnostic.Pixels.AsSpan(edge, 4).SequenceEqual(new byte[] { 196, 38, 255, 128 }));
        Check(diagnostic.Pixels[(70 * 100 + 30) * 4 + 3] == 0 && diagnostic.Pixels[(30 * 100 + 20) * 4 + 3] == 0);
        Check(original.SequenceEqual(frame.Pixels), "Refiner changed the capture or row padding.");
        var quiet = LiveObjectPixelRefiner.Refine([detection], frame, .6, false);
        Check(quiet.DiagnosticImage is null && quiet.Detections.SequenceEqual(refined.Detections));
    }

    public static void TestPixelFallbacks()
    {
        var frame = Frame(); var detection = Object() with { Box = new(.01, .8, .1, .2) };
        var result = LiveObjectPixelRefiner.Refine([detection], frame, .12);
        Near(0, result.Detections[0].Box.X); Near(.122, result.Detections[0].Box.Width);
        Near(.776, result.Detections[0].Box.Y); Near(.224, result.Detections[0].Box.Height);
        Check(result.DiagnosticImage!.Pixels.All(value => value == 0));
        Check(LiveObjectPixelRefiner.Refine([], frame, .12).DiagnosticImage is null);
        Check(LiveObjectPixelRefiner.Refine([detection], Frame(width: 2), .12).Detections[0] == detection);
        var unchanged = LiveObjectPixelRefiner.Refine([detection], frame, -1).Detections[0].Box;
        Near(detection.Box.X, unchanged.X); Near(detection.Box.Y, unchanged.Y);
        Near(detection.Box.Width, unchanged.Width); Near(detection.Box.Height, unchanged.Height);
        Paint(frame, 50, 60, 5, 5);
        var isolated = Object() with { Box = new(.2, .6, .1, .1) };
        var unsupported = LiveObjectPixelRefiner.Refine([isolated], frame, 3).Detections[0].Box;
        Near(0, unsupported.X); Near(.6, unsupported.Width); Near(.3, unsupported.Y); Near(.7, unsupported.Height);
    }

    public static void TestMotionSweep()
    {
        var body = Object(.1, frame: 1, id: Knight) with { Box = new(.1, .5, .05, .1) };
        var prior = Batch(body, 1); var current = Batch(body with { Box = body.Box with { X = .15 }, SourceFrameIdentifier = 2 }, 1.1);
        var mask = MovingObjectMask.Detections(prior.Detections, current, prior, 1, 1.2).Single();
        Check(mask.Box.X < .15 && mask.Box.X + mask.Box.Width > .25);
        Check(mask.SourceFrameIdentifier == 2 && mask.Confidence == body.Confidence && mask.ModelVersion == body.ModelVersion);
        Near(.1435, mask.Box.X); Near(.113, mask.Box.Width);
        var down = Batch(body with { Box = body.Box with { Y = .6 } }, 1.1);
        var vertical = MovingObjectMask.Detections([], down, prior, 1, 1.25).Single().Box;
        Near(.59225, vertical.Y); Near(.2155, vertical.Height);
    }

    public static void TestMaskGuards()
    {
        var body = Object(id: Knight) with { Box = new(.01, .01, .05, .1) };
        var current = Batch(body, 10);
        var displayed = new[] { Object(id: "world.sign") };
        foreach (var time in new[] { 9.9, 10.251, double.NaN })
            Check(MovingObjectMask.Detections(displayed, current, null, 1, time).SequenceEqual(displayed));
        Check(MovingObjectMask.Detections(displayed, current, null, 2, 10.1).SequenceEqual(displayed));
        Check(MovingObjectMask.Detections(displayed, Batch(Object(), 10), null, 1, 10.1).SequenceEqual(displayed));
        var fallback = MovingObjectMask.Detections(displayed, current, null, 1, 10.1);
        Check(fallback.Count == 2 && fallback[0] == displayed[0]); Near(.0035, fallback[1].Box.X);
        foreach (var previous in new[] { Batch(body, 10), Batch(body, 9), Batch(body, 10.01),
            Batch(body, 9.9, 2), Batch(body with { Box = new(.8, .8, .05, .1) }, 9.9) })
            Check(MovingObjectMask.Detections(displayed, current, previous, 1, 10.1).SequenceEqual(fallback));
        var edge = MovingObjectMask.Detections([], Batch(body with { Box = new(0, 0, .05, .1) }, 10), null, 1, 10.2).Single();
        Near(0, edge.Box.X); Near(0, edge.Box.Y);
    }

    public static void TestPipelineStreams()
    {
        var frame = Frame(); Paint(frame, 20, 60, 21, 21);
        var body = Object(frame: 42, id: Knight) with { Box = new(.22, .62, .16, .16) };
        var pipeline = new LiveObjectDetectionPipeline(); pipeline.Accept([body], frame, 2, .5, true);
        var snapshot = pipeline.Snapshot(2, 10);
        Check(snapshot.Raw.Single() == body && snapshot.Tracked.Single().Box != body.Box && snapshot.PixelDiagnostic is not null);
        Check(snapshot.Masks.Single().Box.Width > snapshot.Tracked.Single().Box.Width);
        var withoutDiagnostic = new LiveObjectDetectionPipeline(); withoutDiagnostic.Accept([body], frame, 2, .5, false);
        Check(withoutDiagnostic.Snapshot(2, 10).Tracked.SequenceEqual(snapshot.Tracked));
        Check(withoutDiagnostic.Snapshot(2, 10).Masks.SequenceEqual(snapshot.Masks));
        pipeline.Accept([Object()], Frame(9, 1), 2);
        Check(pipeline.Snapshot(2, 10).Raw.Single() == body, "Late inference rewound accepted results.");
        Check(pipeline.Snapshot(2, 10.401) == LiveObjectDetectionSnapshot.Empty
            || pipeline.Snapshot(2, 10.401) is { Tracked.Count: 0, Raw.Count: 0, Masks.Count: 0, PixelDiagnostic: null });
        Check(pipeline.Snapshot(3, 10.1).Masks.Count == 0);
        pipeline.Reset(); Check(pipeline.Snapshot(2, 10).Raw.Count == 0);
        var weak = body with { Confidence = .08f };
        pipeline.Accept([weak], frame, 3, 0);
        var weakSnapshot = pipeline.Snapshot(3, 10);
        Check(weakSnapshot.Tracked.Count == 0 && weakSnapshot.Raw.Count == 1 && weakSnapshot.Masks.Single().Confidence == .08f);
        var masks = GameplaySceneMasks.Create(frame.Width, frame.Height, weakSnapshot.Tracked, null, weakSnapshot.Masks);
        Check(masks.AtlasRegions.Knight is not null && masks.GroundRegions.Knight is not null,
            "Weak measured Knight stopped protecting the live atlas/ground because it was not displayed.");
        pipeline.Accept([body with { Confidence = .7f }], Frame(1), 4);
        Check(pipeline.Snapshot(4, 1).Tracked.Count == 0, "Capture generation retained an old confirmed track.");
    }
}
