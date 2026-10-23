using HollowKnightVision.Windows.Core;

internal static class GroundSemanticAtlasTests
{
    private static void Check(bool value, string message = "Ground semantic atlas parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    public static void TestSemanticEvidenceApprovesOnlyMatchingStableLineAndPersists()
    {
        var atlas = new GroundSemanticAtlas();
        var ground = new GroundHypothesisAtlasLine(
            1,
            new AtlasPoint(20, 65),
            new AtlasPoint(140, 65));
        var decoration = new GroundHypothesisAtlasLine(
            2,
            new AtlasPoint(20, 25),
            new AtlasPoint(140, 25));

        IReadOnlyList<GroundHypothesisAtlasLine> approved = [];
        for (var index = 0; index < 6; index++)
        {
            approved = atlas.Update(
                [ground, decoration],
                [new CleanFloorLine(40, new PixelSpan(16, 143))],
                new AtlasPoint(10, 5),
                160,
                100,
                true);
        }
        Check(approved.Select(line => line.SegmentId).SequenceEqual([1]));

        var persisted = atlas.Update(
            [ground, decoration],
            [],
            new AtlasPoint(500, 0),
            160,
            100,
            true);
        Check(persisted.Select(line => line.SegmentId).SequenceEqual([1]));
    }

    public static void TestReassociatedIDInheritsApprovalFromSameGeometry()
    {
        var atlas = new GroundSemanticAtlas();
        var original = new GroundHypothesisAtlasLine(
            1,
            new AtlasPoint(20, 65),
            new AtlasPoint(140, 65));
        for (var index = 0; index < 6; index++)
        {
            _ = atlas.Update(
                [original],
                [new CleanFloorLine(40, new PixelSpan(16, 143))],
                new AtlasPoint(10, 5),
                160,
                100,
                true);
        }

        var replacement = new GroundHypothesisAtlasLine(
            9,
            new AtlasPoint(24, 66),
            new AtlasPoint(144, 66));
        Check(atlas.Update(
                [replacement],
                [],
                null,
                160,
                100,
                false)
            .Select(line => line.SegmentId)
            .SequenceEqual([9]));
    }
}
