using HollowKnightVision.Windows.Core;

internal static class FramePipelineTelemetryTests
{
    private static void Check(bool value, string message = "Frame pipeline telemetry parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    private static void CheckClose(double actual, double expected, double tolerance)
    {
        Check(Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected}, got {actual}.");
    }

    public static void TestBoundedRingRetainsNewestSamplesForP95()
    {
        var telemetry = new FramePipelineTelemetry(capacity: 4);
        foreach (var duration in new[] { 0.001, 0.002, 0.003, 0.004, 0.050 })
        {
            telemetry.Record(FramePipelineStage.CaptureToPresent, duration);
        }

        var summary = telemetry.SummaryFor(FramePipelineStage.CaptureToPresent);
        Check(summary.HasValue, "Expected a capture-to-present summary.");
        var value = summary.GetValueOrDefault();
        Check(value.Count == 4);
        CheckClose(value.P50Milliseconds, 4, 0.001);
        CheckClose(value.P95Milliseconds, 50, 0.001);
    }

    public static void TestRejectsNegativeAndNonFiniteDurations()
    {
        var telemetry = new FramePipelineTelemetry(capacity: 2);
        telemetry.Record(FramePipelineStage.Render, -0.1);
        telemetry.Record(FramePipelineStage.Render, double.PositiveInfinity);
        Check(telemetry.SummaryFor(FramePipelineStage.Render) is null);
    }
}
