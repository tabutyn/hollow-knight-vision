namespace HollowKnightVision.Windows.Core;

public enum FramePipelineStage
{
    CaptureDelivery,
    CaptureToStage,
    FramePreparation,
    MenuStencil,
    HudStencil,
    MotionQueueWait,
    PoseAge,
    Motion,
    GroundDetection,
    GroundTracking,
    GlobalSearch,
    ObjectModel,
    ObjectPixelRefinement,
    ObjectTracking,
    RenderQueueWait,
    Render,
    MainQueueWait,
    CaptureToDraw,
    GpuQueueWait,
    GpuExecution,
    DrawToPresent,
    CaptureToPresent,
    CaptureToPublish,
}

public readonly record struct FramePipelineSummary(
    int Count,
    double P50Milliseconds,
    double P95Milliseconds);

/// <summary>
/// Bounded timing samples. Recording is one lock and one ring write per stage;
/// percentile sorting happens only when the periodic status log asks for it.
/// </summary>
public sealed class FramePipelineTelemetry
{
    private readonly object sync = new();
    private readonly int capacity;
    private readonly Dictionary<FramePipelineStage, List<double>> samples = [];
    private readonly Dictionary<FramePipelineStage, int> nextIndex = [];

    public FramePipelineTelemetry(int capacity = 120)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        this.capacity = capacity;
    }

    public void Record(FramePipelineStage stage, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return;
        }

        lock (sync)
        {
            if (!samples.TryGetValue(stage, out var values))
            {
                values = [];
                samples[stage] = values;
            }
            if (values.Count < capacity)
            {
                values.Add(seconds);
            }
            else
            {
                var index = nextIndex.GetValueOrDefault(stage);
                values[index] = seconds;
                nextIndex[stage] = (index + 1) % capacity;
            }
        }
    }

    public FramePipelineSummary? SummaryFor(FramePipelineStage stage)
    {
        double[] values;
        lock (sync)
        {
            values = samples.TryGetValue(stage, out var recorded)
                ? recorded.ToArray()
                : [];
        }
        if (values.Length == 0)
        {
            return null;
        }

        Array.Sort(values);
        double Percentile(double fraction)
        {
            var index = (int)Math.Round(
                (values.Length - 1) * fraction,
                MidpointRounding.AwayFromZero);
            return values[index] * 1_000;
        }
        return new FramePipelineSummary(
            values.Length,
            Percentile(0.50),
            Percentile(0.95));
    }
}
