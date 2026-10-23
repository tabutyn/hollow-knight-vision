using HollowKnightVision.Windows.Core;

internal static class WorldBasisControllerTests
{
    private static void Check(bool value, string message = "World basis controller parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    private static WorldBasisObservationTicket Unwrap(WorldBasisObservationTicket? ticket)
    {
        Check(ticket.HasValue, "Expected an observation ticket.");
        return ticket.GetValueOrDefault();
    }

    public static void TestFastLossAdvancesEpochWithoutErasingWorldBasis()
    {
        var controller = new WorldBasisController();
        controller.BeginCapture(Point(100, -5));
        var old = Unwrap(controller.Observe(Point(3, 4), 1));
        var beforeLoss = controller.Snapshot;

        controller.LoseLocalTracking();
        var recovered = controller.Snapshot;

        Check(recovered.TrackingState == WorldBasisTrackingState.Recovering);
        Check(recovered.LocalEpoch == beforeLoss.LocalEpoch + 1);
        Check(recovered.WorldFromLocal == beforeLoss.WorldFromLocal);
        Check(recovered.BasisRevision == beforeLoss.BasisRevision);
        Check(controller.RebasedWorldPose(old) is null);
    }

    public static void TestDelayedCorrectionIsRejectedAfterLocalEpochChanges()
    {
        var controller = new WorldBasisController();
        controller.BeginCapture();
        var delayed = Unwrap(controller.Observe(Point(4, 2), 1));

        controller.LoseLocalTracking();
        _ = controller.Observe(Point(1, 1), 2);
        var beforeCorrection = controller.Snapshot;

        Check(beforeCorrection.TrackingState == WorldBasisTrackingState.Recovering);
        Check(!controller.ConfirmPlacement(delayed, Point(40, 20)));
        Check(controller.Snapshot == beforeCorrection);
    }

    public static void TestQueuedSameEpochObservationRebasesThroughCorrectedBasis()
    {
        var controller = new WorldBasisController();
        controller.BeginCapture();
        var matched = Unwrap(controller.Observe(Point(10, 5), 10));
        var queued = Unwrap(controller.Observe(Point(12, 8), 11));

        Check(controller.ConfirmPlacement(matched, Point(110, 55)));

        Check(controller.TrackingState == WorldBasisTrackingState.Tracking);
        var rebased = controller.RebasedObservation(queued);
        Check(rebased.HasValue, "Expected a rebased observation.");
        var resolved = rebased.GetValueOrDefault();
        Check(resolved.WorldPose == Point(112, 58));
        Check(resolved.Ticket.BasisRevision == controller.Snapshot.BasisRevision);
        Check(controller.LatestWorldPose == Point(112, 58));
    }

    public static void TestGenerationResetRejectsOldTickets()
    {
        var controller = new WorldBasisController();
        var firstGeneration = controller.BeginCapture(Point(9, 9));
        var old = Unwrap(controller.Observe(Point(1, 1), 1));

        var secondGeneration = controller.ResetCapture(Point(-2, 3));

        Check(secondGeneration == firstGeneration + 1);
        Check(controller.RebasedWorldPose(old) is null);
        Check(!controller.ConfirmPlacement(old, Point(1, 1)));
        Check(controller.Snapshot.WorldFromLocal == Point(-2, 3));
    }

    public static void TestHistoricalCorrectionUsesMatchedPoseAndRemapsLatestPose()
    {
        var controller = new WorldBasisController();
        controller.BeginCapture(Point(10, 0));
        var historical = Unwrap(controller.Observe(Point(5, 0), 100));
        _ = Unwrap(controller.Observe(Point(20, 0), 200));

        Check(controller.ConfirmPlacement(historical, Point(105, 0)));

        Check(controller.Snapshot.WorldFromLocal == Point(100, 0));
        Check(controller.RebasedWorldPose(historical) == Point(105, 0));
        Check(controller.LatestWorldPose == Point(120, 0));
    }

    private static AtlasPoint Point(double x, double y) => new(x, y);
}
