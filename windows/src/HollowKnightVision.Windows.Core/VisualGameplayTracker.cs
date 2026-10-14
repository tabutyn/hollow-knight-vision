namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Converts image translation into the camera-shaped sample consumed by the
/// tiled atlas. Coordinates are pixels, so no game receiver is required.
/// </summary>
public sealed class VisualGameplayTracker
{
    private readonly CameraAccumulator camera = new()
    {
        Smoothing = 0.65,
        MinimumConfidence = 0.10f
    };
    private ulong sequence;

    public ReceiverGroundTruthSample Observe(
        BgraFrame frame,
        LowResolutionTranslationDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (diagnostic?.Motion is { } motion)
        {
            var pixelsPerCellX = (double)frame.Width / 64;
            var pixelsPerCellY = (double)frame.Height / 36;
            camera.Ingest(
                -motion.ScreenShiftX * pixelsPerCellX,
                -motion.ScreenShiftY * pixelsPerCellY,
                (float)motion.Confidence,
                frame.Width);
        }
        var position = camera.Position;
        return new ReceiverGroundTruthSample(
            ReceiverProtocol.ControlVersion,
            "visualTracking",
            Guid.Empty,
            ++sequence,
            frame.CaptureId,
            frame.ObservedAt.ToUnixTimeMilliseconds() / 1_000.0,
            "visual-gameplay",
            false,
            0, 0, 0,
            0, 0,
            true,
            false,
            true,
            position.X,
            -position.Y,
            0,
            position.X,
            -position.Y,
            0,
            frame.Height / 2.0,
            1,
            1,
            null,
            null,
            frame.Width,
            frame.Height,
            frame.Width,
            frame.Height);
    }

    public void Reset()
    {
        camera.Reset();
        sequence = 0;
    }
}
