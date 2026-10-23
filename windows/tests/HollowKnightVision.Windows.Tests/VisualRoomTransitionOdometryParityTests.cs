using HollowKnightVision.Windows.Core;

internal static class VisualRoomTransitionOdometryParityTests
{
    private static void Check(
        bool value,
        string message = "Visual room transition odometry parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");

    public static void TestFloorlessOdometryAccumulatesThroughMovingFadeCircle()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(
            VisibilityCircle(
                FractionalMotionGrid(horizontalShift: 0),
                radius: 29,
                centerX: 28,
                centerY: 18),
            timestamp: 0);

        for (var index = 1; index <= 20; index++)
        {
            var progress = (double)index;
            var step = odometry.Observe(
                VisibilityCircle(
                    FractionalMotionGrid(
                        horizontalShift: -0.55 * progress,
                        verticalShift: 0.22 * progress),
                    radius: 29 - 0.8 * progress,
                    centerX: 28 + 0.5 * progress,
                    centerY: 18 - 0.15 * progress),
                timestamp: 0.05 * progress,
                solveWidth: 640,
                expectedDirection: VisualRoomDirection.Right);
            Check(step is not null, $"Lost fade odometry frame {index}.");
        }

        Check(odometry.SampleCount == 20,
            $"Expected 20 samples, got {odometry.SampleCount}.");
        Near(110, odometry.WorldDeltaX, 18);
        Near(44, odometry.WorldDeltaY, 18);
    }

    public static void TestFloorlessOdometryBridgesOnlyShortSameDirectionOverlapLoss()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(FractionalMotionGrid(horizontalShift: 0), timestamp: 0);
        odometry.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(200, 0), 0),
            VisualRoomDirection.Right);
        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: -1),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) is not null);
        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: -2),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) is not null);

        var beforeOverlapLoss = odometry.WorldDeltaX;
        Check(odometry.Observe(
            VisibilityCircle(FractionalMotionGrid(horizontalShift: -3), radius: 2),
            timestamp: 0.15,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) is not null);
        var afterOverlapLoss = odometry.WorldDeltaX;
        Check(afterOverlapLoss > beforeOverlapLoss + 5,
            "Same-direction overlap bridge was too small.");
        Check(afterOverlapLoss < beforeOverlapLoss + 20,
            "Same-direction overlap bridge was too large.");

        Check(odometry.Observe(
            VisibilityCircle(FractionalMotionGrid(horizontalShift: -4), radius: 2),
            timestamp: 0.20,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is null,
            "Opposite-direction overlap loss must not bridge.");
        Near(afterOverlapLoss, odometry.WorldDeltaX, 0.001);
    }

    public static void TestReliableVelocityHandoffBridgesInitialFadeMismatch()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(MotionGrid(shift: 0), timestamp: 0);
        odometry.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(200, 0), 0),
            VisualRoomDirection.Right);

        Near(10, odometry.Observe(
            MotionGrid(shift: 2),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) ?? double.NaN, 0.001);
        Near(10, odometry.Observe(
            VisibilityCircle(MotionGrid(shift: 3), radius: 2),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) ?? double.NaN, 0.001);
        Near(20, odometry.WorldDeltaX, 0.001);

        var smallContradiction = new LowResolutionTransitionOdometry();
        smallContradiction.Arm(
            FractionalMotionGrid(horizontalShift: 0), timestamp: 0);
        smallContradiction.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(-360, 0), 0),
            VisualRoomDirection.Left);
        Near(-18, smallContradiction.Observe(
            FractionalMotionGrid(horizontalShift: -0.5),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) ?? double.NaN, 0.001);

        var expired = new LowResolutionTransitionOdometry();
        expired.Arm(MotionGrid(shift: 0), timestamp: 0.45);
        expired.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(200, 0), 0),
            VisualRoomDirection.Right);
        Check(expired.Observe(
            MotionGrid(shift: 2),
            timestamp: 0.50,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) is null,
            "Expired handoff velocity must not bridge.");
        Near(0, expired.WorldDeltaX, 0.001);
    }

    public static void TestReliableVelocityHandoffBridgesCollapsedSameDirectionMotion()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(FractionalMotionGrid(horizontalShift: 0), timestamp: 0);
        odometry.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(200, 0), 0),
            VisualRoomDirection.Right);

        Near(10, odometry.Observe(
            FractionalMotionGrid(horizontalShift: -0.25),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) ?? double.NaN, 0.5);
        Near(10, odometry.WorldDeltaX, 0.5);
    }

    public static void TestReliableVelocityHandoffKeepsStrongSameDirectionMeasurement()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(FractionalMotionGrid(horizontalShift: 0), timestamp: 0);
        odometry.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(200, 0), 0),
            VisualRoomDirection.Right);

        Near(9.5, odometry.Observe(
            FractionalMotionGrid(horizontalShift: -0.95),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) ?? double.NaN, 1);
    }

    public static void TestReliableVelocityHandoffBridgesWeakVerticalFadeMotion()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: 0),
            timestamp: 0);
        odometry.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(0, -560), 0),
            VisualRoomDirection.Left);

        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -0.25),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is not null);
        Near(-28, odometry.WorldDeltaY, 2);

        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -0.10),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is not null);
        Near(-56, odometry.WorldDeltaY, 3);

        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -2.46),
            timestamp: 0.15,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is not null);
        Near(-79.6, odometry.WorldDeltaY, 4);
    }

    public static void TestVerticalHandoffUsesLatestMeasuredAcceleration()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: 0),
            timestamp: 0);
        odometry.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(0, -80), 0),
            VisualRoomDirection.Left);

        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -0.4),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is not null);
        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -1.4),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is not null);
        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -1.5),
            timestamp: 0.15,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is not null);
        Near(-24, odometry.WorldDeltaY, 3);
    }

    public static void TestVerticalHandoffSurvivesInputBeginningMidFall()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: 0),
            timestamp: 0);
        odometry.PrimeHorizontalVelocity(
            new LowResolutionCameraVelocitySeed(new CameraVector(0, -560), 0),
            expectedDirection: null);

        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -0.25),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: null) is not null);
        Check(odometry.Observe(
            FractionalMotionGrid(horizontalShift: 0, verticalShift: -0.10),
            timestamp: 0.10,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) is not null);
        Near(-56, odometry.WorldDeltaY, 3);
    }

    public static void TestFloorlessOdometryAccumulatesSlowSubcellTravelWithoutFreezing()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(FractionalMotionGrid(horizontalShift: 0), timestamp: 0);
        for (var index = 1; index <= 20; index++)
        {
            Check(odometry.Observe(
                FractionalMotionGrid(horizontalShift: -0.45 * index),
                timestamp: index * 0.1,
                solveWidth: 640,
                expectedDirection: VisualRoomDirection.Right) is not null,
                $"Missing subcell step {index}.");
        }
        Check(odometry.SampleCount == 20,
            $"Expected 20 samples, got {odometry.SampleCount}.");
        Near(90, odometry.WorldDeltaX, 12);
    }

    public static void TestLowResolutionMotionSeparatesSimultaneousVerticalTravel()
    {
        var reference = MotionGrid(shift: 0);
        var translated = MotionGrid(shift: 2, verticalShift: 2);
        var vector = LowResolutionRoomMotionTracker.Translation(reference, translated);
        Near(2, vector?.ScreenShiftX ?? double.NaN, 0);
        Near(2, vector?.ScreenShiftY ?? double.NaN, 0);
        Check(LowResolutionRoomMotionTracker.Estimate(reference, translated)?.Direction
            == VisualRoomDirection.Left);

        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(reference, timestamp: 0);
        Near(-40, odometry.Observe(
            translated,
            timestamp: 0.1,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Left) ?? double.NaN, 0);
        Near(-40, odometry.WorldDeltaX, 0);
        Near(40, odometry.WorldDeltaY, 0);
    }

    public static void TestFloorlessOdometryRetainsVerticalTravelWithoutHorizontalDirection()
    {
        var reference = FractionalMotionGrid(horizontalShift: 0);
        var translated = FractionalMotionGrid(
            horizontalShift: 0,
            verticalShift: 2);
        Check(LowResolutionRoomMotionTracker.Estimate(reference, translated) is null,
            "Horizontal direction voting must remain absent.");

        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(reference, timestamp: 0);
        Check(odometry.Observe(
            translated,
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: null) is not null);
        Near(0, odometry.WorldDeltaX, 2);
        Near(20, odometry.WorldDeltaY, 2);
    }

    public static void TestFloorlessOdometryRetainsSmallCameraEasingAgainstHeldInput()
    {
        var odometry = new LowResolutionTransitionOdometry();
        odometry.Arm(FractionalMotionGrid(horizontalShift: 0), timestamp: 0);
        Near(-12.5, odometry.Observe(
            FractionalMotionGrid(horizontalShift: 1.25),
            timestamp: 0.05,
            solveWidth: 640,
            expectedDirection: VisualRoomDirection.Right) ?? double.NaN, 2);
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
