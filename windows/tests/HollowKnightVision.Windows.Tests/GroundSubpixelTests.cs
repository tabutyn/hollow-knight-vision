using HollowKnightVision.Windows.Core;

internal static class GroundSubpixelTests
{
    private const int Width = 320, Height = 180;
    private static readonly int[] Rows = [30, 85, 130], Columns = [24, 72, 128, 184, 240];
    private static void Check(bool value) { if (!value) throw new Exception("Subpixel registration parity assertion failed."); }
    private static void Near(double expected, double actual, double tolerance) { if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected} +/- {tolerance}, got {actual}."); }
    private static byte[] Frame(int dx = 0, int dy = 0, double offsetX = 0, double offsetY = 0) => Enumerable.Range(0, Width * Height).Select(index =>
    {
        var x = index % Width - dx - offsetX; var y = index / Width - dy - offsetY;
        return (byte)Math.Clamp(Math.Round(110 + 18 * Math.Sin(.47 * x + .2 * y) + 22 * Math.Cos(.17 * x - .31 * y)
            + 20 * Math.Sin(.11 * x + .57 * y) + 16 * Math.Cos(.63 * x - .23 * y), MidpointRounding.AwayFromZero), 0, 255);
    }).ToArray();
    private static GroundSubpixelRegistration.Patch[] Patches(byte[] pixels) => Rows.SelectMany(y => Columns.Select(x =>
    {
        var values = new byte[192]; for (var row = 0; row < 12; row++) Array.Copy(pixels, (y + row) * Width + x, values, row * 16, 16);
        return new GroundSubpixelRegistration.Patch(x, y, values);
    })).ToArray();
    private static GroundTextureLine[] Lines(int dy = 0) => Rows.Select(row => new GroundTextureLine(row + dy, 16, 303)).ToArray();
    public static void TestFineParallaxConsensus()
    {
        var source = Frame();
        var patches = Patches(source).Take(14).ToArray();
        var target = Frame(36, 0, .3, .25);
        var foreground = Frame(36, 0, .95, .25);
        foreach (var patch in patches.Take(7))
            for (var y = patch.Y - 4; y < patch.Y + 16; y++)
                Array.Copy(foreground, y * Width + patch.X + 32, target, y * Width + patch.X + 32, 24);
        var result = GroundSubpixelRegistration.RefineConsensus(patches, target, Width, Height, 36, 0)
            ?? throw new Exception("Coherent ground motion was lost among foreground matches.");
        Near(.3, result.Offset.X, .1); Near(.25, result.Offset.Y, .1);
        var paired = GroundSubpixelRegistration.RefineConsensus(patches, target, Width, Height, 36, 0, source)
            ?? throw new Exception("Paired refinement lost the selected ground layer.");
        Near(.3, paired.Offset.X, .1); Near(.25, paired.Offset.Y, .1);
        Check(result.Patches >= 4);
        var combined = GroundSubpixelRegistration.Refine(patches, target, Width, Height, 36, 0)
            ?? throw new Exception("Fixture did not produce a mixed global fit.");
        Check(Math.Abs(result.Offset.X - .3) + .1 < Math.Abs(combined.Offset.X - .3));
    }

    public static void TestFineConsensusRejectsUnobservable()
    {
        var patches = Patches(Frame());
        Check(GroundSubpixelRegistration.RefineConsensus(patches, Frame(36), Width, Height, 36, 0) is null);
        Check(GroundSubpixelRegistration.RefineConsensus(patches, Frame(4, 0, .3), Width, Height, 4, 0) is null);
        Check(GroundSubpixelRegistration.RefineConsensus(patches.Take(3).ToArray(), Frame(36, 0, .3), Width, Height, 36, 0) is null);
        Check(GroundSubpixelRegistration.RefineConsensus(patches, new byte[Width * Height], Width, Height, 36, 0) is null);
    }

    public static void TestIsolatedParticlesRejected()
    {
        var pattern = Frame(); var source = new byte[pattern.Length]; var target = new byte[pattern.Length];
        foreach (var point in new[] { (24, 30), (104, 90), (184, 130), (240, 60) })
            for (var y = point.Item2; y < point.Item2 + 8; y++)
            {
                Array.Copy(pattern, y * Width + point.Item1, source, y * Width + point.Item1, 8);
                Array.Copy(pattern, y * Width + point.Item1, target, (y - 9) * Width + point.Item1 + 17, 8);
            }
        var solver = new GroundCameraSolver(subpixelEnabled: true, broadSearchEveryFrame: true);
        solver.Solve(source, Width, Height, [new(Height - 1, 0, Width - 1)], 0, []);
        Check(solver.Solve(target, Width, Height, [], 1.0 / 30, []) is null);
    }

    public static void TestForegroundMinorityFallback()
    {
        var original = Frame(); var wallTarget = Frame(-13, 0, .35);
        var source = new byte[original.Length]; var target = new byte[original.Length];
        for (var y = 0; y < Height; y++)
        {
            // Dim textured wall versus sharper, brighter moving particles.
            // The old peak selector loses the wall on this fixture.
            for (var x = 0; x < 64; x++) source[y * Width + x] = (byte)Math.Round(original[y * Width + x] * .6);
            for (var x = 0; x < 51; x++) target[y * Width + x] = (byte)Math.Round(wallTarget[y * Width + x] * .6);
        }
        foreach (var point in new[] { (192, 32), (240, 32), (192, 104), (240, 140) })
            for (var y = point.Item2; y < point.Item2 + 12; y++)
            {
                Array.Copy(original, y * Width + point.Item1, source, y * Width + point.Item1, 16);
                Array.Copy(original, y * Width + point.Item1, target, y * Width + point.Item1 - 20, 16);
            }
        var solver = new GroundCameraSolver(subpixelEnabled: true, broadSearchEveryFrame: true);
        solver.Solve(source, Width, Height, [new(Height - 1, 0, Width - 1)], 0, []);
        var moved = solver.Solve(target, Width, Height, [], 1.0 / 30, [])
            ?? throw new Exception("Wall majority lost to moving foreground.");
        Near(-12.65, moved.ImageTranslation.X, .4); Near(0, moved.ImageTranslation.Y, .1);
    }

    public static void TestNarrowWallFallback()
    {
        var source = Frame();
        for (var y = 0; y < Height; y++) Array.Clear(source, y * Width + 48, Width - 48);
        var target = new byte[source.Length];
        for (var y = 0; y < Height - 27; y++)
            Array.Copy(source, y * Width + 21, target, (y + 27) * Width, Width - 21);
        foreach (var backend in new[] { GroundComputeBackend.Cpu, GroundComputeBackend.Cuda })
        {
            var solver = new GroundCameraSolver(subpixelEnabled: true, matcherBackend: backend, broadSearchEveryFrame: true);
            solver.Solve(source, Width, Height, [new(Height - 1, 0, Width - 1)], 0, []);
            var moved = solver.Solve(target, Width, Height, [], 1.0 / 30, [])
                ?? throw new Exception($"{backend}: narrow wall lost despite vertical texture spread.");
            Near(-21, moved.ImageTranslation.X, .1); Near(27, moved.ImageTranslation.Y, .1);
            if (backend == GroundComputeBackend.Cuda && Environment.GetEnvironmentVariable("HKV_REQUIRE_CUDA") == "1")
                Check(solver.LastPatchSearchExecution?.Backend == GroundComputeBackend.Cuda);
        }
    }

    public static void TestFreshTextureReferenceFallback()
    {
        const int width = 320, height = 160;
        var a = GroundCameraSolverTests.Pixels(width, height);
        var b = GroundCameraSolverTests.Pixels(width, height, seed: 12345);
        for (var y = 100; y < 112; y++)
            a.AsSpan(y * width + 16, 128).CopyTo(b.AsSpan(y * width + 16));
        var c = GroundCameraSolverTests.Pixels(width, height, seed: 98765);
        for (var y = 0; y < height - 3; y++)
            b.AsSpan(y * width, width - 6).CopyTo(c.AsSpan((y + 3) * width + 6));
        var unrelated = GroundCameraSolverTests.Pixels(width, height, seed: 76543);
        for (var y = 103; y < 115; y++)
            unrelated.AsSpan(y * width + 22, 128).CopyTo(c.AsSpan(y * width + 22));
        foreach (var backend in new[] { GroundComputeBackend.Cpu, GroundComputeBackend.Cuda })
        {
            var solver = new GroundCameraSolver(broadSearchEveryFrame: true, matcherBackend: backend);
            _ = solver.Solve(a, width, height, [new(100, 16, 143)], 0, []);
            var observed = solver.Solve(b, width, height, [new(100, 16, 303)], 1d / 30, []);
            if (observed is null) throw new Exception("Fresh-reference fixture never established its accepted overlap.");
            var result = solver.Solve(c, width, height, [], 2d / 30, []);
            if (result is null || result.MatchSource != "texture-fresh"
                || Math.Abs(solver.ImagePosition.X - 6) > .1 || Math.Abs(solver.ImagePosition.Y - 3) > .1)
                throw new Exception("Loss of old overlap did not recover through the latest verified image.");
            solver.Reset();
            _ = solver.Solve(a, width, height, [new(100, 16, 143)], 0, []);
            _ = solver.Solve(b, width, height, [new(100, 16, 303)], 1d / 30, []);
            solver.FollowMeasuredCamera(new(1, 0), b, width, height, [new(100, 16, 303)], .04, []);
            var recovered = solver.Solve(c, width, height, [], 2d / 30, []);
            if (recovered is null || !recovered.ReferenceTrusted || recovered.MatchSource != "texture-fresh"
                || Math.Abs(solver.ImagePosition.X - 6) > .1 || Math.Abs(solver.ImagePosition.Y - 3) > .1)
                throw new Exception("A provisional origin preempted stronger trusted texture recovery.");
            solver.Reset();
            _ = solver.Solve(a, width, height, [new(100, 16, 143)], 0, []);
            if (solver.Solve(c, width, height, [], 2d / 30, []) is not null)
                throw new Exception("Reset retained an obsolete fresh-image reference.");
        }
    }

    public static void TestTextureFallback()
    {
        var solver = new GroundCameraSolver(subpixelEnabled: true, broadSearchEveryFrame: true);
        // An empty seed row cannot provide floor patches, but the image contains
        // textured surfaces. Test abrupt motion in both axes and mask rejection.
        Check(solver.Solve(Frame(), Width, Height, [new(Height - 1, 0, Width - 1)], 0, []) is not null);
        var moved = solver.Solve(Frame(45, -38, .3, -.2), Width, Height, [], 1.0 / 30, [])
            ?? throw new Exception("Texture fallback failed without floor rows.");
        Check(moved.MatchSource == "texture-recent");
        Near(45.3, moved.ImageTranslation.X, .2); Near(-38.2, moved.ImageTranslation.Y, .2);
        Check(solver.Solve(Frame(50, -40), Width, Height, [], 2.0 / 30,
            [new(0, 0, Width, Height)]) is null);
        var flat = new GroundCameraSolver(broadSearchEveryFrame: true);
        flat.Solve(new byte[Width * Height], Width, Height, [new(Height - 1, 0, Width - 1)], 0, []);
        Check(flat.Solve(new byte[Width * Height], Width, Height, [], 1.0 / 30, []) is null);
    }

    public static void TestSparseAbruptUniqueness()
    {
        const int width = 320, height = 160;
        var solver = new GroundCameraSolver(matcherBackend: GroundComputeBackend.Cuda);
        var original = GroundCameraSolverTests.Pixels(width, height);
        var moved = GroundCameraSolverTests.Pixels(width, height, 60, 40);
        GroundCameraSolver.Patch[] PatchesFor(byte[] pixels) => new[] { 16, 48, 80, 112 }.Select(x =>
            new GroundCameraSolver.Patch(x, 80, GroundCameraSolver.ReadPatch(pixels, width, height, x, 80)!)).ToArray();
        Check(solver.VerifySparseTranslation(PatchesFor(original), moved, width, height, [], 60, 40));
        Check(!solver.VerifySparseTranslation(PatchesFor(original), moved, width, height, [], 68, 40));
        Check(!solver.VerifySparseTranslation(PatchesFor(original), moved, width, height, [new(0, 0, width, height)], 60, 40));
        var tile = GroundCameraSolverTests.Pixels(32, height);
        byte[] Tiled(int dx, int dy) => Enumerable.Range(0, width * height).Select(i =>
            tile[((i / width - dy + height) % height) * 32 + (i % width - dx + width) % 32]).ToArray();
        Check(!solver.VerifySparseTranslation(PatchesFor(Tiled(0, 0)), Tiled(60, 40), width, height, [], 60, 40));
    }

    public static void TestSymmetricTranslation()
    {
        var source = Frame();
        foreach (var sign in new[] { -1, 1 })
        foreach (var fraction in new[] { .15, .4, .7 })
        {
            var target = Frame(sign * 4, -2, sign * fraction, -.3);
            var result = GroundSubpixelRegistration.RefineSymmetric(Patches(source), source, target, Width, Height, sign * 4, -2)
                ?? throw new Exception("Bidirectional fractional registration was lost.");
            Near(sign * fraction, result.Offset.X, .08); Near(-.3, result.Offset.Y, .08);
        }
        Check(GroundSubpixelRegistration.RefineSymmetric(Patches(source), source, new byte[source.Length], Width, Height, 0, 0) is null);
        Check(GroundSubpixelRegistration.RefineSymmetric(Patches(source), source, source, Width, Height, 0, 0) is null);
    }

    public static void TestFractionalTranslation()
    {
        var result = GroundSubpixelRegistration.Refine(Patches(Frame()), Frame(4, -2, .4, -.3), Width, Height, 4, -2)
            ?? throw new Exception("Fractional translation was not recovered.");
        Near(.4, result.Offset.X, .07); Near(-.3, result.Offset.Y, .07); Check(result.Iterations <= 6);
    }
    public static void TestIntegratedFractionalSolve()
    {
        var solver = new GroundCameraSolver(subpixelEnabled: true);
        Check(solver.Solve(Frame(), Width, Height, Lines(), 0, []) is not null);
        var result = solver.Solve(Frame(-3, 5, .35, -.25), Width, Height, Lines(5), 1.0 / 60, []) ?? throw new Exception("Fractional solver rejected the match.");
        Near(-2.65, result.ImageTranslation.X, .12); Near(4.75, result.ImageTranslation.Y, .12);
        Near(-2.65, result.MatchedImageDisplacement!.Value.X, .12); Near(4.75, result.MatchedImageDisplacement!.Value.Y, .12);
    }
    public static void TestIntegerAndFlat()
    {
        var solver = new GroundCameraSolver(subpixelEnabled: true); Check(solver.Solve(Frame(), Width, Height, Lines(), 0, []) is not null);
        var moved = solver.Solve(Frame(-4, 5), Width, Height, Lines(5), 1.0 / 60, []) ?? throw new Exception("Integer match rejected.");
        Check(moved.ImageTranslation == new CameraVector(-4, 5));
        Check(GroundSubpixelRegistration.Refine(Patches(Frame()), Enumerable.Repeat((byte)80, Width * Height).ToArray(), Width, Height, 0, 0) is null);
    }
}
