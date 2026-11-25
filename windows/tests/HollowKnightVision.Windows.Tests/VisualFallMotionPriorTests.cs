using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class VisualFallMotionPriorTests
{
    private static readonly WorldSize Size = new(640, 360);
    private static readonly double Speed = VisualFallMotionPrior.TerminalUnitsPerSecond * VisualFallMotionPrior.PixelsPerUnitAt640;

    public static void TestPlayerFallAndCameraClampAreSeparate()
    {
        var prior = Seed(time => 100, time => 250 - Speed * time);
        var result = Require(prior.Predict(Box(250 - Speed * .1), .1, Size, 640));
        Near(result.CameraY, 100);
        Check(result.MinimumY < 100 && result.MaximumY > 100, "The expected interval must include the clamped camera.");
    }

    public static void TestFollowingCameraCanMoveFasterThanPlayer()
    {
        var prior = Seed(time => 100 - Speed * 1.4 * time, time => 180 + Speed * .4 * time);
        var result = Require(prior.Predict(Box(180 + Speed * .4 * .1), .1, Size, 640));
        Near(result.CameraY, 100 - Speed * 1.4 * .1);
        Check(result.MinimumY < result.CameraY && result.MaximumY > result.CameraY,
            "The calibrated speed limits player motion without limiting the following camera.");
        Check(prior.Predict(null, .1, Size, 640) is null
            && prior.Predict(Box(180), .2, Size, 640) is null
            && prior.Predict(Box(180), double.NaN, Size, 640) is null
            && prior.Predict(Box(180), .1, Size, 320) is null
            && prior.Predict(Box(180), .1, new(640, 480), 640) is null
            && prior.Predict(Box(180) with { Height = 36 }, .1, Size, 640) is null
            && prior.Predict(Box(180) with { X = -5 }, .1, Size, 640) is null,
            "A missing, old, shifted, differently scaled or malformed Knight cannot supply a prediction.");
        prior.ObserveMeasured(100, Box(180), .11, Size, 640);
        Check(prior.Predict(Box(180), .13, Size, 640) is null, "A fresh landing ends the terminal-velocity streak.");
    }

    public static void TestInsufficientEvidenceAndBlackoutCannotStartPrediction()
    {
        var prior = new VisualFallMotionPrior();
        prior.ObserveMeasured(0, Box(180), 0, Size, 640);
        prior.ObserveMeasured(0, Box(180 - Speed * .025), .025, Size, 640);
        Check(prior.Predict(Box(180 - Speed * .05), .05, Size, 640) is null, "One measured interval is insufficient.");
        prior = Seed(time => 0, time => 250 - Speed * time);
        prior.ObserveMeasured(0, null, .08, Size, 640);
        Check(prior.Predict(Box(200), .09, Size, 640) is null, "Blackout clears terminal evidence.");
    }

    public static void TestGameplayFallPredictionUpdatesOnlyPresentation()
    {
        const int width = 320, height = 180;
        const double interval = .025;
        var speed = Speed / 2;
        var camera = new VisualGameplayTracker();
        var pipeline = new GameplayTrackingPipeline(camera, new MaskedFrameMotionTracker(), deterministicGlobalSearch: true);
        var signal = new FrameSignalProfile(2304, 2304, 2304, 110);
        var ground = new byte[width * height];
        var missing = new byte[ground.Length];
        new Random(307).NextBytes(ground);
        var pixels = new byte[ground.Length * 4];
        GameplayTrackedFrame last = null!;
        for (var index = 0; index < 20; index++)
        {
            var body = index < 16 ? null : (StencilRect?)Body(120 - speed * (index - 16) * interval);
            last = Observe(index, hasFloor: true, body);
            if (index >= 16)
                Check(GroundMotionEvidence.IsMeasured(last.Ground), "The fall fixture must start with verified ground measurements.");
        }

        var anchor = camera.Position;
        Check(last.Reference is not null, "The fall fixture must retain a ground reference before the miss.");
        var afterMiss = Observe(20, hasFloor: false, Body(120 - speed * 3.5 * interval));
        var evidence = afterMiss.Observation.FloorlessEvidence;
        Check(evidence is { SelectedSource: FloorlessPoseCandidateSource.FallPrediction, FallPrediction: not null }
            && afterMiss.Observation.Source == "fallPrediction", "A missing floor and fresh body should select the short display prediction.");
        Near(afterMiss.DisplayCamera.X, anchor.X);
        Near(afterMiss.DisplayCamera.Y, anchor.Y - speed * .5 * interval);
        Check(camera.Position == anchor && afterMiss.Reference == last.Reference
            && !afterMiss.AllowsAtlasWrite && afterMiss.WorldEvidence is null
            && !afterMiss.HasMeasuredSceneryContinuity && afterMiss.MeasuredLineage is null,
            "The prediction changed an authoritative position, seeded a ground reference, or granted the Atlas a write.");
        Check(last.MeasuredLineage is { } lineage && afterMiss.FallDisplayReference == (lineage, 19 * interval),
            "The prediction must retain only the display basis of its last measured capture.");

        var registered = Observe(21, hasFloor: false, Body(120 - speed * 4.5 * interval),
            new(null, null, null, ExactIdentity: true));
        Check(registered.Observation.FloorlessEvidence?.SelectedSource == FloorlessPoseCandidateSource.MaskedRegistration
            && !registered.Observation.UsesFallPrediction, "An independent image registration must outrank a prediction.");
        var missingBody = Observe(22, hasFloor: false, body: null);
        Check(!missingBody.Observation.UsesFallPrediction, "A motion mask without a fresh detection cannot authorize the fallback.");
        var expired = Observe(23, hasFloor: false, Body(120 - speed * 5.5 * interval));
        Check(!expired.Observation.UsesFallPrediction, "A predicted capture cannot refresh the last measured age.");

        var recorder = new InputPathRecorder();
        Check(recorder.Start(0), "Recording fixture did not start.");
        recorder.RecordTracking(InputPathTrackingObservation.FromGround(afterMiss.Ground), 20 * interval,
            publishedCameraPosition: afterMiss.DisplayCamera, poseSource: afterMiss.Observation.Source,
            fallPrediction: evidence!.FallPrediction, observedAt: 20 * interval);
        var path = recorder.Stop(1);
        var recorded = path?.TrackingSamples.Single() ?? throw new Exception("Fall prediction was not recorded.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var wire = JsonSerializer.Serialize(recorded, options);
        var restored = JsonSerializer.Deserialize<RecordedTrackingSample>(wire, options);
        var prediction = evidence.FallPrediction!.Value;
        Check(restored?.PoseSource == "fallPrediction" && restored.FallPredictionAge == prediction.Age
            && restored.FallPredictionMinimumY == prediction.MinimumY && restored.FallPredictionMaximumY == prediction.MaximumY,
            "Recorded runs must retain the prediction age and height interval for diagnosis.");

        GameplayTrackedFrame Observe(int index, bool hasFloor, StencilRect? body,
            LowResolutionTranslationDiagnostic? motion = null)
        {
            var time = index * interval;
            var frame = new BgraFrame(width, height, width * 4, pixels, index, DateTimeOffset.UnixEpoch.AddSeconds(time));
            DetectedFloorLine[] lines = hasFloor ? [new(140, new(0, width - 1))] : [];
            var luma = hasFloor ? ground : missing;
            var edges = new GroundEdgeAnalysis(width, height, missing, luma);
            var comparison = new GroundComparisonAnalysis(width, height, missing, luma);
            var theory = new GroundTheoryAnalysis(comparison, GroundTheoryTuning.Default, [], lines);
            var prepared = new GameplayTrackingPreparation(new(edges, theory, lines, lines), motion, [], [])
            {
                ObservedPlayerBounds = body
            };
            return pipeline.Track(frame, prepared, new PixelRect(145, 106, 16, 14),
                new(true, true, false, new(true, false)), signal, time, 1, "fixture", true,
                buttons: null, resetWorldCoordinatesForCorrection: false, captureWorldEvidence: true);
        }

        static StencilRect Body(double screenY) => new(148, height - screenY - 6, 10, 12);
    }

    private static VisualFallMotionPrior Seed(Func<double, double> camera, Func<double, double> knight)
    {
        var prior = new VisualFallMotionPrior();
        for (var index = 0; index < 4; index++)
        {
            var time = index * .025;
            prior.ObserveMeasured(camera(time), Box(knight(time)), time, Size, 640);
        }
        return prior;
    }

    private static StencilRect Box(double y) => new(300, Size.Height - y - 12, 16, 24);
    private static VisualFallPrediction Require(VisualFallPrediction? prediction) =>
        prediction ?? throw new Exception("Expected a short fall prediction.");
    private static void Near(double actual, double expected) =>
        Check(Math.Abs(actual - expected) < .001, $"Expected {expected}, got {actual}.");
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
