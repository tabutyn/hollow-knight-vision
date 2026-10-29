using System.Diagnostics;
using HollowKnightVision.Windows.Core;

internal static class GroundCameraSolverTests
{
    private static void Check(bool value, string message = "Ground registration parity assertion failed.") { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, double tolerance = .000001) => Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}; got {actual}.");
    private static GroundCameraSolver.Solution Required(GroundCameraSolver.Solution? value) => value ?? throw new Exception("Expected a verified texture solution.");
    private static void Vector(CameraVector expected, CameraVector actual, double tolerance = .000001) { Near(expected.X, actual.X, tolerance); Near(expected.Y, actual.Y, tolerance); }
    internal static byte[] Pixels(int width = 160, int height = 100, int dx = 0, int dy = 0, ulong seed = 0)
    {
        var result = new byte[width * height];
        for (var index = 0; index < result.Length; index++)
        {
            var x = index % width - dx; var y = index / width - dy;
            if (x < 0 || x >= width || y < 0 || y >= height) continue;
            var hash = (ulong)x * 73_856_093 ^ (ulong)y * 19_349_663 ^ seed;
            hash ^= hash >> 13; hash = unchecked(hash * 1_274_126_177); result[index] = unchecked((byte)(hash >> 11));
        }
        return result;
    }
    private static GroundTextureLine[] Lines(int row = 40, int end = 143) => [new(row, 16, end)];
    private static GroundCameraSolver.Solution? Solve(GroundCameraSolver solver, int dx, int dy, int? row, double time) =>
        solver.Solve(Pixels(dx: dx, dy: dy), 160, 100, row is { } y ? Lines(y) : [], time, []);
    private static GroundCameraSolver Seeded() { var solver = new GroundCameraSolver(); Required(Solve(solver, 0, 0, 40, 0)); return solver; }
    public static void TestPatchConsensusRejectsForeground()
    {
        const int width = 440, height = 100;
        var source = Pixels(width, height);
        var target = Pixels(width, height, dx: -19);
        var patches = new List<GroundCameraSolver.Patch>();
        for (var i = 0; i < 10; i++)
        {
            var x = 40 + i * 36;
            patches.Add(new(x, 40, GroundCameraSolver.ReadPatch(source, width, height, x, 40)!));
            if (i < 3)
                for (var row = 0; row < 12; row++)
                    source.AsSpan((40 + row) * width + x, 16).CopyTo(target.AsSpan((40 + row) * width + x - 24, 16));
        }
        var fit = GroundPatchConsensus.Refine(patches, target, width, height, -21, 0, []);
        Check(fit is not null && fit.Dx == -19 && fit.Dy == 0 && fit.Patches.Length >= 7,
            "Foreground parallax must not pull the independent floor consensus to a compromise displacement.");
        Check(GroundPatchConsensus.Refine(patches.Take(3).ToArray(), target, width, height, -21, 0, []) is null,
            "A sparse cluster cannot authorize consensus.");
        Check(GroundPatchConsensus.Refine(patches, Pixels(width, height, seed: 98), width, height, -21, 0, []) is null,
            "Unrelated texture must not establish a camera pose.");
    }
    public static void TestSustainedFallbackRetainsBaseline()
    {
        const int width = 320, height = 100;
        var original = Pixels(width, height);
        byte[] Scene(int dx, int noise)
        {
            var scene = Pixels(width, height, seed: 99887);
            for (var y = 40; y < 52; y++) for (var x = 16; x < 80; x++)
                scene[y * width + x] = (byte)Math.Clamp(original[y * width + x] + ((x + y) % 2 == 0 ? noise : -noise), 0, 255);
            var moved = new byte[scene.Length];
            for (var y = 0; y < height; y++) scene.AsSpan(y * width, width - dx).CopyTo(moved.AsSpan(y * width + dx));
            return moved;
        }
        var solver = new GroundCameraSolver(broadSearchEveryFrame: true, maximumLocalSearchRadius: 16);
        solver.BeginObservation();
        var seed = Required(solver.Solve(original, width, height, Lines(40, 79), 0, []));
        solver.EndObservation(seed.MatchSource);
        solver.BeginObservation();
        var initial = Required(solver.Solve(Scene(0, 9), width, height, Lines(40, 303), .1, []));
        solver.EndObservation(initial.MatchSource);
        var position = initial.ImageTranslation.X; var recentFrames = 0; var oldestAge = 0.0;
        for (var i = 1; i <= 9; i++)
        {
            var now = .1 + i / 30.0;
            solver.BeginObservation();
            var result = Required(solver.Solve(Scene(i * 3, 13), width, height, Lines(40, 303), now, []));
            solver.EndObservation(result.MatchSource);
            position += result.ImageTranslation.X;
            Near(i * 3, position, .3);
            if (result.MatchSource == "recent")
            {
                recentFrames++;
                oldestAge = Math.Max(oldestAge, now - result.ReferenceTimestamp!.Value);
            }
        }
        Check(recentFrames >= 5 && oldestAge >= .15,
            $"Fallback did not retain an overlapping baseline: {recentFrames} frames, age {oldestAge}.");
        solver.BeginObservation();
        solver.ApplyGlobalCorrection(new(2, 0), Scene(27, 13), Lines(40, 303), .44, []);
        solver.EndObservation("recent");
        var recentField = typeof(GroundCameraSolver).GetField("recentReference",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var reference = recentField.GetValue(solver)!;
        Near(.44, (double)reference.GetType().GetProperty("Timestamp")!.GetValue(reference)!);
    }

    public static void TestRecentMajorityOverridesSparseForegroundAnchor()
    {
        const int width = 640, height = 100;
        var original = Pixels(width, height);
        var recent = Pixels(width, height, seed: 331);
        // Eight old patches remain visible while the rest of the image changes.
        for (var y = 40; y < 52; y++)
            original.AsSpan(y * width + 16, 128).CopyTo(recent.AsSpan(y * width + 16));
        var lines = new GroundTextureLine[] { new(40, 16, 623) };
        var solver = new GroundCameraSolver(broadSearchEveryFrame: true, maximumLocalSearchRadius: 32);
        Required(solver.Solve(original, width, height, [new(40, 16, 143)], 0, []));
        Required(solver.Solve(recent, width, height, lines, .1, []));
        var current = Pixels(width, height, seed: 552);
        for (var y = 0; y < height; y++)
            recent.AsSpan(y * width, width - 4).CopyTo(current.AsSpan(y * width + 4));
        // The surviving old strip moves two pixels faster than the ground.
        // Its perfect but sparse match must not override the broad recent view.
        for (var y = 40; y < 52; y++)
            recent.AsSpan(y * width + 16, 128).CopyTo(current.AsSpan(y * width + 22));
        var result = Required(solver.Solve(current, width, height, lines, .1 + 1.0 / 30, []));
        Check(result.MatchSource == "recent" && result.Support >= 12,
            "A sparse low-error foreground anchor bypassed independent recent ground consensus.");
        Vector(new(4, 0), solver.ImagePosition, .1);
    }

    public static void TestReferencePromotion()
    {
        Check(GroundReferencePromotionPolicy.Permits(4, 10)); Check(!GroundReferencePromotionPolicy.Permits(4, 10.01));
        Check(!GroundReferencePromotionPolicy.Permits(5, 14.5)); Check(GroundReferencePromotionPolicy.Permits(6, 14.5));
        Check(GroundReferencePromotionPolicy.Permits(12, 18)); Check(!GroundReferencePromotionPolicy.Permits(12, 18.01));
    }
    public static void TestPoseAcceptance()
    {
        Check(!GroundPoseAcceptancePolicy.Rejects(4, 14)); Check(GroundPoseAcceptancePolicy.Rejects(4, 14.01));
        Check(!GroundPoseAcceptancePolicy.Rejects(5, 14.01)); Check(GroundPoseAcceptancePolicy.Rejects(7, 18.01));
        Check(!GroundPoseAcceptancePolicy.Rejects(8, 18.01));
    }
    public static void TestInnovation()
    {
        Check(GroundPoseInnovationPolicy.Rejects(9, 19.45, 11.48)); Check(!GroundPoseInnovationPolicy.Rejects(12, 19.45, 11.48));
        Check(!GroundPoseInnovationPolicy.Rejects(9, 19, 11.48)); Check(!GroundPoseInnovationPolicy.Rejects(9, 19.45, 8));
    }
    public static void TestProvisionalPromotion()
    {
        Check(!GroundProvisionalPromotionPolicy.Permits(.199, 12, 6, 18)); Check(!GroundProvisionalPromotionPolicy.Permits(.2, 11, 6, 18));
        Check(!GroundProvisionalPromotionPolicy.Permits(.2, 12, 5, 10.01)); Check(!GroundProvisionalPromotionPolicy.Permits(.2, 12, 6, 18.01));
        Check(GroundProvisionalPromotionPolicy.Permits(.2, 12, 6, 18)); Check(GroundProvisionalPromotionPolicy.Permits(.2, 12, 4, 10));
    }
    public static void TestRecentCadence()
    {
        Check(!GroundRecentReferenceCadence.ShouldRefresh(.079, 15.99)); Check(GroundRecentReferenceCadence.ShouldRefresh(.08, 0));
        Check(GroundRecentReferenceCadence.ShouldRefresh(0, 16));
    }
    public static void TestMotionScale()
    {
        var solver = new GroundCameraSolver(imageMotionScale: new(.9, .8)); Required(Solve(solver, 0, 0, 40, 0));
        var moved = Required(Solve(solver, -12, 10, 50, 1.0 / 60)); Vector(new(-12, 10), moved.MatchedImageDisplacement!.Value);
        Vector(new(-10.8, 8), moved.ImageTranslation);
    }
    public static void TestCameraCoordinateConversion()
    {
        var scale = new CameraVector(1.2, .8);
        var raw = new GroundCameraSolver(broadSearchEveryFrame: true);
        var converted = new GroundCameraSolver(broadSearchEveryFrame: true, cameraCoordinateScale: scale);
        CameraVector Convert(CameraVector v) => new(v.X * scale.X, v.Y * scale.Y);
        for (var i = 0; i < 20; i++)
        {
            var x = i < 12 ? i * 4 : (20 - i) * 4;
            var y = i % 5;
            var pixels = Pixels(320, 160, x, y); var lines = Lines(40 + y, 303); var time = i / 30d;
            var a = Required(raw.Solve(pixels, 320, 160, lines, time, []));
            var b = Required(converted.Solve(pixels, 320, 160, lines, time, []));
            Check(a.MatchedImageDisplacement == b.MatchedImageDisplacement && a.Support == b.Support
                && a.MatchSource == b.MatchSource, "Camera calibration changed registration decisions.");
            Vector(Convert(a.ImageTranslation), b.ImageTranslation);
            Vector(Convert(raw.ImagePosition), converted.ImagePosition);
            if (a.ReferencePosition is { } reference) Vector(Convert(reference), b.ReferencePosition!.Value);
            if (i == 5)
            {
                var correction = new CameraVector(2, -1);
                raw.ApplyGlobalCorrection(correction, pixels, lines, time, []);
                converted.ApplyGlobalCorrection(Convert(correction), pixels, lines, time, []);
            }
            if (i == 10)
            {
                var translation = new CameraVector(1, 2);
                raw.AcceptFeatureRecovery(pixels, 320, 160, lines, time, [], translation);
                converted.AcceptFeatureRecovery(pixels, 320, 160, lines, time, [], Convert(translation));
            }
            if (i == 15)
            {
                var correction = new CameraVector(-1, 2);
                raw.FollowMeasuredCamera(correction, pixels, 320, 160, lines, time, []);
                converted.FollowMeasuredCamera(Convert(correction), pixels, 320, 160, lines, time, []);
            }
            Vector(Convert(raw.ImagePosition), converted.ImagePosition);
        }
    }

    public static void TestStationaryRefresh()
    {
        foreach (var (dx, dy) in new[] { (1, 0), (2, 0), (-2, 0), (0, 1), (0, -2), (1, 1) })
        {
            var solver = new GroundCameraSolver(); var position = CameraVector.Zero;
            foreach (var (x, y, time) in new[] { (0, 0, 0.0), (dx, dy, .016), (dx, dy, 1.2), (dx, dy, 1.22), (0, 0, 1.24) })
            {
                var result = Required(solver.Solve(Pixels(320, 160, x, y), 320, 160, Lines(40 + y, 303), time, []));
                position = new(position.X + result.ImageTranslation.X, position.Y + result.ImageTranslation.Y); Vector(new(x, y), position);
            }
        }
    }
    private static (byte[] Original, byte[] Current, byte[] Recent, byte[] Continued) WeakOldAnchor()
    {
        var original = Pixels(); var current = Pixels(seed: 99887); var recent = Pixels(seed: 777); var continued = Pixels(seed: 999);
        for (var y = 40; y < 52; y++) for (var x = 24; x < 72; x++)
            current[y * 160 + x] = (byte)Math.Clamp(original[y * 160 + x - 8] + ((x + y) % 2 == 0 ? 4 : -4), 0, 255);
        for (var y = 0; y < 100; y++)
        {
            for (var x = 4; x < 160; x++) recent[y * 160 + x] = current[y * 160 + x - 4];
            for (var x = 0; x < 156; x++) continued[y * 160 + x] = current[y * 160 + x + 4];
        }
        return (original, current, recent, continued);
    }
    public static void TestConsensusReferenceCalibration()
    {
        var data = WeakOldAnchor();
        var solver = new GroundCameraSolver(calibratedMotionEnabled: true, broadSearchEveryFrame: true);
        var scale = GroundCameraSolver.BroadCalibratedCameraScale.X;
        Required(solver.Solve(data.Original, 160, 100, Lines(40, 95), 0, []));
        solver.FollowMeasuredCamera(new(4 * scale, 0), data.Recent, 160, 100, Lines(), 1.0/60, []);
        var recent = Required(solver.Solve(data.Current, 160, 100, Lines(), 2.0/60, []));
        Check(recent.MatchSource != "anchor", "Fixture did not exercise a reference handoff.");
        Vector(new(-4 * scale, 0), recent.ImageTranslation);
        Vector(new(-4 * scale, 0), Required(solver.Solve(data.Continued, 160, 100, Lines(), 3.0/60, [])).ImageTranslation);
    }
    public static void TestStrongerRecentStrip()
    {
        var data = WeakOldAnchor();
        foreach (var count in new[] { 5, 8 })
        {
            var solver = new GroundCameraSolver(); var broad = Lines(40, 16 + count * 16 - 1);
            Required(solver.Solve(data.Original, 160, 100, Lines(40, 95), 0, []));
            solver.FollowMeasuredCamera(new(4, 0), data.Recent, 160, 100, broad, 1.0 / 60, []);
            Vector(new(-4, 0), Required(solver.Solve(data.Current, 160, 100, broad, 2.0 / 60, [])).ImageTranslation);
            Vector(new(-4, 0), Required(solver.Solve(data.Continued, 160, 100, broad, 3.0 / 60, [])).ImageTranslation);
        }
    }
    public static void TestProvisionalRecovery()
    {
        var data = WeakOldAnchor(); var solver = new GroundCameraSolver(); var lines = Lines(40, 95);
        Required(solver.Solve(data.Original, 160, 100, lines, 0, []));
        Check(solver.Solve(data.Recent, 160, 100, [], 1.0 / 60, []) is null);
        solver.FollowMeasuredCamera(new(4, 0), data.Recent, 160, 100, lines, 1.0 / 60, []);
        var provisional = Required(solver.Solve(data.Current, 160, 100, lines, 2.0 / 60, []));
        Check(!provisional.ReferenceTrusted); Vector(new(-4, 0), provisional.ImageTranslation);
        var recovered = Required(solver.Solve(data.Original, 160, 100, lines, 3.0 / 60, []));
        Check(recovered.ReferenceTrusted && recovered.MatchSource == "anchor");
    }
    public static void TestStaticDistractor()
    {
        var solver = new GroundCameraSolver(); var original = Pixels(160, 160);
        GroundTextureLine[] old = [new(20, 16, 63), new(80, 16, 143), new(120, 16, 143)];
        Required(solver.Solve(original, 160, 160, old, 0, [])); var current = Pixels(160, 160, dy: 10);
        for (var i = 0; i < current.Length; i++) current[i] = (byte)Math.Min(255, current[i] + i % 3);
        for (var y = 20; y < 32; y++) for (var x = 16; x <= 63; x++) current[y * 160 + x] = original[y * 160 + x];
        var result = Required(solver.Solve(current, 160, 160, [old[0], new(90, 16, 143), new(130, 16, 143)], 1.0 / 60, []));
        Vector(new(0, 10), result.ImageTranslation);
    }
    public static void TestGlobalCorrection()
    {
        var solver = Seeded(); Required(Solve(solver, -4, 0, 40, 1.0 / 60));
        solver.ApplyGlobalCorrection(new(3, 25), Pixels(dx: -4), Lines(), 1.0 / 60, []);
        Vector(CameraVector.Zero, Required(Solve(solver, -4, 0, 40, 2.0 / 60)).ImageTranslation);
    }
    public static void TestRepeatedHorizontalAcceleration()
    {
        var solver = Seeded(); Required(Solve(solver, -4, 0, 40, 1.0 / 60)); Required(Solve(solver, -4, 0, 40, 2.0 / 60));
        Vector(new(-13, 0), Required(Solve(solver, -17, 0, 40, 3.0 / 60)).ImageTranslation);
    }
    public static void TestHorizontalHandoffs()
    {
        var solver = new GroundCameraSolver(); var position = CameraVector.Zero;
        var shifts = Enumerable.Range(0, 71).Select(index => -index * 4).Concat(Enumerable.Range(0, 70).Select(index => -276 + index * 4)).ToArray();
        for (var i = 0; i < shifts.Length; i++)
        {
            var result = Required(solver.Solve(Pixels(640, 360, shifts[i]), 640, 360, Lines(260, 623), i / 60.0, []));
            position = new(position.X + result.ImageTranslation.X, position.Y + result.ImageTranslation.Y); Vector(new(shifts[i], 0), position);
        }
        Vector(CameraVector.Zero, position);
    }
    public static void TestVerticalHandoffs()
    {
        var solver = new GroundCameraSolver(); var position = CameraVector.Zero;
        var shifts = Enumerable.Range(0, 25).Select(index => index * 4).Concat(Enumerable.Range(0, 24).Select(index => 92 - index * 4)).ToArray();
        for (var i = 0; i < shifts.Length; i++)
        {
            var result = Required(solver.Solve(Pixels(320, 200, dy: shifts[i]), 320, 200, Lines(40 + shifts[i], 303), i / 60.0, []));
            position = new(position.X + result.ImageTranslation.X, position.Y + result.ImageTranslation.Y); Vector(new(0, shifts[i]), position);
        }
        Vector(CameraVector.Zero, position);
    }
    public static void TestOddPhaseRoundTrip()
    {
        var solver = Seeded(); Vector(new(-3, 5), Required(Solve(solver, -3, 5, 45, 1.0 / 60)).ImageTranslation);
        Vector(new(3, -5), Required(Solve(solver, 0, 0, 40, 2.0 / 60)).ImageTranslation);
    }
    public static void TestOnePixelJitter()
    {
        var solver = Seeded(); for (var i = 1; i <= 8; i++) Vector(CameraVector.Zero, Required(Solve(solver, 0, 0, i % 2 == 0 ? 40 : 41, i / 60.0)).ImageTranslation);
    }
    public static void TestEdgeDropout()
    {
        var solver = Seeded(); Near(-3, Required(Solve(solver, -3, 0, null, 1.0 / 60)).ImageTranslation.X);
        Check(solver.Solve(Enumerable.Repeat((byte)90, 16000).ToArray(), 160, 100, [], 2.0 / 60, []) is null);
    }
    public static void TestFeatureRecovery()
    {
        var solver = Seeded(); Required(Solve(solver, -4, 4, 44, 1.0 / 60));
        solver.AcceptFeatureRecovery(Pixels(dx: -14, dy: 14), 160, 100, Lines(54), 2.0 / 60, [], new(-10, 10));
        Vector(new(-4, 4), Required(Solve(solver, -18, 18, 58, 3.0 / 60)).ImageTranslation);
    }
    public static void TestThreePixelJitter()
    {
        var solver = Seeded(); var rows = new[] { 42, 43, 38, 37, 40 };
        for (var i = 0; i < rows.Length; i++) Vector(CameraVector.Zero, Required(Solve(solver, 0, 0, rows[i], (i + 1) / 60.0)).ImageTranslation);
    }
    public static void TestRepeatedVerticalPan()
    {
        var solver = Seeded(); Required(Solve(solver, 0, 0, 40, 1.0 / 60));
        Vector(new(0, 18), Required(Solve(solver, 0, 18, 60, 2.0 / 60)).ImageTranslation);
        Vector(new(0, -18), Required(Solve(solver, 0, 0, 39, 3.0 / 60)).ImageTranslation);
    }
    public static void TestTwentyRoundTrips()
    {
        var solver = Seeded(); var position = CameraVector.Zero;
        for (var i = 1; i <= 40; i++)
        {
            var y = i % 2 == 0 ? 0 : 25; var result = Required(Solve(solver, 0, y, 40 + y + i % 5 - 2, i / 30.0));
            position = new(position.X + result.ImageTranslation.X, position.Y + result.ImageTranslation.Y); Vector(new(0, y), position);
        }
    }
    public static void TestPatchScore()
    {
        var source = Enumerable.Range(0, 192).Select(i => (byte)(40 + i * 37 % 80)).ToArray();
        Check(GroundCameraSolver.PatchError(source, source.Select(value => (byte)(value * 3 / 2 + 10)).ToArray()) < 1);
        Check(GroundCameraSolver.PatchError(source, source.Reverse().ToArray()) > 12);
        var random = new Random(913);
        var image = new byte[96 * 48]; random.NextBytes(image);
        var prepared = new GroundCameraSolver.Patch(0, 0, source);
        for (var y = 0; y <= 36; y += 3)
        for (var x = 0; x <= 80; x += 5)
        {
            var target = GroundCameraSolver.ReadPatch(image, 96, 48, x, y)!;
            Check(GroundCameraSolver.PatchError(prepared, image, 96, x, y)
                == GroundCameraSolver.PatchError(source, target));
        }
        Check(double.IsPositiveInfinity(GroundCameraSolver.PatchError(source, Enumerable.Repeat((byte)80, 192).ToArray())));
    }
    public static void TestWrongParallelRow() => Check(Solve(Seeded(), 0, 0, 47, 1.0 / 60) is null);
    public static void TestDistantPlatform()
    {
        var solver = new GroundCameraSolver(); var original = Pixels(160, 120);
        Required(solver.Solve(original, 160, 120, Lines(), 0, [])); var distant = new byte[160 * 120];
        for (var y = 0; y < 12; y++) Array.Copy(original, (40 + y) * 160, distant, (80 + y) * 160, 160);
        Check(solver.Solve(distant, 160, 120, Lines(80), 1.0 / 60, []) is null);
    }
    public static void TestReleaseTiming()
    {
        var solver = new GroundCameraSolver(); var frame = Pixels(640, 360); var elapsed = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var timer = Stopwatch.StartNew(); Required(solver.Solve(frame, 640, 360, Lines(260, 623), i / 60.0, [])); elapsed.Add(timer.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine(FormattableString.Invariant($"GROUND_SOLVER_640x360_MS median={elapsed.Order().ElementAt(10):F3} max={elapsed.Max():F3}"));
    }
    public static void TestOwnedEvidenceAndMasks()
    {
        var solver = new GroundCameraSolver(); var pixels = Pixels(); GroundTextureSpan[] evidence = [new(16, 143)];
        GroundTextureLine[] lines = [new(40, 16, 143, evidence)];
        Required(solver.Solve(pixels, 160, 100, lines, 0, [])); Array.Fill(pixels, (byte)90); evidence[0] = new(16, 31);
        Vector(new(-4, 0), Required(Solve(solver, -4, 0, 40, 1.0 / 60)).ImageTranslation);
        Check(solver.Solve(Pixels(dx: -8), 160, 100, Lines(), 2.0 / 60, [new(0, 0, 160, 100)]) is null, "Excluded pixels became camera evidence.");
        var sparse = new GroundCameraSolver(); Required(sparse.Solve(Pixels(), 160, 100, [new(40, 16, 143, [new(16, 31)])], 0, []));
        Check(Solve(sparse, -4, 0, 40, 1.0 / 60) is null, "Occlusion-bridged gaps became independent patch support.");
        var detected = new DetectedFloorLine(40, new(16, 143), [new(16, 31)]);
        Check(GroundTextureLine.FromDetected(detected).EvidenceRanges!.SequenceEqual(new[] { new GroundTextureSpan(16, 31) }));
    }
    public static void TestRollbackAndTrust()
    {
        var solver = Seeded(); Required(Solve(solver, -4, 0, 40, .2));
        solver.FollowMeasuredCamera(new(2, 0), Pixels(dx: -6), 160, 100, Lines(), .2, []);
        Check(solver.HasProvisionalReference); Vector(new(-6, 0), solver.ImagePosition);
        var update = Required(Solve(solver, -8, 0, 40, .3)); Check(update.ReferenceTrusted); Vector(new(-2, 0), update.ImageTranslation);
        solver.FollowMeasuredCamera(new(2, 0), Pixels(dx: -10), 160, 100, Lines(), .3, []);
        Check(solver.HasProvisionalReference);
        // Defer applies to the immediately preceding Solve attempt. A measured
        // fallback deliberately clears that transaction, so test promotion on
        // an actual provisional texture solve, as the Mac tracker does.
        var data = WeakOldAnchor(); var provisional = new GroundCameraSolver(); var lines = Lines(40, 95);
        Required(provisional.Solve(data.Original, 160, 100, lines, 0, []));
        provisional.FollowMeasuredCamera(new(4, 0), data.Recent, 160, 100, lines, 1.0 / 60, []);
        Check(!Required(provisional.Solve(data.Current, 160, 100, lines, 2.0 / 60, [])).ReferenceTrusted);
        provisional.PromoteProvisionalReference(data.Current, 160, 100, lines, 2.0 / 60, []);
        Check(!provisional.HasProvisionalReference);
        provisional.DeferReferenceUpdates(2.0 / 60);
        Check(provisional.HasProvisionalReference, "Rejected promotion lost the pre-attempt provisional epoch.");
        Check(Required(provisional.Solve(data.Original, 160, 100, lines, 3.0 / 60, [])).ReferenceTrusted);
        solver.Reset(); Check(!solver.HasProvisionalReference && !solver.Seeded); Vector(CameraVector.Zero, solver.ImagePosition);
        Check(solver.Solve(Pixels(), 160, 100, [], 0, []) is null);
    }

    public static void TestResizeClockAndReset()
    {
        var solver = Seeded(); Check(solver.Seeded);
        Vector(CameraVector.Zero, Required(Solve(solver, 0, 0, 40, 1.0 / 60)).ImageTranslation);
        Check(!solver.DistinctFrame && !solver.Seeded, "Repeated source pixels counted as independent capture evidence.");
        Required(Solve(solver, -4, 0, 40, 2.0 / 60)); var before = solver.ImagePosition;
        Check(solver.Solve(new byte[1], 160, 100, Lines(), 3.0 / 60, []) is null);
        Check(Solve(solver, -4, 0, 40, double.NaN) is null && Solve(solver, -4, 0, 40, 0) is null);
        Vector(before, solver.ImagePosition);
        // A new shape with no detected ground must leave no old-sized anchor,
        // including a provisional reference restored by a failed alternate probe.
        solver.FollowMeasuredCamera(new(2, 0), Pixels(dx: -6), 160, 100, Lines(), 3.0 / 60, []);
        Check(solver.HasProvisionalReference);
        Check(solver.Solve(Pixels(320, 180), 320, 180, [], .1, []) is null);
        Check(!solver.HasProvisionalReference); Vector(CameraVector.Zero, solver.ImagePosition);
        Required(solver.Solve(Pixels(320, 180), 320, 180, Lines(40, 303), .2, [])); Check(solver.Seeded);
        Vector(new(-4, 2), Required(solver.Solve(Pixels(320, 180, -4, 2), 320, 180, Lines(42, 303), .3, [])).ImageTranslation);
    }
}
