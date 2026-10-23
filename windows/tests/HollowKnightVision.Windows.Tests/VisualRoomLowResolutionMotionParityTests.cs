using HollowKnightVision.Windows.Core;

internal static class VisualRoomLowResolutionMotionParityTests
{
    private static void Check(
        bool value,
        string message = "Visual room low-resolution motion parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");

    public static void TestLowResolutionSubcellMotionSurvivesRoomWideFade()
    {
        var reference = FractionalMotionGrid(horizontalShift: 0);
        var faded = FractionalMotionGrid(horizontalShift: 0.45, gain: 0.28);
        var vector = LowResolutionRoomMotionTracker.Translation(reference, faded);

        Near(0.45, vector?.ScreenShiftX ?? double.NaN, 0.18);
    }

    public static void TestLowResolutionMotionSurvivesShrinkingVisibilityCircle()
    {
        var reference = VisibilityCircle(
            FractionalMotionGrid(horizontalShift: 0),
            radius: 30);
        var moved = VisibilityCircle(
            FractionalMotionGrid(horizontalShift: 1.2, verticalShift: 0.6),
            radius: 14);
        var vector = LowResolutionRoomMotionTracker.Translation(reference, moved);

        Near(1.2, vector?.ScreenShiftX ?? double.NaN, 0.35);
        Near(0.6, vector?.ScreenShiftY ?? double.NaN, 0.35);
    }

    public static void TestLowResolutionMotionIgnoresMovingVisibilityCircle()
    {
        var reference = VisibilityCircle(
            FractionalMotionGrid(horizontalShift: 0),
            radius: 28,
            centerX: 27,
            centerY: 19);
        var moved = VisibilityCircle(
            FractionalMotionGrid(horizontalShift: -1.4, verticalShift: 0.7),
            radius: 15,
            centerX: 36,
            centerY: 15);
        var vector = LowResolutionRoomMotionTracker.Translation(reference, moved);

        Near(-1.4, vector?.ScreenShiftX ?? double.NaN, 0.35);
        Near(0.7, vector?.ScreenShiftY ?? double.NaN, 0.35);
    }

    public static void TestMovingVisibilityCircleCannotCreateFalseMotion()
    {
        var texture = FractionalMotionGrid(horizontalShift: 0);
        var reference = VisibilityCircle(texture, radius: 22, centerX: 25, centerY: 18);
        var movedCircle = VisibilityCircle(texture, radius: 15, centerX: 38, centerY: 14);

        Check(LowResolutionRoomMotionTracker.Translation(reference, movedCircle) is null);
    }

    public static void TestTinyVisibilityCircleCannotCreateMotion()
    {
        var reference = VisibilityCircle(
            FractionalMotionGrid(horizontalShift: 0),
            radius: 3);
        var moved = VisibilityCircle(
            FractionalMotionGrid(horizontalShift: 2, verticalShift: 1),
            radius: 2,
            centerX: 34,
            centerY: 17);

        Check(LowResolutionRoomMotionTracker.Translation(reference, moved) is null);
    }

    public static void TestRuntimeResolutionMotionRetainsFastVerticalTravel()
    {
        var reference = MotionGrid(shift: 0, width: 64, height: 36);
        var translated = MotionGrid(
            shift: -3,
            verticalShift: -7,
            width: 64,
            height: 36);
        var vector = LowResolutionRoomMotionTracker.Translation(reference, translated);

        Near(-3, vector?.ScreenShiftX ?? double.NaN, 0);
        Near(-7, vector?.ScreenShiftY ?? double.NaN, 0);
    }

    private static LowResolutionMotionGrid MotionGrid(
        int shift,
        int verticalShift = 0,
        double gain = 1,
        int width = 32,
        int height = 18)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - shift;
                var sourceY = y - verticalShift;
                if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height)
                {
                    continue;
                }
                var value = (sourceX * 37 + sourceY * 61 + sourceX * sourceY * 3) % 211 + 22;
                pixels[y * width + x] = (byte)Math.Clamp(
                    (int)Math.Round(value * gain, MidpointRounding.AwayFromZero),
                    0,
                    255);
            }
        }
        return new LowResolutionMotionGrid(width, height, pixels);
    }

    private static LowResolutionMotionGrid FractionalMotionGrid(
        double horizontalShift,
        double verticalShift = 0,
        double gain = 1,
        int width = 64,
        int height = 36)
    {
        double Source(int x, int y) =>
            x < 0 || x >= width || y < 0 || y >= height
                ? 0
                : (x * 37 + y * 61 + x * y * 3) % 211 + 22;

        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - horizontalShift;
                var sourceY = y - verticalShift;
                var x0 = (int)Math.Floor(sourceX);
                var y0 = (int)Math.Floor(sourceY);
                var tx = sourceX - x0;
                var ty = sourceY - y0;
                var upper = Source(x0, y0) * (1 - tx) + Source(x0 + 1, y0) * tx;
                var lower = Source(x0, y0 + 1) * (1 - tx) + Source(x0 + 1, y0 + 1) * tx;
                var value = (upper * (1 - ty) + lower * ty) * gain;
                pixels[y * width + x] = (byte)Math.Clamp(
                    (int)Math.Round(value, MidpointRounding.AwayFromZero),
                    0,
                    255);
            }
        }
        return new LowResolutionMotionGrid(width, height, pixels);
    }

    private static LowResolutionMotionGrid VisibilityCircle(
        LowResolutionMotionGrid grid,
        double radius,
        double? centerX = null,
        double? centerY = null)
    {
        var resolvedCenterX = centerX ?? grid.Width * 0.5;
        var resolvedCenterY = centerY ?? grid.Height * 0.5;
        const double feather = 3;
        var pixels = grid.Luma.Select((value, index) =>
        {
            var x = index % grid.Width + 0.5;
            var y = index / grid.Width + 0.5;
            var distance = Math.Sqrt(
                (x - resolvedCenterX) * (x - resolvedCenterX)
                + (y - resolvedCenterY) * (y - resolvedCenterY));
            var visibility = Math.Max(
                0.04,
                Math.Min(1, (radius + feather - distance) / feather));
            return (byte)Math.Round(value * visibility, MidpointRounding.AwayFromZero);
        }).ToArray();
        return new LowResolutionMotionGrid(grid.Width, grid.Height, pixels);
    }
}
