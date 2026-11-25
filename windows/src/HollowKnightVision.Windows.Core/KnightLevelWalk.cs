namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Brackets walked floor with measurements of the Knight's feet and a
/// registered camera. An occluded frame may carry input and camera evidence,
/// but only a new sighting can add ground.
/// </summary>
public sealed class KnightLevelWalk
{
    private sealed class Run(double time, double x, double y, double cameraY, bool standing)
    {
        internal readonly double Started = time;
        internal readonly double Y = y;
        internal readonly double CameraY = cameraY;
        internal double LastSeen = time;
        internal double LastX = x;
        internal double Left = x;
        internal double Right = x;
        internal int Sightings = 1;
        internal double LeftSeconds;
        internal double RightSeconds;
        internal bool Confirmed;
        internal double? OffRowSince;
        internal double? StandingSince;
        internal bool EstablishedStandingLevel = standing;
    }

    private Run? run;
    private double? previousTime;
    private int previousDirection;
    private AtlasPoint? previousCamera;
    private double previousCameraTime;
    private (AtlasPoint Foot, double Since, double Seen)? screenRest;

    public void Reset()
    {
        run = null;
        previousTime = null;
        previousDirection = 0;
        previousCamera = null;
        previousCameraTime = 0;
        screenRest = null;
    }

    public CleanFloorLine? Observe(AtlasPoint? foot, AtlasPoint? camera, InputButtons? buttons, double time,
        int width, int height, bool recentJump = false)
    {
        if (buttons is not { } input || recentJump || (input & InputButtons.ActionZ) != 0 || !double.IsFinite(time)
            || width <= 0 || height <= 0)
        {
            Reset();
            return null;
        }

        var direction = Direction(input);
        var scale = width / 640d;
        var dt = time - (previousTime ?? time);
        if (previousTime is not null && (dt <= 0 || dt > .18))
        {
            Reset();
            dt = 0;
        }
        if (camera is null && direction == 0 && foot is { } standing && Finite(standing))
        {
            var since = screenRest is { } rest && time - rest.Seen <= .15
                && Distance(standing, rest.Foot) <= 2.5 * scale ? rest.Since : time;
            screenRest = (standing, since, time);
        }
        if (run is { } active)
        {
            var sinceCamera = time - previousCameraTime;
            var cameraDiscontinuity = camera is { } next && previousCamera is { } last
                && (Math.Abs(next.X - last.X) > (5 + 550 * sinceCamera) * scale
                    || Math.Abs(next.Y - active.CameraY) > 3.5 * scale);
            if (sinceCamera > .18 || cameraDiscontinuity || time - active.LastSeen > 1.25) run = null;
            else
            {
                if (previousDirection == -1 || direction == -1) active.LeftSeconds += dt;
                if (previousDirection == 1 || direction == 1) active.RightSeconds += dt;
            }
        }
        previousTime = time;
        previousDirection = direction;
        if (camera is not { } pose || !Finite(pose)) return null;
        previousCamera = pose;
        previousCameraTime = time;
        if (foot is not { } contact || !Finite(contact)) return null;
        var x = contact.X + pose.X;
        var y = contact.Y - pose.Y;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return null;

        Run Seed() => new(time, x, y, pose.Y, screenRest is { } rest
            && rest.Seen - rest.Since >= .14 && time - rest.Seen <= .15
            && Distance(contact, rest.Foot) <= 2.5 * scale);

        if (run is not { } walk)
        {
            run = Seed();
            return null;
        }

        var dx = x - walk.LastX;
        var dy = Math.Abs(y - walk.Y);
        var lean = direction == 0 ? 12 : 4;
        if (dy > 9 * scale || dx > (lean + 480 * walk.RightSeconds) * scale
            || -dx > (lean + 480 * walk.LeftSeconds) * scale)
        {
            run = Seed();
            return null;
        }
        if (dy > 2.5 * scale)
        {
            var since = walk.OffRowSince ?? time;
            walk.OffRowSince = since;
            if ((!walk.Confirmed && !walk.EstablishedStandingLevel) || time - since > .35) run = Seed();
            return null;
        }
        walk.OffRowSince = null;
        if (direction == 0 && Math.Abs(dx) <= 3 * scale)
        {
            walk.StandingSince ??= time;
            if (time - walk.StandingSince >= .14) walk.EstablishedStandingLevel = true;
        }
        else walk.StandingSince = null;
        if (walk.LeftSeconds > 0) walk.Left = Math.Min(walk.Left, x);
        if (walk.RightSeconds > 0) walk.Right = Math.Max(walk.Right, x);
        walk.Sightings++;
        walk.Confirmed |= time - walk.Started >= .24 && walk.Sightings >= 4 && walk.Right - walk.Left >= 4 * scale;
        walk.LastSeen = time;
        walk.LastX = x;
        walk.LeftSeconds = walk.RightSeconds = 0;
        var row = Math.Round(walk.Y + pose.Y, MidpointRounding.AwayFromZero);
        if (!walk.Confirmed || direction == 0 || row < 0 || row >= height) return null;
        var left = Math.Clamp(Math.Round(walk.Left - pose.X - 4 * scale, MidpointRounding.AwayFromZero), 0, width);
        var right = Math.Clamp(Math.Round(walk.Right - pose.X + 4 * scale, MidpointRounding.AwayFromZero), -1, width - 1);
        return left < right ? new((int)row, new((int)left, (int)right)) : null;
    }

    public static int Direction(InputButtons buttons) =>
        ((buttons & InputButtons.Right) != 0 ? 1 : 0) - ((buttons & InputButtons.Left) != 0 ? 1 : 0);

    private static bool Finite(AtlasPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double Distance(AtlasPoint a, AtlasPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
