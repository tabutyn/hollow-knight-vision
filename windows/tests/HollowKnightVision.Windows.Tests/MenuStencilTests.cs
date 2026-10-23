using HollowKnightVision.Windows.Core;

internal static class MenuStencilTests
{
    internal static void TestConsensusAlignment()
    {
        var shifts = new[] { new MenuStencilConsensus.Offset(0, 0), new(2, 1), new(-1, -1) };
        var captures = shifts.Select((shift, index) => new MenuStencilConsensus.Capture(
            Pixels(ConsensusFixture(shift.X, shift.Y, (byte)(170 + index * 30), 17 - index * 2, 4 + index)),
            new(8 + (index == 1 ? -1 : 0), 3 + (index == 2 ? 1 : 0), 12, 10))).ToArray();
        var result = MenuStencilConsensus.Build(captures, new(8, 3, 12, 10), preservesAnnotationBounds: true)
            ?? throw new Exception("Consensus rejected Mac synthetic captures.");
        Equal(new StencilRect(8, 3, 12, 10), result.Bounds);
        Check(shifts.SequenceEqual(result.AlignedOffsets), "Capture registration differs from Mac.");
        Check(result.IncludedPixelCount > 8 && result.IncludedPixelCount < 120, "Consensus did not isolate glyphs.");
        Equal(result.IncludedPixelCount, result.Reference.Mask!.Count(value => value != 0));
        var kernel = ImageStencilKernel.FromReference("synthetic-text", result.Reference);
        var comparison = captures[0].Pixels.Compare(kernel, result.Bounds)!;
        Check(!comparison.SupportsCorrelation, "Uniform glyph color should use color fallback.");
        Check(comparison.Confidence(.82, 105) > .40, "Consensus text does not match its source.");
        var selector = MenuStencilConsensus.Build(captures, new(8, 3, 12, 10))!;
        Equal(new StencilRect(10, 5, 7, 5), selector.Bounds);
    }

    internal static void TestLocalizedText()
    {
        var nominal = new StencilRect(12, 5, 3, 5);
        var captures = new[] { (2, 1), (27, 15), (3, 14) }.Select(noise => new MenuStencilConsensus.Capture(
            Pixels(ConsensusFixture(0, 0, 220, noise.Item1, noise.Item2)), nominal)).ToArray();
        var result = MenuStencilConsensus.BuildLocalizedText(captures, nominal)!;
        Check(result.Bounds.X < nominal.X && result.Bounds.Right > nominal.Right && result.IncludedPixelCount > 8,
            "Localized glyph width was clipped to English annotation.");
        // Separate value column must not be merged into the nearest label cluster.
        var columns = Pixels(Frame(100, 20, (x, y) => y is >= 5 and < 10 && (x is >= 10 and < 20 || x is >= 60 and < 70)
            ? ((byte)220, (byte)220, (byte)220) : ((byte)10, (byte)20, (byte)60)));
        var isolated = MenuStencilConsensus.BuildLocalizedText([new(columns, new(12, 5, 3, 5))], new(12, 5, 3, 5))!;
        Check(isolated.Bounds.X <= 10 && isolated.Bounds.Right <= 21, "Localized stencil swallowed a value column.");
    }

    internal static void TestVerifiedBounds()
    {
        var mask = new byte[60];
        for (var y = 1; y <= 4; y++) for (var x = 2; x <= 7; x++) mask[y * 10 + x] = 255;
        var reference = new ImageStencilReference(10, 6, Enumerable.Repeat((byte)180, 180).ToArray(), mask);
        var destination = new StencilRect(100, 200, 200, 60);
        Equal(new StencilRect(140, 210, 120, 40), reference.VerifiedBounds(destination, 0));
        Equal(new StencilRect(139, 209, 122, 42), reference.VerifiedBounds(destination));
        Equal(destination, reference.VerifiedBounds(destination, 1000));
        Equal(destination, (reference with { Mask = new byte[60] }).VerifiedBounds(destination));
        Equal(destination, (reference with { Mask = null }).VerifiedBounds(destination));
        var frame = reference.MakeFrame()!;
        Equal((byte)0, frame.Row(0)[3]); Equal((byte)255, frame.Row(1)[2 * 4 + 3]);
        Check(!(reference with { Mask = new byte[1] }).IsValid, "Bad reference mask length accepted.");
    }

    internal static void TestSelectorContrast()
    {
        var bounds = new StencilRect(3, 3, 11, 5);
        var selected = Pixels(SelectorFixture(true)); var idle = Pixels(SelectorFixture(false));
        var positive = MenuStencilConsensus.Build([new(selected, bounds)], bounds, alignmentRadius: 0, preservesAnnotationBounds: true)!;
        var filtered = MenuStencilConsensus.RemovingStableBackground(positive.Reference, positive.Bounds, [idle, idle])!;
        for (var y = 1; y <= 3; y++)
        {
            for (var x = 1; x <= 3; x++) Equal((byte)0, filtered.Mask![y * filtered.Width + x]);
            for (var x = 7; x <= 9; x++) Equal((byte)255, filtered.Mask![y * filtered.Width + x]);
        }
        Check(MenuStencilConsensus.RemovingStableBackground(positive.Reference, positive.Bounds, [idle]) is null,
            "One negative frame cannot establish stable foreground.");
        Check(MenuStencilConsensus.RemovingStableBackground(positive.Reference, positive.Bounds, [selected, selected]) is null,
            "Background-only result must not become a selector.");
        var mask = ImageStencilForegroundMask.Create([idle, idle], bounds)!;
        Equal(18, selected.LikelyMenuTextPixelCount(bounds)); Equal(9, selected.LikelyMenuTextPixelCount(bounds, mask));
        Equal(9, selected.BrightMenuPixelCount(bounds, mask));
        Check(mask.Contains(4, 4) && !mask.Contains(10, 4) && !mask.Contains(-1, -1), "Idle occupancy mask misaligned.");
        Check(ImageStencilForegroundMask.Create([idle], bounds) is null, "One frame accepted as stable baseline.");
    }

    internal static void TestPixelKernel()
    {
        var source = Pixels(Frame(4, 2, (x, y) => { var value = (byte)((y * 4 + x) * 10 + 10); return (value, value, value); }));
        var reference = source.Reference(new(0, 0, 4, 2))!;
        var kernel = ImageStencilKernel.FromReference("ramp", reference);
        var same = source.Compare(kernel, new(0, 0, 4, 2))!;
        Check(same.SupportsCorrelation, "Varying image lost correlation.");
        Near(1, same.NormalizedCorrelation); Near(1, same.Confidence(.82, 105));
        var lighter = Pixels(Frame(4, 2, (x, y) => { var value = (byte)((y * 4 + x) * 10 + 30); return (value, value, value); }));
        var comparison = lighter.Compare(kernel, new(0, 0, 4, 2))!;
        Near(1, comparison.NormalizedCorrelation); Near(20, comparison.MeanAbsoluteColorError);
        Near(.82 + .18 * (1 - 20.0 / 105), comparison.Confidence(.82, 105));
        Equal(2, ImageStencilKernel.FromReference("sparse", reference, 2).Samples.Count);
        Check(source.Compare(kernel, new(-1, 0, 4, 2)) is null && source.Compare(kernel, new(0, 1, 4, 2)) is null,
            "Out-of-band comparison accepted.");
        var bad = new ImageStencilKernel("invalid", new(0, 0, 4, 2), []);
        Check(source.Compare(bad, new(0, 0, 4, 2)) is null, "Empty kernel matched.");
        var flat = Pixels(Frame(4, 2, (_, _) => ((byte)100, (byte)100, (byte)100)));
        Check(!flat.Compare(kernel, new(0, 0, 4, 2))!.SupportsCorrelation, "Constant live image supports correlation.");
    }

    internal static void TestBandAndScaling()
    {
        var frame = Frame(4, 4, (x, y) => ((byte)(x * 20), (byte)(y * 30), (byte)90));
        var band = new ImageStencilPixels(frame, 4, 4, 1, 3);
        Equal(1, band.OriginY); Equal(2, band.Height);
        Check(band.Rgb(0, 0) is null && band.Rgb(4, 1) is null && band.Rgb(0, 3) is null, "Band bounds ignored.");
        Equal(((byte)40, (byte)30, (byte)90), band.Rgb(2, 1)!.Value);
        var fractional = band.Reference(new(.2, 1.2, 1.1, .6))!;
        Equal(2, fractional.Width); Equal(1, fractional.Height);
        Check(band.Reference(new(0, .9, 1, 1)) is null, "Partially outside crop returned reference.");
        var original = Pixels(frame);
        var scaledFrame = Frame(8, 8, (x, y) => ((byte)((x / 2) * 20), (byte)((y / 2) * 30), (byte)90));
        var normalized = new ImageStencilPixels(scaledFrame, 4, 4);
        Check(original.Reference(new(0, 0, 4, 4))!.Rgb.SequenceEqual(normalized.Reference(new(0, 0, 4, 4))!.Rgb),
            "2x source downsampling changed reference pixels.");
        var fullScaled = Pixels(scaledFrame);
        var kernel = ImageStencilKernel.FromReference("scaled", original.Reference(new(0, 0, 4, 4))!);
        Near(1, fullScaled.Compare(kernel, new(0, 0, 8, 8))!.Confidence(.82, 105));
    }

    internal static void TestTextPredicatesAndInvalidConsensus()
    {
        var colors = new (byte, byte, byte)[] { (66,66,66), (65,65,65), (80,80,80), (100,100,158), (100,100,159), (200,200,200) };
        var pixels = Pixels(Frame(6, 1, (x, _) => colors[x]));
        Check(pixels.IsLikelyMenuText(0, 0) && !pixels.IsLikelyMenuText(1, 0) && pixels.IsLikelyMenuText(2, 0), "Faded text thresholds changed.");
        Equal(3, pixels.LikelyMenuTextPixelCount(new(-10, -10, 50, 50)));
        Equal(2, pixels.BrightMenuPixelCount(new(0, 0, 6, 1)));
        Check(MenuStencilConsensus.Build([], new(0, 0, 1, 1)) is null, "Empty consensus accepted.");
        var loneDust = Pixels(Frame(32, 18, (x, y) => x == 12 && y == 8 ? ((byte)220, (byte)220, (byte)220) : ((byte)0, (byte)0, (byte)0)));
        Check(MenuStencilConsensus.Build([new(loneDust, new(8, 3, 12, 10))], new(8, 3, 12, 10)) is null, "Isolated dust became a glyph.");
        var band = new ImageStencilPixels(Frame(32, 18, (_, _) => ((byte)220, (byte)220, (byte)220)), 32, 18, 1, 17);
        Check(MenuStencilConsensus.Build([new(band, new(8, 3, 12, 10))], new(8, 3, 12, 10)) is null, "Nonzero-origin consensus must be rejected.");
    }

    private static BgraFrame ConsensusFixture(int shiftX, int shiftY, byte text, int noiseX, int noiseY) => Frame(32, 18, (x, y) =>
    {
        if (x == noiseX && y == noiseY) return ((byte)240, (byte)240, (byte)240);
        var gx = x - 10 - shiftX; var gy = y - 5 - shiftY;
        if (gx is >= 0 and < 2 && gy is >= 0 and < 5 || gx is >= 0 and < 7 && gy is >= 2 and < 4) return (text, text, text);
        return ((byte)12, (byte)24, (byte)62);
    });
    private static BgraFrame SelectorFixture(bool selected) => Frame(20, 12, (x, y) => y is >= 4 and < 7
        && (x is >= 4 and < 7 || selected && x is >= 10 and < 13) ? ((byte)230, (byte)230, (byte)230) : ((byte)12, (byte)24, (byte)62));
    private static ImageStencilPixels Pixels(BgraFrame frame) => new(frame, frame.Width, frame.Height);
    private static BgraFrame Frame(int width, int height, Func<int, int, (byte Red, byte Green, byte Blue)> color)
    {
        var stride = width * 4 + 12; var bytes = new byte[stride * height]; // exercise padded capture rows
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var rgb = color(x, y); var offset = y * stride + x * 4;
            bytes[offset] = rgb.Blue; bytes[offset + 1] = rgb.Green; bytes[offset + 2] = rgb.Red; bytes[offset + 3] = 255;
        }
        return new(width, height, stride, bytes, 0, DateTimeOffset.UnixEpoch);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Near(double expected, double actual) => Check(double.IsFinite(actual) && Math.Abs(expected - actual) < .000001, $"Expected {expected}, got {actual}.");
}
