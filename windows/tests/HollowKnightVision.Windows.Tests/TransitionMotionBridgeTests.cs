using HollowKnightVision.Windows.Core;

internal static class TransitionMotionBridgeTests
{
    private static void Check(bool value, string message = "Transition bridge parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static void Near(double expected, double actual, double tolerance = .000001) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");
    private static BgraFrame Frame(int shift = 0)
    {
        const int width = 640, height = 360;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var sourceX = x - shift;
            var value = sourceX < 0 || sourceX >= width ? 0
                : (sourceX * 37 + y * 61 + sourceX * y * 3) % 211 + 22;
            var index = (y * width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = (byte)value;
            pixels[index + 3] = 255;
        }
        return new(width, height, width * 4, pixels, shift, DateTimeOffset.UnixEpoch);
    }
    private static void Paint(BgraFrame frame, PixelRect rect)
    {
        for (var y = rect.Y; y < rect.Bottom; y++) for (var x = rect.X; x < rect.Right; x++)
        {
            var index = y * frame.Stride + x * 4;
            frame.Pixels[index] = 20; frame.Pixels[index + 1] = 20; frame.Pixels[index + 2] = 245;
        }
    }

    public static void TestResumeAfterBlackout()
    {
        var bridge = new TransitionMotionBridge();
        bridge.Anchor(new AtlasPoint(10, 4));
        bridge.SuspendForTransition();
        var position = bridge.ResumeAfterTransition();
        Check(position is not null); Near(10, position!.Value.X); Near(4, position.Value.Y);
    }

    public static void TestResumeRequiresSuspension()
    {
        var bridge = new TransitionMotionBridge();
        bridge.Anchor(new AtlasPoint(10, 4));
        Check(bridge.ResumeAfterTransition() is null);
    }

    public static void TestFallbackTranslation()
    {
        var registration = new MaskedFrameMotionTracker();
        var bridge = new TransitionMotionBridge();
        bridge.Anchor(new AtlasPoint(0, 0));
        Check(registration.Observe(Frame(), []) is null);
        var diagnostic = registration.Observe(Frame(-20), []);
        var result = bridge.Track(diagnostic, 640, 360, 640, lockVertical: true);
        Check(result is not null && result.Confidence >= TransitionMotionBridge.MinimumConfidence);
        Near(20, result!.CameraPosition.X, 2); Near(0, result.CameraPosition.Y);
        Check(result.Source == TransitionMotionSource.MaskedRegistration);
    }

    public static void TestFallbackIgnoresMovingForeground()
    {
        var firstRect = new PixelRect(45, 55, 100, 90);
        var secondRect = new PixelRect(235, 55, 100, 90);
        var first = Frame(); var second = Frame(); Paint(first, firstRect); Paint(second, secondRect);
        var registration = new MaskedFrameMotionTracker();
        var bridge = new TransitionMotionBridge(); bridge.Anchor(new AtlasPoint(0, 0), firstRect);
        _ = registration.Observe(first, [new(firstRect.X, firstRect.Y, firstRect.Width, firstRect.Height)]);
        var diagnostic = registration.Observe(second, [new(secondRect.X, secondRect.Y, secondRect.Width, secondRect.Height)]);
        var result = bridge.Track(diagnostic, 640, 360, 640, lockVertical: true, secondRect);
        Check(result is not null, $"Expected a static masked registration; diagnostic={diagnostic}.");
        Near(0, result!.CameraPosition.X, .5); Near(0, result.CameraPosition.Y);
    }

    public static void TestContinuity()
    {
        var current = new AtlasPoint(-324, 0);
        Check(!GroundPoseContinuityGate.Accepts(new(-121, 4), current, 640, 0, false, 4));
        Check(!GroundPoseContinuityGate.Accepts(new(-324, -18), current, 640, 0, false, 3));
        Check(GroundPoseContinuityGate.Accepts(new(-361, 1), current, 640, 0, false, 9));
        Check(GroundPoseContinuityGate.Accepts(new(-319, 2), current, 640, 0, false, 4));
        Check(GroundPoseContinuityGate.Accepts(new(-324, -18), current, 640, 0, false, 4));
        Check(!GroundPoseContinuityGate.Accepts(new(-324, -32), current, 640, 0, false));
        Check(GroundPoseContinuityGate.Accepts(new(48, -20), current, 640, 4, true));
        Check(!GroundPoseContinuityGate.Accepts(new(48, -20), current, 640, 3, true));
        Check(GroundPoseContinuityGate.Accepts(new(-370, -20), current, 640, 0, false,
            localTextureSupport: 8, localTextureError: 18, captureElapsed: .1));
        Check(!GroundPoseContinuityGate.Accepts(new(-500, -20), current, 640, 0, false,
            localTextureSupport: 8, localTextureError: 18, captureElapsed: .1));
    }

    public static void TestForegroundResidual()
    {
        var previous = new PixelRect(100, 40, 24, 32);
        var current = previous with { X = 104 };
        Near(0, TransitionForegroundResidualGate.Adjusted(new(-1, 0), previous, current, 640, 640).X);
        Near(-5, TransitionForegroundResidualGate.Adjusted(new(-5, 0), previous, current, 640, 640).X);
    }

    public static void TestSmallMotion()
    {
        var accumulator = new TransitionSmallMotionAccumulator();
        Near(0, accumulator.Ingest(-1)); Near(0, accumulator.Ingest(-1)); Near(0, accumulator.Ingest(-1));
        Near(0, accumulator.Ingest(0)); Near(0, accumulator.Ingest(0)); Near(0, accumulator.Ingest(0));
        Near(0, accumulator.Ingest(1)); Near(0, accumulator.Ingest(1)); Near(0, accumulator.Ingest(1));
        Near(4, accumulator.Ingest(1)); Near(-5, accumulator.Ingest(-5));
        Check(double.IsNaN(accumulator.Ingest(double.NaN)));
    }
}
