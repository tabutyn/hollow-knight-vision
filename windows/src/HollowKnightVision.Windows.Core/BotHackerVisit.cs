namespace HollowKnightVision.Windows.Core;

/// <summary>Uses measured receiver frames to decorate observations within one Hacker visit.</summary>
public sealed class BotHackerVisit(GroundTruthLineDocument labels, AtlasPathLinkStore? links = null, AtlasPoint? fixedWorldGoal = null)
{
    private const double CellSize = 64;
    private readonly HashSet<(int X, int Y)> covered = [];
    private readonly Dictionary<string, RouteRoom> rooms = new(StringComparer.Ordinal);
    private readonly BotHackerContact contact = new();
    private AtlasPoint? origin;
    private RouteRoom? activeRoom;
    private RouteSample? previous;

    public bool ObjectiveReachable { get; private set; }

    private sealed class RouteRoom(AtlasPathCalibration calibration, AtlasPlatformGraph graph)
    {
        public AtlasPathCalibration Calibration { get; set; } = calibration;
        public AtlasPlatformGraph Graph { get; set; } = graph;
        public AtlasPathNavigator Navigation { get; } = new();
    }

    private sealed record RouteSample(AtlasPathHint Path, AtlasPathPoint Knight, double Time, int Best, BotRoadHint Road);

    public BotCaptureObservation Extend(BotCaptureObservation row, ReceiverGroundTruthSample sample, WorldSize size,
        int revision, int? health)
    {
        ObjectiveReachable = false;
        if (size.Width > 8192 || size.Height > 8192
            || GroundTruthCameraTransform.FromSample(sample, row.CaptureTimestamp, size) is not { } camera
            || camera.PixelsPerWorldUnitX > 10_000 || camera.PixelsPerWorldUnitY > 10_000
            || string.IsNullOrWhiteSpace(camera.SceneName) || !AtlasPathPoint.TryQuantize(camera.CameraWorld.X * camera.PixelsPerWorldUnitX,
                camera.CameraWorld.Y * camera.PixelsPerWorldUnitY, out _))
        {
            Interrupt();
            return row;
        }

        var scale = new AtlasPoint(camera.PixelsPerWorldUnitX, camera.PixelsPerWorldUnitY);
        var center = new AtlasPoint(camera.CameraWorld.X * scale.X, camera.CameraWorld.Y * scale.Y);
        origin ??= center;
        var pose = new AtlasPoint(center.X - origin.Value.X, center.Y - origin.Value.Y);
        AddCoverage(covered, pose, size);
        var view = View(pose, size, revision, health);
        var worldOrigin = new AtlasPoint(center.X - size.Width / 2, center.Y - size.Height / 2);
        var result = row with { AtlasView = view, HackerPathVersion = 1,
            Blockade = contact.Observe(row, camera, size, sample.Grounded) };

        var room = Room(camera.SceneName, scale);
        if (!ReferenceEquals(activeRoom, room))
        {
            previous = null;
            activeRoom?.Navigation.Interrupt();
            activeRoom = room;
        }
        ObjectiveReachable = !fixedWorldGoal.HasValue;
        var graph = room.Graph;
        if (row.Features[4] < .5 || row.Issues.Any(issue => issue != "missing-terrain-analysis")
            || !AtlasPathPoint.TryQuantize(worldOrigin.X + row.Features[0] * size.Width,
                worldOrigin.Y + (row.Features[1] - row.Features[3]) * size.Width, out var feet))
        {
            previous = null;
            room.Navigation.Interrupt();
            return result;
        }

        var goal = graph.UpperRightObjective;
        if (fixedWorldGoal is { } fixedGoal)
        {
            if (camera.SceneName != "Tutorial_01"
                || !AtlasPathPoint.TryQuantize(fixedGoal.X * scale.X, fixedGoal.Y * scale.Y, out var gate)
                || graph.NearestStroke(gate, 4 * graph.KnightHeight, walkOnly: true) is not { } target)
            {
                previous = null;
                return result;
            }
            goal = target.Closest(gate);
        }
        var path = goal is null ? null : room.Navigation.Follow(graph, feet, sample.Grounded, measured: true,
            admitted: null, walked: [], fixedObjective: goal);
        ObjectiveReachable |= path.HasValue;
        if (path is not { } next)
        {
            previous = null;
            room.Navigation.Interrupt();
            return result;
        }

        var dx = next.FallSteering ?? next.Next.X - next.Current.X;
        var dy = next.FallSteering.HasValue ? -1 : next.Next.Y - next.Current.Y;
        var magnitude = Math.Max(1, Math.Sqrt((double)dx * dx + (double)dy * dy));
        var road = new BotRoadHint(dx / magnitude, dy / magnitude,
            Math.Min(1, graph.KnightHeight / size.Width), Math.Min(1, next.Remaining / (4 * size.Width)), Frontier: false);
        var knight = new AtlasPathPoint(feet.X, feet.Y + graph.KnightHeight);
        var (progress, alignment) = Score(graph.KnightHeight, next, knight, row.CaptureTimestamp);
        var best = previous is { } prior && prior.Path.Objective == next.Objective ? Math.Min(prior.Best, next.Remaining) : next.Remaining;
        previous = new(next, knight, row.CaptureTimestamp, best, road);
        return result with { RoadHint = road, PathProgress = progress, PathAlignment = alignment };
    }

    public void Interrupt()
    {
        ObjectiveReachable = false;
        previous = null;
        contact.Interrupt();
        activeRoom?.Navigation.Interrupt();
    }

    private RouteRoom Room(string scene, AtlasPoint scale)
    {
        var calibration = new AtlasPathCalibration("scene/" + scene, scene, default, scale);
        if (!rooms.TryGetValue(scene, out var room))
        {
            room = new(calibration, AuthoredAtlasGraph.Build(labels, calibration, links));
            rooms[scene] = room;
        }
        else if (room.Calibration != calibration)
        {
            // A frame's projection can vary by a fraction of a pixel as the
            // camera moves. Retain route continuity across calibration updates.
            room.Graph = AuthoredAtlasGraph.Build(labels, calibration, links, room.Graph);
            room.Calibration = calibration;
        }
        return room;
    }

    private (double Progress, double? Alignment) Score(int height, AtlasPathHint next, AtlasPathPoint knight, double time)
    {
        if (previous is not { } old || time - old.Time is <= 0 or >= .45
            || old.Knight.DistanceTo(knight) > 3 * height)
            return (0, null);
        var motionX = knight.X - old.Knight.X;
        var motionY = knight.Y - old.Knight.Y;
        var alignment = (motionX * old.Road.Dx + motionY * old.Road.Dy) / height;
        if (next.PriorRemaining is not { } remaining || remaining >= old.Best) return (0, alignment);

        var horizontal = old.Path.Next.X != old.Path.Current.X;
        var direction = horizontal ? Math.Sign(old.Path.Next.X - old.Path.Current.X) : Math.Sign(old.Path.Next.Y - old.Path.Current.Y);
        var oldAxis = horizontal ? old.Path.Current.X : old.Path.Current.Y;
        var newAxis = horizontal ? next.Projection.X : next.Projection.Y;
        var physical = horizontal ? motionX : motionY;
        var count = Math.Max(0, Math.Min(old.Best - remaining, Math.Min((newAxis - oldAxis) * direction, physical * direction)));
        return (count / (double)height, alignment);
    }

    private BotAtlasView View(AtlasPoint pose, WorldSize size, int revision, int? health)
    {
        var middle = Cell(pose.Y + size.Height / 2);
        var missing = new List<double>(15);
        for (var y = -1; y <= 1; y++)
        for (var x = -2; x <= 2; x++)
        {
            var unknown = 0;
            foreach (var oy in new[] { -.125, .125 })
            foreach (var ox in new[] { -.125, .125 })
            {
                var wx = pose.X + size.Width * (.5 + x * .5 + ox);
                var wy = pose.Y + size.Height * (.5 + y * .5 + oy);
                if (!covered.Contains((Cell(wx), Cell(wy)))) unknown++;
            }
            missing.Add(unknown / 4d);
        }
        return new(Proximity(-1), Proximity(1), [pose.X, pose.Y], size.Width, covered.Count, 0, revision, health, missing);

        double Proximity(int direction)
        {
            var column = Cell(direction < 0 ? pose.X : pose.X + size.Width);
            for (var step = 1; step <= 64; step++)
            {
                var known = 0;
                for (var row = middle - 1; row <= middle + 1; row++)
                    if (covered.Contains((column + direction * step, row))) known++;
                if (known < 2) return 1 / (1 + (step - 1) / 5d);
            }
            return 0;
        }
    }

    private static void AddCoverage(HashSet<(int X, int Y)> cells, AtlasPoint pose, WorldSize size)
    {
        var firstX = CenterStart(pose.X);
        var firstY = CenterStart(pose.Y);
        var endX = CenterStart(pose.X + size.Width);
        var endY = CenterStart(pose.Y + size.Height);
        for (var y = firstY; y < endY; y++)
        for (var x = firstX; x < endX; x++)
            cells.Add((x, y));

        static int CenterStart(double value) => (int)Math.Ceiling(value / CellSize - .5);
    }

    private static int Cell(double value) => (int)Math.Floor(value / CellSize);
}
