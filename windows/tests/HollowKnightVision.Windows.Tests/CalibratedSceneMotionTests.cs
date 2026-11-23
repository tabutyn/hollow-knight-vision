using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;
using Scene = HollowKnightVision.Windows.Core.CalibratedSceneMotionTracker;

internal static class CalibratedSceneMotionTests
{
    public static void TestMeasuredSceneContinuityRequiresTheSamePublishedReference()
    {
        var lineage = new MeasuredAtlasPoseLineage();
        var start = new AtlasPoint(5, 6);
        var end = new AtlasPoint(8, 13);
        var measured = new CalibratedSceneMotionEvidence(end, .05, .10, 7, true, true, .1, .2)
        {
            ReferencePosition = start
        };
        bool CheckMeasurement(CalibratedSceneMotionEvidence scene, long generation = 1,
            string room = "entrance", bool discontinuity = false, bool measuredGround = true)
        {
            lineage.Reset();
            _ = lineage.Observe(1, "entrance", .05, start, measuredGround, null, false);
            return lineage.Observe(generation, room, scene.Timestamp, end, false, scene, discontinuity);
        }
        Check(CheckMeasurement(measured), "Both calibrated axes continue the measured ground reference.");
        Check(!CheckMeasurement(measured with { HorizontalMeasured = false }), "Vertical evidence cannot locate both axes.");
        Check(!CheckMeasurement(measured with { VerticalMeasured = false }), "Horizontal evidence cannot recover height.");
        Check(!CheckMeasurement(measured with { ReferenceTimestamp = .049 }), "A nearby timestamp is a different image.");
        Check(!CheckMeasurement(measured with { ReferencePosition = new(4, 6) }), "A discarded pose cannot anchor saved imagery.");
        Check(!CheckMeasurement(measured with { Position = new(9, 13) }), "The placed pose must match the measured result.");
        Check(!CheckMeasurement(measured with { Timestamp = .23 }), "A stale reference cannot continue an Atlas pose.");
        Check(!CheckMeasurement(measured with { Timestamp = double.NaN }), "An invalid timestamp cannot persist.");
        Check(!CheckMeasurement(measured, room: "exit"), "Room transitions break the measured chain.");
        Check(!CheckMeasurement(measured, generation: 2), "Capture resets discard the old reference.");
        Check(!CheckMeasurement(measured, discontinuity: true), "A ground correction begins a new chain.");
        Check(!CheckMeasurement(measured, measuredGround: false), "An unmeasured frame cannot anchor the Atlas.");

        Check(CheckMeasurement(measured), "Seed the first scenery frame from ground.");
        var next = measured with { ReferenceTimestamp = .10, Timestamp = .15, ReferencePosition = end };
        Check(lineage.Observe(1, "entrance", .15, end, false, next, false),
            "Consecutive scenery frames can share the same measured chain.");
        _ = lineage.Observe(1, "entrance", .14, end, false, null, false);
        Check(!lineage.Observe(1, "entrance", .15, end, false, next, false),
            "A backwards capture timestamp must invalidate exact references.");
    }

    public static void TestBoundedPatchFitRejectsAmbiguityAndForeground()
    {
        var source = Grid();
        var target = Grid(2, -3);
        var point = new AtlasPoint(60, 50);
        var fit = Scene.RegisterPatch(source, target, point)
            ?? throw new InvalidOperationException("The uniquely textured patch did not match.");
        Near(2, fit.X, .01);
        Near(-3, fit.Y, .01);
        Check(Scene.RegisterPatch(source, Grid(8), point) is null, "The optimum is on the search boundary.");
        Check(Scene.RegisterPatch(source, target with { Exclusions = [new StencilRect(50, 35, 30, 30)] },
            point) is null, "A masked patch cannot vote.");
        var black = source with { Pixels = new float[160 * 90] };
        Check(Scene.RegisterPatch(black, black, point) is null, "A flat patch cannot vote.");
        var periodic = source with
        {
            Pixels = Enumerable.Range(0, 160 * 90)
                .Select(i => (float)(i % 160 % 4 * 40 + i / 160 % 4 * 20)).ToArray()
        };
        Check(Scene.RegisterPatch(periodic, periodic, point) is null, "A repeated patch cannot vote.");
    }

    public static void TestExposureChangeAndMissingFrames()
    {
        var tracker = Calibrated();
        var measured = Require(tracker.Observe(Grid(3, -5, .65), .1, 640));
        Check(measured is { HorizontalMeasured: true, VerticalMeasured: true, SupportedPatches: >= 3,
            ReferenceTimestamp: .05 }, "The ground-calibrated axes should report their reference time.");
        Near(-12, measured.Position.X);
        Near(-20, measured.Position.Y);
        Check(tracker.Observe(Grid(4, -7), .5, 640) is null
            && tracker.Observe(Grid(5, -9), .55, 640) is null, "A capture gap must expire calibration.");
    }

    public static void TestDistributedMotionSurvivesMovingOcclusion()
    {
        var tracker = Calibrated();
        var partlyOccluded = Grid(3, -5);
        for (var row = 0; row < 90; row++) Array.Clear(partlyOccluded.Pixels, row * 160 + 40, 80);
        var measured = Require(tracker.Observe(partlyOccluded, .1, 640));
        Check(measured.SupportedPatches >= 3, "The occluder left too little distributed support.");
        Near(-12, measured.Position.X);
        Near(-20, measured.Position.Y);
        Check(tracker.Observe(partlyOccluded with { Pixels = new float[160 * 90] }, .15, 640) is null,
            "A full blackout cannot supply scenery motion.");
    }

    public static void TestHorizontalCalibrationCannotSupplyVerticalMotion()
    {
        var measured = Require(Calibrated(dy: 0).Observe(Grid(3, -2), .1, 640));
        Check(measured.HorizontalMeasured && !measured.VerticalMeasured,
            "Horizontal calibration cannot constrain a fall.");
    }

    public static void TestPatchReplacementPreservesAxisLineage()
    {
        foreach (var calibrateX in new[] { true, false })
        {
            var dx = calibrateX ? 2 : 0;
            var dy = calibrateX ? 0 : -3;
            var tracker = Calibrated(dx, dy);
            var measurements = 0;
            for (var step = 1; step <= 24; step++)
            {
                var sample = tracker.Observe(Grid(dx + step * 2, dy - step), .05 + step * .05, 640);
                if (sample is null) continue;
                measurements++;
                Check(calibrateX ? !sample.VerticalMeasured : !sample.HorizontalMeasured,
                    $"The unknown axis gained calibration at step {step}.");
            }
            Check(measurements > 12, "The test must exercise successive generations of scenery patches.");
        }
    }

    public static void TestUncalibratedPatchesCannotOutvoteMeasuredAxis()
    {
        var tracker = Calibrated(3, -3, cameraShiftX: 10);
        CalibratedSceneMotionEvidence? previous = null;
        for (var step = 1; step <= 22; step++)
            previous = Require(tracker.Observe(Grid(3, -3 - step * 2), .05 + step * .05, 640));
        var before = Require(previous);
        var after = Require(tracker.Observe(Grid(6, -47), 1.2, 640));
        Check(after.HorizontalMeasured, "A surviving measured horizontal scale was discarded.");
        Near(-10, after.Position.X - before.Position.X);
        Near(0, after.Position.Y - before.Position.Y);
    }

    public static void TestGameplayUsesOnlyCalibratedAxes()
    {
        var current = new AtlasPoint(10, 5);
        var coarse = new AtlasPoint(12, 7);
        var mask = new AtlasPoint(16, 8);
        var masked = new TransitionMotionBridgeResult(mask, new(6, 3), .9f, TransitionMotionSource.MaskedRegistration);
        var scene = new CalibratedSceneMotionEvidence(new(18, 22), .05, .10, 7, false, true, .1, .2);
        Check(VisualGameplayTracker.SelectFloorlessPose(current, scene, masked, coarse, null)
            == new FloorlessPoseCandidate(new(16, 22), FloorlessPoseCandidateSource.CalibratedScene),
            "Vertical-only scenery must retain horizontal position from the current registration.");
        Check(VisualGameplayTracker.SelectFloorlessPose(current, scene, null, coarse, null)?.Position
            == new AtlasPoint(12, 22), "Coarse horizontal motion fills a missing masked registration.");
        Check(VisualGameplayTracker.SelectFloorlessPose(current, scene with { HorizontalMeasured = true },
            masked, coarse, null)?.Position == scene.Position, "Two measured axes can carry the scenery pose.");
        Check(VisualGameplayTracker.SelectFloorlessPose(current, scene with { VerticalMeasured = false },
            masked, coarse, null) == new FloorlessPoseCandidate(mask, FloorlessPoseCandidateSource.MaskedRegistration),
            "A missing vertical calibration leaves the masked pose in control.");
    }

    public static void TestGameplayCalibratesSceneryFromTrackedGround()
    {
        const int width = 640, height = 360;
        var visual = new VisualGameplayTracker();
        var pipeline = new GameplayTrackingPipeline(visual, new MaskedFrameMotionTracker(), deterministicGlobalSearch: true);
        var stationaryScene = new LowResolutionMotionGrid(64, 36, new byte[64 * 36]);
        var signal = new FrameSignalProfile(64 * 36, 64 * 36, 64 * 36, 100, stationaryScene);
        var identity = new LowResolutionTranslationDiagnostic(null, null, null, ExactIdentity: true);
        GameplayTrackedFrame Observe(int index, int dx, int dy, bool floorVisible, bool hasCommitted,
            bool resumed = false)
        {
            var time = index * .05;
            var frame = new BgraFrame(width, height, width * 4, new byte[width * height * 4],
                index, DateTimeOffset.UnixEpoch.AddSeconds(time));
            var pixels = Grid(dx, dy).Pixels;
            var luma = new byte[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                luma[y * width + x] = (byte)pixels[y / 4 * 160 + x / 4];
            if (!floorVisible) Array.Clear(luma, (155 + dy * 4) * width, 95 * width);
            DetectedFloorLine[] floors = floorVisible ? [new(200 + dy * 4, new PixelSpan(0, width - 1))] : [];
            var edgePixels = new byte[luma.Length];
            var edges = new GroundEdgeAnalysis(width, height, edgePixels, luma);
            var comparison = new GroundComparisonAnalysis(width, height, edgePixels, luma);
            var theory = new GroundTheoryAnalysis(comparison, GroundTheoryTuning.Default, [], floors);
            var prepared = new GameplayTrackingPreparation(new GameplayTerrainAnalysis(edges, theory, floors, floors),
                identity, [], []);
            return pipeline.Track(frame, prepared, null, new(true, !resumed, resumed, new(true, resumed)),
                signal, time, 1, "entrance", hasCommitted, null, false);
        }

        foreach (var hasCommitted in new[] { false, true })
        {
            pipeline.Reset();
            visual.Reset();
            for (var index = 0; index < 19; index++) _ = Observe(index, 0, 0, true, hasCommitted);
            var groundSeed = Observe(19, 0, 0, true, hasCommitted);
            for (var index = 20; index < 24; index++)
                _ = Observe(index, (index - 19) * 2, (index - 19) * -3, true, hasCommitted);
            var measured = Observe(24, 10, -15, true, hasCommitted).Observation;
            Check(measured.GroundVerified && measured.GroundTracking.LocalMatchedImageDisplacement is not null,
                "Ground must measure the first camera step.");
            var falling = Observe(25, 11, -17, false, hasCommitted);
            var motion = falling.Observation;
            Check(motion.Source == "calibrated-scene"
                && motion.FloorlessEvidence?.Scene is { VerticalMeasured: true, HorizontalMeasured: true },
                $"Expected scenery, got {motion.Source} with {motion.FloorlessEvidence}. "
                + $"Ground matched {measured.GroundTracking.LocalMatchedImageDisplacement} "
                + $"at {measured.GroundTracking.LocalReferenceTimestamp}.");
            Near(measured.Telemetry.CameraY - 8, motion.Telemetry.CameraY);
            Check(falling.DisplayCamera == new AtlasPoint(motion.Telemetry.CameraX, motion.Telemetry.CameraY),
                "The Atlas overlay must use the selected scenery pose.");
            Check(!motion.AllowsAtlasWrite && falling.AllowsAtlasWrite == hasCommitted
                && falling.HasMeasuredSceneryContinuity == hasCommitted,
                "Calibrated scenery may continue an existing Atlas and cannot bootstrap a new one.");
            if (hasCommitted) CheckWorldPersistence(groundSeed, falling);

            pipeline.ResetMotionReference();
            visual.PauseGroundVerification();
            var resumed = Observe(26, 14, -21, false, hasCommitted, resumed: true);
            Check(resumed.Observation.FloorlessEvidence is null && !resumed.AllowsAtlasWrite,
                "Scenery must release its pose reference across a cut.");
        }
    }

    private static void CheckWorldPersistence(GameplayTrackedFrame seed, GameplayTrackedFrame falling)
    {
        const int width = 640, height = 360;
        var path = Path.Combine(Path.GetTempPath(), $"hkv-calibrated-scene-world-{Guid.NewGuid():N}");
        try
        {
            var world = new WindowsLiveWorldPipeline(path);
            WindowsLiveWorldPipelineResult Write(GameplayTrackedFrame tracked, int id, double timestamp)
            {
                Check(tracked.AllowsAtlasWrite, "The production tracking pipeline must admit the frame.");
                var frame = new BgraFrame(width, height, width * 4, new byte[width * height * 4],
                    id, DateTimeOffset.UnixEpoch.AddSeconds(timestamp));
                return world.Process(new(frame, frame, timestamp, timestamp, tracked.DisplayCamera, width, [],
                    [new(0, 0, width, height)], 1, tracked.RoomId, 1, 64, tracked.Observation.GroundVerified));
            }
            var ground = Write(seed, 19, .95);
            Check(ground.AllowsAtlasWrite, ground.Diagnostic);
            var scenery = Write(falling, 25, 1.25);
            Check(scenery.AllowsAtlasWrite, scenery.Diagnostic);
            var saved = new LiveWorldSessionStore(path).Load().Snapshot;
            Check(saved.Observations.Count == 2 && saved.Observations[^1].RawPose == falling.DisplayCamera,
                "The reopened world did not keep the measured scenery position.");
            Check(saved.Landmarks.Count == 0, "A scenery-only frame cannot provide ground landmarks.");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static Scene Calibrated(int dx = 2, int dy = -3, double? cameraShiftX = null)
    {
        var tracker = new Scene();
        var x = cameraShiftX ?? dx * 4;
        Check(tracker.Observe(Grid(), 0, 640) is null, "Calibration needs two frames.");
        Check(tracker.Observe(Grid(dx, dy), .05, 640, new(new(-x, dy * 4), 0, new(x, dy * 4))) is null,
            "Calibration prepares the reference for subsequent frames.");
        return tracker;
    }

    private static Scene.Grid Grid(int dx = 0, int dy = 0, double exposure = 1)
    {
        const int width = 160, height = 90;
        var pixels = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var hash = unchecked((ulong)(x - dx + 1000) * 0x9e3779b185ebca87uL);
            hash ^= unchecked((ulong)(y - dy + 1000) * 0xc2b2ae3d27d4eb4fuL);
            hash ^= hash >> 33;
            hash = unchecked(hash * 0xff51afd7ed558ccduL);
            pixels[y * width + x] = (float)(unchecked((byte)(hash >> 24)) * exposure);
        }
        return new(width, height, pixels, []);
    }

    private static CalibratedSceneMotionEvidence Require(CalibratedSceneMotionEvidence? sample) =>
        sample ?? throw new InvalidOperationException("Expected a measured scenery pose.");

    private static void Near(double expected, double actual, double tolerance = .1) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
