using HollowKnightVision.Windows.Core;

internal static class SceneRegionDetectorTests
{
    private static void Check(bool value, string message = "Legacy region detector parity assertion failed.")
    { if (!value) throw new Exception(message); }
    private static bool Contains(StencilRect rect, double x, double y) => x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;
    private static BgraFrame Background(int width = 320, int height = 180)
    {
        var frame = new BgraFrame(width, height, width * 4, new byte[width * height * 4], 1, DateTimeOffset.UnixEpoch);
        PaintBottom(frame, 0, 0, width, height, 8, 13, 20);
        return frame;
    }
    // Reuse the exact Mac bottom-origin fixture coordinates, flipping only at the test-image boundary.
    private static void PaintBottom(BgraFrame frame, int x, int y, int width, int height,
        byte red = 255, byte green = 255, byte blue = 255, byte alpha = 255)
    {
        var top = frame.Height - y - height;
        for (var row = Math.Max(0, top); row < Math.Min(frame.Height, top + height); row++)
            for (var col = Math.Max(0, x); col < Math.Min(frame.Width, x + width); col++)
            {
                var i = row * frame.Stride + col * 4;
                frame.Pixels[i] = blue; frame.Pixels[i + 1] = green; frame.Pixels[i + 2] = red; frame.Pixels[i + 3] = alpha;
            }
    }
    private static BgraFrame Head(int x = 68, int y = 71, int width = 11)
    { var frame = Background(); PaintBottom(frame, x, y, width, 14); return frame; }
    private static BgraFrame Particle(int x, int y = 61)
    {
        var frame = Background(160, 90);
        PaintBottom(frame, x - 2, y - 2, 5, 5, 87, 87, 87);
        PaintBottom(frame, x, y, 1, 1);
        return frame;
    }

    public static void TestGeoDigits()
    {
        StencilRect Geo(int digits)
        {
            var frame = Background(640, 360);
            for (var digit = 0; digit < digits; digit++) PaintBottom(frame, 100 + digit * 12, 300, 7, 17);
            return new SceneRegionDetector().Detect(frame).Geo;
        }
        Check(Math.Abs(Geo(1).Width - 37.12) < .001);
        Check(Math.Abs(Geo(3).Width - 57.6) < .001);
    }

    public static void TestBrightKnightNotHudOrGlow()
    {
        var frame = Head();
        PaintBottom(frame, 240, 92, 2, 2);
        PaintBottom(frame, 145, 105, 18, 24, 158, 199, 255);
        var knight = new SceneRegionDetector().Detect(frame).Knight;
        Check(knight is not null && Contains(knight.Value, 73.5, 180 - 78));
        Check(knight!.Value.Height == 23 && knight.Value.Width == 12);
    }

    public static void TestFixedAnimationWidth()
    {
        var detector = new SceneRegionDetector();
        var boxes = new[] { 9, 13, 10, 12 }.Select(width => detector.Detect(Head(width: width)).Knight).ToArray();
        Check(boxes.All(box => box is not null));
        Check(boxes.Select(box => box!.Value.Width).Distinct().Count() == 1);
    }

    public static void TestStoppedKnightDoesNotDrift()
    {
        var detector = new SceneRegionDetector();
        var boxes = new[] { 68, 73, 73, 73, 73, 73, 73 }.Select(x => detector.Detect(Head(x)).Knight!.Value).ToArray();
        Check(boxes[5].MidX == boxes[6].MidX && boxes[5].MidY == boxes[6].MidY);
    }

    public static void TestFastVerticalJump()
    {
        var detector = new SceneRegionDetector();
        var boxes = new[] { 58, 82, 112 }.Select(y => detector.Detect(Head(142, y)).Knight!.Value).ToArray();
        Check(boxes[0].MidY - boxes[2].MidY > 35);
        Check(boxes.Select(box => box.Width).Distinct().Count() == 1);
        Check(Contains(boxes[2], 147.5, 180 - 119));
    }

    public static void TestOcclusionRejectsDistantLight()
    {
        var detector = new SceneRegionDetector();
        detector.Detect(Head());
        var hidden = detector.Detect(Head(238, 91)).Knight!.Value;
        Check(Contains(hidden, 73.5, 180 - 78) && !Contains(hidden, 243.5, 180 - 98));
    }

    public static void TestHaloCannotInitializeKnight()
    {
        var frame = Background();
        PaintBottom(frame, 233, 78, 22, 34, 148, 158, 168);
        PaintBottom(frame, 238, 91, 11, 14);
        Check(new SceneRegionDetector().Detect(frame).Knight is null);
    }

    public static void TestConditionalFocusPrompt()
    {
        var detector = new SceneRegionDetector(); var blank = Background();
        Check(detector.Detect(blank).FocusPrompt is null);
        var frame = Background();
        foreach (var x in new[] { 140, 147, 154, 168, 175 }) PaintBottom(frame, x, 10, 2, 9);
        var regions = detector.Detect(frame);
        Check(regions.FocusPrompt == new StencilRect(129, 156, 76, 20));
        Check(regions.Knight is null && regions.OmittedRects.Count == 4);
        var masked = FrameRegionRenderer.MapFrame(frame, regions);
        Check(masked.Pixels[((180 - 14) * 320 + 150) * 4 + 3] == 0);
        Check(detector.Detect(blank).FocusPrompt is not null);
        Check(detector.Detect(blank).FocusPrompt is null);
    }

    public static void TestParticleLifecycle()
    {
        var detector = new SceneRegionDetector(); SceneRegions? firstEligible = null, regions = null;
        foreach (var (x, index) in new[] { 103, 105, 107, 109, 111 }.Select((x, i) => (x, i)))
        {
            regions = detector.Detect(Particle(x));
            if (index < 2) Check(regions.Particles!.Count == 0);
            if (index == 2) firstEligible = regions;
        }
        Check(firstEligible!.Particles!.Count > 0 && regions!.Particles!.Count > 0);
        Check(regions!.Particles!.Any(rect => Contains(rect, 111, 90 - 61)));
        Check(regions.OmittedRects.Count == 3 + regions.Particles!.Count);
        Check(detector.Detect(Background(160, 90)).Particles!.Count > 0);
        Check(detector.Detect(Background(160, 90)).Particles!.Count == 0);
    }

    public static void TestCameraCompensatedLight()
    {
        var detector = new SceneRegionDetector(); SceneRegions? regions = null;
        for (var step = 0; step < 5; step++)
            regions = detector.Detect(Particle(112 - step * 2), new(step * 2, 0), 160);
        Check(regions!.Particles!.Count == 0, "Horizontal camera motion turned a static light into particles.");
        detector.Reset();
        for (var step = 0; step < 5; step++)
            regions = detector.Detect(Particle(112, 61 + step * 2), new(0, step * 2), 160);
        Check(regions!.Particles!.Count == 0, "Vertical camera compensation used the wrong image origin.");
    }

    public static void TestParticleHistoryCanBeSkipped()
    {
        var detector = new SceneRegionDetector();
        var gray = Background(160, 90); PaintBottom(gray, 0, 0, 160, 90, 26, 26, 26);
        Check(detector.Detect(gray, includeParticles: false).Particles!.Count == 0);
        for (var x = 103; x <= 111; x += 2) Check(detector.Detect(Particle(x), includeParticles: false).Particles!.Count == 0);
        Check(detector.Detect(Particle(113)).Particles!.Count == 0, "Disabled history was unexpectedly advanced.");
        Check(detector.Detect(Particle(115)).Particles!.Count == 0);
        Check(detector.Detect(Particle(117)).Particles!.Count > 0);
    }

    public static void TestResetAndPredictionExpiry()
    {
        var detector = new SceneRegionDetector(); var blank = Background();
        Check(detector.Detect(Head()).Knight is not null);
        for (var i = 0; i < 12; i++) Check(detector.Detect(blank).Knight is not null);
        Check(detector.Detect(blank).Knight is null);
        Check(detector.Detect(Head(238, 91)).Knight is not null);
        detector.Reset(); Check(detector.Detect(blank).Knight is null);
        for (var x = 103; x <= 111; x += 2) detector.Detect(Particle(x));
        detector.Reset(); Check(detector.Detect(Particle(113)).Particles!.Count == 0);
        Check(detector.Detect(Background(80, 70)).Particles!.Count == 0); // changed aspect ratio cannot retain old-sized history
        Check(detector.Detect(Background(320, 1)).Particles!.Count == 0);
    }

    public static void TestAppearanceRejections()
    {
        var hud = Background(); PaintBottom(hud, 68, 150, 11, 14);
        Check(new SceneRegionDetector().Detect(hud).Knight is null);
        var transparent = Background(); PaintBottom(transparent, 68, 71, 11, 14, alpha: 180);
        Check(new SceneRegionDetector().Detect(transparent).Knight is null);
        var translucent = Background(); PaintBottom(translucent, 68, 71, 11, 14, 200, 200, 200, 181);
        Check(new SceneRegionDetector().Detect(translucent).Knight is null, "Straight RGB was not premultiplied before head thresholding.");
        var pale = Background(); PaintBottom(pale, 68, 71, 11, 14, 170, 170, 170);
        Check(new SceneRegionDetector().Detect(pale).Knight is null);
        var colored = Background(); PaintBottom(colored, 68, 71, 11, 14, 171, 171, 255);
        Check(new SceneRegionDetector().Detect(colored).Knight is null);
    }
}
