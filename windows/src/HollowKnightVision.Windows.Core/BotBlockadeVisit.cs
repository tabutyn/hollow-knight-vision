namespace HollowKnightVision.Windows.Core;

/// <summary>The bottom-left camera origin and view width in a measured coordinate space.</summary>
public sealed record BotBlockadePose(string Space, double X, double Y, double Width)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Space) && double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && Width > 0;

    public static BotBlockadePose? Visual(BotCaptureObservation row, long coordinateEpoch = 0) =>
        row.VisualContext is { CameraX: { } x, CameraY: { } y }
            ? new($"visual/{row.Generation}/{row.RoomId}/{coordinateEpoch}", x, y, 1) : null;

    internal static BotBlockadePose? Hacker(GroundTruthCameraTransform camera, WorldSize size)
    {
        if (Math.Abs(camera.PixelsPerWorldUnitX / camera.PixelsPerWorldUnitY - 1) >= .02) return null;
        var width = size.Width / camera.PixelsPerWorldUnitX;
        return new("hacker/" + camera.SceneName, camera.CameraWorld.X - width / 2,
            camera.CameraWorld.Y - size.Height / (2 * camera.PixelsPerWorldUnitY), width);
    }
}

/// <summary>Registers stationary pushes using fresh Knight positions in one visit's coordinate space.</summary>
public sealed class BotBlockadeVisit
{
    private readonly List<BotBlockadeMark> marks = [];
    private (AtlasPoint Position, double Time)? previous;
    private (AtlasPoint Anchor, double Time, int Side, int Samples)? push;
    private (long Generation, int Room, string Space)? chart;
    private (long? Frame, double Time)? lastDetection;
    private BotBlockadeEvidence? lastEvidence;

    public void Reset()
    {
        marks.Clear();
        chart = null;
        Interrupt();
    }

    public void Interrupt()
    {
        previous = null;
        push = null;
        lastDetection = null;
        lastEvidence = null;
    }

    public BotCaptureObservation AddEvidence(BotCaptureObservation row, BotBlockadePose? pose) =>
        row with { BlockadeVersion = 1, BlockadePose = pose, Blockade = Observe(row, pose) };

    public BotBlockadeEvidence? Observe(BotCaptureObservation row, BotBlockadePose? pose)
    {
        if (chart is { } current && (current.Generation != row.Generation || current.Room != row.RoomId
            || pose is not null && current.Space != pose.Space)) Reset();
        if (row.KnightPrediction is not null && row.Issues.Count == 0 && pose is { IsValid: true }
            && chart is not null && push is { } driveInProgress && row.DriveDirection == driveInProgress.Side
            && previous is { } previousSighting && row.CapturedAt - previousSighting.Time is >= 0 and <= .25)
            return null;
        if (pose is not { IsValid: true } || row.Features is not { Count: >= 5 } || row.Features[4] < .5
            || row.Issues is null || row.Issues.Any(issue => issue != "missing-terrain-analysis") || row.KnightSource == "visual-predicted"
            || row.DetectionTimestamp is not { } detection
            || row.CaptureTimestamp - detection is not (>= 0 and <= BotRecordingContract.MaximumDetectionAge)
            || !row.Features.Take(5).All(double.IsFinite))
        {
            Interrupt();
            return null;
        }
        chart = (row.Generation, row.RoomId, pose.Space);
        var width = pose.Width;
        var position = new AtlasPoint(pose.X + row.Features[0] * width, pose.Y + row.Features[1] * width);
        var time = row.CapturedAt;
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(time))
        {
            Interrupt();
            return null;
        }
        var repeated = lastDetection is { } last && (row.DetectionFrameId.HasValue
            ? row.DetectionFrameId == last.Frame : detection == last.Time);
        if (previous is { } prior && (time - prior.Time is < 0 or > .35 || Distance(prior.Position, position) > .18 * width))
        {
            Interrupt();
            lastDetection = (row.DetectionFrameId, detection);
            if (repeated) return null;
        }

        if (row.Grounded == false || row.DriveDirection is not (-1 or 1)
            || push is { } currentPush && currentPush.Side != row.DriveDirection) push = null;
        if (repeated) return lastEvidence;
        lastDetection = (row.DetectionFrameId, detection);

        if (row.Grounded != false && row.DriveDirection is -1 or 1)
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
                var mark = new BotBlockadeMark(pose.Space, point.X, point.Y, side, width, false);
                if (matching < 0) marks.Add(mark);
                else if (marks[matching].Crossed) marks[matching] = mark;
            }
            push = drive;
        }
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
        lastEvidence = nearest;
        return lastEvidence;
    }

    private static double Distance(AtlasPoint a, AtlasPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
