using HollowKnightVision.Windows.Core;

internal static class GroundLongRowParityTests
{
    private const int Width = 640;
    private const int Height = 360;

    private static void Check(bool value, string message = "Ground long-row parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Close(double expected, double actual, double tolerance = .000_001)
    {
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected}, got {actual}.");
    }

    public static void TestSuddenLandingShiftRequiresBroadPreciseTexture()
    {
        foreach (var dy in new[] { -96, -64, -40, 40, 64, 96 })
        {
            var solver = new GroundCameraSolver();
            _ = solver.Solve(Pixels(0), Width, Height,
                [new GroundTextureLine(140, 16, 623)], 0, []);
            var result = solver.Solve(Pixels(dy, noise: 4), Width, Height,
                    [new GroundTextureLine(140 + dy, 16, 623)], 1d / 60, [])
                ?? throw new Exception($"Expected broad solve for dy={dy}.");
            Close(0, result.ImageTranslation.X);
            Close(dy, result.ImageTranslation.Y);
            Check(result.Support >= 8);
            Check(result.Error <= 6);
        }
    }

    public static void TestLongRowRejectsWrongTextureWrongRowNarrowMaskedAndOversized()
    {
        foreach (var mode in new[]
                 { "unrelated", "wrong-row", "narrow", "masked", "oversized", "noisy" })
        {
            var solver = new GroundCameraSolver();
            var end = mode == "narrow" ? 79 : 623;
            _ = solver.Solve(Pixels(0), Width, Height,
                [new GroundTextureLine(140, 16, end)], 0, []);
            var dy = mode == "oversized" ? 112 : 64;
            var result = solver.Solve(
                Pixels(dy,
                    salt: mode == "unrelated" ? 17_791 : 0,
                    noise: mode == "noisy" ? 24 : 0),
                Width,
                Height,
                [new GroundTextureLine(140 + dy + (mode == "wrong-row" ? 16 : 0), 16, end)],
                1d / 60,
                mode == "masked" ? [new StencilRect(0, 200, 640, 20)] : []);
            Check(result is null, $"Unexpected solution for {mode}.");
        }
    }

    public static void TestManyIndependentPatchesRecoverModeratelyNoisyLanding()
    {
        var solver = new GroundCameraSolver();
        _ = solver.Solve(Pixels(0), Width, Height,
            [new GroundTextureLine(120, 16, 623), new GroundTextureLine(140, 16, 623)],
            0, []);
        var result = solver.Solve(Pixels(64, noise: 12), Width, Height,
                [new GroundTextureLine(184, 16, 623), new GroundTextureLine(204, 16, 623)],
                1d / 60, [])
            ?? throw new Exception("Expected moderately noisy broad landing solve.");
        Close(0, result.ImageTranslation.X);
        Close(64, result.ImageTranslation.Y);
        Check(result.Support >= 12);
        Check(result.Error > 6 && result.Error <= 10,
            $"Unexpected broad landing error {result.Error}.");
    }

    public static void TestRepeatedTextureCannotChooseAnAmbiguousLongRowPhase()
    {
        var pixels = new byte[Width * Height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % Width % 16;
            var y = index / Width % 16;
            pixels[index] = (byte)((x * 37 + y * 71 + x * y * 3) % 256);
        }
        var solver = new GroundCameraSolver();
        _ = solver.Solve(pixels, Width, Height,
            [new GroundTextureLine(120, 16, 623), new GroundTextureLine(140, 16, 623)],
            0, []);
        Check(solver.Solve(pixels, Width, Height,
            [new GroundTextureLine(184, 16, 623), new GroundTextureLine(204, 16, 623)],
            1d / 60, []) is null);
    }

    private static byte[] Pixels(int dy, int salt = 0, int noise = 0)
    {
        var pixels = new byte[Width * Height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % Width;
            var y = index / Width - dy;
            var hash = unchecked((ulong)((long)x * 73_856_093L))
                ^ unchecked((ulong)((long)y * 19_349_663L))
                ^ (ulong)salt;
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            var value = unchecked((byte)(hash >> 11));
            var jitter = noise == 0 ? 0
                : (index * 17 + 31) % (noise * 2 + 1) - noise;
            pixels[index] = (byte)Math.Clamp(value + jitter, 0, 255);
        }
        return pixels;
    }
}
