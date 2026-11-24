namespace HollowKnightVision.Windows.Core;

/// <summary>The bottom-left camera origin and view width in a measured coordinate space.</summary>
public sealed record BotBlockadePose(string Space, double X, double Y, double Width)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Space) && double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && Width > 0;

    public static BotBlockadePose? Visual(BotCaptureObservation row, long coordinateEpoch = 0) =>
        row.VisualContext is { CameraX: { } x, CameraY: { } y }
            ? new(VisualSpace(row.Generation, row.RoomId, coordinateEpoch), x, y, 1) : null;

    internal static string VisualSpace(long generation, int room, long coordinateEpoch) =>
        FormattableString.Invariant($"visual/{generation}/{room}/{coordinateEpoch}");

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
    private readonly record struct VisualBasis(long Generation, int Room, long TrackerEpoch, int Width,
        double StartedAt, double Latest, AtlasPoint? SceneryCamera);

    private readonly List<BotBlockadeMark> marks = [];
    private (AtlasPoint Position, double Time)? previous;
    private (AtlasPoint Anchor, double Time, int Side, int Samples)? push;
    private (long Generation, int Room, string Space)? chart;
    private (long? Frame, double Time)? lastDetection;
    private BotBlockadeEvidence? lastEvidence;
    private VisualBasis? visualBasis;
    private long visualEpoch;

    public void Reset()
    {
        visualBasis = null;
        visualEpoch = 0;
        ClearContacts();
    }

    private void ClearContacts()
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

    public BotCaptureObservation AddVisualEvidence(BotCaptureObservation row)
    {
        var pose = BotBlockadePose.Visual(row, visualEpoch);
        if (pose is null && visualBasis is { SceneryCamera: { } camera } basis
            && (basis.Generation, basis.Room) == (row.Generation, row.RoomId)
            && basis.Latest == row.CaptureTimestamp && basis.Width == row.VisualContext?.FrameWidth)
        {
            pose = new(BotBlockadePose.VisualSpace(row.Generation, row.RoomId, visualEpoch),
                camera.X / basis.Width, camera.Y / basis.Width, 1);
        }
        return AddEvidence(row, pose);
    }

    /// <summary>Accepts camera basis updates at capture cadence, including frames between policy samples.</summary>
    public void ObserveVisualBasis(long generation, int room, long trackerEpoch, int width, double timestamp,
        bool verifiedCamera, GroundHypothesisTrackingResult ground, AtlasPoint? measuredSceneryCamera = null)
    {
        if (width <= 0 || !double.IsFinite(timestamp)) return;
        var scenery = !verifiedCamera && measuredSceneryCamera is { } camera
            && double.IsFinite(camera.X) && double.IsFinite(camera.Y) ? measuredSceneryCamera : null;
        if (visualBasis is not { } prior || (prior.Generation, prior.Room) != (generation, room))
        {
            Reset();
            visualBasis = new(generation, room, trackerEpoch, width, timestamp, timestamp, scenery);
            return;
        }
        if (timestamp <= prior.Latest) return;

        var correction = ground.GlobalCorrection;
        var changed = trackerEpoch != prior.TrackerEpoch || width != prior.Width
            || correction is { } offset && (offset.X != 0 || offset.Y != 0);
        if (!changed)
        {
            visualBasis = prior with { Latest = timestamp, SceneryCamera = scenery };
            return;
        }

        var source = BotBlockadePose.VisualSpace(generation, room, visualEpoch);
        var destination = BotBlockadePose.VisualSpace(generation, room, ++visualEpoch);
        if (width == prior.Width && verifiedCamera && GroundMotionEvidence.IsMeasured(ground)
            && ground.LocalTextureSupport > 0 && ground.LocalReferenceTimestamp is { } reference
            && reference >= prior.StartedAt && reference <= prior.Latest && timestamp - reference <= .35
            && ground.LocalReferencePosition is { } position && IsFinite(position)
            && ground.LocalMatchedImageDisplacement is { } displacement && IsFinite(displacement)
            && correction is { } translation && IsFinite(translation)
            && chart == (generation, room, source))
        {
            Rebase(new(translation.X / width, translation.Y / width), destination);
            chart = (generation, room, destination);
        }
        else ClearContacts();
        visualBasis = new(generation, room, trackerEpoch, width, timestamp, timestamp, scenery);
    }

    private void Rebase(AtlasPoint translation, string destination)
    {
        for (var index = 0; index < marks.Count; index++) marks[index] = Moved(marks[index]);
        if (previous is { } sighting) previous = (MovedPoint(sighting.Position), sighting.Time);
        if (push is { } drive) push = (MovedPoint(drive.Anchor), drive.Time, drive.Side, drive.Samples);
        if (lastEvidence is { } evidence) lastEvidence = evidence with { Mark = Moved(evidence.Mark) };

        BotBlockadeMark Moved(BotBlockadeMark mark) =>
            mark with { Space = destination, X = mark.X + translation.X, Y = mark.Y + translation.Y };
        AtlasPoint MovedPoint(AtlasPoint point) => new(point.X + translation.X, point.Y + translation.Y);
    }

    private static bool IsFinite(CameraVector value) => double.IsFinite(value.X) && double.IsFinite(value.Y);

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
