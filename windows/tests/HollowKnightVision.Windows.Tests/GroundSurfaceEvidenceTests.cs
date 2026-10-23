using System.Reflection;
using HollowKnightVision.Windows.Core;

internal static class GroundSurfaceEvidenceTests
{
    private static void Check(bool value, string message = "Ground surface evidence parity assertion failed.")
    { if (!value) throw new Exception(message); }

    private static void CheckLines(IReadOnlyList<DetectedFloorLine> actual, params CleanFloorLine[] expected) =>
        Check(actual.Select(line => line.ToSemanticLine()).SequenceEqual(expected),
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual.Select(line => line.ToSemanticLine()))}].");

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
