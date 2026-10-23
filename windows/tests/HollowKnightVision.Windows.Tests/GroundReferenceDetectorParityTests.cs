using HollowKnightVision.Windows.Core;

internal static class GroundReferenceDetectorParityTests
{
    private static void Check(
        bool value,
        string message = "Ground reference detector parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static PixelRect FromMacBottomLeft(
        int imageHeight,
        int x,
        int y,
        int width,
        int height) => new(x, imageHeight - y - height, width, height);

    public static void TestKnownForegroundAtDifferentRowDoesNotRejectLine()
    {
        var line = new DetectedFloorLine(80, new PixelSpan(40, 79));
        var foreground = FromMacBottomLeft(100, 38, 40, 44, 40);

        var filtered = GroundLineDetector.RejectKnownForegroundLines(
            [line],
            imageHeight: 100,
            [foreground]);

        Check(filtered.Count == 1 && filtered[0] == line);
    }

    public static void TestKnightUpperBodyRejectsLongFalseLineButKeepsFloorAtFeet()
    {
        var falseUpperLine = new DetectedFloorLine(
            35,
            new PixelSpan(20, 180),
            [new PixelSpan(85, 115)]);
        var floorAtFeet = new DetectedFloorLine(76, new PixelSpan(0, 199));
        var knight = FromMacBottomLeft(100, 85, 25, 30, 40);

        var filtered = GroundLineDetector.RejectKnownForegroundLines(
            [falseUpperLine, floorAtFeet],
            imageHeight: 100,
            [knight],
            knight);

        Check(filtered.Count == 1 && filtered[0] == floorAtFeet);
    }

    public static void TestSmallKnightDetectionInfersBodyAboveDetectedPatch()
    {
        var falseUpperLine = new DetectedFloorLine(
            306,
            new PixelSpan(150, 400),
            [new PixelSpan(290, 320)]);
        var floorAtFeet = new DetectedFloorLine(350, new PixelSpan(0, 639));
        var weakKnightPatch = FromMacBottomLeft(360, 287, 22, 32, 14);

        var filtered = GroundLineDetector.RejectKnownForegroundLines(
            [falseUpperLine, floorAtFeet],
            imageHeight: 360,
            [weakKnightPatch],
            weakKnightPatch);

        Check(filtered.Count == 1 && filtered[0] == floorAtFeet);
    }

    public static void TestJitteringKnightBoxCannotEraseRoomWideFloorEvidence()
    {
        var floor = new DetectedFloorLine(
            262,
            new PixelSpan(16, 624),
            [new PixelSpan(16, 270), new PixelSpan(330, 624)]);

        for (var bottom = 76; bottom <= 92; bottom++)
        {
            var knight = FromMacBottomLeft(360, 280, bottom, 40, 26);
            var filtered = GroundLineDetector.RejectKnownForegroundLines(
                [floor],
                imageHeight: 360,
                [knight],
                knight);
            Check(filtered.Count == 1 && filtered[0] == floor);
        }
    }

    public static void TestRequestedTuningDefaultsAreLockedIn()
    {
        var tuning = GroundTheoryTuning.Default;
        Check(tuning.GroundThreshold == 10);
        Check(tuning.MinimumSegmentLength == 48);
        Check(tuning.LineSeparation == 36);
        Check(tuning.OcclusionMergeGap == 180);
    }

    public static void TestGroundDetectorDoesNotBridgeThresholdGaps()
    {
        const int width = 12;
        var ground = new byte[width];
        for (var x = 0; x <= 3; x++) ground[x] = 255;
        for (var x = 5; x <= 8; x++) ground[x] = 255;

        var detection = GroundLineDetector.Detect(
            new GroundComparisonAnalysis(width, 1, ground),
            searchMinimumFrameY: 0,
            searchMaximumFrameY: 1,
            new GroundTheoryTuning(128, 5));

        Check(detection is null);
    }

    public static void TestTheoryDetectorKeepsMultipleLongLevelsAndRejectsShortIsland()
    {
        const int width = 200;
        const int height = 100;
        var ground = new byte[width * height];
        for (var x = 10; x <= 150; x++) ground[20 * width + x] = 255;
        for (var x = 40; x <= 180; x++) ground[60 * width + x] = 230;
        for (var x = 10; x <= 30; x++) ground[40 * width + x] = 255;

        var detection = GroundLineDetector.Detect(
            new GroundComparisonAnalysis(width, height, ground),
            searchMinimumFrameY: 20,
            searchMaximumFrameY: 90,
            new GroundTheoryTuning(128, 40));

        Check(detection is not null);
        Check(detection!.Segments.Count == 2);
        var rows = detection.Segments
            .Select(segment => (int)Math.Round(
                segment.FrameY,
                MidpointRounding.AwayFromZero))
            .ToHashSet();
        Check(rows.SetEquals([40, 80]));
    }

    public static void TestLineSeparationKeepsLongerOverlappingLineButPreservesOtherColumns()
    {
        const int width = 220;
        const int height = 40;
        var ground = new byte[width * height];
        for (var x = 10; x <= 190; x++) ground[10 * width + x] = 220;
        for (var x = 40; x <= 130; x++) ground[14 * width + x] = 255;
        for (var x = 195; x <= 219; x++) ground[13 * width + x] = 255;

        var lines = GroundLineDetector.CleanFloorLines(
            new GroundComparisonAnalysis(width, height, ground),
            new GroundTheoryTuning(128, 20, 6, 0));

        Check(lines.Any(line => line.Row == 10
            && line.XSpan == new PixelSpan(10, 190)));
        Check(!lines.Any(line => line.Row == 14));
        Check(lines.Any(line => line.Row == 13
            && line.XSpan == new PixelSpan(195, 219)));
    }

    public static void TestDominantFloorSuppressesShortForegroundTopInsideOcclusion()
    {
        const int width = 320;
        const int height = 80;
        var ground = new byte[width * height];
        for (var x = 16; x <= 110; x++) ground[40 * width + x] = 220;
        for (var x = 160; x <= 300; x++) ground[40 * width + x] = 220;
        for (var x = 112; x <= 150; x++) ground[24 * width + x] = 255;

        var lines = GroundLineDetector.CleanFloorLines(
            new GroundComparisonAnalysis(width, height, ground),
            new GroundTheoryTuning(128, 30, 40, 300));

        Check(lines.Any(line => line.Row == 40
            && line.XSpan == new PixelSpan(16, 300)
            && line.EvidenceSpans.SequenceEqual(
                [new PixelSpan(16, 110), new PixelSpan(160, 300)])));
        Check(!lines.Any(line => line.Row == 24));
    }

    public static void TestSparseLongBridgeCannotOutrankSolidFloor()
    {
        const int width = 320;
        const int height = 80;
        var pixels = new byte[width * height];
        for (var x = 20; x <= 180; x++) pixels[30 * width + x] = 180;
        for (var x = 20; x <= 39; x++) pixels[35 * width + x] = 200;
        for (var x = 280; x <= 299; x++) pixels[35 * width + x] = 200;

        var lines = GroundLineDetector.CleanFloorLines(
            new GroundComparisonAnalysis(width, height, pixels),
            new GroundTheoryTuning(60, 30, 40, 300));

        Check(lines.Any(line => line.Row == 30));
        Check(!lines.Any(line => line.Row == 35));
    }

    public static void TestEmptyGapDoesNotCountTowardMinimumEvidenceLength()
    {
        const int width = 320;
        const int height = 80;
        var pixels = new byte[width * height];
        for (var x = 20; x <= 23; x++) pixels[30 * width + x] = 255;
        for (var x = 280; x <= 283; x++) pixels[30 * width + x] = 255;

        var lines = GroundLineDetector.CleanFloorLines(
            new GroundComparisonAnalysis(width, height, pixels),
            new GroundTheoryTuning(60, 30, 40, 300));

        Check(lines.Count == 0);
    }

    public static void TestOcclusionMergeGapJoinsDistantEvidenceIntoOneGroundLine()
    {
        const int width = 280;
        const int height = 30;
        var ground = new byte[width * height];
        for (var x = 10; x <= 60; x++) ground[12 * width + x] = 255;
        for (var x = 201; x <= 260; x++) ground[12 * width + x] = 255;
        var comparison = new GroundComparisonAnalysis(width, height, ground);

        var separated = GroundLineDetector.CleanFloorLines(
            comparison,
            new GroundTheoryTuning(128, 100, 0, 139));
        Check(separated.Count == 0);

        var joined = GroundLineDetector.CleanFloorLines(
            comparison,
            new GroundTheoryTuning(128, 100, 0, 140));
        Check(joined.Count == 1);
        Check(joined[0].Row == 12);
        Check(joined[0].XSpan == new PixelSpan(10, 260));
        Check(joined[0].EvidenceSpans.SequenceEqual(
            [new PixelSpan(10, 60), new PixelSpan(201, 260)]));
    }
}
