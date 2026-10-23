using HollowKnightVision.Windows.Core;

internal static class BroadRecentConsensusParityTests
{
    private const int Width = 256;
    private const int Height = 120;

    private static void Check(bool value, string message = "Broad recent consensus parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Vector(CameraVector actual, double x, double y)
    {
        Check(Math.Abs(actual.X - x) <= .000_001 && Math.Abs(actual.Y - y) <= .000_001,
            $"Expected ({x}, {y}); got ({actual.X}, {actual.Y}).");
    }

    public static void TestFivePreciseRecentPatchesOverrideFourRepeatedOldTiles()
    {
        var fixture = Fixture(falsePatches: 4, oldCells: 7, recentCells: 5,
            rows: [40], oldNoise: 4, recentNoise: 0);
        Vector(Required(fixture.Result).ImageTranslation, -4, 0);
    }

    public static void TestEquallySupportedPreciseRecentSurfaceWinsAndStaysAnchored()
    {
        foreach (var count in new[] { 5, 6 })
        {
            var fixture = Fixture(falsePatches: count, recentCells: count,
                rows: [40], oldNoise: 4, recentNoise: 2);
            Vector(Required(fixture.Result).ImageTranslation, -4, 0);
            var next = fixture.Solver.Solve(
                fixture.Continued, Width, Height, fixture.Lines, 3d / 60, []);
            Vector(Required(next).ImageTranslation, -4, 0);
        }
    }

    public static void TestEqualRecentSupportStillRequiresPrecisionFreshnessAndFivePatches()
    {
        foreach (var (count, noise, time) in new[]
                 { (4, 0, 2d / 60), (5, 8, 2d / 60), (5, 0, .2) })
        {
            var fixture = Fixture(falsePatches: 5, recentCells: count,
                rows: [40], oldNoise: 4, recentNoise: noise, solveTime: time);
            Vector(Required(fixture.Result).ImageTranslation, 12, 0);
        }
    }

    public static void TestBroadNoisyRecentSurfaceOverridesSparseOldPhaseAndStaysAnchored()
    {
        foreach (var (falsePatches, oldNoise, recentNoise) in new[]
                 { (5, 16, 10), (6, 12, 8) })
        {
            var fixture = Fixture(falsePatches: falsePatches,
                oldNoise: oldNoise, recentNoise: recentNoise);
            Vector(Required(fixture.Result).ImageTranslation, -4, 0);
            var next = fixture.Solver.Solve(
                fixture.Continued, Width, Height, fixture.Lines, 3d / 60, []);
            Vector(Required(next).ImageTranslation, -4, 0);
        }
    }

    public static void TestBroadRecentEvidenceMustBeFreshAndClearlyBetter()
    {
        var noisy = Fixture(falsePatches: 6, oldNoise: 4, recentNoise: 12);
        var stale = Fixture(falsePatches: 6, oldNoise: 4, recentNoise: 0,
            solveTime: .2);
        Check(Required(noisy.Result).Support == 6);
        Check(Required(stale.Result).Support == 6);
    }

    private static (GroundCameraSolver Solver, GroundCameraSolver.Solution? Result,
        byte[] Continued, GroundTextureLine[] Lines) Fixture(
        int falsePatches,
        int oldCells = 8,
        int recentCells = 8,
        int[]? rows = null,
        int oldNoise = 0,
        int recentNoise = 0,
        double solveTime = 2d / 60)
    {
        rows ??= [40, 80];
        var original = Pixels();
        var current = Pixels(99_887);
        for (var y = 40; y < 52; y++)
        {
            for (var x = 24; x < 24 + falsePatches * 16; x++)
            {
                var noise = (x + y) % 2 == 0 ? oldNoise : -oldNoise;
                current[y * Width + x] = (byte)Math.Clamp(
                    original[y * Width + x - 8] + noise, 0, 255);
            }
        }
        var recent = Pixels(777);
        var continued = Pixels(999);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 4; x < Width; x++)
            {
                var noise = (x + y) % 2 == 0 ? recentNoise : -recentNoise;
                recent[y * Width + x] = (byte)Math.Clamp(
                    current[y * Width + x - 4] + noise, 0, 255);
            }
            for (var x = 0; x < Width - 4; x++)
            {
                continued[y * Width + x] = current[y * Width + x + 4];
            }
        }
        var solver = new GroundCameraSolver();
        var lines = rows.Select(row => new GroundTextureLine(
            row, 16, 16 + recentCells * 16 - 1)).ToArray();
        _ = solver.Solve(original, Width, Height,
            [new GroundTextureLine(40, 16, 16 + oldCells * 16 - 1)], 0, []);
        solver.FollowMeasuredCamera(new CameraVector(4, 0), recent,
            Width, Height, lines, 1d / 60, []);
        var result = solver.Solve(current, Width, Height, lines, solveTime, []);
        return (solver, result, continued, lines);
    }

    private static byte[] Pixels(ulong seed = 0)
    {
        var result = new byte[Width * Height];
        for (var index = 0; index < result.Length; index++)
        {
            var x = index % Width;
            var y = index / Width;
            var hash = (ulong)x * 73_856_093 ^ (ulong)y * 19_349_663 ^ seed;
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            result[index] = unchecked((byte)(hash >> 11));
        }
        return result;
    }

    private static GroundCameraSolver.Solution Required(
        GroundCameraSolver.Solution? result) =>
        result ?? throw new Exception("Expected broad recent consensus solution.");
}
