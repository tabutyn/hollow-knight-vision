namespace HollowKnightVision.Windows.Core;

/// <summary>An Atlas objective stored in room world coordinates so it survives changes in projection.</summary>
public sealed record BotAtlasObjective(string Scene, AtlasPoint WorldPosition)
{
    public AtlasPathPoint? Project(string scene, AtlasPoint scale, AtlasPlatformGraph graph) =>
        Scene == scene && AtlasPathPoint.TryQuantize(WorldPosition.X * scale.X, WorldPosition.Y * scale.Y, out var point)
            && graph.NearestStroke(point, graph.KnightHeight) is { } stroke ? stroke.Closest(point) : null;
}

/// <summary>Uses measured receiver frames to decorate observations within one Hacker visit.</summary>
public sealed class BotHackerVisit(GroundTruthLineDocument labels, AtlasPathLinkStore? links = null,
    AtlasPoint? fixedWorldGoal = null, bool includeThinkingRoutes = false)
{
    private readonly Dictionary<string, RouteRoom> rooms = new(StringComparer.Ordinal);
    private readonly BotBlockadeVisit contact = new();
    public IReadOnlyList<BotBlockadeMark> ActiveWallContacts => contact.ActiveMarks;
    private RouteRoom? activeRoom;
    private RouteSample? previous;
    private BotAtlasObjective? previousObjective;
    private Guid? session;
    private long generation = -1;

    public bool ObjectiveReachable { get; private set; }
    public IReadOnlyList<ThinkingRoute> ThinkingRoutes { get; private set; } = [];
    public (string Chart, AtlasPathPoint Feet, int ArrivalRadius)? ThinkingPosition { get; private set; }

    private sealed class RouteRoom(AtlasPathCalibration calibration, AtlasPlatformGraph graph, int linkRevision)
    {
        public AtlasPathCalibration Calibration { get; set; } = calibration;
        public AtlasPlatformGraph Graph { get; set; } = graph;
        public int LinkRevision { get; set; } = linkRevision;
        public AtlasPathNavigator Navigation { get; } = new();
        public AtlasPathNavigator? Exploration { get; set; }
        public HashSet<(int X, int Y)> Covered { get; } = [];
        public AtlasPoint? CoverageOrigin { get; set; }
        public AtlasPathPoint? Goal { get; set; }
        public int? BestRemaining { get; set; }
        public bool Automatic { get; set; }
        private readonly HashSet<(int Lane, int Position, int Direction)> credited = [];

        public int Credit(AtlasPathHint route, int count)
        {
            var horizontal = route.Current.X != route.Next.X;
            var direction = horizontal ? Math.Sign(route.Next.X - route.Current.X) : Math.Sign(route.Next.Y - route.Current.Y);
            var lane = AtlasPathPoint.Round((horizontal ? route.Current.Y : route.Current.X) / (double)Math.Max(6, Graph.KnightHeight / 2));
            var start = horizontal ? route.Current.X : route.Current.Y;
            var added = 0;
            for (var offset = 0; offset < count; offset++)
                if (credited.Add((lane, start + direction * offset, horizontal ? direction : 2 * direction))) added++;
            return added;
        }
    }

    private sealed record RouteSample(AtlasPathHint Path, AtlasPathPoint Knight, double Time, BotRoadHint Road);

    public BotCaptureObservation? ObserveMatched(BotHackerCaptureFrame frame, double availableAt)
    {
        var match = frame.Match;
        var capture = match.Capture;
        var size = new WorldSize(capture.Image.Width, capture.Image.Height);
        var timing = new BotCaptureTiming(capture.CapturedAt, availableAt, frame.Generation, frame.RoomId, 0);
        var row = Observe(size, capture.UnityFrame, match.Sample, timing, capture.MovementButtons, 0,
            objective: frame.Objective, roomKey: frame.RoomKey, automaticObjective: frame.AutomaticObjective,
            registeredRoom: frame.RegisteredRoom);
        if (row is null) return null;
        var camera = GroundTruthCameraTransform.FromSample(match.Sample, match.SampleObservedAt, size);
        return row with { AtlasView = camera is null ? null : frame.RegisteredRoom?.BotView(camera, row.MeasuredHealth) };
    }

    public BotCaptureObservation? Observe(WorldSize size, int? renderedFrame, ReceiverGroundTruthSample? sample,
        BotCaptureTiming timing, InputButtons? input, int revision, int? health = null,
        BotAtlasObjective? objective = null, string? roomKey = null, bool automaticObjective = false,
        HackerAtlasRoom? registeredRoom = null)
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
        return Extend(row, sample, size, revision, health ?? row.MeasuredHealth, objective, roomKey, automaticObjective, registeredRoom);
    }

    private BotCaptureObservation Extend(BotCaptureObservation row, ReceiverGroundTruthSample sample, WorldSize size,
        int revision, int? health, BotAtlasObjective? objective, string? roomKey, bool automaticObjective,
        HackerAtlasRoom? registeredRoom)
    {
        ObjectiveReachable = false;
        ThinkingRoutes = [];
        ThinkingPosition = null;
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
        var coverageOrigin = room.CoverageOrigin ??= center;
        var pose = new AtlasPoint(center.X - coverageOrigin.X, center.Y - coverageOrigin.Y);
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
            activeRoom?.Exploration?.Interrupt();
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
            room.Exploration?.Interrupt();
            return result;
        }

        if (includeThinkingRoutes) ThinkingPosition = (room.Calibration.Room, feet, Math.Max(3, graph.KnightWidth / 2));
        var automatic = automaticObjective && objective is null && !fixedWorldGoal.HasValue;
        var goal = automatic ? null : objective is null ? graph.UpperRightObjective : objective.Project(camera.SceneName, scale, graph);
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
        if (room.Goal != goal || room.Automatic != automatic)
        {
            room.Goal = goal;
            room.Automatic = automatic;
            room.BestRemaining = null;
            previous = null;
            room.Navigation.ClearObjective();
        }
        var path = goal is null && !automatic ? null : room.Navigation.Follow(graph, feet, sample.Grounded, measured: true,
            admitted: automatic ? (a, b) => AtlasPathRouting.AllowsRegisteredSegment(graph, a, b, (int)BotAtlasFrontiers.CellSize, Known) : null,
            walked: [], fixedObjective: goal, fixedObjectiveOnGround: objective is null);
        ObjectiveReachable |= path.HasValue;
        if (room.Exploration is { } exploration)
        {
            var routes = new List<ThinkingRoute>(2);
            if (!automatic && exploration.Follow(graph, feet, sample.Grounded, measured: true,
                admitted: (a, b) => AtlasPathRouting.AllowsRegisteredSegment(graph, a, b, (int)BotAtlasFrontiers.CellSize, Known),
                walked: []) is { } frontier)
                routes.Add(ThinkingRoute.FromPath(room.Calibration.Room, frontier, graph.KnightHeight, size.Width, true));
            if (path is { } planned)
                routes.Add(ThinkingRoute.FromPath(room.Calibration.Room, planned, graph.KnightHeight, size.Width, automatic));
            ThinkingRoutes = routes;
        }
        var knight = new AtlasPathPoint(feet.X, feet.Y + graph.KnightHeight);
        if (path is not { } next)
        {
            if (automatic && room.Navigation.LastProjection is { } projection)
            {
                var completed = Score(room, projection, room.Navigation.PriorRemaining, knight, row.CaptureTimestamp);
                result = result with { PathProgress = completed.Progress, PathAlignment = completed.Alignment };
            }
            previous = null;
            if (room.Navigation.HasStartProjection) room.BestRemaining = null;
            room.Navigation.Interrupt();
            return result;
        }

        var road = BotPathMotion.Road(next, graph.KnightHeight, size.Width, frontier: automatic);
        var (progress, alignment) = Score(room, next.Projection, next.PriorRemaining, knight, row.CaptureTimestamp);
        if (automatic && previous?.Path.Objective != next.Objective) room.BestRemaining = null;
        room.BestRemaining = Math.Min(room.BestRemaining ?? next.Remaining, next.Remaining);
        previous = new(next, knight, row.CaptureTimestamp, road);
        return result with { RoadHint = road, PathProgress = progress, PathAlignment = alignment };

        bool Known(AtlasPathPoint point)
        {
            var floor = point.Y - graph.KnightHeight;
            return room.Covered.Contains((BotAtlasFrontiers.Cell(point.X - coverageOrigin.X + size.Width / 2),
                       BotAtlasFrontiers.Cell(floor - coverageOrigin.Y + size.Height / 2)))
                || registeredRoom is { } saved && saved.SceneName == camera.SceneName
                && saved.HasRegisteredGround(new(point.X / scale.X, floor / scale.Y));
        }
    }

    public void Interrupt()
    {
        ObjectiveReachable = false;
        ThinkingRoutes = [];
        ThinkingPosition = null;
        previous = null;
        contact.Interrupt();
        activeRoom?.Navigation.Interrupt();
        activeRoom?.Exploration?.Interrupt();
    }

    private RouteRoom Room(string scene, AtlasPoint scale, string? roomKey)
    {
        var calibration = new AtlasPathCalibration(roomKey ?? "scene/" + scene, scene, default, scale);
        var revision = links?.Revision ?? 0;
        if (!rooms.TryGetValue(scene, out var room))
        {
            room = new(calibration, AuthoredAtlasGraph.Build(labels, calibration, links), revision)
            {
                Exploration = includeThinkingRoutes ? new() : null
            };
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
                room.BestRemaining = null;
                room.Navigation.Interrupt();
                room.Exploration?.Interrupt();
            }
            room.LinkRevision = revision;
        }
        return room;
    }

    private (double Progress, double? Alignment) Score(RouteRoom room, AtlasPathPoint projection, int? remaining,
        AtlasPathPoint knight, double time)
    {
        var height = room.Graph.KnightHeight;
        if (previous is not { } old || time - old.Time is <= 0 or >= .45
            || old.Knight.DistanceTo(knight) > 3 * height)
            return (0, null);
        var motionX = knight.X - old.Knight.X;
        var motionY = knight.Y - old.Knight.Y;
        var alignment = old.Road.Dx == 0 && old.Road.Dy == 0
            ? (double?)null : (motionX * old.Road.Dx + motionY * old.Road.Dy) / height;
        if (room.BestRemaining is not { } baseline || remaining is not { } distance || distance >= baseline)
            return (0, alignment);

        var count = BotPathMotion.ForwardPixels(old.Path, old.Knight, baseline, knight, projection, distance);
        if (room.Automatic && count > 0) count = room.Credit(old.Path, count);
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
