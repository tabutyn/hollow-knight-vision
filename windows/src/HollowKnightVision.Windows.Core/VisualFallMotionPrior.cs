namespace HollowKnightVision.Windows.Core;

public readonly record struct VisualFallPrediction(double CameraY, double MinimumY, double MaximumY, double Age)
{
    public const double MaximumAge = .08;
}

/// <summary>Short display estimate from calibrated player fall speed and the visible Knight's screen motion.</summary>
internal sealed class VisualFallMotionPrior
{
    internal const double TerminalUnitsPerSecond = 20.948;
    internal const double PixelsPerUnitAt640 = 22.22423;

    private readonly record struct Observation(double Timestamp, double PlayerWorldY, double Height, double SolveWidth);
    private readonly Queue<double> velocities = new(3);
    private Observation? previous;

    internal void Reset()
    {
        previous = null;
        velocities.Clear();
    }

    internal void ObserveMeasured(double cameraY, StencilRect? knight, double timestamp,
        WorldSize frameSize, double solveWidth)
    {
        if (!double.IsFinite(timestamp) || Screen(knight, frameSize, solveWidth) is not { } screen
            || !double.IsFinite(cameraY + screen.Y))
        {
            Reset();
            return;
        }

        var prior = previous;
        var sample = new Observation(timestamp, cameraY + screen.Y, screen.Height, solveWidth);
        previous = sample;
        if (prior is not { } reference || solveWidth != reference.SolveWidth
            || !CompatibleHeight(screen.Height, reference.Height)
            || timestamp - reference.Timestamp is not (> 0 and <= .12))
        {
            velocities.Clear();
            return;
        }

        var velocity = (sample.PlayerWorldY - reference.PlayerWorldY) / (timestamp - reference.Timestamp);
        var terminal = TerminalUnitsPerSecond * PixelsPerUnitAt640 * (solveWidth / FrameGeometry.ReferenceWidth);
        if (!(velocity < -.95 * terminal && velocity > -1.05 * terminal))
        {
            velocities.Clear();
            return;
        }
        if (velocities.Count == 3) velocities.Dequeue();
        velocities.Enqueue(velocity);
    }

    internal VisualFallPrediction? Predict(StencilRect? knight, double timestamp, WorldSize frameSize, double solveWidth)
    {
        if (previous is not { } reference || velocities.Count != 3 || solveWidth != reference.SolveWidth
            || Screen(knight, frameSize, solveWidth) is not { } screen
            || !CompatibleHeight(screen.Height, reference.Height)) return null;
        var age = timestamp - reference.Timestamp;
        if (age is not (> 0 and <= VisualFallPrediction.MaximumAge)) return null;

        var terminal = TerminalUnitsPerSecond * PixelsPerUnitAt640 * (solveWidth / FrameGeometry.ReferenceWidth);
        var tolerance = 2 * (solveWidth / FrameGeometry.ReferenceWidth);
        var cameraY = reference.PlayerWorldY + velocities.Average() * age - screen.Y;
        var minimum = reference.PlayerWorldY - 1.05 * terminal * age - screen.Y - tolerance;
        var maximum = reference.PlayerWorldY - .95 * terminal * age - screen.Y + tolerance;
        return double.IsFinite(cameraY) && double.IsFinite(minimum) && double.IsFinite(maximum)
            ? new(cameraY, minimum, maximum, age) : null;
    }

    private static bool CompatibleHeight(double height, double reference) =>
        Math.Abs(height / reference - 1) <= .15;

    private static (double Y, double Height)? Screen(StencilRect? knight, WorldSize frameSize, double solveWidth)
    {
        if (!double.IsFinite(solveWidth) || solveWidth <= 0
            || !double.IsFinite(frameSize.Width) || !double.IsFinite(frameSize.Height)
            || frameSize.Width <= 0 || frameSize.Height <= 0
            || Math.Abs(frameSize.Width / frameSize.Height - 16d / 9) > .02
            || knight is not { IsFinite: true, IsEmpty: false } box
            || box.X < 0 || box.Y < 0 || box.Right > frameSize.Width || box.Bottom > frameSize.Height)
            return null;
        var scale = solveWidth / frameSize.Width;
        return ((frameSize.Height - box.MidY) * scale, box.Height * scale);
    }
}
