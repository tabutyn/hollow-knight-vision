using System.Reflection;
using HollowKnightVision.Windows.Core;

internal static class GroundSurfaceEvidenceTests
{
    private static void Check(bool value, string message = "Ground surface evidence parity assertion failed.")
    { if (!value) throw new Exception(message); }

    private static void CheckLines(IReadOnlyList<DetectedFloorLine> actual, params CleanFloorLine[] expected) =>
        Check(actual.Select(line => line.ToSemanticLine()).SequenceEqual(expected),
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual.Select(line => line.ToSemanticLine()))}].");

    public static void TestNearbyRidgesWithInterleavedEvidenceProduceOneLine()
    {
        const int width = 350, height = 60;
        var response = new byte[width * height];
        foreach (var (row, left, right) in new[]
            { (20, 10, 40), (20, 110, 150), (22, 41, 70), (22, 140, 169), (22, 290, 339) })
            Array.Fill(response, (byte)80, row * width + left, right - left + 1);
        var comparison = new GroundComparisonAnalysis(width, height, response,
            Enumerable.Repeat((byte)20, response.Length).ToArray());
        var lines = GroundLineDetector.SemanticFloorLines(comparison, new(8, 40, 40, 90));
        CheckLines(lines, new CleanFloorLine(20, new(10, 150)), new CleanFloorLine(22, new(290, 339)));
        Check(lines[0].EvidenceSpans.SequenceEqual(new PixelSpan[] { new(10, 40), new(110, 150) }));
    }

    public static void TestCaptionStrokesCannotExtendAnAdjacentPlatform()
    {
        const int width = 240, height = 64, row = 22;
        var response = new byte[width * height];
        var luma = Enumerable.Repeat((byte)20, response.Length).ToArray();
        Array.Fill(response, (byte)100, row * width + 12, 74);
        Array.Fill(response, (byte)100, row * width + 140, 64);
        for (var x = 140; x <= 203; x += 5)
        for (var y = 21; y <= 27; y++)
            luma[y * width + x] = 210;

        var comparison = new GroundComparisonAnalysis(width, height, response, luma);
        CheckLines(GroundLineDetector.SemanticFloorLines(comparison, GroundTheoryTuning.SemanticDefault),
            new CleanFloorLine(row, new(12, 85)));
        CheckLines(GroundLineDetector.CleanFloorLines(comparison, GroundTheoryTuning.Default),
            new CleanFloorLine(row, new(12, 203)));
    }

    public static void TestBrightOpeningSeparatesTwoPlatformsWithLitCaps()
    {
        const int width = 320, height = 80, row = 24;
        var response = new byte[width * height];
        var luma = Enumerable.Repeat((byte)140, response.Length).ToArray();
        foreach (var left in new[] { 20, 216 })
        {
            Array.Fill(response, (byte)40, row * width + left, 56);
            for (var y = row + 1; y < height; y++)
                Array.Fill(luma, y <= row + 12 ? (byte)85 : (byte)20, y * width + left, 56);
        }

        var comparison = new GroundComparisonAnalysis(width, height, response, luma);
        var lines = GroundLineDetector.SemanticFloorLines(comparison, GroundTheoryTuning.SemanticDefault);
        Check(lines.Count == 2 && Contains(lines, 45) && Contains(lines, 245) && !Contains(lines, 145));

        var valid = Enumerable.Repeat((byte)1, response.Length).ToArray();
        for (var y = row + 13; y <= row + 24; y++) Array.Clear(valid, y * width + 216, 56);
        var masked = GroundLineDetector.SemanticFloorLines(comparison with { ValidSourcePixels = valid },
            GroundTheoryTuning.SemanticDefault);
        Check(Contains(masked, 45) && !Contains(masked, 245));

        static bool Contains(IReadOnlyList<DetectedFloorLine> floors, int x) =>
            floors.Any(line => line.Row == row && line.XSpan.Start <= x && line.XSpan.EndInclusive >= x);
    }

    public static void TestTransientBrightStrokeCannotStandInForLitPlatform()
    {
        const int width = 240, height = 76, row = 30;
        var response = new byte[width * height];
        Array.Fill(response, (byte)100, row * width + 24, 64);
        foreach (var (top, bottom) in new[] { (row - 4, row + 8), (row + 2, row + 3) })
        {
            var luma = Enumerable.Repeat((byte)20, response.Length).ToArray();
            for (var y = top; y <= bottom; y++)
                Array.Fill(luma, (byte)210, y * width + 24, 64);
            var comparison = new GroundComparisonAnalysis(width, height, response, luma);
            Check(GroundLineDetector.SemanticFloorLines(comparison, GroundTheoryTuning.SemanticDefault).Count == 0);
        }
    }

    public static void TestLocalKernelTrimsOverhangAndRecoversAnUnderestimatedEndpoint()
    {
        const int width = 150, height = 60, row = 20;
        var response = new byte[width * height];
        var luma = Enumerable.Repeat((byte)125, response.Length).ToArray();
        Array.Fill(response, (byte)50, row * width + 24, 65);
        for (var y = row + 1; y < height; y++) Array.Fill(luma, (byte)12, y * width + 35, 66);

        var comparison = new GroundComparisonAnalysis(width, height, response, luma);
        var line = GroundLineDetector.SemanticFloorLines(comparison, GroundTheoryTuning.SemanticDefault).Single();
        Check(Math.Abs(line.XSpan.Start - 35) <= 3 && Math.Abs(line.XSpan.EndInclusive - 100) <= 3,
            $"The local kernel produced {line.XSpan}.");
        Check(line.EvidenceSpans.SequenceEqual(new[] { line.XSpan }));
    }

    public static void TestStrongCoreKeepsWeakEndpointsButWeakOnlyLineIsRejected()
    {
        const int width = 200, height = 100;
        var pixels = new byte[width * height];
        for (var x = 20; x <= 119; x++) pixels[20 * width + x] = 8;
        for (var x = 45; x <= 94; x++) pixels[20 * width + x] = 9;
        for (var x = 20; x <= 119; x++) pixels[70 * width + x] = 8;
        var analysis = new GroundComparisonAnalysis(width, height, pixels,
            Enumerable.Repeat((byte)20, width * height).ToArray());
        CheckLines(GroundLineDetector.SemanticFloorLines(analysis, GroundTheoryTuning.SemanticDefault),
            new CleanFloorLine(20, new PixelSpan(20, 119)));
    }

    public static void TestSplitFragmentCannotBorrowBodySupportAcrossStep()
    {
        const int width = 200, height = 100;
        var pixels = new byte[width * height];
        for (var x = 0; x <= 59; x++) pixels[20 * width + x] = 180;
        for (var x = 140; x <= 199; x++) pixels[20 * width + x] = 180;
        for (var x = 60; x <= 139; x++) pixels[60 * width + x] = 180;
        var luma = Enumerable.Repeat((byte)20, width * height).ToArray();
        for (var row = 21; row <= 32; row++)
        for (var x = 0; x <= 59; x++)
            luma[row * width + x] = 100;
        var analysis = new GroundComparisonAnalysis(width, height, pixels, luma);
        CheckLines(GroundLineDetector.SemanticFloorLines(analysis, GroundTheoryTuning.SemanticDefault),
            new CleanFloorLine(20, new PixelSpan(140, 199)),
            new CleanFloorLine(60, new PixelSpan(60, 139)));
    }

    public static void TestVisibleBottomBandRejectsDecorationAndKeepsClippedFloor()
    {
        const int width = 100, height = 60, row = 54;
        var pixels = new byte[width * height];
        for (var x = 0; x < width; x++) pixels[row * width + x] = 180;
        var bright = new GroundComparisonAnalysis(width, height, pixels,
            Enumerable.Repeat((byte)120, width * height).ToArray());
        Check(GroundLineDetector.SemanticFloorLines(bright, GroundTheoryTuning.SemanticDefault).Count == 0);
        var dark = new GroundComparisonAnalysis(width, height, pixels,
            Enumerable.Repeat((byte)20, width * height).ToArray());
        CheckLines(GroundLineDetector.SemanticFloorLines(dark, GroundTheoryTuning.SemanticDefault),
            new CleanFloorLine(row, new PixelSpan(0, 99)));
    }

    public static void TestRejectedDecorationCannotSuppressNearbyRealFloor()
    {
        const int width = 200, height = 80;
        var pixels = new byte[width * height];
        for (var x = 10; x <= 189; x++) pixels[20 * width + x] = 180;
        for (var x = 60; x <= 139; x++) pixels[40 * width + x] = 150;
        var luma = Enumerable.Repeat((byte)120, width * height).ToArray();
        for (var row = 41; row <= 55; row++)
        for (var x = 60; x <= 139; x++)
            luma[row * width + x] = 20;
        var analysis = new GroundComparisonAnalysis(width, height, pixels, luma);
        CheckLines(GroundLineDetector.SemanticFloorLines(analysis, GroundTheoryTuning.SemanticDefault),
            new CleanFloorLine(40, new PixelSpan(60, 139)));
    }

    public static void TestMacReplayUsesTheOriginalSurfaceRule()
    {
        const int width = 160, height = 100, row = 42;
        var response = new byte[width * height];
        Array.Fill(response, (byte)180, row * width, width);
        var luma = Enumerable.Repeat((byte)20, width * height).ToArray();

        Array.Fill(luma, (byte)180, row * width, width);
        var shallow = new GroundComparisonAnalysis(width, height, response, luma);
        Check(GroundLineDetector.SemanticFloorLines(shallow, GroundTheoryTuning.SemanticDefault).Count == 0);
        Check(GroundLineDetector.MacReplaySemanticFloorLines(shallow, GroundTheoryTuning.SemanticDefault)
            is [{ Row: row }]);

        Array.Fill(luma, (byte)80, (row - 5) * width, 18 * width);
        var lit = new GroundComparisonAnalysis(width, height, response, luma);
        Check(GroundLineDetector.SemanticFloorLines(lit, GroundTheoryTuning.SemanticDefault).Count == 0);
        Check(GroundLineDetector.MacReplaySemanticFloorLines(lit, GroundTheoryTuning.SemanticDefault)
            is [{ Row: row }]);

        var valid = Enumerable.Repeat((byte)1, width * height).ToArray();
        Array.Clear(valid, (row + 18) * width, width);
        Check(GroundLineDetector.MacReplaySemanticFloorLines(
            lit with { ValidSourcePixels = valid }, GroundTheoryTuning.SemanticDefault).Count == 0);

        var clippedResponse = new byte[width * height];
        Array.Fill(clippedResponse, (byte)180, (height - 9) * width, width);
        Array.Fill(luma, (byte)80);
        Check(GroundLineDetector.MacReplaySemanticFloorLines(
            new(width, height, clippedResponse, luma), GroundTheoryTuning.SemanticDefault).Count == 0);
    }

    public static void TestEvidenceScoringDoesNotLetDarkGapApproveBrightEdge()
    {
        const int width = 200, height = 80;
        var luma = new byte[width * height];
        for (var row = 21; row <= 32; row++)
        {
            for (var x = 0; x <= 39; x++) luma[row * width + x] = 120;
            for (var x = 160; x <= 199; x++) luma[row * width + x] = 120;
        }
        var surface = new SurfaceEvidence(new GroundComparisonAnalysis(width, height, [], luma));
        Check(surface.Supports(20, new PixelSpan(0, 199)));
        Check(!surface.Supports(20, new PixelSpan(0, 39), new PixelSpan(160, 199)));
    }

    public static void TestMaskedPixelsDoNotChangeSupportedBodyVote()
    {
        const int width = 100, height = 60;
        var luma = Enumerable.Repeat((byte)20, width * height).ToArray();
        var valid = Enumerable.Repeat((byte)1, width * height).ToArray();
        for (var row = 21; row <= 32; row++)
        for (var x = 0; x <= 60; x++)
        {
            luma[row * width + x] = 255;
            valid[row * width + x] = 0;
        }
        var surface = new SurfaceEvidence(new GroundComparisonAnalysis(width, height, [], luma, valid));
        Check(surface.Supports(20, new PixelSpan(0, 99)));
    }

    private sealed class SurfaceEvidence
    {
        private readonly object instance;
        private readonly MethodInfo supports;

        internal SurfaceEvidence(GroundComparisonAnalysis analysis)
        {
            var type = typeof(GroundLineDetector).GetNestedType("GroundSurfaceEvidence", BindingFlags.NonPublic)
                ?? throw new Exception("Windows GroundSurfaceEvidence implementation is missing.");
            instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [analysis], null) ?? throw new Exception("Windows GroundSurfaceEvidence could not be constructed.");
            supports = type.GetMethod("Supports", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new Exception("Windows GroundSurfaceEvidence.Supports is missing.");
        }

        internal bool Supports(int row, params PixelSpan[] ranges) =>
            (bool)(supports.Invoke(instance, [row, ranges])
                ?? throw new Exception("Windows GroundSurfaceEvidence.Supports returned no result."));
    }
}
