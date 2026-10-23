using HollowKnightVision.Windows.Core;

internal static class RegistrationFrameMaskTests
{
    private static void Check(bool value, string message = "Registration mask parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static BgraFrame Frame(int width, int height, int shift = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var cellX = (x - shift + width) / 10; var cellY = y / 10;
            var value = (byte)((cellX * 37 + cellY * 61 + cellX * cellY * 3) % 211 + 22);
            var i = (y * width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value; pixels[i + 3] = 255;
        }
        return new(width, height, width * 4, pixels, 1, DateTimeOffset.UnixEpoch);
    }
    private static byte[] Pixel(BgraFrame frame, int x, int y) => frame.Pixels.AsSpan(y * frame.Stride + x * 4, 4).ToArray();
    private static void Black(BgraFrame frame, int x, int y) => Check(Pixel(frame, x, y).SequenceEqual(new byte[] { 0, 0, 0, 255 }));

    public static void TestPaddingBeforeScaling()
    {
        var source = Frame(200, 100);
        var output = RegistrationFrameMask.Applying(source, [new(40, 20, 20, 10)], new(100, 50), 5);
        Check(output.Width == source.Width && output.Height == source.Height);
        Black(output, 72, 60); Black(output, 70, 60);
        Check(Pixel(output, 69, 75).SequenceEqual(Pixel(source, 69, 75)));
        Check(Pixel(output, 150, 30).SequenceEqual(Pixel(source, 150, 30)));
        Check(Pixel(source, 72, 60)[0] > 0, "Mask mutated the shared capture.");
    }

    public static void TestNonzeroExtentClipping()
    {
        var source = Frame(200, 100);
        // Mac's bottom-origin box at y=45,h=10 lies above the presentation's
        // top edge: top-origin y=50-(45+10)=-5. The source origin is (4,6).
        var extent = new StencilRect(4, 6, 200, 100);
        var rectangles = RegistrationFrameMask.Rectangles(extent, [new(90, -5, 20, 10)], new(100, 50), 5);
        Check(rectangles.Single() == new StencilRect(174, 6, 30, 20));
        var output = RegistrationFrameMask.Applying(source, [new(90, -5, 20, 10)], new(100, 50), 5, new(4, 6));
        Black(output, 186, 10);
        Check(Pixel(output, 169, 10).SequenceEqual(Pixel(source, 169, 10)));
        Check(Pixel(output, 6, 96).SequenceEqual(Pixel(source, 6, 96)));
    }

    public static void TestInvalidPreservesSource()
    {
        var source = Frame(80, 40);
        var none = RegistrationFrameMask.Applying(source, [], new(40, 20));
        var invalid = RegistrationFrameMask.Applying(source, [new(double.PositiveInfinity, 0, 4, 4)], new(40, 20));
        foreach (var output in new[] { none, invalid,
            RegistrationFrameMask.Applying(source, [new(0, 0, 4, 4)], new(0, 20)),
            RegistrationFrameMask.Applying(source, [new(0, 0, 4, 4)], new(40, 20), double.NaN),
            RegistrationFrameMask.Applying(source, [new(0, 0, 4, 4)], new(40, 20), -1) })
        { Check(output.Pixels.SequenceEqual(source.Pixels)); Check(ReferenceEquals(output, source)); }
    }

    public static void TestMultipleMasks()
    {
        var source = Frame(100, 50);
        var output = RegistrationFrameMask.Applying(source, [new(5, 40, 5, 5), new(80, 10, 5, 5)], new(100, 50), 0);
        Black(output, 7, 43); Black(output, 82, 13);
        Check(Pixel(output, 50, 25).SequenceEqual(Pixel(source, 50, 25)));
        Check(output.CaptureId == source.CaptureId && output.ObservedAt == source.ObservedAt);
    }

    private static void PaintKnight(BgraFrame frame, StencilRect rect)
    {
        for (var y = (int)rect.Y; y < rect.Bottom; y++) for (var x = (int)rect.X; x < rect.Right; x++)
        { var i = y * frame.Stride + x * 4; frame.Pixels[i] = 240; frame.Pixels[i + 1] = 20; frame.Pixels[i + 2] = 255; }
    }

    public static void TestUnionMaskMotion()
    {
        var firstRect = new StencilRect(120, 140, 24, 32); var secondRect = firstRect.Offset(80, 0);
        var first = Frame(640, 360); var second = Frame(640, 360);
        PaintKnight(first, firstRect); PaintKnight(second, secondRect);
        var tracker = new MaskedFrameMotionTracker();
        Check(tracker.Observe(first, [firstRect]) is null);
        Check(tracker.Observe(second, [secondRect])?.Motion is null, "Moving Knight or mask invented camera motion.");
        tracker.Reset();
        var shifted = Frame(640, 360, 20); PaintKnight(shifted, secondRect);
        tracker.Observe(first, [firstRect]);
        var movement = tracker.Observe(shifted, [secondRect])?.Motion;
        Check(movement is not null && Math.Abs(movement.ScreenShiftX - 2) < .2 && Math.Abs(movement.ScreenShiftY) < .1,
            $"Union masks lost real background motion: {movement}.");
        tracker.Reset();
        Check(tracker.Observe(shifted, [secondRect]) is null, "Reset compared across a blackout.");
        Check(tracker.Observe(Frame(320, 180), []) is null, "Resize compared incompatible captures.");
    }
}
