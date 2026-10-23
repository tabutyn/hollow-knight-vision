using HollowKnightVision.Windows.Core;

internal static class InputAcknowledgementTrackerTests
{
    private static void Check(bool value, string message = "Input acknowledgement tracker parity assertion failed.")
    { if (!value) throw new Exception(message); }

    public static void TestAcknowledgementTrackerMeasuresOnlyMatchedStateChanges()
    {
        var sessionId = Guid.NewGuid();
        var tracker = new InputAcknowledgementTracker();
        var first = Snapshot(sessionId, 0, false, InputButtons.None);
        var heartbeat = Snapshot(sessionId, 1, false, InputButtons.None);
        var press = Snapshot(sessionId, 2, true, InputButtons.ActionX);

        Check(tracker.RecordSent(first, 1.0));
        Check(!tracker.RecordSent(heartbeat, 1.01));
        Check(tracker.RecordSent(press, 1.02));
        CheckMatchedLatency(
            tracker.Receive(Acknowledgement(sessionId, 2, InputButtons.ActionX, true), 1.035),
            0.015);
        CheckNear(tracker.P95Latency ?? -1, 0.015);
    }

    public static void TestAcknowledgementTrackerRejectsMismatchAndOldOrUnknownAcknowledgements()
    {
        var sessionId = Guid.NewGuid();
        var tracker = new InputAcknowledgementTracker();
        var press = Snapshot(sessionId, 3, true, InputButtons.Left);
        Check(tracker.RecordSent(press, 1));

        Check(tracker.Receive(
            Acknowledgement(sessionId, 3, InputButtons.Right, true),
            1.02).Kind == InputAcknowledgementTracker.ResultKind.RejectedStateMismatch);
        Check(tracker.Receive(
            Acknowledgement(Guid.NewGuid(), 3, InputButtons.Left, true),
            1.03).Kind == InputAcknowledgementTracker.ResultKind.IgnoredOldSession);
        Check(tracker.Receive(
            Acknowledgement(sessionId, 2, InputButtons.Left, true),
            1.04).Kind == InputAcknowledgementTracker.ResultKind.IgnoredUnknownSequence);
        Check(tracker.P95Latency is null);
    }

    public static void TestAcknowledgementTrackerBoundsPendingStatesAndCalculatesRollingP95()
    {
        var sessionId = Guid.NewGuid();
        var tracker = new InputAcknowledgementTracker(
            maximumOutstanding: 2,
            maximumLatencySamples: 3);
        var first = Snapshot(sessionId, 0, false, InputButtons.None);
        var press = Snapshot(sessionId, 1, true, InputButtons.Left);
        var release = Snapshot(sessionId, 2, true, InputButtons.None);
        Check(tracker.RecordSent(first, 0));
        Check(tracker.RecordSent(press, 1));
        Check(tracker.RecordSent(release, 2));
        Check(tracker.OutstandingCount == 2);
        Check(tracker.Receive(
            Acknowledgement(sessionId, 0, InputButtons.None, false),
            0.1).Kind == InputAcknowledgementTracker.ResultKind.IgnoredUnknownSequence);
        CheckMatchedLatency(tracker.Receive(
            Acknowledgement(sessionId, 1, InputButtons.Left, true),
            1.010), 0.010);
        CheckMatchedLatency(tracker.Receive(
            Acknowledgement(sessionId, 2, InputButtons.None, true),
            2.030), 0.030);
        CheckNear(tracker.P95Latency ?? -1, 0.030);
    }

    private static ReceiverInputSnapshot Snapshot(
        Guid sessionId,
        ulong sequence,
        bool enabled,
        InputButtons heldButtons) =>
        ReceiverInputSnapshot.Create(sessionId, sequence, enabled, heldButtons);

    private static ReceiverInputAcknowledgement Acknowledgement(
        Guid sessionId,
        ulong sequence,
        InputButtons appliedButtons,
        bool enabled) =>
        new(ReceiverProtocol.InputVersion, "ack", sessionId, sequence, appliedButtons, enabled);

    private static void CheckMatchedLatency(
        InputAcknowledgementTracker.Result result,
        double expected)
    {
        Check(result.Kind == InputAcknowledgementTracker.ResultKind.Matched,
            $"Expected matched acknowledgement, got {result.Kind}.");
        Check(result.Latency is { } latency);
        CheckNear(result.Latency!.Value, expected);
    }

    private static void CheckNear(double actual, double expected) =>
        Check(Math.Abs(actual - expected) <= 0.000001,
            $"Expected {expected}; got {actual}.");
}
