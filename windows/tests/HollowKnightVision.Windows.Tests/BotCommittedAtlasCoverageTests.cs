using HollowKnightVision.Windows.Core;

internal static class BotCommittedAtlasCoverageTests
{
    private static readonly WorldSize Size = new(640, 360);

    internal static void TestRevisitAndCorrectionKeepCoverageWithoutAwardingCells()
    {
        var coverage = new BotCommittedAtlasCoverage();
        Check(coverage.View(new(320, 180), Size, 7, null) is null, "An empty map has no placed coverage.");
        var start = Snapshot(1, [Row(0, 320)]);
        coverage.Update(start);
        Check(coverage.View(new(320, 180), Size, 7, 5) is { CoveredCells: 30, EarnedCells: 0, Health: 5 },
            "Restored territory must be a baseline and its view must use the raster's camera center.");

        var extended = Snapshot(2, [Row(0, 320), Row(1, 448), Row(2, 576)]);
        coverage.Update(extended);
        var left = coverage.View(new(320, 180), Size, 7, null)!;
        var right = coverage.View(new(576, 180), Size, 7, null)!;
        Check(left is { CoveredCells: 42, EarnedCells: 12 } && left.Left > left.Right && right.Right > right.Left,
            "Measured movement must extend the placed grid and change its frontier.");

        var corrected = Snapshot(3, [Row(0, 960), Row(1, 1088), Row(2, 1216), Row(3, 1088)]);
        coverage.Update(corrected);
        coverage.Update(corrected);
        var moved = coverage.View(new(960, 180), Size, 7, null)!;
        Check(moved is { CoveredCells: 42, EarnedCells: 12, Revision: 3 }
            && moved.Left == left.Left && moved.Right == left.Right && moved.Missing!.SequenceEqual(left.Missing!),
            "A map correction and a revisit in the same publication must not earn or leave stale cells.");

        var progressed = Snapshot(4, [.. corrected.Observations, Row(4, 1344)]);
        coverage.Update(progressed);
        Check(coverage.View(new(1344, 180), Size, 7, null) is { CoveredCells: 48, EarnedCells: 18 },
            "Measured progress beyond the corrected area must still earn cells.");
        coverage.Reset();
        coverage.Update(progressed);
        Check(coverage.View(new(1344, 180), Size, 7, null) is { CoveredCells: 48, EarnedCells: 0 },
            "A new run must use the latest committed graph as its baseline.");
    }

    internal static void TestUnplacedRecoveryAndCompositionMasksDoNotEarn()
    {
        var coverage = new BotCommittedAtlasCoverage();
        var start = Row(0, 320);
        var pending = new[] { Row(1, 1664), Row(2, 1792) };
        coverage.Update(Snapshot(1, [start]));
        coverage.Update(Snapshot(2, [start, .. pending], [
            Island(0, [start]), Island(1, pending, pending: true)
        ]));
        Check(coverage.View(new(320, 180), Size, 7, null) is { CoveredCells: 30, EarnedCells: 0 },
            "An unplaced island must not extend committed coverage.");

        var attached = new[] { Row(1, 1472), Row(2, 1600) };
        coverage.Update(Snapshot(3, [start, .. attached], [Island(0, [start]), Island(1, attached)]));
        Check(coverage.View(new(1600, 180), Size, 7, null) is { CoveredCells: 66, EarnedCells: 0 },
            "Joining already recorded footage must establish a new baseline.");
        var masked = Row(3, 1728) with { ExcludedRects = [new(new(576, 0), new(64, 360))] };
        coverage.Update(Snapshot(4, [start, .. attached, masked], [Island(0, [start]), Island(1, [.. attached, masked])]));
        Check(coverage.View(new(1728, 180), Size, 7, null) is { CoveredCells: 69, EarnedCells: 3 },
            "Only the visible part of a successfully placed extension should earn cell credit.");
    }

    internal static void TestSavedRoomCropsNeverEarnExploration()
    {
        var coverage = new BotCommittedAtlasCoverage();
        var start = Snapshot(1, [Row(0, 320)]);
        coverage.Update(start);
        Check(coverage.Totals == (30, 0), "The full frame supplies the initial coverage baseline.");

        var bounds = new Dictionary<int, VisualRoomCompositionBounds> { [7] = new(null, 576) };
        coverage.Update(start, roomBounds: bounds);
        Check(coverage.Totals == (12, 0), "Loading the doorway metadata removes cropped cells from the baseline.");

        coverage.Update(Snapshot(2, [.. start.Observations, Row(1, 448)]), roomBounds: bounds);
        Check(coverage.Totals == (12, 0), "Moving the camera past the saved room edge does not award cropped cells.");
    }

    private static LiveWorldObservation Row(int id, double x) => LiveWorldObservation.Create(
        id, id, id + 1, 640, 360, 640, 160, [], new(x, 180), new(x, 180), roomId: 7);

    private static LiveWorldSnapshot Snapshot(int revision, LiveWorldObservation[] rows, LiveTrackingIsland[]? islands = null) =>
        new(mapRevision: revision, anchoredObservationId: 0, observations: rows, trackingIslands: islands ?? [Island(0, rows)]);

    private static LiveTrackingIsland Island(int id, LiveWorldObservation[] rows, bool pending = false) =>
        new(id, 7, rows[0].Timestamp, rows[0].OptimizedPose,
            pending ? LiveIslandPlacement.Estimated : LiveIslandPlacement.Reference, default,
            pending ? LiveTrackingIsland.RecoveryBreakReason : "atlas-score-test",
            rows.Select(row => new LiveIslandMember(row.Id, new(row.OptimizedPose.X - rows[0].OptimizedPose.X, 0), true)).ToArray());

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
