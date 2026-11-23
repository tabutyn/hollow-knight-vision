namespace HollowKnightVision.Windows.Core;

/// <summary>Registers stationary pushes in camera-compensated coordinates for one visit.</summary>
internal sealed class BotHackerContact
{
    private readonly List<BotBlockadeMark> marks = [];
    private (AtlasPoint Position, double Time)? previous;
    private (AtlasPoint Anchor, double Time, int Side, int Samples)? push;
    private string? scene;

    public void Reset()
    {
        marks.Clear();
        scene = null;
        Interrupt();
    }

    public void Interrupt()
    {
        previous = null;
        push = null;
    }

    public BotBlockadeEvidence? Observe(BotCaptureObservation row, GroundTruthCameraTransform camera, WorldSize size,
        bool grounded)
    {
        if (row.Features[4] < .5 || row.Issues.Any(issue => issue != "missing-terrain-analysis")
            || Math.Abs(camera.PixelsPerWorldUnitX / camera.PixelsPerWorldUnitY - 1) >= .02)
        {
            previous = null;
            push = null;
            return null;
        }
        var width = size.Width / camera.PixelsPerWorldUnitX;
        var position = new AtlasPoint(camera.CameraWorld.X - width / 2 + row.Features[0] * width,
            camera.CameraWorld.Y - size.Height / (2 * camera.PixelsPerWorldUnitY) + row.Features[1] * width);
        var time = row.CaptureTimestamp;
        if (scene != camera.SceneName) Reset();
        if (previous is { } prior && (time - prior.Time is < 0 or > .35 || Distance(prior.Position, position) > .18 * width))
        {
            previous = null;
            push = null;
        }
        scene = camera.SceneName;

        if (grounded && row.DriveDirection is -1 or 1)
        {
            var side = row.DriveDirection.Value;
            var toleranceX = width * Math.Clamp(row.Features[2] * .15, .0015, .006);
            var toleranceY = width * Math.Clamp(row.Features[3] * .25, .006, .018);
            if (push is not { } drive || drive.Side != side || Math.Abs(position.X - drive.Anchor.X) > toleranceX
                || Math.Abs(position.Y - drive.Anchor.Y) > toleranceY)
                drive = (position, time, side, 0);
            drive.Samples++;
            if (drive.Samples >= 2 && time - drive.Time >= .15)
            {
                var point = new AtlasPoint(position.X + side * width * Math.Max(.008, row.Features[2] / 2), position.Y);
                var matching = marks.FindIndex(candidate => candidate.Direction == side
                    && Distance(new(candidate.X, candidate.Y), point) <= .045 * width);
                var mark = new BotBlockadeMark("hacker/" + scene, point.X, point.Y, side, width, false);
                if (matching < 0) marks.Add(mark);
                else if (marks[matching].Crossed) marks[matching] = mark;
            }
            push = drive;
        }
        else push = null;

        BotBlockadeEvidence? nearest = null;
        for (var i = 0; i < marks.Count; i++)
        {
            var mark = marks[i];
            if (mark.Crossed) continue;
            var dx = (mark.X - position.X) / width;
            var dy = (mark.Y - position.Y) / width;
            if (Math.Abs(dy) > .065) continue;
            var ahead = dx * mark.Direction;
            if (previous is { } evidence && ahead < -.018 && Math.Abs(evidence.Position.Y - mark.Y) <= .065 * width
                && (mark.X - evidence.Position.X) * mark.Direction >= -.018 * width)
            {
                marks[i] = mark with { Crossed = true };
                continue;
            }
            if (ahead is >= -.018 and <= .095 && (nearest is null || Math.Abs(dx) < Math.Abs(nearest.Dx)))
                nearest = new(mark, dx, dy);
        }
        previous = (position, time);
        return nearest;
    }

    private static double Distance(AtlasPoint a, AtlasPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
