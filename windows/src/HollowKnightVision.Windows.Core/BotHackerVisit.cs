namespace HollowKnightVision.Windows.Core;

/// <summary>An Atlas objective stored in room world coordinates so it survives changes in projection.</summary>
public sealed record BotAtlasObjective(string Scene, AtlasPoint WorldPosition)
{
    public AtlasPathPoint? Project(string scene, AtlasPoint scale, AtlasPlatformGraph graph) =>
        Scene == scene && AtlasPathPoint.TryQuantize(WorldPosition.X * scale.X, WorldPosition.Y * scale.Y, out var point)
            && graph.NearestStroke(point, graph.KnightHeight) is { } stroke ? stroke.Closest(point) : null;
}

/// <summary>Uses measured receiver frames to decorate observations within one Hacker visit.</summary>
public sealed class BotHackerVisit(GroundTruthLineDocument labels, AtlasPathLinkStore? links = null, AtlasPoint? fixedWorldGoal = null)
{
    private readonly Dictionary<string, RouteRoom> rooms = new(StringComparer.Ordinal);
    private readonly BotBlockadeVisit contact = new();
    private RouteRoom? activeRoom;
    private RouteSample? previous;
    private BotAtlasObjective? previousObjective;
    private Guid? session;
    private long generation = -1;

    public bool ObjectiveReachable { get; private set; }

    private sealed class RouteRoom(AtlasPathCalibration calibration, AtlasPlatformGraph graph, int linkRevision)
    {
        public AtlasPathCalibration Calibration { get; set; } = calibration;
        public AtlasPlatformGraph Graph { get; set; } = graph;
        public int LinkRevision { get; set; } = linkRevision;
        public AtlasPathNavigator Navigation { get; } = new();
        public HashSet<(int X, int Y)> Covered { get; } = [];
        public AtlasPoint? CoverageOrigin { get; set; }
    }

    private sealed record RouteSample(AtlasPathHint Path, AtlasPathPoint Knight, double Time, int Best, BotRoadHint Road);

    public BotCaptureObservation? Observe(WorldSize size, int? renderedFrame, ReceiverGroundTruthSample? sample,
        BotCaptureTiming timing, InputButtons? input, int revision, int? health = null,
        BotAtlasObjective? objective = null, string? roomKey = null)
    {
        var row = BotObservationBuilder.Hacker(size, renderedFrame, sample, labels, timing, input);
        if (row is null || sample is null)
        {
            Interrupt();
            return null;
        }
        if (session != sample.SessionId || generation != timing.Generation)
        {
            Interrupt();
            rooms.Clear();
            activeRoom = null;
            contact.Reset();
            session = sample.SessionId;
            generation = timing.Generation;
        }
        return Extend(row, sample, size, revision, health ?? row.MeasuredHealth, objective, roomKey);
    }

    private BotCaptureObservation Extend(BotCaptureObservation row, ReceiverGroundTruthSample sample, WorldSize size,
        int revision, int? health, BotAtlasObjective? objective, string? roomKey)
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
        var room = Room(camera.SceneName, scale, roomKey);
        room.CoverageOrigin ??= center;
        var pose = new AtlasPoint(center.X - room.CoverageOrigin.Value.X, center.Y - room.CoverageOrigin.Value.Y);
        AddCoverage(room.Covered, pose, size);
        var view = BotAtlasFrontiers.View(pose, size, revision, health, room.Covered.Count, 0, (x, y) => room.Covered.Contains((x, y)));
        var worldOrigin = new AtlasPoint(center.X - size.Width / 2, center.Y - size.Height / 2);
        var result = contact.AddEvidence(row, BotBlockadePose.Hacker(camera, size)) with
        {
            AtlasView = view,
            HackerPathVersion = 1
        };

        if (!ReferenceEquals(activeRoom, room) || previousObjective != objective)
        {
            previous = null;
            activeRoom?.Navigation.Interrupt();
            activeRoom = room;
            previousObjective = objective;
        }
        ObjectiveReachable = !fixedWorldGoal.HasValue && objective is null;
        var graph = room.Graph;
        if (row.Features[4] < .5 || row.Issues.Any(issue => issue != "missing-terrain-analysis")
            || !AtlasPathPoint.TryQuantize(worldOrigin.X + row.Features[0] * size.Width,
                worldOrigin.Y + (row.Features[1] - row.Features[3]) * size.Width, out var feet))
        {
            previous = null;
            room.Navigation.Interrupt();
            return result;
        }

        var goal = objective is null ? graph.UpperRightObjective : objective.Project(camera.SceneName, scale, graph);
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
            admitted: null, walked: [], fixedObjective: goal, fixedObjectiveOnGround: objective is null);
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

    private RouteRoom Room(string scene, AtlasPoint scale, string? roomKey)
    {
        var calibration = new AtlasPathCalibration(roomKey ?? "scene/" + scene, scene, default, scale);
        var revision = links?.Revision ?? 0;
        if (!rooms.TryGetValue(scene, out var room))
        {
            room = new(calibration, AuthoredAtlasGraph.Build(labels, calibration, links), revision);
            rooms[scene] = room;
        }
        else if (room.Calibration != calibration || room.LinkRevision != revision)
        {
            // A frame's projection can vary by a fraction of a pixel as the
            // camera moves. Retain route continuity across calibration updates.
            room.Graph = AuthoredAtlasGraph.Build(labels, calibration, links, room.Graph);
            room.Calibration = calibration;
            if (room.LinkRevision != revision)
            {
                previous = null;
                room.Navigation.Interrupt();
            }
            room.LinkRevision = revision;
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

    private static void AddCoverage(HashSet<(int X, int Y)> cells, AtlasPoint pose, WorldSize size)
    {
        var firstX = BotAtlasFrontiers.FirstCenter(pose.X);
        var firstY = BotAtlasFrontiers.FirstCenter(pose.Y);
        var endX = BotAtlasFrontiers.FirstCenter(pose.X + size.Width);
        var endY = BotAtlasFrontiers.FirstCenter(pose.Y + size.Height);
        for (var y = firstY; y < endY; y++)
        for (var x = firstX; x < endX; x++)
            cells.Add((x, y));
    }
}
