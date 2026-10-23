using HollowKnightVision.Windows.Core;

internal static class FrameSignalTests
{
    private static void Check(bool value, string message = "Frame-signal parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static void Near(double value, double expected, double tolerance = .000001) =>
        Check(Math.Abs(value - expected) <= tolerance, $"Expected {expected}, got {value}.");
    private static BgraFrame Frame(int width = 64, int height = 36, byte gray = 0, int padding = 0)
    {
        var stride = width * 4 + padding;
        var pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        var frame = new BgraFrame(width, height, stride, pixels, 1, DateTimeOffset.UnixEpoch);
        Paint(frame, 0, 0, width, height, gray, gray, gray);
        return frame;
    }
    private static void Paint(BgraFrame frame, int x, int y, int width, int height, byte red, byte green, byte blue)
    {
        for (var row = y; row < y + height; row++) for (var col = x; col < x + width; col++)
        { var i = row * frame.Stride + col * 4; frame.Pixels[i] = blue; frame.Pixels[i + 1] = green; frame.Pixels[i + 2] = red; }
    }
    private static FrameSignalProfile Profile(double mean, double signaled = .2, double visible = .2) =>
        new(100, (int)(signaled * 100), (int)(visible * 100), mean);

    public static void TestWholeBlackVersusDarkRoom()
    {
        var black = Frame(80, 50);
        var hudOnly = Frame(80, 50);
        Paint(hudOnly, 3, 2, 18, 6, 255, 255, 255);
        var dimRoom = Frame(80, 50);
        Paint(dimRoom, 0, 0, 80, 50, 6, 8, 9);
        Paint(dimRoom, 28, 12, 24, 30, 0, 0, 0);
        Check(!FrameRegionRenderer.ContainsGameplaySignal(black));
        Check(!FrameRegionRenderer.ContainsGameplaySignal(hudOnly), "A HUD alone is not a usable scene.");
        Check(FrameRegionRenderer.ContainsGameplaySignal(dimRoom), "Dark room and opaque silhouette were rejected.");
        var output = FrameRegionRenderer.MapFrame(dimRoom, SceneRegions.Startup());
        Check(output.Pixels[(25 * 80 + 35) * 4 + 3] == 255);
    }

    public static void TestSignalThresholdsAndGeometry()
    {
        foreach (var value in new byte[] { 0, 4, 5, 15, 16, 255 })
        {
            var profile = FrameRegionRenderer.SignalProfile(Frame(gray: value, padding: 20));
            Check(profile.SampleCount == 64 * 36);
            Check(profile.SignaledCount == (value >= 5 ? profile.SampleCount : 0));
            Check(profile.VisibleCount == (value >= 16 ? profile.SampleCount : 0));
            Near(profile.MeanPeak, value);
            Check(profile.HasGameplaySignal == (value >= 5));
            Check(profile.MotionGrid!.Luma.All(luma => luma == value));
        }
        Check(!new FrameSignalProfile(100, 7, 0, 1).HasGameplaySignal);
        Check(new FrameSignalProfile(100, 8, 0, 1).HasGameplaySignal);
        Check(!new FrameSignalProfile(1000, 49, 0, 1).HasGameplaySignal);
        Check(new FrameSignalProfile(1000, 50, 0, 1).HasGameplaySignal);
        Near(new FrameSignalProfile(0, 0, 0, 0).SignaledFraction, 0);
        Near(new FrameSignalProfile(0, 0, 0, 0).VisibleFraction, 0);
        Near(new FrameSignalProfile(100, 25, 10, 2).SignaledFraction, .25);
        Near(new FrameSignalProfile(100, 25, 10, 2).VisibleFraction, .1);
        Check(FrameRegionRenderer.SignalProfile(Frame(80, 50)).MotionGrid!.Height == 40);
        Check(FrameRegionRenderer.SignalProfile(Frame(640, 10)).MotionGrid!.Height == 16);
        var colored = Frame(); Paint(colored, 0, 0, 64, 36, 1, 2, 16);
        var blue = FrameRegionRenderer.SignalProfile(colored);
        Check(blue.VisibleCount == 2304 && blue.MotionGrid!.Luma.All(value => value == ((77 + 300 + 464) >> 8)));
        var geometry = Frame(80, 100); Paint(geometry, 0, 0, 80, 10, 255, 255, 255);
        var tall = FrameRegionRenderer.SignalProfile(geometry);
        Check(tall.MotionGrid!.Height == 80 && tall.MotionGrid.Luma.Take(64 * 8).All(value => value == 255),
            "Whole-frame signal sampling cropped away the top of a tall source.");
    }

    public static void TestDarkeningAndBlackoutGate()
    {
        var gate = new TransitionFrameGate();
        Check(gate.Observe(Profile(100, visible: .8)).AdmitsTracking);
        Check(gate.Observe(Profile(18)).AdmitsTracking);
        Check(!gate.Observe(Profile(0, 0, 0)).AdmitsTracking);
        for (var i = 0; i < 3; i++) Check(!gate.Observe(Profile(12, visible: .15)).AdmitsTracking);
        var resumed = gate.Observe(Profile(12, visible: .15));
        Check(resumed.AdmitsTracking && resumed.ResumedAfterTransition);
        var continued = gate.Observe(Profile(11.5, visible: .14));
        Check(continued.AdmitsTracking && !continued.ResumedAfterTransition);
    }

    public static void TestSettlingResetAndBoundaries()
    {
        var gate = new TransitionFrameGate();
        gate.Observe(Profile(0, 0, 0));
        Check(!gate.Observe(Profile(12)).AdmitsTracking);
        Check(!gate.Observe(Profile(12)).AdmitsTracking);
        Check(!gate.Observe(Profile(20)).AdmitsTracking); // changing fade restarts the stable count
        Check(!gate.Observe(Profile(20)).AdmitsTracking);
        Check(!gate.Observe(Profile(20)).AdmitsTracking);
        Check(gate.Observe(Profile(20)).ResumedAfterTransition);
        gate.Observe(Profile(0, 0, 0));
        gate.Reset();
        var reset = gate.Observe(Profile(100));
        Check(reset.AdmitsTracking && !reset.ResumedAfterTransition);
        gate.Observe(Profile(0, 0, 0));
        Check(!gate.Observe(Profile(100)).AdmitsTracking);
        Check(!gate.Observe(Profile(120)).AdmitsTracking); // relative tolerance max(current, previous)*0.20
        Check(!gate.Observe(Profile(144)).AdmitsTracking);
        Check(gate.Observe(Profile(172)).ResumedAfterTransition);
        gate.Observe(Profile(0, 0, 0));
        Check(!gate.Observe(Profile(2.5)).AdmitsTracking); // absolute floor, exact inclusive boundary
        Check(!gate.Observe(Profile(5)).AdmitsTracking);
        Check(gate.Observe(Profile(7.5)).ResumedAfterTransition);
    }

    public static void TestDoorwayBlackBands()
    {
        var doorway = Frame(640, 360);
        Paint(doorway, 60, 70, 290, 220, 24, 52, 96);
        var profile = FrameRegionRenderer.SignalProfile(doorway);
        Check(profile.MotionGrid!.Width == 64 && profile.MotionGrid.Height == 36);
        var bands = profile.EdgeBlackBands!;
        Check(bands.Left is not null && bands.Right is not null);
        Near(bands.Left!.WidthFraction, .09375, .02); Near(bands.Right!.WidthFraction, .453125, .02);
        Check(bands.Left.ContentYRangeFraction.Upper - bands.Left.ContentYRangeFraction.Lower < .5);
        Check(bands.Band(VisualRoomDirection.Left) == bands.Left && bands.Band(VisualRoomDirection.Right) == bands.Right);
        // This covers the detector portion only; Mac's combined doorway-layout test remains unmapped.
    }

    public static void TestBlackBandRunRules()
    {
        var frame = Frame();
        Paint(frame, 1, 0, 1, 36, 100, 100, 100); // one bright column inside the matte is tolerated
        Paint(frame, 5, 5, 59, 4, 100, 100, 100);
        Paint(frame, 5, 10, 59, 5, 100, 100, 100); // one unsupported row joins these two spans
        Paint(frame, 5, 20, 59, 5, 100, 100, 100);
        var bands = FrameRegionRenderer.SignalProfile(frame).EdgeBlackBands!;
        Near(bands.Left!.WidthFraction, 5d / 64);
        Near(bands.Left.ContentYRangeFraction.Lower, 4d / 36);
        Near(bands.Left.ContentYRangeFraction.Upper, 17d / 36);
        Check(bands.Right is null);
        var black = FrameRegionRenderer.SignalProfile(Frame()).EdgeBlackBands!;
        Near(black.Left!.WidthFraction, .75);
        Near(black.Left.ContentYRangeFraction.Lower, .38); Near(black.Left.ContentYRangeFraction.Upper, .68);
        Check(black.RectangularOmissions(640, 360).Count == 0, "Whole blackout must not yield a mostly empty atlas write.");
        var trailing = Frame(); Paint(trailing, 3, 28, 61, 8, 100, 100, 100);
        var range = FrameRegionRenderer.SignalProfile(trailing).EdgeBlackBands!.Left!.ContentYRangeFraction;
        Near(range.Lower, 27d / 36); Near(range.Upper, 1);
        var oneColumn = Frame(gray: 100); Paint(oneColumn, 0, 0, 1, 36, 0, 0, 0);
        Check(FrameRegionRenderer.SignalProfile(oneColumn).EdgeBlackBands!.Left is null);
    }

    public static void TestRectangularOmissions()
    {
        var bands = new FrameEdgeBlackBands(new(.10, new(.35, .65)), new(.20, new(.35, .65)));
        Check(bands.RectangularOmissions(640, 360).SequenceEqual(new[] { new StencilRect(0, 0, 65, 360), new StencilRect(511, 0, 129, 360) }));
        Check(bands.RectangularOmissions(1280, 720).SequenceEqual(new[] { new StencilRect(0, 0, 130, 720), new StencilRect(1022, 0, 258, 720) }));
        Check(bands.RectangularOmissions(double.NaN, 360).Count == 0 && bands.RectangularOmissions(640, 0).Count == 0);
        Check(new FrameEdgeBlackBands(new(.45, new(0, 1)), new(.45, new(0, 1))).RectangularOmissions(640, 360).Count == 0);
        Check(new FrameEdgeBlackBands(null, null).RectangularOmissions(640, 360).Count == 0);
        // Omit only the two straight edges; a black silhouette in the interior stays opaque.
        var frame = Frame(640, 360, 0);
        var output = FrameRegionRenderer.MapFrame(frame, SceneRegions.Startup(), bands.RectangularOmissions(640, 360));
        Check(output.Pixels[(180 * 640 + 64) * 4 + 3] == 0 && output.Pixels[(180 * 640 + 511) * 4 + 3] == 0);
        Check(output.Pixels[(180 * 640 + 300) * 4 + 3] == 255);
    }

    public static void TestLiveAtlasAdmission()
    {
        var admission = new GameplayFrameAdmission();
        var visible = Frame(640, 360, 60);
        var pixels = FrameRegionRenderer.SignalProfile(visible);
        var first = admission.Observe(pixels, true, 1);
        Check(first.AdmitsTracking && first.AllowsAtlasWrite && first.ResetMotionReference);
        Check(!admission.Observe(pixels, false, 1).AllowsAtlasWrite, "Visible menu became atlas evidence.");
        var hudOnly = Frame(640, 360);
        Paint(hudOnly, 30, 20, 120, 25, 255, 255, 255);
        var black = admission.Observe(FrameRegionRenderer.SignalProfile(hudOnly), true, 1);
        Check(!black.AdmitsTracking && !black.AllowsAtlasWrite && black.ResetMotionReference,
            "HUD recognition authorized loading-frame persistence.");
        for (var i = 0; i < 3; i++) Check(!admission.Observe(pixels, true, 1).AllowsAtlasWrite);
        var resumed = admission.Observe(pixels, true, 1);
        Check(resumed.AdmitsTracking && !resumed.AllowsAtlasWrite && resumed.ResetMotionReference);
        var stable = admission.Observe(pixels, true, 1);
        Check(stable.AllowsAtlasWrite && !stable.ResetMotionReference);
        admission.Observe(FrameRegionRenderer.SignalProfile(Frame()), true, 1);
        var restarted = admission.Observe(pixels, true, 2);
        Check(restarted.AllowsAtlasWrite && restarted.ResetMotionReference && !restarted.Transition.ResumedAfterTransition);
    }
}
