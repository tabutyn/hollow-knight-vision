using HollowKnightVision.Windows.Core;

internal static class GroundClearanceTests
{
    public static void TestGroundClearanceRejectsOnlyEnclosedShorterFloors()
    {
        var outer = Floor(100, 10, 119);
        var above = Floor(81, 10, 40);
        var below = Floor(119, 60, 119);
        var headroom = Floor(120, 10, 40);
        var offset = Floor(98, 120, 170);
        var sameRow = Floor(100, 70, 100);
        var result = Filter([above, outer, below, headroom, offset, sameRow], new(0, 0, 10, 20));
        Check(result.SequenceEqual([outer, headroom, offset, sameRow]),
            "Clearance did not retain the separate platforms and surfaces with enough headroom.");

        var bridge = new DetectedFloorLine(100, new(0, 120), [new(0, 20), new(100, 120)]);
        var ledge = Floor(112, 40, 75);
        Check(Filter([bridge, ledge]).SequenceEqual([bridge, ledge]),
            "A ledge was suppressed by an unsupported bridge.");
    }

    public static void TestGroundClearanceTrimsOnlyShortSharedTips()
    {
        var left = Floor(80, 0, 99);
        var right = Floor(90, 90, 189);
        var cut = Filter([left, right]);
        Check(cut.Count == 2 && cut[0].XSpan == new PixelSpan(0, 94)
            && cut[1].XSpan == new PixelSpan(95, 189), "Lightly overlapping tips did not meet without a gap.");
        var reversed = Filter([right, left]);
        Check(reversed.Select(line => line.XSpan).Reverse().SequenceEqual(cut.Select(line => line.XSpan)),
            "The endpoint cut changed with input order.");
        var substantialOverlap = Floor(90, 50, 149);
        Check(Filter([left, substantialOverlap]).SequenceEqual([left, substantialOverlap]),
            "The tip adjustment changed platforms with substantial overlap.");

        var thinTip = new DetectedFloorLine(80, new(0, 99), [new(0, 89), new(99, 99)]);
        var supported = Filter([thinTip, right]);
        Check(supported.Count == 2 && supported[0].XSpan == new PixelSpan(0, 89)
            && supported[0].EvidenceSpans.SequenceEqual([new PixelSpan(0, 89)]) && supported[1] == right,
            "Trimming discarded measured floor columns instead of the weak tip.");
    }

    private static DetectedFloorLine Floor(int row, int start, int end) => new(row, new(start, end));

    private static IReadOnlyList<DetectedFloorLine> Filter(DetectedFloorLine[] lines, PixelRect? knight = null) =>
        GroundLineDetector.RejectKnownForegroundLines(lines, 360, [], knight);

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
